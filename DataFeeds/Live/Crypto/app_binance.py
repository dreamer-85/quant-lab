#!/usr/bin/env python
"""Live Binance crypto feed (WebSocket): stream bars/trades/quotes into DataFeeds layout.

Example:
    python Live/Crypto/app_binance.py --symbol BTCUSDT --interval 1m --duration 60 --out feeds
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from datafeeds.core.script_runner import run_script
from datafeeds.providers.crypto.binance import BinanceLiveFeed

if __name__ == "__main__":
    raise SystemExit(run_script(BinanceLiveFeed, "live"))