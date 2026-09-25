#!/usr/bin/env python
"""Historical Deriv forex feed (WebSocket 'ticks_history'): OHLCV candles (+ optional recent mid ticks).

Example:
    python Historical\\Forex\\app_deriv.py --symbol EURUSD --start 2026-09-01 --end 2026-09-03 --interval 60 --out feeds
The interval is in SECONDS (Deriv granularity). Optional: --app-id <id> (default 1089).
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from datafeeds.core.script_runner import run_script
from datafeeds.providers.forex.deriv import DerivHistoricalFeed

if __name__ == "__main__":
    raise SystemExit(run_script(DerivHistoricalFeed, "historical"))