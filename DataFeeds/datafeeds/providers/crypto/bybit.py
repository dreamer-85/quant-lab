"""Bybit public market data provider (spot + linear perpetuals).

REST  : https://api.bybit.com/v5/market
WS    : wss://stream.bybit.com/v5/public/spot (or .../linear)

Mirrors the Bybit v5 endpoint usage already implemented in the ResearchEngine
(``Research/Engine/Ingest/Bybit``) so DataFeeds and the engine agree on the
source of truth. Symbols use Bybit form, e.g. ``BTCUSDT``.
"""

from __future__ import annotations

import json
import time

from ...core.base_feeds import FeedResult, HistoricalFeed, LiveFeed
from ...core.datatypes import Bar, Quote, Trade
from ...core.http import http_get_json
from ...core.io import symbol_dir, write_bars, write_quotes, write_trades
from ...core.registry import register_feed
from ...core.times import parse_interval, to_unix_ms, utcnow_ms
from ...core.ws import WsConnection

REST_BASE = "https://api.bybit.com"
WS_SPOT = "wss://stream.bybit.com/v5/public/spot"
WS_LINEAR = "wss://stream.bybit.com/v5/public/linear"
MAX_PAGES = 2000

# seconds -> Bybit v5 kline interval token
_INTERVALS = {
    60: "1", 180: "3", 300: "5", 900: "15", 1800: "30", 3600: "60",
    7200: "120", 14400: "240", 21600: "360", 43200: "720", 86400: "D",
    604800: "W", 2592000: "M",
}


def native_interval(seconds: int) -> str:
    token = _INTERVALS.get(seconds)
    if token is None:
        raise ValueError(f"Bybit does not support kline interval {seconds}s (choose 1m..1M)")
    return token


def _base(category: str) -> str:
    return f"{REST_BASE}/v5/market"


def _ensure_result(payload, endpoint: str) -> dict:
    if payload.get("retCode") != 0:
        raise RuntimeError(f"Bybit {endpoint} failed: retCode={payload.get('retCode')} retMsg={payload.get('retMsg')}")
    return payload.get("result", {})


@register_feed("crypto", "bybit", "historical")
class BybitHistoricalFeed(HistoricalFeed):
    provider = "bybit"
    market = "crypto"

    def pull(self, symbol, start, end, interval=None, out_root=None, **kwargs):
        start_dt, end_dt = self._window(start, end)
        seconds, token = self._intervals(interval)
        category = kwargs.get("category", "spot")
        out_root = out_root or "feeds"
        start_ms, end_ms = to_unix_ms(start_dt), to_unix_ms(end_dt)
        bars, trades = [], []

        if kwargs.get("bars", True):
            cursor = start_ms
            pages = 0
            while cursor < end_ms and pages < MAX_PAGES:
                pages += 1
                payload = http_get_json(
                    f"{_base(category)}/kline",
                    params={
                        "category": category, "symbol": symbol, "interval": token,
                        "start": str(cursor), "end": str(end_ms), "limit": "1000",
                    },
                )
                result = _ensure_result(payload, "kline")
                rows = result.get("list", [])
                if not rows:
                    break
                # list is newest-first; each row: [start, open, high, low, close, volume, turnover]
                for row in rows:
                    row_ms = int(row[0])
                    if row_ms < start_ms or row_ms > end_ms:
                        continue
                    bars.append(Bar(row_ms, float(row[1]), float(row[2]), float(row[3]), float(row[4]), float(row[5])))
                next_ms = int(rows[-1][0]) + (seconds * 1000)
                if next_ms <= cursor:
                    break
                cursor = next_ms
            path, n = write_bars(out_root, self.market, self.provider, symbol, seconds, bars)
            bars_path, bars_count = path, n
        else:
            bars_path, bars_count = None, 0

        if kwargs.get("trades", False):
            payload = http_get_json(
                f"{_base(category)}/recent-trade",
                params={"category": category, "symbol": symbol, "limit": "1000"},
            )
            result = _ensure_result(payload, "recent-trade")
            for row in result.get("list", []):
                ts = int(row.get("time", 0))
                if ts < start_ms or ts > end_ms:
                    continue
                trades.append(
                    Trade(
                        timestamp_ms=ts,
                        price=float(row.get("price", 0)),
                        size=float(row.get("size", 0)),
                        side=str(row.get("side", "unknown")).lower(),
                        trade_id=row.get("execId"),
                    )
                )
            trades.sort(key=lambda t: t.timestamp_ms)
            trades_path, trades_count = write_trades(out_root, self.market, self.provider, symbol, trades)
        else:
            trades_path, trades_count = None, 0

        files = {}
        if bars_count:
            files["bars"] = bars_path
        if trades_count:
            files["trades"] = trades_path
        return FeedResult(self.market, self.provider, symbol, "historical", files,
                          {"bars": bars_count, "trades": trades_count}, str(out_root))

    def native_interval(self, seconds: int) -> str:
        return native_interval(seconds)


