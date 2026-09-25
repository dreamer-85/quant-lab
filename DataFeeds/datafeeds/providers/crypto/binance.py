"""Binance public market data provider (spot; USDT-M futures via market=usdm).

REST  : https://api.binance.com          (spot:   /api/v3)
         https://fapi.binance.com       (usdm:   /fapi/v1)
WS    : wss://stream.binance.com:9443   (spot:   /stream?streams=...)
         wss://fstream.binance.com      (usdm:   /stream?streams=...)

Symbols use Binance form, e.g. ``BTCUSDT``. The websocket-client library
automatically answers the exchange's control ping frames, so no manual
keep-alive is required.
"""

from __future__ import annotations

import json
import time

from ...core.base_feeds import FeedResult, HistoricalFeed, LiveFeed
from ...core.datatypes import Bar, Quote, Trade
from ...core.http import HttpError, http_get_json
from ...core.io import symbol_dir, write_bars, write_quotes, write_trades, write_pull_manifest
from ...core.registry import register_feed
from ...core.times import parse_interval, to_unix_ms, utcnow_ms
from ...core.ws import WsConnection

REST_SPOT = "https://api.binance.com"
REST_USDM = "https://fapi.binance.com"
WS_SPOT = "wss://stream.binance.com:9443"
# Alternate spot streaming host on the standard 443 port; some networks block
# non-standard ports, so live sessions fall back to it when 9443 is unreachable.
WS_SPOT_ALT = "wss://data-stream.binance.vision"
WS_USDM = "wss://fstream.binance.com"
MAX_PAGES = 2000
PAGE = 1000

# seconds -> Binance interval token
_INTERVALS = {
    1: "1s", 60: "1m", 180: "3m", 300: "5m", 900: "15m", 1800: "30m",
    3600: "1h", 7200: "2h", 14400: "4h", 21600: "6h", 28800: "8h",
    43200: "12h", 86400: "1d", 259200: "3d", 604800: "1w", 2592000: "1M",
}


def native_interval(seconds: int) -> str:
    token = _INTERVALS.get(seconds)
    if token is None:
        raise ValueError(f"Binance does not support kline interval {seconds}s (choose 1s..1M)")
    return token


def _rest(market: str) -> str:
    return REST_USDM if market == "usdm" else REST_SPOT


def _ws(market: str) -> str:
    return WS_USDM if market == "usdm" else WS_SPOT


def _ws_candidates(market: str, streams: list[str]) -> list[str]:
    """Stream URLs to try in order for a live session (primary first, alternates after)."""
    path = "/stream?streams=" + "/".join(streams)
    urls = [f"{_ws(market)}{path}"]
    if market != "usdm":
        urls.append(f"{WS_SPOT_ALT}{path}")
    return urls


def _parse_kline(row) -> Bar:
    # [openTime, open, high, low, close, volume, closeTime, quoteVol, trades, ...]
    return Bar(
        timestamp_ms=int(row[0]),
        open=float(row[1]),
        high=float(row[2]),
        low=float(row[3]),
        close=float(row[4]),
        volume=float(row[5]),
    )


@register_feed("crypto", "binance", "historical")
class BinanceHistoricalFeed(HistoricalFeed):
    provider = "binance"
    market = "crypto"

    def pull(self, symbol, start, end, interval=None, out_root=None, **kwargs):
        start_dt, end_dt = self._window(start, end)
        seconds, token = self._intervals(interval)
        market = kwargs.get("market", "spot")
        out_root = out_root or "feeds"
        start_ms, end_ms = to_unix_ms(start_dt), to_unix_ms(end_dt)
        rest = _rest(market)
        bars, trades = [], []

        if kwargs.get("bars", True):
            cursor = start_ms
            pages = 0
            while cursor <= end_ms and pages < MAX_PAGES:
                pages += 1
                payload = http_get_json(
                    f"{rest}/api/v3/klines" if market == "spot" else f"{rest}/fapi/v1/klines",
                    params={"symbol": symbol, "interval": token,
                            "startTime": str(cursor), "endTime": str(end_ms), "limit": str(PAGE)},
                )
                if not payload:
                    break
                next_cursor = None
                for row in payload:
                    bar = _parse_kline(row)
                    if bar.timestamp_ms < start_ms:
                        continue
                    if bar.timestamp_ms > end_ms:
                        continue
                    bars.append(bar)
                    next_cursor = bar.timestamp_ms + (seconds * 1000)
                if next_cursor is None or next_cursor <= cursor:
                    break
                cursor = next_cursor
            bars_path, bars_count = write_bars(out_root, self.market, self.provider, symbol, seconds, bars)
        else:
            bars_path, bars_count = None, 0

        if kwargs.get("trades", False):
            try:
                seen = set()
                trades_by_id = {}
                cursor = start_ms
                pages = 0
                while cursor <= end_ms and pages < MAX_PAGES:
                    pages += 1
                    payload = http_get_json(
                        f"{rest}/api/v3/aggTrades" if market == "spot" else f"{rest}/fapi/v1/aggTrades",
                        params={"symbol": symbol, "startTime": str(cursor),
                                "endTime": str(end_ms), "limit": str(PAGE)},
                    )
                    if not payload:
                        break
                    for row in payload:
                        ts = int(row.get("T", 0))
                        tid = str(row.get("a")) if row.get("a") is not None else f"{ts}-{row.get('p')}-{row.get('q')}"
                        if tid in seen:
                            continue
                        if ts < start_ms or ts > end_ms:
                            continue
                        seen.add(tid)
                        trades_by_id[tid] = Trade(
                            timestamp_ms=ts,
                            price=float(row.get("p", 0)),
                            size=float(row.get("q", 0)),
                            side="sell" if row.get("m") else "buy",
                            trade_id=tid,
                        )
                    last_ts = max((row.get("T", 0) for row in payload), default=0)
                    if last_ts <= 0 or len(payload) < PAGE:
                        break
                    new_cursor = last_ts + 1
                    if new_cursor <= cursor:
                        break
                    cursor = new_cursor
                trades = sorted(trades_by_id.values(), key=lambda t: t.timestamp_ms)
                trades_path, trades_count = write_trades(out_root, self.market, self.provider, symbol, trades)
            except HttpError as exc:
                # aggTrades time-window pagination can be restricted for deep history.
                print(f"  [binance] aggTrades skipped ({exc}); using bars only")
                trades_path, trades_count = None, 0

        files = {}
        counts = {}
        if bars_count:
            files["bars"] = bars_path
            counts["bars"] = bars_count
        if trades_count:
            files["trades"] = trades_path
            counts["trades"] = trades_count

        notes = []
        if kwargs.get("quotes", False) and "quotes" not in files:
            # Binance's REST API exposes no historical book-ticker stream; quoting
            # depth only exists live. Record it so a missing quotes.csv is expected.
            notes.append(
                "historical quote book is not available via REST — no quotes.csv staged; "
                "use a live session (source=ws-live) for bookTicker rows or leave spread features at 0"
            )

        if files:
            write_pull_manifest(
                out_root, self.market, self.provider, symbol,
                source="REST", start_ms=start_ms, end_ms=end_ms,
                files=files, notes=notes,
            )
        return FeedResult(self.market, self.provider, symbol, "historical", files,
                          counts, str(out_root))

    def native_interval(self, seconds: int) -> str:
        return native_interval(seconds)


