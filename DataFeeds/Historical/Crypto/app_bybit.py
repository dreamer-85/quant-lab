#!/usr/bin/env python
"""Historical Bybit crypto feed (REST): pull OHLCV bars (and recent trades) into DataFeeds layout.

Example:
    python Historical\\Crypto\\app_bybit.py --symbol BTCUSDT --start 2026-09-01 --end 2026-09-03 --interval 1m --out feeds
Optional: --category linear for USDT perpetuals.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from datafeeds.core.script_runner import run_script
from datafeeds.providers.crypto.bybit import BybitHistoricalFeed

if __name__ == "__main__":
    raise SystemExit(run_script(BybitHistoricalFeed, "historical"))