"""Abstract feed contracts + result projection.

Every concrete provider ships a ``HistoricalFeed`` and/or ``LiveFeed``
subclass. Subclasses must be registered via ``register_feed`` so the CLI and
entry scripts can find them; see ``datafeeds/core/registry.py``.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field

from . import io
from .times import interval_label, parse_interval, parse_datetime


@dataclass
class FeedResult:
    """Summary of a completed pull: where rows landed and how many.

    ``output_files`` is a dict of feed kind -> file path, e.g.
    ``{"bars": Path(...), "trades": Path(...)}``.
    """

    market: str
    provider: str
    symbol: str
    mode: str
    output_files: dict = field(default_factory=dict)
    counts: dict = field(default_factory=dict)  # feed kind -> row count
    feed_root: str = ""
    #: Diagnostics that do not belong in ``counts`` but must not be dropped either — sequence gaps,
    #: whether a capture got its opening snapshot, rows by kind. Surfaced by the CLI so a staged file
    #: can be judged rather than merely counted.
    summary_extra: dict = field(default_factory=dict)

    def describe(self) -> str:
        lines = [f"feed   : {self.market}/{self.provider} [{self.mode}] {self.symbol}"]
        for kind in sorted(self.output_files):
            lines.append(f"{kind:<7}: {self.output_files[kind]} ({self.counts.get(kind, 0)} rows)")
        if self.summary_extra:
            for key in sorted(self.summary_extra):
                lines.append(f"{key:<7}: {self.summary_extra[key]}")
        return "\n".join(lines)


class HistoricalFeed(ABC):
    """Pulls a bounded [start, end] window from an exchange REST API."""

    provider: str = ""
    market: str = ""
    human_interval = True  # True: intervals are "1m"/"1h"; False: bare seconds

    @abstractmethod
    def pull(
        self,
        symbol: str,
        start,
        end,
        interval=None,
        out_root=None,
        app_id=None,
        **kwargs,
    ) -> FeedResult:
        """Stages bars/trades/quotes under ``out_root`` and returns a result."""
        raise NotImplementedError

    def _intervals(self, interval):
        seconds = parse_interval(interval) if self.human_interval else int(interval)
        return seconds, self.native_interval(seconds)

    def native_interval(self, seconds: int) -> str:
        """Maps interval seconds to the provider's native interval token."""
        return str(seconds)

    @staticmethod
    def _window(start, end):
        return parse_datetime(start), parse_datetime(end)


class LiveFeed(ABC):
    """Subscribes a WebSocket feed and streams rows to a data folder."""

    provider: str = ""
    market: str = ""

    @abstractmethod
    def stream(self, symbol: str, duration_seconds: float, interval=None, out_root=None, **kwargs) -> FeedResult:
        raise NotImplementedError