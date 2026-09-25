"""Deriv public market data provider (forex, synthetics, indices).

Deriv exposes market data through its WebSocket API v3
(``wss://ws.derivws.com/websockets/v3?app_id=<app_id>``); there is no public
REST candle endpoint, so historical and live paths are both WebSocket-based.

Historical:  ``ticks_history`` with ``style:"candles"`` (OHLC bars over an
             arbitrary [start, end] range) and optionally ``style:"ticks"``
             (recent mid prices -> quotes.csv).
Live:        subscribe to ``ticks`` (bid/ask/mid quotes) and ``ohlc``
             (running OHLC bars, deduplicated per candle epoch).

Authentication: pass ``--api-token`` (Deriv API token) alongside the app_id.
When a token is supplied the session sends ``authorize`` after connecting and
waits for the acknowledge before issuing data requests. Both ``--app-id`` and
``--api-token`` flow through the unified CLI and the entry scripts.

Symbols use Deriv form, e.g. ``EURUSD``, ``GBPUSD``, ``R_100``.
"""

from __future__ import annotations

import json
import time

from ...core.base_feeds import FeedResult, HistoricalFeed, LiveFeed
from ...core.datatypes import Bar, Quote
from ...core.io import write_bars, write_quotes
from ...core.registry import register_feed
from ...core.times import to_unix_ms
from ...core.ws import WsConnection

WS_URL = "wss://ws.derivws.com/websockets/v3?app_id={app_id}"
DEFAULT_APP_ID = 1089
MAX_PAGES = 500

# Allowed candle granularities (seconds) per the Deriv API.
_GRANULARITIES = sorted({60, 120, 180, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 86400})


def native_interval(seconds: int) -> int:
    """Deriv granularity is constrained; align the request up to the next allowed value."""
    if seconds <= 60:
        return 60
    for allowed in reversed(_GRANULARITIES):
        if seconds >= allowed:
            return allowed
    return 60


def _url(app_id) -> str:
    return WS_URL.format(app_id=app_id or DEFAULT_APP_ID)


def _raise_for_error(msg: dict, what: str) -> None:
    if msg.get("msg_type") == "error":
        err = msg.get("error", {})
        raise RuntimeError(f"Deriv {what} error: code={err.get('code')} message={err.get('message')}")


def _authorize(conn: WsConnection, token: str) -> str:
    """Authorizes an already-connected session and returns the login id.

    Sends ``{"authorize": <token>}`` and consumes the single acknowledge reply
    so the streaming receive loop below only sees data frames.
    """
    conn.send_json({"authorize": token})
    for _ in range(5):
        frame = conn.recv_once()
        if frame is None:
            break
        msg = json.loads(frame)
        _raise_for_error(msg, "authorize")
        if msg.get("msg_type") == "authorize":
            auth = msg.get("authorize") or {}
            return str(auth.get("loginid", ""))
    raise RuntimeError("Deriv authorize: no acknowledge received for api token")


def _api_token(kwargs: dict) -> str:
    return kwargs.get("api_token") or kwargs.get("token") or ""


def _session_conn(conn: WsConnection, token: str, app_id) -> None:
    """Runs the pre-request authorize handshake when a token is present."""
    if token:
        login = _authorize(conn, token)
        print(f"  [deriv] authorized as {login} (app_id={app_id or DEFAULT_APP_ID})")


