#!/usr/bin/env python
"""Live Deriv forex feed (WebSocket): stream ticks (bid/ask quotes) and OHLC bars into DataFeeds layout.

Example:
    python Live/Forex/app_deriv.py --symbol EURUSD --interval 60 --duration 30 --out feeds
Optional: --app-id <id> (default 1089).
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from datafeeds.core.script_runner import run_script
from datafeeds.providers.forex.deriv import DerivLiveFeed

if __name__ == "__main__":
    raise SystemExit(run_script(DerivLiveFeed, "live"))