"""One-script research: declared exchange + data + features + strategy expectation.

Run with:

    python -m quantlab research docs/examples/python/research_binance.py

The ``Research`` class lists what to measure (exchange, symbols, event types,
interval, window, features); ``quantlab research`` pulls that exchange's data
when it is not already staged under the feed root, generates the job JSON, and
runs the strategy through the local Runner. ``Strategy`` is the standard
``python_strategy`` contract (docs/python-strategies.md) — the expectation the
research evaluates.

Depth-based features (e.g. ``imbalance``) return 0.0 until ``book_updates.csv``
is staged by hand under <data-dir>/<market>/<exchange>/<symbol>/; providers
auto-pull bars/trades/quotes only.
"""

import statistics


class Research:
    exchange = "binance"
    market = "crypto"
    symbols = ["BTCUSDT"]
    event_types = ["Bar", "Trade", "Quote"]
    interval_seconds = 60
    start = "2026-09-01T00:00:00"
    end = "2026-09-01T01:00:00"
    features = ["mid_price", "spread", "trade_volume"]
    horizons = ["5m"]
    experiment_config = {"window": "20"}


class Strategy:
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