@register_feed("forex", "deriv", "historical")
class DerivHistoricalFeed(HistoricalFeed):
    provider = "deriv"
    market = "forex"
    human_interval = False  # interval is bare seconds (granularity)

    def pull(self, symbol, start, end, interval=None, out_root=None, **kwargs):
        start_dt, end_dt = self._window(start, end)
        seconds = int(interval)
        granularity = native_interval(seconds)
        if granularity != seconds:
            print(f"  [deriv] granularity {seconds}s not offered; using {granularity}s")
        app_id = kwargs.get("app_id")
        out_root = out_root or "feeds"
        start_s = int(to_unix_ms(start_dt) // 1000)
        end_s = int(to_unix_ms(end_dt) // 1000)
        api_token = _api_token(kwargs)
        bars, quotes = [], []

        if kwargs.get("bars", True):
            cursor = start_s
            pages = 0
            while cursor < end_s and pages < MAX_PAGES:
                pages += 1
                collected = []
                request = {
                    "ticks_history": symbol,
                    "style": "candles",
                    "granularity": granularity,
                    "start": cursor,
                    "end": end_s,
                }

                def handler(text):
                    msg = json.loads(text)
                    _raise_for_error(msg, "ticks_history")
                    if msg.get("msg_type") == "candles":
                        collected.extend(msg.get("candles", []))
                    return False

                conn = WsConnection(_url(app_id), handler)
                try:
                    _session_conn(conn, api_token, app_id)
                    conn.send_json(request)
                    conn.run()
                finally:
                    conn.close()
                if not collected:
                    break
                for row in collected:
                    epoch = int(row.get("epoch", 0))
                    if cursor <= epoch <= end_s:
                        bars.append(
                            Bar(epoch * 1000, float(row.get("open", 0)), float(row.get("high", 0)),
                                float(row.get("low", 0)), float(row.get("close", 0)), float(row.get("volume", 0)))
                        )
                newest = max(int(row.get("epoch", 0)) for row in collected)
                cursor = newest + granularity
            bars_path, bars_count = write_bars(out_root, self.market, self.provider, symbol, granularity, bars)
        else:
            bars_path, bars_count = None, 0

        if kwargs.get("quotes", False):
            recent = {"ticks_history": symbol, "style": "ticks", "count": max(1, min(int(kwargs.get("count", 5000)), 5000)), "end": "latest"}
            collected_prices, collected_times = [], []

            def tick_handler(text):
                msg = json.loads(text)
                _raise_for_error(msg, "ticks_history")
                if msg.get("msg_type") == "history":
                    hist = msg.get("history", {}) or {}
                    collected_prices.extend(hist.get("prices", []))
                    collected_times.extend(hist.get("times", []))
                return False

            conn = WsConnection(_url(app_id), tick_handler)
            try:
                _session_conn(conn, api_token, app_id)
                conn.send_json(recent)
                conn.run()
            finally:
                conn.close()
            for price, t in zip(collected_prices, collected_times):
                ts = int(float(t)) * 1000
                if start_s * 1000 <= ts <= end_s * 1000:
                    quotes.append(Quote(ts, float(price), 0.0, float(price), 0.0))
            quotes_path, quotes_count = write_quotes(out_root, self.market, self.provider, symbol, quotes)
        else:
            quotes_path, quotes_count = None, 0

        files = {}
        if bars_count:
            files["bars"] = bars_path
        if quotes_count:
            files["quotes"] = quotes_path
        return FeedResult(self.market, self.provider, symbol, "historical", files,
                          {"bars": bars_count, "quotes": quotes_count}, str(out_root))


@register_feed("forex", "deriv", "live")
class DerivLiveFeed(LiveFeed):
    provider = "deriv"
    market = "forex"

    def stream(self, symbol, duration_seconds, interval=None, out_root=None, **kwargs):
        out_root = out_root or "feeds"
        seconds = int(interval) if interval else 3600
        granularity = native_interval(seconds)
        app_id = kwargs.get("app_id")
        want_bars = kwargs.get("bars", True)
        want_quotes = kwargs.get("quotes", True)

        bars, quotes = [], []
        candle_by_epoch = {}
        stop_at = time.monotonic() + float(duration_seconds)

        def handler(text: str):
            msg = json.loads(text)
            _raise_for_error(msg, "live")
            mtype = msg.get("msg_type")
            if mtype == "tick":
                tick = msg.get("tick", {})
                ts = int(tick.get("epoch", 0)) * 1000
                quotes.append(Quote(ts, float(tick.get("bid", 0)), 0.0, float(tick.get("ask", 0)), 0.0))
            elif mtype == "ohlc":
                o = msg.get("ohlc", {})
                if int(o.get("granularity", 0)) == granularity:
                    epoch = int(o.get("epoch", 0))
                    candle_by_epoch[epoch] = Bar(epoch * 1000, float(o.get("open", 0)), float(o.get("high", 0)),
                                                 float(o.get("low", 0)), float(o.get("close", 0)), float(o.get("volume", 0)))
            return time.monotonic() < stop_at

        conn = WsConnection(_url(app_id), handler)
        try:
            _session_conn(conn, _api_token(kwargs), app_id)
            if want_quotes:
                conn.send_json({"ticks": symbol, "subscribe": 1})
            if want_bars:
                conn.send_json({"ohlc": symbol, "granularity": granularity, "subscribe": 1})
            conn.run()
        finally:
            conn.close()

        bars = sorted(candle_by_epoch.values(), key=lambda b: b.timestamp_ms)
        quotes = sorted(quotes, key=lambda q: q.timestamp_ms)
        files, counts = {}, {}
        if bars:
            path, n = write_bars(out_root, self.market, self.provider, symbol, granularity, bars)
            files["bars"], counts["bars"] = path, n
        if quotes:
            path, n = write_quotes(out_root, self.market, self.provider, symbol, quotes)
            files["quotes"], counts["quotes"] = path, n
        print(f"  [deriv] live session closed: bars={len(bars)} quotes={len(quotes)}")
        return FeedResult(self.market, self.provider, symbol, "live", files, counts, str(out_root))