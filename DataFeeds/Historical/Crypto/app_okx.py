#!/usr/bin/env python
"""Historical OKX crypto feed (REST): pull OHLCV bars (and recent trades) into DataFeeds layout.

Example:
    python Historical\\Crypto\\app_okx.py --symbol BTC-USDT --start 2026-09-01 --end 2026-09-03 --interval 1m --out feeds
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from datafeeds.core.script_runner import run_script
from datafeeds.providers.crypto.okx import OkxHistoricalFeed

if __name__ == "__main__":
    raise SystemExit(run_script(OkxHistoricalFeed, "historical"))