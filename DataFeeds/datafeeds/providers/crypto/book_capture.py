"""Order book capture from a venue delta stream.

No REST endpoint in this toolchain serves historical L2 deltas, so a job that
wants order book depth has to capture it from a websocket and stage it. This
module is the producer for ``book_updates.csv``.

Two things make a delta capture different from the other live feeds, and both are
load-bearing:

* A delta stream is meaningless without its base. The engine rebuilds the book by
  applying updates to an empty state, so a capture that dropped the opening
  snapshot would replay a book that starts empty and is wrong until depth happens
  to accumulate. The venue's first message is a full snapshot and is written as
  ordinary ``add`` rows, which makes the file self-contained and replayable.
* The venue reports a level's new absolute size, never whether the level was
  inserted or re-sized. A non-zero size is therefore written as ``add`` and a zero
  size as ``remove``; the two are indistinguishable from the wire, and the
  reconstructor does not need them to be.
"""

from __future__ import annotations

import json
import time
from typing import Callable

from ...core.base_feeds import FeedResult
from ...core.datatypes import BookUpdate
from ...core.io import write_book_updates
from ...core.registry import register_feed
from ...core.ws import WsConnection

_SNAPSHOT_MARKERS = ("snapshot",)


class BybitBookCapture:
    """Decodes Bybit v5 ``orderbook.<depth>`` messages into book updates."""

    def __init__(self, symbol: str, depth: int = 50, on_update: Callable[[BookUpdate], None] | None = None):
        self.symbol = symbol
        self.depth = depth
        self._on_update = on_update
        self.updates: list[BookUpdate] = []
        self._have_snapshot = False
        self.snapshot_rows = 0
        self.delta_rows = 0
        self.messages = 0
        self.unknown_messages = 0
        self.last_sequence: int | None = None
        self.sequence_gaps = 0

    @property
    def topic(self) -> str:
        return f"orderbook.{self.depth}.{self.symbol}"

    @property
    def have_snapshot(self) -> bool:
        return self._have_snapshot

    def subscribe_message(self) -> dict:
        return {"op": "subscribe", "args": [self.topic]}

    def handle(self, text: str) -> None:
        """Feeds one raw websocket text frame. Never raises on bad input."""
        try:
            msg = json.loads(text)
        except ValueError:
            self.unknown_messages += 1
            return

        if not isinstance(msg, dict) or "data" not in msg:
            # Control frames (subscribe confirmations, pongs, heartbeats) carry no book data.
            return

        # A connection can carry other topics, and a frame for one of them is not book data. Counting
        # it would inflate the message totals that are supposed to describe this book.
        topic = msg.get("topic")
        if isinstance(topic, str) and topic and not topic.startswith(f"orderbook.{self.depth}."):
            return

        self.messages += 1
        is_snapshot = str(msg.get("type", "")).lower() in _SNAPSHOT_MARKERS
        payload = msg.get("data")
        rows = payload if isinstance(payload, list) else [payload]

        for row in rows:
            if not isinstance(row, dict):
                self.unknown_messages += 1
                continue

            timestamp = _timestamp_ms(row, msg)
            sequence = row.get("u")
            if isinstance(sequence, int):
                if self.last_sequence is not None and sequence > self.last_sequence + 1:
                    # A gap means levels were changed in messages this capture never saw, so the
                    # book it reconstructs is wrong by an unknown amount from here on. Counted
                    # rather than silently accepted, because it invalidates the whole replay.
                    self.sequence_gaps += 1
                self.last_sequence = sequence

            for side, key in (("bid", "b"), ("ask", "a")):
                for level in row.get(key) or []:
                    self._emit(timestamp, side, level, is_snapshot)

        if is_snapshot:
            self._have_snapshot = True

    def _emit(self, timestamp_ms: int, side: str, level, is_snapshot: bool) -> None:
        if not isinstance(level, (list, tuple)) or len(level) < 2:
            self.unknown_messages += 1
            return

        try:
            price = float(level[0])
            quantity = float(level[1])
        except (TypeError, ValueError):
            self.unknown_messages += 1
            return

        if price <= 0:
            return

        # The venue sends the level's new absolute size; zero is the only delete signal it gives.
        action = "remove" if quantity == 0 else "add"
        update = BookUpdate(timestamp_ms=timestamp_ms, side=side, price=price, quantity=quantity, action=action)
        self.updates.append(update)
        if self._on_update is not None:
            self._on_update(update)

        if is_snapshot:
            self.snapshot_rows += 1
        else:
            self.delta_rows += 1

    def summary(self) -> dict:
        return {
            "symbol": self.symbol,
            "depth": self.depth,
            "topic": self.topic,
            "have_snapshot": self._have_snapshot,
            "messages": self.messages,
            "book_messages": self.messages,
            "snapshot_rows": self.snapshot_rows,
            "delta_rows": self.delta_rows,
            "total_rows": len(self.updates),
            "sequence_gaps": self.sequence_gaps,
            "last_sequence": self.last_sequence,
            "unknown_messages": self.unknown_messages,
        }

    def run(self, duration_seconds: float, url: str, poll: Callable[[], float] = time.monotonic) -> dict:
        """Subscribes and pumps frames until the duration elapses.

        The deadline is enforced in the frame handler rather than around ``run`` because the receive
        loop only stops when the handler says so. Returns the summary, so a caller can refuse to stage
        a capture that never saw its opening snapshot.
        """
        stop_at = poll() + float(duration_seconds)

        def handler(text: str) -> bool:
            self.handle(text)
            return poll() < stop_at

        connection = WsConnection(url, handler, ping_payload='{"op":"ping"}', ping_interval_s=20)
        try:
            connection.send_json(self.subscribe_message())
            connection.run()
        finally:
            connection.close()

        summary = self.summary()
        summary["duration_seconds"] = float(duration_seconds)
        return summary


