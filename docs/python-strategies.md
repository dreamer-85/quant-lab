# Python Strategy Scripts

Jobs with `"experimentName": "python_strategy"` load a user-written Python file
(called the *strategy script*) and drive it over the observation/feature stream —
the Lean workflow, but your strategy code is 100% Python and the dataset comes
from the DataFeeds layer or Lean zips.

The runner/engine (C#) reads data, replays events, builds observations and
computes features; your script decides what it means.

## Job setup

```jsonc
{
  "jobId": "sma-btc-60s",
  "dataset": "datafeeds",
  "symbols": ["BTCUSDT"],
  "assetClass": "crypto",
  "venue": "binance",
  "resolution": "Minute",
  "startTime": "2024-01-01T00:00:00",
  "endTime": "2024-01-01T01:00:00",
  "source": { "mode": "feed", "provider": "binance" },
  "eventTypes": ["Trade", "Quote", "Bar"],
  "observationInterval": "00:01:00",
  "features": ["mid_price", "spread", "trade_volume"],
  "horizons": ["5m"],                       // optional: resolved forward labels
  "experimentName": "python_strategy",
  "strategyScript": "C:\\strategies\\sma_cross.py",
  "outputFormat": "csv"
}
```

`strategyScript` may be absolute or relative to the working directory. The
experimentConfig key `"script"` overrides it. Validation fails fast when
`python_strategy` has no script.

## One-script research (`quantlab research`)

For the common case — *"I want data from exchange X, these features computed,
and my strategy to evaluate it"* — a single `.py` file replaces the job file.
It declares data/features in a `Research` class and the strategy expectation in
the standard `Strategy` class:

```python
# my_research.py
class Research:
    exchange = "binance"                  # the provider to pull from
    market = "crypto"
    symbols = ["BTCUSDT"]
    event_types = ["Bar", "Trade", "Quote"]
    interval_seconds = 60
    start = "2026-09-01T00:00:00"
    end = "2026-09-01T01:00:00"
    features = ["mid_price", "spread", "trade_volume"]
    horizons = ["5m"]                     # optional
    experiment_config = {"window": "20"}  # optional; lands in context["config"]

class Strategy:
    # ... the exact python_strategy contract above ...
```

| `Research` attribute | default | meaning |
| --- | --- | --- |
| `exchange` | required | provider name (`binance`/`bybit`/`okx`/`deriv`, ...) |
| `market` | `crypto` | `crypto` or `forex` |
| `symbols` | required | provider-native tickers, e.g. `["BTCUSDT"]` |
| `event_types` | `["Bar","Trade","Quote"]` | canonical names; aliases like `trades`/`orderbookupdate` accepted |
| `interval_seconds` | `60` | observation grid / bar interval |
| `start`, `end` | empty | ISO-8601 window; empty = the orchestrator derives it from the staged data's span |
| `resolution` | derived | `Second`/`Minute`/`Hour`/`Daily` from `interval_seconds` |
| `features`, `raw_fields`, `horizons` | `[]` | passed to the job unchanged |
| `job_id` | `f"{exchange}-{first symbol}"` | run/output folder name |
| `live` | `False` | `True` = open a bounded live websocket session instead of pulling historical data |
| `live_duration_seconds` | `30` | live session length in seconds (used when `live = True`) |
| `stream_live` | `False` | `True` = event-driven live evaluation: the engine subscribes the websocket and emits one observation per event (`live_duration_seconds` required, `start`/`end` must stay empty) |

Run it as one command:

```
python -m quantlab research my_research.py
```

The orchestrator (1) pulls the declared exchange/symbols/window into the feed
root when they are not already staged, (2) generates a job JSON (saved as
`<output-dir>/<job_id>.job.json` for reproducibility — same schema as above but
with `source.mode: feed`), (3) runs it through the local Runner DLL, and (4)
prints the manifest. Data and results default to `<script-dir>/feeds` and
`<script-dir>/results` (next to the strategy file, so the fetched CSVs are easy
to inspect); set `QUANTLAB_DATA_ROOT`/`QUANTLAB_OUTPUT_ROOT` or
`--data-dir`/`--output-dir` to relocate. Use `--job-only` to print the generated
JSON without running, `--no-pull` to require data already staged, and `--build`
to build the DLL. A runnable copy lives at
`docs/examples/python/research_binance.py`.

Each run writes a flat, answer-style bundle under `<output-dir>/<job_id>/`:
`<job_id>.report.csv` (observation rows), `<job_id>.signals.csv` (strategy
rows), `summary.txt` (provenance + counts at a glance), and `manifest.json`
(tear-away JSON for tooling). The staged CSVs next to the strategy get a
`pull.json` recording source, requested window, per-file row counts, and
first/last timestamps, so the pulled data's completeness is checkable.

Bars/trades/quotes auto-pull from the provider. Order-book event types are not
staged by providers yet — the orchestrator prints a note and they resolve to
`0.0` features unless you add `book_updates.csv` by hand under
`<data-dir>/<market>/<exchange>/<symbol>/` (see [research-job.md](research-job.md)
→ feed mode).

### Live sessions

Set `live = True` (optionally `live_duration_seconds`, default 30) and the
orchestrator opens a bounded websocket session per symbol instead of the
historical pull: the freshly captured trades/quotes/bars land in the staged
layout and the engine evaluates the strategy over that window immediately —
same hooks, same features, one command:

```python
class MyStrategy(ResearchStrategy):
    exchange = "binance"
    symbols = ["BTCUSDT"]
    live = True
    live_duration_seconds = 60      # stream ~1 minute, then test
    features = ["mid_price", "spread", "trade_volume"]
```

Leave `start`/`end` empty: the orchestrator derives the replay window from the
just-captured session (earliest to latest staged event), so the engine validates
and replays the whole span. Note this is *capture-then-evaluate*: a live window
is recorded and scored right away — it is not tick-by-tick real-time trading
inside the engine. `live = True` plus `--no-pull` is rejected (the session *is*
the pull).

### Streaming live sessions (event-driven)

`stream_live = True` switches the engine **itself** onto a live websocket feed:
the runner subscribes the exchange stream in-process and evaluates the strategy
tick-by-tick — `on_observation` fires once for every trade/quote/bar that
arrives. Nothing is aggregated into observation windows and nothing is dropped
between interval boundaries:

```python
class MyStrategy(ResearchStrategy):
    exchange = "binance"
    symbols = ["BTCUSDT"]
    event_types = ["Trade", "Quote", "Bar"]   # these ARE the live subscriptions
    stream_live = True
    live_duration_seconds = 300
    features = ["mid_price", "spread", "trade_volume"]

    def on_observation(self, observation, features):
        # called per event; observation["timestamp"] is the event's own time
        ...
```

- **Per-event, virtual clock.** One observation per event, timestamped with the
  event's own timestamp (the engine clock is driven by the data, Lean-style —
  not the wall clock). Want fixed intervals instead? Keep `Bar` in
  `event_types`: bars arrive as events with their period (e.g. `1s`/`1m` per
  `resolution`) and you compute on those.