@register_feed("crypto", "binance", "live")
class BinanceLiveFeed(LiveFeed):
    provider = "binance"
    market = "crypto"

    def stream(self, symbol, duration_seconds, interval=None, out_root=None, **kwargs):
        out_root = out_root or "feeds"
        seconds = parse_interval(interval) if interval else 60
        token = native_interval(seconds)
        market = kwargs.get("market", "spot")
        want_bars = kwargs.get("bars", True)
        want_trades = kwargs.get("trades", True)
        want_quotes = kwargs.get("quotes", True)
        lower = symbol.lower()

        bars, trades, quotes = [], [], []
        stop_at = time.monotonic() + float(duration_seconds)

        def handler(text: str):
            try:
                msg = json.loads(text)
            except ValueError:
                return time.monotonic() < stop_at
            data = msg.get("data", {})
            event = data.get("e")
            if event == "kline":
                k = data.get("k", {})
                bars.append(Bar(int(k.get("t", 0)), float(k.get("o", 0)), float(k.get("h", 0)),
                                float(k.get("l", 0)), float(k.get("c", 0)), float(k.get("v", 0))))
            elif event == "trade":
                trades.append(Trade(int(data.get("T", 0)), float(data.get("p", 0)), float(data.get("q", 0)),
                                    "sell" if data.get("m") else "buy", str(data.get("t")) if data.get("t") is not None else None))
            elif event == "bookTicker" or ("b" in data and "a" in data):
                # bookTicker frames carry no event name (only u/s/b/B/a/A).
                quotes.append(Quote(int(data.get("T", utcnow_ms())), float(data.get("b", 0)), float(data.get("B", 0)),
                                    float(data.get("a", 0)), float(data.get("A", 0))))
            return time.monotonic() < stop_at

        streams = []
        if want_bars:
            streams.append(f"{lower}@kline_{token}")
        if want_trades:
            streams.append(f"{lower}@trade")
        if want_quotes:
            streams.append(f"{lower}@bookTicker")

        last_error = None
        for url in _ws_candidates(market, streams):
            conn = None
            try:
                conn = WsConnection(url, handler)
                conn.run()
                conn.close()
                last_error = None
                break
            except Exception as exc:  # noqa: BLE001
                last_error = exc
                if conn is not None:
                    try:
                        conn.close()
                    except Exception:  # noqa: BLE001
                        pass
        if last_error is not None:
            print(f"  [binance] websocket unavailable: {last_error}")

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

        notes = []
        if want_quotes and "quotes" not in files:
            notes.append("no bookTicker rows captured during this live session")
        if files:
            sess_start_ms = int(time.time() * 1000) - int(float(duration_seconds) * 1000)
            write_pull_manifest(
                out_root, self.market, self.provider, symbol,
                source="ws-live", start_ms=sess_start_ms,
                end_ms=int(time.time() * 1000),
                files=files, notes=notes,
            )
        print(f"  [binance] live session closed: bars={len(bars)} trades={len(trades)} quotes={len(quotes)}")
        return FeedResult(self.market, self.provider, symbol, "live", files, counts, str(out_root))