"""Base-class API one-script research (docs/examples/python/research_strategy_api.py).

Run it directly — the engine pulls nothing here because the demo data window is
staged; run the same class from the cloud with `quantlab run cloud job.json`
(especially together with `quantlab research`) for the whole exchange->features
->strategy expectation loop:

    python research_strategy_api.py
    python -m quantlab research docs/examples/python/research_strategy_api.py --job-only

``quantlab.research.ResearchStrategy`` mirrors the python_strategy contract
(docs/python-strategies.md): context data + config, observation/features dicts,
row dicts to return, final metrics. The generated job sets
``experimentConfig["class"]`` to the subclass name so the engine instantiates it
under its own name.
"""

import statistics

from quantlab.research import ResearchStrategy


class MyStrategy(ResearchStrategy):
    exchange = "binance"
    market = "crypto"
    symbols = ["BTCUSDT"]
    event_types = ["Trade", "Quote", "Bar"]
    interval_seconds = 60
    start = "2026-09-01T00:00:00"
    end = "2026-09-01T01:00:00"
    features = ["mid_price", "spread", "trade_volume"]
    horizons = ["5m"]
    experiment_config = {"window": "20"}

    def __init__(self):
        self.window = 20
        self.history = []
        self.signals = []

    def initialize(self, context):
        self.window = int(context["config"].get("window", "20"))

    def on_observation(self, observation, features):
        self.history.append(observation["close"])
        if len(self.history) < self.window:
            return None

        sma = statistics.mean(self.history[-self.window:])
        decision = 1.0 if observation["close"] > sma else -1.0
        self.signals.append(decision)
        return {
            "decision": decision,
            "close": observation["close"],
            "sma": sma,
            "mid_feature": features.get("mid_price", 0.0),
        }

    def finalize(self):
        buys = sum(1 for d in self.signals if d > 0)
        return {
            "metrics": {"signals": len(self.signals), "buys": buys},
            "metadata": {"strategy": "sma_cross", "window": str(self.window)},
        }


if __name__ == "__main__":
    raise SystemExit(MyStrategy.run())