def _timestamp_ms(row: dict, msg: dict) -> int:
    # Bybit v5 puts the exchange time on the message envelope and, for order books, repeats it inside
    # ``data``. The level timestamp is the one that matters for ordering against other feeds, so
    # ``data`` is preferred and the envelope is the fallback.
    for source in (row, msg):
        for key in ("ts", "T", "E"):
            value = source.get(key)
            if isinstance(value, (int, float)) and value > 0:
                # Epoch milliseconds are ~1.7e12 and epoch microseconds ~1.7e15, so anything past
                # 1e14 is microseconds from this era. The cut has to sit above today's millisecond
                # timestamps, not below them, or a valid ms value gets divided into the wrong unit.
                return int(value if value < 1e14 else value / 1000.0)
    return int(time.time() * 1000)


@register_feed("crypto", "bybit", "capture")
class BybitBookCaptureFeed:
    """Captures L2 order book deltas into book_updates.csv.

    Registered as its own mode rather than a flag on the live feed, because it is a
    different job: the live feed stages bars, trades and top-of-book quotes, while this
    stages depth the engine can reconstruct a book from.
    """

    provider = "bybit"
    market = "crypto"
    mode = "capture"

    __doc__ = __doc__

    def capture(
        self,
        symbol: str,
        duration_seconds: float,
        out_root: str = "feeds",
        depth: int = 50,
        category: str = "spot",
        **kwargs,
    ) -> FeedResult:
        from .bybit import WS_LINEAR, WS_SPOT

        if depth < 50:
            # Below 50 Bybit sends point-in-time snapshots, not deltas. Capturing those into
            # book_updates.csv would produce a file full of "add" rows that reconstruct a book that
            # never changes, which reads as a working capture and is not one.
            raise ValueError(
                "--depth must be at least 50: shallower depths are snapshots, not deltas, and cannot "
                "produce a replayable book"
            )

        url = WS_LINEAR if category == "linear" else WS_SPOT
        capture = BybitBookCapture(symbol, depth=depth)
        capture.run(duration_seconds, url)
        return self.stage(capture, out_root)

    def stage(self, capture: "BybitBookCapture", out_root: str = "feeds") -> FeedResult:
        """Stages a finished capture, or refuses to stage one that cannot be replayed.

        Split from ``capture`` so the decision is testable without a websocket.
        """
        summary = capture.summary()
        result = FeedResult(
            market=self.market,
            provider=self.provider,
            symbol=capture.symbol,
            mode=self.mode,
            feed_root=str(out_root),
            counts={"book_updates": len(capture.updates)},
            summary_extra=summary,
        )

        if not capture.have_snapshot:
            # A capture without its opening snapshot replays a book that starts empty and is wrong
            # until depth happens to accumulate. Staging it would turn "we got no usable data" into
            # "we got data that is quietly wrong", so nothing is written.
            result.counts["book_updates"] = 0
            result.summary_extra = {
                "error": "no opening snapshot received, nothing staged: a delta capture without its "
                         "base cannot rebuild a book",
                **summary,
            }
            return result

        if summary["sequence_gaps"]:
            result.summary_extra = {
                "warning": f"{summary['sequence_gaps']} sequence gap(s): levels changed in frames this "
                           "capture never saw, so the reconstructed book is wrong from the first gap on",
                **summary,
            }

        path, count = write_book_updates(out_root, self.market, self.provider, capture.symbol, capture.updates)
        result.output_files["book_updates"] = path
        result.counts["book_updates"] = count
        return result
