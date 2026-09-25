"""Live-session research (docs/examples/python/research_live.py).

Same as the historical one-script flow, but with ``live = True`` the orchestrator
opens a bounded websocket session on the exchange, stages the freshly captured
trades/quotes/bars, then evaluates the strategy over that window immediately:

    python research_live.py

Requires exchange websocket access. Leave ``start``/``end`` empty so the job
replays the whole captured session.
"""

import statistics

from quantlab.research import ResearchStrategy


class LiveSma(ResearchStrategy):
    exchange = "binance"
    market = "crypto"
    symbols = ["BTCUSDT"]
    event_types = ["Trade", "Quote", "Bar"]
    interval_seconds = 60
    live = True
    live_duration_seconds = 60
    features = ["mid_price", "spread", "trade_volume"]
    horizons = ["5m"]

    def __init__(self):
        self.history = []
        self.signals = []

    def on_observation(self, observation, features):
        self.history.append(observation["close"])
        if len(self.history) < 5:
            return None
        sma = statistics.mean(self.history[-5:])
        decision = 1.0 if observation["close"] > sma else -1.0
        self.signals.append(decision)
        return {"decision": decision, "sma": sma, "spread": features.get("spread", 0.0)}

    def finalize(self):
        buys = sum(1 for d in self.signals if d > 0)
        return {"metrics": {"signals": len(self.signals), "buys": buys}}


if __name__ == "__main__":
    raise SystemExit(LiveSma.run())