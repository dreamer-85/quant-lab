"""Typed, append-free writers for the DataFeeds on-disk layout.

Layout (any folder under a feed root, e.g. ``DataFeeds/feeds``)::

    <feed_root>/<market>/<provider>/<symbol>/
        bars_<seconds>.csv     timestamp_ms,open,high,low,close,volume
        trades.csv             timestamp_ms,price,size,side,trade_id
        quotes.csv             timestamp_ms,bid_price,bid_size,ask_price,ask_size
        book_updates.csv       timestamp_ms,side,price,quantity,action

``book_updates.csv`` (optional) carries order book level updates consumed by
``LocalFeedDataSource`` as ``OrderBookUpdate`` events: ``side`` is ``bid``|``ask``,
``action`` is ``add``|``modify``|``remove`` (default ``add``), and ``quantity`` is
the new absolute size at the price (0 for a remove).

Files are written atomically and sorted ascending by timestamp so the
ResearchEngine ``LocalFeedDataSource`` can stream them lazily as deterministic,
memory-bounded event sub-streams.
"""

from __future__ import annotations

import csv
import json
import os
import tempfile
from datetime import datetime, timezone
from pathlib import Path

from .datatypes import (
    BAR_HEADER,
    BOOK_UPDATE_HEADER,
    QUOTE_HEADER,
    TRADE_HEADER,
    Bar,
    Quote,
    Trade,
)
from .times import interval_label


def symbol_dir(feed_root, market: str, provider: str, symbol: str) -> Path:
    """Returns the directory where one symbol's data for a provider is staged."""
    market = market.lower().replace(" ", "_")
    provider = provider.lower().replace(" ", "_")
    safe_symbol = str(symbol).replace("/", "").replace(" ", "_")
    path = Path(feed_root) / market / provider / safe_symbol
    return path


def _write_rows(path: Path, header: str, rows, columns) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".feed-", suffix=".csv", dir=str(path.parent))
    count = 0
    try:
        with os.fdopen(fd, "w", newline="", encoding="utf-8") as handle:
            writer = csv.writer(handle)
            writer.writerow(header.split(","))
            for row in rows:
                writer.writerow(columns(row))
                count += 1
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    return count


class StreamingWriters:
    """Appends rows to the staged CSVs as they arrive, for live sessions.

    The ``write_*`` helpers above are atomic-at-end: they collect everything,
    sort it, and swap a complete file into place in one ``os.replace``. That is
    the right shape for a historical pull, where a reader only ever looks at
    finished data, and it is why a live pull produced no files until the
    websocket closed.

    A live session has the opposite requirement. A research engine watching
    the folder needs to see rows while the session is still running, so this
    writer opens each file once, writes the header, appends each row, and
    flushes immediately. Rows go out in arrival order, which for a websocket
    feed is ascending in timestamp apart from rare out-of-order delivery.

    Concurrency note: the same trade can arrive twice from a reconnecting
    websocket. ``seen`` de-duplicates on the natural key for each row type so a
    reconnect does not inflate volume. The buffer is per-session and bounded by
    how much the session actually trades.
    """

    def __init__(self, feed_root, market: str, provider: str, symbol: str,
                 enabled: bool = True, interval_seconds=None):
        self.enabled = bool(enabled)
        # Bars land in bars_<interval>.csv, which must match the name the
        # non-streaming writer would have produced for the same interval.
        self._bars_filename = f"bars_{interval_label(interval_seconds or 60)}.csv"
        self.files: dict = {}
        self.counts: dict = {}
        self._handles: dict = {}
        self._writers: dict = {}
        self._seen: set = set()
        self._dir = symbol_dir(feed_root, market, provider, symbol)
        if not self.enabled:
            return
        self._dir.mkdir(parents=True, exist_ok=True)

    def _writer_for(self, kind: str, filename: str, header: str, columns):
        if kind not in self._writers:
            path = self._dir / filename
            # A pre-existing file from an earlier session would duplicate its
            # header, so start clean when opening for append.
            if path.exists():
                path.unlink()
            handle = path.open("a", newline="", encoding="utf-8")
            writer = csv.writer(handle)
            writer.writerow(header.split(","))
            handle.flush()
            self._handles[kind] = handle
            self._writers[kind] = writer
            self.files[kind] = path
            self.counts[kind] = 0
        return self._writers[kind], self._handles[kind]

    def _emit(self, kind: str, key, filename: str, header: str, columns, row) -> None:
        if not self.enabled or key in self._seen:
            return
        self._seen.add(key)
        writer, handle = self._writer_for(kind, filename, header, columns)
        writer.writerow(columns(row))
        handle.flush()
        self.counts[kind] = self.counts.get(kind, 0) + 1

    def trade(self, t: Trade) -> None:
        self._emit("trades", (t.timestamp_ms, t.trade_id or t.price, t.size), "trades.csv", TRADE_HEADER,
                   lambda r: [r.timestamp_ms, r.price, r.size, r.side, r.trade_id or ""], t)

    def quote(self, q: Quote) -> None:
        self._emit("quotes", (q.timestamp_ms, q.bid_price, q.ask_price), "quotes.csv", QUOTE_HEADER,
                   lambda r: [r.timestamp_ms, r.bid_price, r.bid_size, r.ask_price, r.ask_size], q)

    def bar(self, b: Bar) -> None:
        self._emit("bars", b.timestamp_ms, self._bars_filename, BAR_HEADER,
                   lambda r: [r.timestamp_ms, r.open, r.high, r.low, r.close, r.volume], b)

    def book_update(self, u) -> None:
        self._emit("book_updates", (u.timestamp_ms, u.side, u.price, u.quantity), "book_updates.csv",
                   BOOK_UPDATE_HEADER,
                   lambda r: [r.timestamp_ms, r.side, r.price, r.quantity, r.action], u)

    def close(self) -> None:
        for handle in self._handles.values():
            try:
                handle.flush()
                handle.close()
            except OSError:
                pass
        self._handles.clear()
        self._writers.clear()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
        return False


