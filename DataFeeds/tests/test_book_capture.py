"""The order book capture decoder.

A delta capture is only replayable if the opening snapshot is kept (the engine
rebuilds the book from an empty state) and if a missing level is recorded as a
removal (otherwise the book only ever grows). Both are checked here, along with
the sequence-gap signal, because a file that violates either still looks
plausible: it loads, it has rows, and it replays into a wrong book.
"""

import json
import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from datafeeds.providers.crypto.book_capture import BybitBookCapture  # noqa: E402
from datafeeds.core.datatypes import BOOK_UPDATE_HEADER  # noqa: E402


def _msg(kind, ts, seq, bids, asks):
    return json.dumps(
        {
            "topic": f"orderbook.50.BTCUSDT",
            "type": kind,
            "ts": ts,
            "data": {"s": "BTCUSDT", "b": bids, "a": asks, "u": seq, "seq": seq},
        }
    )


def test_snapshot_becomes_add_rows():
    capture = BybitBookCapture("BTCUSDT", depth=50)
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100.5", "2"]], [["101.5", "3"]]))

    assert capture.have_snapshot
    assert [(u.side, u.price, u.quantity, u.action) for u in capture.updates] == [
        ("bid", 100.5, 2.0, "add"),
        ("ask", 101.5, 3.0, "add"),
    ]


def test_zero_size_is_a_removal():
    # The venue sends the new absolute size and zero is the only delete signal it gives. Writing a
    # zero-quantity row as an "add" would leave the level in the book forever.
    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100.5", "0"]], []))

    assert [(u.price, u.quantity, u.action) for u in capture.updates] == [(100.5, 0.0, "remove")]


def test_resized_level_is_written_with_its_new_size_not_a_delta():
    # Quantity is the new absolute size resting at the price, so a resize must not be stored as an
    # increment or the reconstructed depth is wrong from the first resize on.
    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100.5", "7"]], []))

    assert capture.updates[0].quantity == 7.0
    assert capture.updates[0].action == "add"


def test_control_frames_are_ignored():
    capture = BybitBookCapture("BTCUSDT")
    for frame in ('{"success":true}', '{"op":"pong"}', '{"topic":"publicTrade.BTCUSDT","data":[]}'):
        capture.handle(frame)

    assert capture.updates == []
    assert capture.messages == 0


def test_malformed_frames_never_raise():
    capture = BybitBookCapture("BTCUSDT")
    for frame in ("not json", '{"data":"nope"}', '{"data":{"b":"bad"}}', '{"data":{"b":[["x","y"]]}}'):
        capture.handle(frame)

    assert capture.updates == [], "a bad frame must be counted, not crash the capture"
    assert capture.unknown_messages > 0


def test_sequence_gaps_are_counted():
    # A gap means levels changed in frames the capture never saw, so the reconstructed book is wrong
    # by an unknown amount from there on. It has to be visible.
    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100", "1"]], [["101", "1"]]))
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100", "2"]], []))
    capture.handle(_msg("delta", 1_700_000_002_000, 9, [["100", "3"]], []))

    assert capture.sequence_gaps == 1
    assert capture.last_sequence == 9


def test_contiguous_sequences_report_no_gap():
    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100", "1"]], [["101", "1"]]))
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100", "2"]], []))
    capture.handle(_msg("delta", 1_700_000_002_000, 3, [["100", "3"]], []))

    assert capture.sequence_gaps == 0


def test_updates_are_staged_with_the_engine_header(tmp_path):
    # The staged file is what the engine reads, so the column order has to be the engine's.
    from datafeeds.core.io import write_book_updates

    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100", "1"]], [["101", "2"]]))
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100", "0"]], []))

    path, count = write_book_updates(tmp_path, "crypto", "bybit", "BTCUSDT", capture.updates)
    lines = path.read_text(encoding="utf-8").strip().splitlines()

    assert count == 3
    assert lines[0] == BOOK_UPDATE_HEADER
    assert lines[1] == "1700000000000,bid,100.0,1.0,add"
    assert lines[3] == "1700000001000,bid,100.0,0.0,remove"


def test_shallow_depth_is_refused(tmp_path):
    # Below 50 Bybit sends point-in-time snapshots. Capturing those produces a file full of "add"
    # rows that reconstruct a book that never changes: a working-looking file that is not one.
    from datafeeds.providers.crypto.book_capture import BybitBookCaptureFeed

    with pytest.raises(ValueError, match="at least 50"):
        BybitBookCaptureFeed().capture("BTCUSDT", duration_seconds=0.01, out_root=str(tmp_path), depth=1)


def test_capture_without_a_snapshot_stages_nothing(tmp_path):
    # A delta capture with no base replays a book that starts empty. Refusing to stage it is the
    # difference between "no data" and "data that is quietly wrong".
    from datafeeds.providers.crypto.book_capture import BybitBookCaptureFeed

    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100", "1"]], []))

    result = BybitBookCaptureFeed().stage(capture, str(tmp_path))

    assert result.counts["book_updates"] == 0
    assert result.output_files == {}
    assert "error" in result.summary_extra
    assert not list(tmp_path.rglob("book_updates.csv"))


def test_capture_with_a_snapshot_is_staged_and_summarised(tmp_path):
    from datafeeds.providers.crypto.book_capture import BybitBookCaptureFeed

    capture = BybitBookCapture("BTCUSDT", depth=50)
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100", "1"]], [["101", "1"]]))
    capture.handle(_msg("delta", 1_700_000_001_000, 2, [["100", "0"]], []))

    result = BybitBookCaptureFeed().stage(capture, str(tmp_path))

    assert result.counts["book_updates"] == 3
    assert result.output_files["book_updates"].exists()
    assert result.summary_extra["have_snapshot"] is True
    assert result.summary_extra["snapshot_rows"] == 2
    assert "error" not in result.summary_extra


def test_sequence_gaps_surface_in_the_staged_result(tmp_path):
    # The file is still written, but a gap invalidates the replay, so the result has to say so
    # rather than hand back a row count that reads like a clean capture.
    from datafeeds.providers.crypto.book_capture import BybitBookCaptureFeed

    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100", "1"]], [["101", "1"]]))
    capture.handle(_msg("delta", 1_700_000_002_000, 9, [["100", "3"]], []))

    result = BybitBookCaptureFeed().stage(capture, str(tmp_path))

    assert result.counts["book_updates"] == 3
    assert "warning" in result.summary_extra
    assert "1 sequence gap" in result.summary_extra["warning"]


def test_millisecond_timestamps_are_not_rescaled():
    # Epoch ms is ~1.7e12. A cut-off below that divides a valid timestamp into the wrong unit and
    # lands the capture in the past relative to every other feed.
    capture = BybitBookCapture("BTCUSDT")
    capture.handle(_msg("snapshot", 1_700_000_000_000, 1, [["100", "1"]], []))

    assert capture.updates[0].timestamp_ms == 1_700_000_000_000


def test_microsecond_timestamps_are_converted():
    capture = BybitBookCapture("BTCUSDT")
    capture.handle(
        '{"topic":"orderbook.50.BTCUSDT","type":"snapshot","ts":1700000000000000,'
        '"data":{"b":[["100","1"]],"u":1}}'
    )

    assert capture.updates[0].timestamp_ms == 1_700_000_000_000


def test_other_topics_are_not_counted_as_book_data():
    capture = BybitBookCapture("BTCUSDT")
    capture.handle('{"topic":"orderbook.200.BTCUSDT","type":"snapshot","data":{"b":[["1","1"]],"u":1}}')

    assert capture.updates == []
    assert capture.messages == 0
