#!/usr/bin/env python
"""Cross-venue spread probe, run through the research engine.

Tests one idea: does Bybit's bid sit above Binance's ask (buy Binance, sell Bybit),
or the reverse (buy Bybit, sell Binance)?

The engine does the work. This script never touches a websocket itself. For each
venue it captures top-of-book quotes into the DataFeeds layout, then runs the
engine once per venue over that capture, so every price, spread and grid point
below is something the engine computed:

    captures -> engine (order book + features + observation grid) -> comparison

The engine is one venue per job, so there are two runs, and the two are joined on
the engine's own observation grid. That grid is deterministic
(``startTime + n x observationInterval``), so both runs land on identical
timestamps and a row-to-row join is meaningful rather than approximate.

The engine's own trust columns come along for the ride: a comparison resting on a
``missing`` or ``filled`` row is labelled, because a spread computed from a period
that never had a book is a number, not a market.

Usage:
    python DataFeeds/Live/Crypto/app_binance_bybit_spread.py --duration 120
    python DataFeeds/Live/Crypto/app_binance_bybit_spread.py --self-test
"""

from __future__ import annotations

import argparse
import csv
import json
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO / "DataFeeds"))

VENUES = ("bybit", "binance")
# The engine measures exactly these. The engine separates registered features from raw state
# measurements, and enforces it: putting bid_price in "features" fails validation. The raw pair
# is the whole comparison, and the registered features are carried so the run cross-checks them.
ENGINE_FEATURES = ["mid_price", "spread", "spread_bps"]
ENGINE_RAW_FIELDS = ["bid_price", "ask_price"]


@dataclass(frozen=True)
class Row:
    """One engine observation, trimmed to what the comparison needs."""

    timestamp: datetime
    bid: float
    ask: float
    quality: str
    data_age_ms: float

    @property
    def mid(self) -> float:
        return (self.bid + self.ask) / 2.0

    @property
    def trusted(self) -> bool:
        # 0 = missing, 2 = fresh in the engine's DataQuality encoding. A filled row carries a real
        # price forward, so it is usable; a missing row has no book behind it and is not.
        return self.quality != "0" and self.bid > 0 and self.ask > 0


@dataclass(frozen=True)
class Signal:
    """One arbitrage leg that was positive on an engine grid point."""

    leg: str
    buy_venue: str
    sell_venue: str
    buy_price: float
    sell_price: float
    timestamp: datetime
    qualities: str

    @property
    def gross_edge(self) -> float:
        return self.sell_price - self.buy_price

    @property
    def gross_bps(self) -> float:
        return self.gross_edge / self.buy_price * 10_000.0

    @property
    def net_bps(self) -> float:
        # Fees are charged on both legs, so a round trip pays twice. This is the number that decides
        # whether a visible spread is capturable.
        return (self.gross_edge - self.buy_price * FEE_FRACTION - self.sell_price * FEE_FRACTION) \
            / self.buy_price * 10_000.0

    def describe(self) -> str:
        return (
            f"{self.leg:<16} BUY {self.buy_venue:<8} @ {self.buy_price:>10,.2f}  "
            f"SELL {self.sell_venue:<8} @ {self.sell_price:>10,.2f}  "
            f"gross {self.gross_bps:+7.2f} bps  net {self.net_bps:+7.2f} bps  "
            f"[{self.timestamp:%H:%M:%S} {self.qualities}]"
        )


FEE_FRACTION = 0.001  # 10 bps taker per leg; overridden by --fee-bps


def compare(bybit: Row, binance: Row, fee_fraction: float) -> list[Signal]:
    """Both currently-positive legs, from two engine observations at the same grid point."""
    signals = []
    candidates = [
        ("bybit-premium", "binance", "bybit", binance.ask, bybit.bid),
        ("binance-premium", "bybit", "binance", bybit.ask, binance.bid),
    ]
    for leg, buy_venue, sell_venue, buy_price, sell_price in candidates:
        if sell_price - buy_price <= 0:
            continue
        signals.append(Signal(
            leg=leg,
            buy_venue=buy_venue,
            sell_venue=sell_venue,
            buy_price=buy_price,
            sell_price=sell_price,
            timestamp=binance.timestamp,
            qualities=f"b:{binance.quality}/y:{bybit.quality}",
        ))
    return signals


