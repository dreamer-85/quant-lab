"""SMA crossover strategy for the 'python_strategy' experiment.

Contract: docs/python-strategies.md
Job: set experimentName to "python_strategy" and point strategyScript at this file.
Observations arrive from the engine as dicts of raw market fields plus the
requested feature vector; rows returned from on_observation land in the
experiment output (timestamp/symbol are auto-filled).
"""

import statistics


class Strategy:
    def __init__(self):
        self.window = 20
        self.history = []
        self.signals = []

    def initialize(self, context):
        # context["config"] is the raw experimentConfig (dict[str, str]).
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

    def on_outcome(self, outcome):
        pass

    def finalize(self):
        buys = sum(1 for d in self.signals if d > 0)
        return {
            "metrics": {"signals": len(self.signals), "buys": buys},
            "metadata": {"strategy": "sma_cross", "window": str(self.window)},
        }