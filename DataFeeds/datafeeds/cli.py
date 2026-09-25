"""Unified DataFeeds CLI.

Usage::

    python -m datafeeds list
    python -m datafeeds pull   --market crypto --provider okx --mode historical \\
                               --symbol BTC-USDT --start 2026-09-01 --end 2026-09-03 \\
                               --interval 1m --out feeds/crypto/okx

The equivalent per-folder entry scripts (``Historical/<market>/*.py``,
``Live/<market>/*.py``) are thin wrappers around the same feed classes.
"""

from __future__ import annotations

import argparse
import sys

from .core import io as feed_io
from .core.registry import get_feed, list_feeds
from . import providers  # noqa: F401  (registers all feeds)


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="datafeeds",
        description="Pull crypto + forex market data (historical and live) from multiple exchanges.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("list", help="List every registered historical/live feed provider")

    pull = sub.add_parser("pull", help="Pull a data range / run a live session")
    pull.add_argument("--market", required=True, choices=["crypto", "forex"], help="Market bucket")
    pull.add_argument("--provider", required=True, help="Exchange provider (okx, bybit, binance, deriv, ...)")
    pull.add_argument("--mode", required=True, choices=["historical", "live"], help="Feed mode")
    pull.add_argument("--symbol", required=True, help="Provider-native symbol, e.g. BTCUSDT / BTC-USDT / EURUSD")
    pull.add_argument("--start", default=None, help="Start time (historical only), e.g. 2026-09-01T00:00:00")
    pull.add_argument("--end", default=None, help="End time (historical only)")
    pull.add_argument("--interval", default=None, help="Bar interval, e.g. 1m / 5m / 1h / 1d (crypto) or seconds (deriv)")
    pull.add_argument("--duration", type=float, default=30.0, help="Live session length in seconds (live only)")
    pull.add_argument("--out", "-o", default="feeds", help="Feed output root (default: feeds)")
    pull.add_argument("--bars", action="store_true", default=True, help="Pull/stage OHLCV bars")
    pull.add_argument("--no-bars", dest="bars", action="store_false", help="Disable bars")
    pull.add_argument("--trades", action="store_true", default=True, help="Pull/stage trades")
    pull.add_argument("--no-trades", dest="trades", action="store_false", help="Disable trades")
    pull.add_argument("--quotes", action="store_true", default=True, help="Pull/stage quotes")
    pull.add_argument("--no-quotes", dest="quotes", action="store_false", help="Disable quotes")
    pull.add_argument("--app-id", type=int, default=None, help="Deriv app_id (default: 1089)")
    pull.add_argument("--api-token", default=None, help="Deriv API token (authorizes the session when supplied)")
    pull.add_argument("--category", default="spot", help="Bybit category: spot | linear")
    pull.add_argument("--market-type", dest="market_type", default="spot", help="Binance market: spot | usdm")
    return parser


def _run_pull(args: argparse.Namespace) -> FeedResult:
    feed_cls = get_feed(args.market, args.provider, args.mode)
    feed = feed_cls()
    if args.mode == "historical":
        if not args.start or not args.end:
            raise ValueError("--start and --end are required for historical pulls")
        return feed.pull(
            args.symbol,
            start=args.start,
            end=args.end,
            interval=args.interval,
            out_root=args.out,
            bars=args.bars,
            trades=args.trades,
            quotes=args.quotes,
            app_id=args.app_id,
            api_token=args.api_token,
            category=args.category,
            market=args.market_type,
        )
    return feed.stream(
        args.symbol,
        duration_seconds=args.duration,
        interval=args.interval,
        out_root=args.out,
        bars=args.bars,
        trades=args.trades,
        quotes=args.quotes,
        app_id=args.app_id,
        api_token=args.api_token,
        category=args.category,
        market=args.market_type,
    )


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)
    try:
        if args.command == "list":
            rows = list_feeds()
            if not rows:
                print("no feeds registered")
                return 0
            width = max(len(r["provider"]) for r in rows)
            for row in rows:
                print(f"{row['market']:<7} {row['provider']:<{width}} {row['mode']:<12} {row['description']}")
            return 0

        result = _run_pull(args)
        print()
        print(result.describe())
        return 0
    except Exception as exc:  # noqa: BLE001
        print(f"datafeeds: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())