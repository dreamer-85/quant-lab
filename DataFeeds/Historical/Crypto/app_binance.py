#!/usr/bin/env python
"""Historical Binance crypto feed (REST): pull OHLCV bars (and aggTrades) into DataFeeds layout.

Example:
    python Historical\\Crypto\\app_binance.py --symbol BTCUSDT --start 2026-09-01 --end 2026-09-03 --interval 1m --out feeds
Optional: --market usdm for USDT-M futures (fapi.binance.com).
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from datafeeds.core.script_runner import run_script
from datafeeds.providers.crypto.binance import BinanceHistoricalFeed

if __name__ == "__main__":
    raise SystemExit(run_script(BinanceHistoricalFeed, "historical"))