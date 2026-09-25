"""OKX public market data provider (spot + perpetuals).

REST  : https://www.okx.com/api/v5/market
WS    : wss://ws.okx.com:8443/ws/v5/public

Symbols use OKX ``instId`` form, e.g. ``BTC-USDT``. Historical candles paginate
backwards through ``history-candles`` (100 rows/page); trades come from the
recent-trades endpoint. Both live and historical rows are normalized into the
shared DataFeeds schema.
"""

from __future__ import annotations

import json
import time

from ...core.base_feeds import FeedResult, HistoricalFeed, LiveFeed
from ...core.datatypes import Bar, Quote, Trade
from ...core.http import http_get_json
from ...core.io import symbol_dir, write_bars, write_quotes, write_trades
from ...core.registry import register_feed
from ...core.times import from_unix_ms, parse_interval, to_unix_ms, utcnow_ms
from ...core.ws import WsConnection

REST_BASE = "https://www.okx.com/api/v5/market"
WS_URL = "wss://ws.okx.com:8443/ws/v5/public"
MAX_PAGES = 2000

# seconds -> OKX bar token (history-candles does not support "1s")
_BAR_TOKENS = {
    60: "1m", 180: "3m", 300: "5m", 900: "15m", 1800: "30m",
    3600: "1H", 7200: "2H", 14400: "4H", 21600: "6H", 43200: "12H",
    86400: "1D", 604800: "1W", 2592000: "1M",
}


def native_bar(seconds: int) -> str:
    token = _BAR_TOKENS.get(seconds)
    if token is None:
        raise ValueError(f"OKX does not support bar interval {seconds}s (choose 1m..1M)")
    return token


def _candles(inst_id: str, bar: str, after_ms: int, limit: int):
    """Newest-first page of candles strictly older than after_ms."""
    return http_get_json(
        f"{REST_BASE}/history-candles",
        params={"instId": inst_id, "bar": bar, "after": str(after_ms), "limit": str(limit)},
    )


def _ensure_ok(payload) -> None:
    if payload.get("code") not in ("0", 0):
        raise RuntimeError(f"OKX request failed: code={payload.get('code')} msg={payload.get('msg')}")


def _parse_candle(row) -> Bar:
    # [ts(ms), o, h, l, c, vol, volCcy, volCcyQuote, confirm]
    return Bar(
        timestamp_ms=int(row[0]),
        open=float(row[1]),
        high=float(row[2]),
        low=float(row[3]),
        close=float(row[4]),
        volume=float(row[5]),
    )


@register_feed("crypto", "okx", "historical")
class OkxHistoricalFeed(HistoricalFeed):
    provider = "okx"
    market = "crypto"

    def pull(self, symbol, start, end, interval=None, out_root=None, **kwargs):
        start_dt, end_dt = self._window(start, end)
        seconds, bar = self._intervals(interval)
        out_root = out_root or "feeds"
        candles, trades = [], []
        rows_bars = rows_trades = 0

        # Candles: page backwards from the end of the window.
        if kwargs.get("bars", True):
            end_ms = to_unix_ms(end_dt)
            after = end_ms + (seconds * 1000)
            for _ in range(MAX_PAGES):
                payload = _candles(symbol, bar, after, 100)
                _ensure_ok(payload)
                data = payload.get("data", [])
                if not data:
                    break
                for row in data:
                    if len(row) >= 9 and row[8] != "1":
                        continue  # skip unconfirmed candle
                    candle = _parse_candle(row)
                    if candle.timestamp_ms < to_unix_ms(start_dt):
                        continue
                    if candle.timestamp_ms > end_ms:
                        continue
                    candles.append(candle)
                oldest = int(data[-1][0])
                after = oldest
                if oldest <= to_unix_ms(start_dt):
                    break
            path, rows_bars = write_bars(out_root, self.market, self.provider, symbol, seconds, candles)

        # Trades: the public endpoint only serves recent executions.
        if kwargs.get("trades", False):
            payload = http_get_json(f"{REST_BASE}/trades", params={"instId": symbol, "limit": "500"})
            _ensure_ok(payload)
            for row in payload.get("data", []):
                ts = int(row["ts"])
                t_ms, t_ms_end = to_unix_ms(start_dt), to_unix_ms(end_dt)
                if ts < t_ms or ts > t_ms_end:
                    continue
                trades.append(
                    Trade(
                        timestamp_ms=ts,
                        price=float(row["px"]),
                        size=float(row["sz"]),
                        side=str(row.get("side", "unknown")).lower(),
                        trade_id=str(row.get("tradeId")) if row.get("tradeId") else None,
                    )
                )
            trades.sort(key=lambda t: t.timestamp_ms)
            path, rows_trades = write_trades(out_root, self.market, self.provider, symbol, trades)

        files = {}
        if rows_bars:
            files["bars"] = symbol_dir(out_root, self.market, self.provider, symbol) / f"bars_{seconds}.csv"
        if rows_trades:
            files["trades"] = symbol_dir(out_root, self.market, self.provider, symbol) / "trades.csv"
        return FeedResult(
            market=self.market, provider=self.provider, symbol=symbol, mode="historical",
            output_files=files, counts={"bars": rows_bars, "trades": rows_trades}, feed_root=str(out_root),
        )

    def native_interval(self, seconds: int) -> str:
        return native_bar(seconds)