@register_feed("crypto", "bybit", "live")
class BybitLiveFeed(LiveFeed):
    provider = "bybit"
    market = "crypto"

    def stream(self, symbol, duration_seconds, interval=None, out_root=None, **kwargs):
        out_root = out_root or "feeds"
        seconds = parse_interval(interval) if interval else 60
        token = native_interval(seconds)
        category = kwargs.get("category", "spot")
        want_bars = kwargs.get("bars", True)
        want_trades = kwargs.get("trades", True)
        want_quotes = kwargs.get("quotes", True)

        bars, trades, quotes = [], [], []
        stop_at = time.monotonic() + float(duration_seconds)

        def handler(text: str):
            try:
                msg = json.loads(text)
            except ValueError:
                return time.monotonic() < stop_at
            if msg.get("op") in ("pong", "subscribe"):
                return time.monotonic() < stop_at
            topic = msg.get("topic", "")
            if not topic:
                return time.monotonic() < stop_at
            if "publicTrade" in topic:
                for row in msg.get("data", []):
                    trades.append(Trade(int(row.get("T", 0)), float(row.get("p", 0)), float(row.get("v", 0)),
                                        str(row.get("S", "unknown")).lower(), row.get("i")))
            elif "kline" in topic:
                for row in msg.get("data", []):
                    bars.append(Bar(int(row.get("start", 0)), float(row.get("open", 0)), float(row.get("high", 0)),
                                    float(row.get("low", 0)), float(row.get("close", 0)), float(row.get("volume", 0))))
            elif "orderbook" in topic:
                data = msg.get("data", {}) or {}
                bids, asks = data.get("b", []), data.get("a", [])
                if bids and asks:
                    best_bid = max(bids, key=lambda l: float(l[0]))
                    best_ask = min(asks, key=lambda l: float(l[0]))
                    ts = int(data.get("ts", utcnow_ms()))
                    quotes.append(Quote(ts, float(best_bid[0]), float(best_bid[1]), float(best_ask[0]), float(best_ask[1])))
            return time.monotonic() < stop_at

        topics = []
        if want_trades:
            topics.append(f"publicTrade.{symbol}")
        if want_quotes:
            depth = "1"
            topics.append(f"orderbook.{depth}.{symbol}")
        if want_bars:
            topics.append(f"kline.{token}.{symbol}")

        url = WS_LINEAR if category == "linear" else WS_SPOT
        conn = WsConnection(url, handler, ping_payload='{"op":"ping"}', ping_interval_s=20)
        conn.send_json({"op": "subscribe", "args": topics})
        conn.run()
        conn.close()

        files, counts = {}, {}
        if bars:
            path, n = write_bars(out_root, self.market, self.provider, symbol, seconds, bars)
            files["bars"], counts["bars"] = path, n
        if trades:
            path, n = write_trades(out_root, self.market, self.provider, symbol, trades)
            files["trades"], counts["trades"] = path, n
        if quotes:
            path, n = write_quotes(out_root, self.market, self.provider, symbol, quotes)
            files["quotes"], counts["quotes"] = path, n
        print(f"  [bybit] live session closed: bars={len(bars)} trades={len(trades)} quotes={len(quotes)}")
        return FeedResult(self.market, self.provider, symbol, "live", files, counts, str(out_root))