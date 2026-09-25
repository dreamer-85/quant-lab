"""Provider-agnostic row types shared by every DataFeeds provider.

Timestamps are always integer Unix milliseconds (UTC); prices are floats.
The ResearchEngine feed reader (``LocalFeedDataSource``) consumes exactly
the CSV files produced from these rows.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(order=True)
class Bar:
    """OHLCV row. timestamp_ms is the *open* time of the interval."""

    timestamp_ms: int
    open: float
    high: float
    low: float
    close: float
    volume: float


@dataclass(order=True)
class Trade:
    """Individual execution row."""

    timestamp_ms: int
    price: float
    size: float
    side: str = "unknown"  # "buy" | "sell" | "unknown"
    trade_id: Optional[str] = None


@dataclass(order=True)
class Quote:
    """Top-of-book (or mid) quote row. bid_price == ask_price for mid-only feeds."""

    timestamp_ms: int
    bid_price: float
    bid_size: float
    ask_price: float
    ask_size: float


FEED_TYPES = ("bars", "trades", "quotes")
"""Kinds of data staged by a feed. Bar files are named ``bars_<seconds>.csv``."""

# CSV columns in the order writers emit and the engine reads.
BAR_HEADER = "timestamp_ms,open,high,low,close,volume"
TRADE_HEADER = "timestamp_ms,price,size,side,trade_id"
QUOTE_HEADER = "timestamp_ms,bid_price,bid_size,ask_price,ask_size"