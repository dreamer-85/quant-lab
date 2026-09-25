"""Streaming live research (docs/examples/python/stream_live.py).

``stream_live = True`` switches the engine *itself* onto the exchange's live
websocket: one observation per incoming event (no observation grid, nothing
dropped), timestamped with the event's own time, features recomputed per event.
Here we subscribe Trade/Quote/Bar with 1-second bars so a closed bar is (almost)
guaranteed inside the session:

    python stream_live.py

Requires exchange websocket access. Leave ``start``/``end`` empty — the window is
derived live and the socket stays open until ``live_duration_seconds`` elapses.
"""

import statistics

from quantlab.research import ResearchStrategy


class StreamSma(ResearchStrategy):
    exchange = "binance"
    market = "crypto"
    symbols = ["BTCUSDT"]
    event_types = ["Trade", "Quote", "Bar"]
    interval_seconds = 1              # resolution => 1s klines arrive as Bar events
    stream_live = True
    live_duration_seconds = 45
    features = ["mid_price", "spread", "trade_volume"]

    def __init__(self):
        self.seen = {"Trade": 0, "Quote": 0, "Bar": 0}
        self.closes = []
        self.bars = 0

    def on_observation(self, observation, features):
        # Per-event emission: exactly one of these lists is non-empty.
        if observation.get("trades"):
            self.seen["Trade"] += len(observation["trades"])
        if observation.get("quotes"):
            self.seen["Quote"] += len(observation["quotes"])
        if observation.get("bars"):
            self.seen["Bar"] += len(observation["bars"])
            self.closes.append(observation["close"])
            self.bars += 1
            return {"bar": observation["close"], "mid_feature": features.get("mid_price", 0.0)}
        return None

    def finalize(self):
        sma = statistics.mean(self.closes[-5:]) if self.closes else 0.0
        return {
            "metrics": {
                "events": sum(self.seen.values()),
                "trades_seen": self.seen["Trade"],
                "quotes_seen": self.seen["Quote"],
                "bars_seen": self.seen["Bar"],
                "last_sma5": round(sma, 2),
            },
            "metadata": {"mode": "stream_live", "provider": "binance"},
        }


if __name__ == "__main__":
    raise SystemExit(StreamSma.run())