- Requires `live_duration_seconds > 0` and **empty** `start`/`end`: the window is
  derived live (`now − 5 min` → `now + duration + 5 min`) and the socket stays
  open until the deadline.
- Distinct from `live = True` (capture the session, then replay/evaluate):
  `stream_live = True` skips capture entirely and is rejected with `--no-pull`
  — the websocket *is* the source.
- Providers: `binance` and `bybit` support live mode today. Historical pulls
  (`source.mode: feed`) are unaffected; Binance book-ticker/depth quotes are
  live-only on Binance REST.
- Internals: the feed flows through the same `IEventDataSource` → replay →
  features → hooks pipeline with `observationInterval` unset so the replay
  engine emits one observation per event; the websocket layer is C#
  (`ClientWebSocketTransport`), so there is no pythonnet interop on the hot path.

### Base-class API (`quantlab.research.ResearchStrategy`)

For a reusable, Lean-style authoring surface — declare everything on a class and
call `run()` — subclass `ResearchStrategy` instead of writing two classes. It
carries the same declaration keys as the `Research` table above plus the
expectation hooks; `run()` does the whole pull → job → engine → report loop:

```python
# my_strategy.py
from quantlab.research import ResearchStrategy

class MyStrategy(ResearchStrategy):
    exchange = "binance"
    symbols = ["BTCUSDT"]
    features = ["mid_price", "spread", "trade_volume"]

    def on_observation(self, observation, features):
        # ... strategy expectation; return a row dict or None ...

if __name__ == "__main__":
    raise SystemExit(MyStrategy.run().exit_code)   # or: quantlab research my_strategy.py
```

- The generated job sets `experimentConfig["class"]` to the subclass name, so the
  engine instantiates `MyStrategy` (not the literal `Strategy`).
- `run()` locates the script file from the class's module, so the same file works
  from an `import`, from the CLI, and (see below) from the cloud.
