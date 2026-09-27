"""Contract tests for the staged CSV layout.

The ResearchEngine reads these files from C# (``LocalFeedDataSource``), so the
column order and the literal vocabulary are a cross-language contract. A change
on either side that keeps Python's own tests green will still break the engine,
so the expected bytes are pinned here rather than derived from the writer.
"""

import tempfile
import unittest
from pathlib import Path

from datafeeds.core.datatypes import (
    BAR_HEADER,
    BOOK_UPDATE_HEADER,
    QUOTE_HEADER,
    TRADE_HEADER,
    BookUpdate,
)
from datafeeds.core.io import write_book_updates


class StagedLayoutContract(unittest.TestCase):
    """Headers must match what the engine's reader parses positionally."""

    def test_headers_match_engine_reader(self):
        self.assertEqual(BAR_HEADER, "timestamp_ms,open,high,low,close,volume")
        self.assertEqual(TRADE_HEADER, "timestamp_ms,price,size,side,trade_id")
        self.assertEqual(QUOTE_HEADER, "timestamp_ms,bid_price,bid_size,ask_price,ask_size")
        self.assertEqual(BOOK_UPDATE_HEADER, "timestamp_ms,side,price,quantity,action")

    def test_book_updates_round_trip(self):
        rows = [
            BookUpdate(1704067200000, "bid", 42100.0, 1.5, "add"),
            BookUpdate(1704067200000, "ask", 42101.0, 2.0, "add"),
            BookUpdate(1704067260000, "bid", 42100.0, 0.0, "remove"),
            BookUpdate(1704067320000, "ask", 42101.0, 3.0, "modify"),
        ]
        with tempfile.TemporaryDirectory() as root:
            path, count = write_book_updates(root, "crypto", "binance", "BTCUSDT", rows)
            self.assertEqual(count, 4)
            self.assertEqual(
                path,
                Path(root) / "crypto" / "binance" / "BTCUSDT" / "book_updates.csv",
            )
            lines = path.read_text().strip().split("\n")

        self.assertEqual(lines[0], "timestamp_ms,side,price,quantity,action")
        self.assertEqual(lines[1], "1704067200000,bid,42100.0,1.5,add")
        # A removal is signalled by quantity 0, not by a negative size.
        self.assertEqual(lines[3], "1704067260000,bid,42100.0,0.0,remove")

    def test_book_updates_sorted_ascending(self):
        rows = [
            BookUpdate(1704067320000, "ask", 42101.0, 3.0, "modify"),
            BookUpdate(1704067200000, "bid", 42100.0, 1.5, "add"),
        ]
        with tempfile.TemporaryDirectory() as root:
            path, _ = write_book_updates(root, "crypto", "binance", "BTCUSDT", rows)
            lines = path.read_text().strip().split("\n")

        stamps = [int(line.split(",")[0]) for line in lines[1:]]
        self.assertEqual(stamps, sorted(stamps), "the engine streams these in file order")

    def test_empty_input_writes_header_only(self):
        with tempfile.TemporaryDirectory() as root:
            path, count = write_book_updates(root, "crypto", "binance", "BTCUSDT", [])
            self.assertEqual(count, 0)
            self.assertEqual(path.read_text().strip(), "timestamp_ms,side,price,quantity,action")


if __name__ == "__main__":
    unittest.main()
