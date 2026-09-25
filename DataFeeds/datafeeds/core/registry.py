"""Provider registry: the single extensibility point for DataFeeds.

To add a new exchange you implement a ``HistoricalFeed`` and/or ``LiveFeed``
subclass and register it here (or with the ``@register_feed`` decorator):

    @register_feed("crypto", "kraken", "historical")
    class KrakenHistoricalFeed(HistoricalFeed): ...

Once registered the feed is available through the unified CLI
(``python -m datafeeds pull --market crypto --provider kraken --mode historical``)
and through the per-folder entry scripts. That is the whole integration surface.
"""

from __future__ import annotations

# Key: (market, provider, mode)  e.g. ("crypto", "okx", "historical")
FEEDS: dict[tuple[str, str, str], type] = {}


def register_feed(market: str, provider: str, mode: str):
    """Class decorator that registers a feed class in the global registry."""

    def decorator(cls):
        FEEDS[(market.lower(), provider.lower(), mode.lower())] = cls
        return cls

    return decorator


def get_feed(market: str, provider: str, mode: str):
    key = (market.lower(), provider.lower(), mode.lower())
    if key not in FEEDS:
        available = [f"{m}/{p}/{md}" for (m, p, md) in sorted(FEEDS)]
        raise KeyError(
            f"no feed registered for market={market!r} provider={provider!r} mode={mode!r}; "
            f"available: {', '.join(available) or 'none'}"
        )
    return FEEDS[key]


def list_feeds() -> list[dict]:
    """Describes every registered feed (used by ``python -m datafeeds list``)."""
    rows = []
    for (market, provider, mode), cls in sorted(FEEDS.items()):
        import importlib

        doc = cls.__doc__
        if not doc:
            try:
                doc = importlib.import_module(cls.__module__).__doc__
            except Exception:
                doc = ""
        description = (doc or "").strip().splitlines()[0] if doc else ""
        rows.append(
            {
                "market": market,
                "provider": provider,
                "mode": mode,
                "class": f"{cls.__module__}.{cls.__name__}",
                "description": description,
            }
        )
    return rows