- Subclassing is structurally extensible: add hooks/attributes once on a shared
  base and every strategy inherits them, the same way `QCAlgorithm` evolves
  without breaking strategies. Any script — base-class or file-based — is a plain
  `.py` that the engine loads with the same hooks.
- A runnable copy lives at `docs/examples/python/research_strategy_api.py`.

### Results & iteration primitives

A run returns a `RunResult`, not a bare exit code:

```python
result = MyStrategy.run(sample_seconds=900)   # replay only the last 15 min
result.exit_code                              # 0 on success
result.job_id                                 # e.g. binance-btcusdt
result.summary                                # the plain-text summary.txt
result.metrics                                # counts (observations, signals, buys, ...)
result.report_df()                            # pandas frame of the engine rows
result.signals_df()                           # pandas frame of the strategy rows
log = (Path(result.results_dir) / "strategy.log").read_text()
```

- `sample_seconds` (CLI: `quantlab research my_strategy.py --sample`) derives a
  window covering the most recent N seconds of staged/pulled data — fast
  iterations without touching the full window.
- Strategy `print()`s are captured to `results/<job>/strategy.log`; the engine's
  embedded interpreter has no visible stdout.
- Declarations are validated up front with friendly errors (typo a hook, get
  `did you mean 'on_observation'?`).
- Notebook/REPL classes have no file to point `strategyScript` at, so define
  them with `%%writefile` and re-import via `quantlab.research.load_script` — a
  runnable walk-through is at `docs/examples/notebooks/research.ipynb`.

## Cloud runs (`quantlab run cloud`)

The VM runs strategies exactly like the local runner (`deploy/cloud/run-job.sh`).
`submit_job` now stages the strategy too: when the job's `strategyScript`
(`experimentConfig["script"]`) points at a local file, it is scp'd to
`<remote-repo>/Research/Python/_strategy_<name>.py` and the remote job is
rewritten to that repo-relative path. Copies land next to the `quantlab` package,
so a strategy that imports `quantlab.research.ResearchStrategy` resolves it from
the repo without extra setup.

So the reusable-library workflow is:

```
python -m quantlab research my_strategy.py --job-only > job.json   # or the base-class run()
python -m quantlab run cloud job.json --data-dir <bucket-data> --output-dir <bucket-output>
```

The same script, the same computed features, and the same expectations run on the
VM; results download with `quantlab results download <job-id>`.

## Script contract

The module must define a class named `Strategy`. Every hook is optional.

```python
class Strategy:
    def initialize(self, context):
        # context: dict with run/job metadata (see below)
        pass

    def on_observation(self, observation, features):
        # observation: dict of raw market state (see below)
        # features:   dict feature_name -> float (from job.features)
        # return a dict row to append to the experiment output, or None
        pass

    def on_outcome(self, outcome):
        # outcome: dict for one resolved forward label (see below)
        pass

    def finalize(self):
        # optional return:
        #   {"rows":    [ {...}, ... ],
        #    "metrics": {"sharpe": 1.2, ...},
        #    "metadata":{"notes": "..."}}
        pass
```

### `initialize(context)`

| key | type | meaning |
| --- | --- | --- |
| `job_id` | str | job identifier |
| `dataset`, `asset_class`, `venue` | str | job fields |
| `resolution` | str | Lean resolution (`Minute`, …) |
| `symbols`, `features`, `raw_fields`, `horizons` | list[str] | configured lists |
| `start`, `end` | str | ISO-8601 run window |
| `observation_interval_seconds` | float | observation grid spacing |
| `experiment_name` | str | always `python_strategy` |
| `configuration_hash` | str | SHA-256 of the job ground truth |
| `config` | dict[str,str] | raw `experimentConfig` |

Use it to load state (a buffer for a rolling average, a trained model file, …).

### `on_observation(observation, features)`

Raw market fields (all present, `0.0` when absent):

| key | type | meaning |
| --- | --- | --- |
| `timestamp` | str | ISO-8601 observation time |
| `symbol` | str | e.g. `BTCUSDT` |
| `open`, `high`, `low`, `close` | float | period OHLC derived from the period's events, falling back to the market state's last price when a bar-only period yields nothing |
| `volume`, `vwap` | float | traded volume / volume-weighted price |
| `trade_count`, `quote_count` | int | events seen since the previous observation |
| `last_price`, `bid`, `ask`, `mid` | float | market state snapshot |
| `spread`, `spread_bps` | float | top-of-book spread |
| `bid_size`, `ask_size` | float | top-of-book size |
| `bid_depth`, `ask_depth`, `depth_imbalance` | float | book depth / imbalance |
| `bars` | list | `{timestamp, open, high, low, close, volume}` |
| `trades` | list | `{timestamp, price, size, side, trade_id}`, `side` in `buy/sell/unknown` |
| `quotes` | list | `{timestamp, bid_price, bid_size, ask_price, ask_size}` |