def write_bars(feed_root, market: str, provider: str, symbol: str, interval_seconds, bars) -> tuple[Path, int]:
    """Writes sorted OHLCV bars to ``bars_<seconds>.csv``. Returns (path, count)."""
    ordered = sorted(bars, key=lambda b: b.timestamp_ms) if bars else []
    path = symbol_dir(feed_root, market, provider, symbol) / f"bars_{interval_label(interval_seconds)}.csv"
    count = _write_rows(
        path,
        BAR_HEADER,
        ordered,
        lambda b: [b.timestamp_ms, b.open, b.high, b.low, b.close, b.volume],
    )
    return path, count


def write_trades(feed_root, market: str, provider: str, symbol: str, trades) -> tuple[Path, int]:
    """Writes sorted trades to ``trades.csv``. Returns (path, count)."""
    ordered = sorted(trades, key=lambda t: t.timestamp_ms) if trades else []
    path = symbol_dir(feed_root, market, provider, symbol) / "trades.csv"
    count = _write_rows(
        path,
        TRADE_HEADER,
        ordered,
        lambda t: [t.timestamp_ms, t.price, t.size, t.side, t.trade_id or ""],
    )
    return path, count


def write_quotes(feed_root, market: str, provider: str, symbol: str, quotes) -> tuple[Path, int]:
    """Writes sorted quotes to ``quotes.csv``. Returns (path, count)."""
    ordered = sorted(quotes, key=lambda q: q.timestamp_ms) if quotes else []
    path = symbol_dir(feed_root, market, provider, symbol) / "quotes.csv"
    count = _write_rows(
        path,
        QUOTE_HEADER,
        ordered,
        lambda q: [q.timestamp_ms, q.bid_price, q.bid_size, q.ask_price, q.ask_size],
    )
    return path, count


def write_book_updates(feed_root, market: str, provider: str, symbol: str, book_updates) -> tuple[Path, int]:
    """Writes sorted order book level changes to ``book_updates.csv``. Returns (path, count).

    Note that no exchange REST endpoint in this toolchain serves *historical* L2
    deltas, so this writer is fed by a websocket capture (or by hand) rather than
    by a historical backfill. See ``providers/crypto/binance.py``.
    """
    ordered = sorted(book_updates, key=lambda u: u.timestamp_ms) if book_updates else []
    path = symbol_dir(feed_root, market, provider, symbol) / "book_updates.csv"
    count = _write_rows(
        path,
        BOOK_UPDATE_HEADER,
        ordered,
        lambda u: [u.timestamp_ms, u.side, u.price, u.quantity, u.action],
    )
    return path, count


def describe(out_root) -> dict:
    """Summarizes the staged files under a feed root (relative paths + sizes)."""
    root = Path(out_root)
    files = []
    if root.exists():
        for path in sorted(root.rglob("*.csv")):
            rel = path.relative_to(root).as_posix()
            files.append({"file": rel, "rows": _count_rows(path)})
    return {"feed_root": str(root), "files": files}


def _count_rows(path: Path) -> int:
    try:
        with path.open("r", encoding="utf-8") as handle:
            return sum(1 for _ in handle) - 1
    except OSError:
        return 0


def csv_bounds(path: Path):
    """First/last ``timestamp_ms`` in an existing staged CSV (header skipped)."""
    first = last = None
    try:
        with path.open("r", encoding="utf-8") as handle:
            next(handle, None)
            for line in handle:
                parts = line.split(",", 1)
                if not parts or not parts[0].strip().isdigit():
                    continue
                ts = int(parts[0])
                if first is None:
                    first = ts
                last = ts
    except OSError:
        return None, None
    return first, last


def write_pull_manifest(
    feed_root,
    market: str,
    provider: str,
    symbol: str,
    *,
    source: str,
    start_ms,
    end_ms,
    files: dict,
    notes=None,
) -> Path:
    """Writes ``pull.json`` next to the staged CSVs so completeness is checkable.

    ``files`` maps kind -> Path (e.g. ``trades``). Each entry gets its row count
    and first/last timestamp recorded; the manifest is overwritten atomically.
    """
    manifest = {
        "market": market,
        "provider": provider,
        "symbol": symbol,
        "source": source,
        "requested_window_ms": {"start_ms": start_ms, "end_ms": end_ms},
        "pulled_at_utc": datetime.now(timezone.utc).isoformat(),
        "files": {},
        "notes": notes or [],
    }
    for kind, path in files.items():
        entry = {"file": str(path), "rows": _count_rows(path)}
        first, last = csv_bounds(path)
        entry["first_ms"] = first
        entry["last_ms"] = last
        manifest["files"][kind] = entry

    target = symbol_dir(feed_root, market, provider, symbol) / "pull.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=".pull-", suffix=".json", dir=str(target.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(manifest, handle, indent=2)
            handle.write("\n")
        os.replace(tmp, target)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    return target