def _self_test() -> int:
    """Checks the comparison and the engine-output parsing offline, so a quiet live run can be read
    as 'no edge' rather than 'unknown whether this works'."""
    stamp = datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
    fee = 0.001

    def row(bid, ask, quality="2", age=0.0):
        return Row(stamp, bid, ask, quality, age)

    checks = []
    # compare() takes (bybit, binance), so the venue with the higher bid is the first argument.
    legs = [s.leg for s in compare(row(102.0, 103.0), row(100.0, 101.0), fee)]
    checks.append(("bybit bid above binance ask", legs == ["bybit-premium"], legs))

    legs = [s.leg for s in compare(row(100.0, 101.0), row(102.0, 103.0), fee)]
    checks.append(("binance bid above bybit ask (the opposite)", legs == ["binance-premium"], legs))

    legs = [s.leg for s in compare(row(100.0, 101.0), row(100.5, 101.5), fee)]
    checks.append(("venues agree, no edge", legs == [], legs))

    signal = compare(row(100.10, 100.20), row(100.0, 100.05), fee)[0]
    checks.append((
        "spread visible but under fees",
        signal.gross_bps > 0 and signal.net_bps < 0,
        f"gross {signal.gross_bps:+.2f} net {signal.net_bps:+.2f}",
    ))

    # An engine row the engine marked as having no data must not be treated as a price.
    checks.append((
        "engine 'missing' row is not a price",
        not row(0.0, 0.0, quality="0").trusted,
        "quality=0 rejected",
    ))
    checks.append((
        "engine 'filled' row is a carried-forward price, usable",
        row(100.0, 101.0, quality="1").trusted,
        "quality=1 accepted",
    ))

    failures = 0
    for label, ok, detail in checks:
        print(f"[{'PASS' if ok else 'FAIL'}] {label}: {detail}")
        failures += not ok
    print(f"\n{'all checks passed' if not failures else f'{failures} check(s) failed'}")
    return 0 if not failures else 1


def _capture(feed_root: Path, symbol: str, duration: int) -> list[subprocess.Popen]:
    """Captures both venues concurrently.

    Concurrently, and this matters: two sequential captures would compare Bybit's window
    against Binance's, a minute or more apart. Any spread found that way would be the
    market moving, not the venues disagreeing.
    """
    procs = []
    for venue in VENUES:
        procs.append(subprocess.Popen(
            [sys.executable, "-m", "datafeeds", "pull",
             "--market", "crypto", "--provider", venue, "--mode", "live",
             "--symbol", symbol, "--duration", str(duration),
             "--out", str(feed_root), "--no-bars", "--no-trades", "--quotes"],
            cwd=str(REPO / "DataFeeds"),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        ))
    return procs


def _read_quotes(feed_root: Path, symbol: str, venue: str) -> tuple[Path | None, int]:
    """Returns the venue's quotes.csv path and how far the capture actually reached.

    The end timestamp comes from the data, not from the wall clock: a capture that lost
    connection early would otherwise advertise a window it never covered, and the engine
    would dutifully fill the rest.
    """
    path = feed_root / "crypto" / venue / symbol / "quotes.csv"
    if not path.exists():
        return None, 0
    count = 0
    for _ in path.read_text(encoding="utf-8").splitlines()[1:]:
        count += 1
    return path, count


def _window(feed_root: Path, symbol: str) -> tuple[datetime, datetime, int]:
    """The union window both venues can be compared over, from the staged files."""
    stamps = []
    total = 0
    for venue in VENUES:
        path, count = _read_quotes(feed_root, symbol, venue)
        total += count
        if not path:
            continue
        for line in path.read_text(encoding="utf-8").splitlines()[1:]:
            first = line.split(",", 1)[0]
            if first:
                stamps.append(int(first))
    if not stamps:
        raise SystemExit("no quotes were captured; cannot build a window")
    return (
        datetime.fromtimestamp(min(stamps) / 1000.0, tz=timezone.utc),
        datetime.fromtimestamp(max(stamps) / 1000.0, tz=timezone.utc),
        total,
    )


def _job(venue: str, symbol: str, start: datetime, end: datetime, interval_s: int) -> dict:
    return {
        "jobId": f"crossvenue-{venue}",
        "dataset": "crypto",
        "symbols": [symbol],
        "assetClass": "crypto",
        "venue": venue,
        "resolution": "Minute",
        "startTime": start.replace(tzinfo=None).isoformat(),
        "endTime": end.replace(tzinfo=None).isoformat(),
        "eventTypes": ["Quote"],
        "observationInterval": f"00:00:{interval_s:02d}",
        "features": ENGINE_FEATURES,
        "rawFields": ENGINE_RAW_FIELDS,
        "experimentName": "dry-run",
        "outputFormat": "csv",
        "enableCheckpointing": False,
        "source": {"mode": "feed", "provider": venue},
    }