@register_feed("crypto", "okx", "live")
class OkxLiveFeed(LiveFeed):
    provider = "okx"
    market = "crypto"

    def stream(self, symbol, duration_seconds, interval=None, out_root=None, **kwargs):
        out_root = out_root or "feeds"
        seconds = parse_interval(interval) if interval else 60
        bar = native_bar(seconds)
        want_bars = kwargs.get("bars", True)
        want_trades = kwargs.get("trades", True)
        want_quotes = kwargs.get("quotes", True)

        bars, trades, quotes = [], [], []

        stop_at = time.monotonic() + float(duration_seconds)

        def handler(text: str):
            if text.strip() in ("ping", "Ping") or '"event":"ping"' in text:
                conn.send("pong")
                return time.monotonic() < stop_at
            try:
                msg = json.loads(text)
            except ValueError:
                return time.monotonic() < stop_at
            event = msg.get("event")
            if event in ("subscribe", "error"):
                if event == "error":
                    print(f"  [okx] subscribe error: {msg.get('msg')}")
                return time.monotonic() < stop_at
            if "arg" not in msg or "data" not in msg:
                return time.monotonic() < stop_at
            channel = msg["arg"].get("channel", "")
            now = utcnow_ms()
            for row in msg["data"]:
                if channel.startswith("candle"):
                    if len(row) >= 6:
                        bars.append(Bar(int(row[0]), float(row[1]), float(row[2]), float(row[3]), float(row[4]), float(row[5])))
                elif channel == "trades":
                    trades.append(
                        Trade(int(row["ts"]), float(row["px"]), float(row["sz"]), str(row.get("side", "unknown")).lower(), row.get("tradeId"))
                    )
                elif channel.startswith("books"):
                    bids, asks = row.get("bids", []), row.get("asks", [])
                    if bids and asks:
                        best_bid = max(bids, key=lambda l: float(l[0]))
                        best_ask = min(asks, key=lambda l: float(l[0]))
                        ts = int(row.get("ts", now))
                        quotes.append(Quote(ts, float(best_bid[0]), float(best_bid[1]), float(best_ask[0]), float(best_ask[1])))
            return time.monotonic() < stop_at

        args = []
        if want_bars:
            args.append({"channel": f"candle{bar}", "instId": symbol})
        if want_trades:
            args.append({"channel": "trades", "instId": symbol})
        if want_quotes:
            args.append({"channel": "books5", "instId": symbol})
        conn = WsConnection(WS_URL, handler)
        conn.send_json({"op": "subscribe", "args": args})
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
        print(f"  [okx] live session closed: bars={len(bars)} trades={len(trades)} quotes={len(quotes)}")
        return FeedResult(self.market, self.provider, symbol, "live", files, counts, str(out_root))