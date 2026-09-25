"""Time / interval helpers shared by DataFeeds providers."""

from __future__ import annotations

import re
from datetime import datetime, timezone

_INTERVAL_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*([smhdw]?)\s*$")
_UNIT_SECONDS = {"s": 1, "m": 60, "h": 3600, "d": 86400, "w": 604800, "": 1}


def parse_interval(value) -> int:
    """Parses an interval into seconds.

    Accepts ``300``, ``"5m"``, ``"1h"``, ``"1d"``, ``"30s"`` ...
    """
    if value is None:
        raise ValueError("interval is required")
    text = str(value)
    match = _INTERVAL_RE.match(text)
    if not match:
        raise ValueError(f"invalid interval {value!r} (use seconds, or e.g. 1m/1h/1d)")
    amount = float(match.group(1))
    unit = _UNIT_SECONDS[match.group(2)]
    seconds = int(amount * unit)
    if seconds <= 0:
        raise ValueError(f"invalid interval {value!r}")
    return seconds


def interval_label(seconds: int) -> str:
    """Human-ish label used for bar file names, e.g. ``bars_60.csv``."""
    return str(int(seconds))


def to_unix_ms(dt) -> int:
    """Converts a naive-UTC or aware datetime to Unix milliseconds."""
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return int(dt.timestamp() * 1000)


def from_unix_ms(ms) -> datetime:
    """Converts Unix milliseconds to a naive-UTC datetime."""
    return datetime.fromtimestamp(ms / 1000.0, tz=timezone.utc).replace(tzinfo=None)


def parse_datetime(value) -> datetime:
    """Parses a loosely formatted UTC datetime/date. Naive values are assumed UTC."""
    from datetime import date as _date

    if isinstance(value, datetime):
        return value.replace(tzinfo=None) if value.tzinfo is not None else value
    if isinstance(value, _date):
        return datetime(value.year, value.month, value.day)
    text = str(value).strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    dt = datetime.fromisoformat(text)
    return dt.replace(tzinfo=None) if dt.tzinfo is not None else dt


def utcnow_ms() -> int:
    return int(datetime.now(timezone.utc).timestamp() * 1000)