`bars`/`trades`/`quotes` are only present when the job subscribed to that event
type and at least one occurred in the period.

`features` is `{feature_name: float}` for the features requested in the job
(see [features.md](features.md)).

Returning a `dict` appends it as one result row. Keys `timestamp` and `symbol`
are auto-filled from the current observation when your dict does not define
them, so rows join cleanly. Return `None` to skip.

### `on_outcome(outcome)`

One call per resolved forward label (see [research-job.md](research-job.md) →
horizons).

| key | type | meaning |
| --- | --- | --- |
| `reference_timestamp` | str | time the label is measured relative to |
| `future_price` | float | price at the horizon |
| `reference_price` | float | present when known |
| `outcome_return` | float | `(future-ref)/ref` when reference is known |
| `horizon_seconds` | float | the horizon |
| `observed_at` | str | when the label resolved |

Outcomes MUST only be used for labels, never to shape future feature state.

### `finalize()`

Return `None` or a dict:

- `rows`: list of row dicts appended to the experiment output.
- `metrics`: dict of scalar metrics, written to `<experiment>_metrics.json`.
- `metadata`: dict[str, str] written into the run metadata.

All rows (`on_observation` returns + `finalize` rows) plus engine counters
(`observation_count`, `outcome_count`, `row_count`) appear in the experiment
output:

```
<outputRoot>/<jobId>/experiment/python_strategy.csv
<outputRoot>/<jobId>/experiment/python_strategy_metrics.json
```

## Example strategy

A runnable copy lives at `docs/examples/python/sma_cross.py`; the same code
inline for reference:

```python
# sma_cross.py
import statistics

class Strategy:
    def initialize(self, context):
        self.history = []
        self.decisions = 0

    def on_observation(self, observation, features):
        self.history.append(observation["close"])
        if len(self.history) < 20:
            return None

        window = self.history[-20:]
        if len(window) == 20 and sum(window) / 20 < observation["close"]:
            self.decisions += 1
            return {"decision": "buy", "close": observation["close"], "mid_feature": features.get("mid_price", 0.0)}

    def finalize(self):
        return {"metrics": {"decisions": self.decisions}, "metadata": {"strategy": "sma_cross"}}
```

## Requirements and limits

- **Python runtime.** The engine loads scripts through pythonnet (package
  `QuantConnect.pythonnet`, pinned in `Common`/`Research.Engine`). pythonnet
  does not auto-detect per-user Python installs, so point it at your DLL before
  running any python job:

  ```powershell
  $env:PYTHONNET_PYDLL = "C:\Users\KONZA\AppData\Local\Programs\Python\Python313\python313.dll"
  ```

  (pythonnet 2.0.66 was verified here against Python 3.13.) Without a usable
  runtime the job fails with a clear `StrategyScriptException`; the engine and
  all non-python experiments are unaffected. To use a virtual environment, set
  `PYTHONNET_PYDLL` to the base install and activate it from `initialize`
  (see `QuantConnect.Python.PythonInitializer.ActivatePythonVirtualEnvironment`).

- **Return values are JSON-serialized** on the way back. Return plain Python
  values only (`float`/`int`/`str`/`bool`/`None`/`list`/`dict`). numpy scalars
  must be unwrapped with `.item()`. Do not return `float('nan')`/infs in rows.

- **Inputs are plain dicts.** `timestamp` values are ISO-8601 strings; prices
  are `float`. Decimals and `DateTime` are converted on the boundary.

- **One script per job.** A job runs a single `Strategy` instance across all
  symbols (observations stream per symbol; rows carry `symbol` to disambiguate).

- **Fail-fast.** An unhandled exception inside any hook fails the whole job with
  the Python traceback in the error message.

- **Performance.** Objects are passed per observation without copying arrays.
  Keep hot-path work lean; batched number-crunching (numpy/pandas) belongs in a
  per-script store warmed in `initialize` and consumed in `finalize`.

## Tests

- `Tests/Research/EngineTests/PythonStrategyTests.cs` — the experiment/host
  contract with a fake host (contract, auto-fill, executor end-to-end).
- `Tests/Research/EngineTests/PythonNetStrategyIntegrationTests.cs` — the real
  pythonnet host driving a real CPython; skipped automatically when no runtime
  is available.