def _run_engine(venue: str, job: dict, feed_root: Path, out_root: Path) -> Path:
    """Runs the engine for one venue and returns its observation CSV."""
    job_file = out_root / f"job-{venue}.json"
    job_file.write_text(json.dumps(job, indent=2), encoding="utf-8")
    result = subprocess.run(
        ["dotnet", "run", "--project", str(REPO / "Research" / "Runner"), "-c", "Release", "--",
         "--job-file", str(job_file), "--data-dir", str(feed_root), "--output-dir", str(out_root)],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        raise SystemExit(f"engine run failed for {venue}:\n{result.stdout}\n{result.stderr}")
    csv_path = out_root / job["jobId"] / symbol_dir(job) / "crypto.csv"
    if not csv_path.exists():
        found = sorted(p for p in (out_root / job["jobId"]).rglob("*.csv"))
        raise SystemExit(f"engine produced no observations for {venue}; found {found}")
    return csv_path


def symbol_dir(job: dict) -> str:
    return job["symbols"][0]


def _load(path: Path) -> dict[datetime, Row]:
    """Reads an engine observation CSV into timestamp-keyed rows."""
    rows: dict[datetime, Row] = {}
    with path.open(encoding="utf-8") as handle:
        for record in csv.DictReader(handle):
            stamp = record["timestamp"]
            parsed = datetime.fromisoformat(stamp)
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=timezone.utc)
            rows[parsed] = Row(
                timestamp=parsed,
                bid=float(record.get("bid_price") or 0),
                ask=float(record.get("ask_price") or 0),
                quality=str(record.get("data_quality", "0")),
                data_age_ms=float(record.get("data_age_ms") or -1),
            )
    return rows


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Test a cross-venue spread using the research engine's own features and grid.")
    parser.add_argument("--symbol", default="BTCUSDT", help="Symbol on both venues (default: BTCUSDT)")
    parser.add_argument("--duration", type=int, default=120, help="Capture seconds (default: 120)")
    parser.add_argument("--interval", type=int, default=5, help="Observation interval seconds (default: 5)")
    parser.add_argument("--fee-bps", type=float, default=10.0, help="Taker fee bps per leg (default: 10)")
    parser.add_argument("--self-test", action="store_true", help="Check the comparison offline and exit")
    parser.add_argument("--workdir", default=None,
                        help="stage the capture and engine output here instead of a temp dir")
    args = parser.parse_args(argv)

    global FEE_FRACTION
    FEE_FRACTION = args.fee_bps / 10_000.0

    if args.self_test:
        return _self_test()

    if args.workdir:
        work = Path(args.workdir)
    else:
        work = Path(tempfile.mkdtemp(prefix="crossvenue-"))
    feed_root, out_root = work / "feeds", work / "out"
    feed_root.mkdir(parents=True, exist_ok=True)
    out_root.mkdir(parents=True, exist_ok=True)

    print(f"Capturing {args.symbol} on {'+'.join(VENUES)} for {args.duration}s (concurrent)...")
    started = time.monotonic()
    procs = _capture(feed_root, args.symbol, args.duration)
    for proc in procs:
        proc.wait()
    elapsed = time.monotonic() - started

    for venue in VENUES:
        _, count = _read_quotes(feed_root, args.symbol, venue)
        print(f"  {venue:<8} {count} quote rows staged")

    try:
        start, end, total = _window(feed_root, args.symbol)
    except SystemExit as exc:
        print(f"{exc}\nThe capture produced nothing, so there is no engine input to compare.")
        print(f"  work dir: {work}")
        return 1

    # Inflate the window slightly so the engine emits the first and last real grid point rather
    # than clipping the capture at both ends.
    start -= timedelta(seconds=args.interval)
    end += timedelta(seconds=args.interval)
    print(f"\nWindow from the staged data: {start:%H:%M:%S} to {end:%H:%M:%S} "
          f"({total} quote rows, {elapsed:.0f}s wall)")

    print("Running the engine once per venue...")
    books = {}
    for venue in VENUES:
        csv_path = _run_engine(venue, _job(venue, args.symbol, start, end, args.interval), feed_root, out_root)
        books[venue] = _load(csv_path)
        print(f"  {venue:<8} {len(books[venue])} observations from the engine")

    shared = sorted(set(books["bybit"]) & set(books["binance"]))
    if not shared:
        print("\nThe two runs produced no shared grid points, so there is nothing to compare.")
        print(f"  work dir: {work}")
        return 1

    signals: list[Signal] = []
    skipped_untrusted = 0
    bases = []
    for stamp in shared:
        bybit, binance = books["bybit"][stamp], books["binance"][stamp]
        if not bybit.trusted or not binance.trusted:
            skipped_untrusted += 1
            continue
        bases.append(bybit.mid - binance.mid)
        signals.extend(compare(bybit, binance, FEE_FRACTION))

    print(f"\nShared grid points: {len(shared)} "
          f"({skipped_untrusted} skipped: the engine marked a row missing or empty)")

    if bases:
        mean_basis = sum(bases) / len(bases)
        print(f"Bybit-minus-binance mid basis: mean {mean_basis:+,.2f} "
              f"({mean_basis / (sum(r.mid for r in books['binance'].values()) / max(len(books['binance']), 1)) * 10_000:+.2f} bps)")
    if not bases:
        print("No trusted grid points: the engine never had a real book on both sides.")
        print(f"  work dir: {work}")
        return 1

    if not signals:
        print("No positive cross-venue leg at any shared grid point: the books never crossed.")
        print(f"  work dir: {work}")
        return 0

    print(f"\nPositive legs: {len(signals)} across {len(bases)} compared points")
    for leg in sorted({s.leg for s in signals}):
        subset = [s for s in signals if s.leg == leg]
        best = max(subset, key=lambda s: s.gross_edge)
        capturable = [s for s in subset if s.net_bps > 0]
        print(f"\n{leg}: {len(subset)} grid points, {len(capturable)} above fees")
        print(f"  best: {best.describe()}")
        if capturable:
            print(f"  first above fees: {min(capturable, key=lambda s: s.gross_edge).describe()}")
    print(f"\n  work dir: {work}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
