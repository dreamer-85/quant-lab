"""Shared bootstrap for the per-folder entry scripts.

Each entry script (e.g. ``Historical/Crypto/app_okx.py``) defines *its own*
argparse surface and invokes ``run_script`` here, which delegates to the
provider feed class. Keeping the argument handling here means every script
behaves identically while remaining free to add provider-specific flags.
"""

from __future__ import annotations

import argparse
import sys


def add_common_args(parser: argparse.ArgumentParser, mode: str) -> None:
    parser.add_argument("--symbol", "-s", required=True, help="Provider-native symbol, e.g. BTCUSDT / BTC-USDT / EURUSD")
    if mode == "historical":
        parser.add_argument("--start", required=True, help="Start time (UTC), e.g. 2026-09-01T00:00:00")
        parser.add_argument("--end", required=True, help="End time (UTC), e.g. 2026-09-03T00:00:00")
    parser.add_argument("--interval", default=None, help="Bar interval, e.g. 1m/1h/1d (crypto) or seconds (deriv)")
    parser.add_argument("--duration", type=float, default=30.0, help="Live session length in seconds")
    parser.add_argument("--out", "-o", default="feeds", help="Feed output root (default: feeds)")
    parser.add_argument("--no-bars", dest="bars", action="store_false", default=True, help="Skip OHLCV bars")
    parser.add_argument("--no-trades", dest="trades", action="store_false", default=True, help="Skip trades")
    parser.add_argument("--no-quotes", dest="quotes", action="store_false", default=True, help="Skip quotes")
    parser.add_argument("--app-id", type=int, default=None, help="Deriv app_id (default: 1089)")
    parser.add_argument("--api-token", default=None, help="Deriv API token (authorizes the session when supplied)")
    parser.add_argument("--category", default="spot", help="Bybit category: spot | linear")
    parser.add_argument("--market-type", dest="market_type", default="spot", help="Binance market: spot | usdm")


def run_script(feed_cls, mode: str, argv: list[str] | None = None) -> int:
    """Parses args and executes the feed. ``feed_cls`` is the registered class."""
    parser = argparse.ArgumentParser(prog=sys.argv[0], description=(feed_cls.__doc__ or "").strip())
    add_common_args(parser, mode)
    args = parser.parse_args(argv)

    try:
        feed = feed_cls()
        if mode == "historical":
            result = feed.pull(
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
        else:
            result = feed.stream(
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
        print()
        print(result.describe())
        return 0
    except Exception as exc:  # noqa: BLE001
        print(f"{parser.prog}: {exc}", file=sys.stderr)
        return 1