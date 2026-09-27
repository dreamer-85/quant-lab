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
staged by providers — the orchestrator prints a note and they resolve to `0.0`
features unless `book_updates.csv` is present under
`<data-dir>/<market>/<exchange>/<symbol>/`. Capture it from the venue's delta
stream before the run:

```
python -m datafeeds capture --market crypto --provider bybit --symbol BTCUSDT ^
  --duration 600 --depth 50 --out <data-dir>
```

The capture keeps the opening snapshot and refuses to stage a file without one,
because the engine rebuilds the book from an empty state (see
[research-job.md](research-job.md) → feed mode).

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
| `history_periods` | int | periods of `observation["history"]` the job asked for (0 = off) |
| `expose_events` | bool | whether `observation["events"]` is supplied |
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
| `open`, `high`, `low`, `close` | float | period OHLC — see the note below |
| `volume`, `vwap` | float | traded quantity / volume-weighted average price |
| `trade_count`, `quote_count` | int | events seen since the previous observation |
| `event_count` | int | **total** events in the period, every type |
| `data_quality` | str | `fresh`, `filled` or `missing` — whether the period got new data — see below |
| `is_filled` | bool | `True` when `data_quality` is `filled` |
| `data_age_ms` | float | ms between the period and the newest event it reflects; `-1.0` if none seen |
| `last_event_timestamp` | str | ISO-8601 of the newest event folded in; `None` if none seen |
| `last_price`, `bid`, `ask`, `mid` | float | market state snapshot |
| `spread`, `spread_bps` | float | top-of-book spread |
| `bid_size`, `ask_size` | float | top-of-book size |
| `bid_depth`, `ask_depth`, `depth_imbalance` | float | book depth / imbalance |
| `bars` | list | `{type, timestamp, open, high, low, close, volume}` |
| `trades` | list | `{type, timestamp, price, size, side, trade_id}`, `side` in `buy/sell/unknown` |
| `quotes` | list | `{type, timestamp, bid_price, bid_size, ask_price, ask_size}` |
| `orderbook` | list | `{type, timestamp, side, price, quantity, action, order_count}`, `side` in `bid`/`ask`, `action` in `add`/`modify`/`remove` |
| `orderbook_snapshots` | list | `{type, timestamp, bids, asks, best_bid, best_ask, best_bid_size, best_ask_size, mid}` |
| `custom_events` | list | `{type, timestamp, custom_type, value, data}` |
| `events` | list | every event in stream order, each with a `type` — only when `expose_events` is on |
| `bid_levels` | list | `{price, quantity}` for each reconstructed bid level, best first |
| `ask_levels` | list | `{price, quantity}` for each reconstructed ask level, best first |
| `raw` | dict | the raw fields the job requested — see below |
| `history` | list | the previous `history_periods` periods for this symbol |

Nested keys of the event dicts:

- every book level — in `orderbook_snapshots[].bids`/`.asks` and in the
  reconstructed `bid_levels`/`ask_levels` — is a pair of `price` and
  `quantity`, best level first.
- snapshot: `bids` and `asks` are those level lists. `best_bid`/`best_ask` are
  the top prices, `best_bid_size`/`best_ask_size` their quantities, and `mid`
  their average (0 when either side is empty).
- custom: `type` is always the string `"custom"` — it is the event-*class*
  discriminator, shared by every producer-defined event. The semantic label is
  `custom_type` (`Liquidation`, `Funding`, `Auction`, …), `value` is its numeric
  payload, and `data` is a pass-through dict of whatever extra keys the source
  attached (absent when empty). Filter on `custom_type`, not on `type`.


`event_count` is always present, including `0`. It is the honest total: it also
counts events that appear in none of the per-type lists, so
`len(trades) + len(quotes) + len(bars) + len(orderbook) < event_count` is a
normal, meaningful state rather than a bug.

The per-type lists are only present when the job subscribed to that event type
and at least one occurred in the period. `orderbook_snapshots` and
`custom_events` follow the same rule. `event_count` is how a script detects
"something happened that I have no list for".

Every event dict carries a `type` discriminator (`trade`, `quote`, `bar`,
`orderbook_update`, `orderbook_snapshot`, or the lowercased enum name such as
`funding`, `liquidation`, `auction`, `custom`). The `events` list is built from
the same serializer as the per-type lists, so the two can never disagree.

`order_count` is present only when the exchange published it. The field is
nullable, and a null value is dropped at the boundary rather than sent as
`None`, so always read it with `row.get("order_count")`.

#### The `events` list

Off by default because it duplicates the per-type lists and can be large on a
busy book. Turn it on in the job:

```json
{ "scriptExposeEvents": true }
```

Use it when you need one ordered stream across types — for example, replaying
trades and book updates in true arrival order, or counting how many events of
each type landed in a period.

#### `raw`: the fields the job asked for

`raw` is a dict containing exactly the names in the job's `rawFields`, resolved
the same way the observation CSV resolves them. `context["raw_fields"]` is
therefore a real availability guarantee: every name in it is readable at
`observation["raw"][name]`.

```json
{ "rawFields": ["bid_price", "ask_price", "depth", "trade_flow"] }
```

```python
flow = observation["raw"]["trade_flow"]
```

Two rules worth knowing:

- A name that collides with a computed **feature** is omitted from `raw`, because
  the feature owns the column. The value is in `features[name]` instead. Preflight
  reports the collision as an error, so a valid job never hits this.
- Names are matched case-insensitively and trimmed, so `"Bid_Price"` and
  `"bid_price"` both resolve. The dict is keyed by the name as the job wrote it.

#### `history`: previous periods

Set `scriptHistoryPeriods` in the job to receive the previous N periods for the
current symbol, so a script can compute its own indicators instead of
reimplementing a rolling window:

```json
{ "scriptHistoryPeriods": 20 }
```

```python
closes = [row["close"] for row in observation["history"]] + [observation["close"]]
sma = sum(closes) / len(closes)
```

- Oldest first, and **excludes the current period**.
- Bounded to exactly N entries, so memory and marshalling cost are predictable.
- Keyed per symbol: a multi-symbol job never leaks one symbol's periods into
  another's window.
- Each entry has `timestamp`, `data_quality`, `data_age_ms`, the OHLCV/VWAP/count
  scalars, the state prices (`last_price`, `mid`, `bid`, `ask`, `spread`,
  `bid_depth`, `ask_depth`), and every feature value for that period, keyed by
  feature name. OHLC uses the same convention as the current period, so
  `history[-1]["close"]` and the previous `observation["close"]` agree.
- `0` (the default) omits the key entirely.

Because the window is per symbol and excludes now, `len(history)` grows to N and
stays there; use it to detect a cold start (`len(history) < N`).

#### `data_quality`: which periods are real

The observation grid is uniform in wall-clock time. The data is not. A period
that receives no events still appears, carrying the previous state forward, and is
stamped `filled`:

```python
if observation["data_quality"] != "fresh":
    return                      # padding, not a measurement
```

This matters because a "20 period" window is 20 *periods*, not 20 intervals of
time. If the feed is quieter than `observationInterval`, most of those 20
periods are padding and the window quietly covers far more time than intended.
Two ways to avoid that:

- set `observationInterval` to the cadence the data actually has, so most
  periods are `fresh`; or
- set `fillForward: false`, which drops empty periods entirely. The series is then
  irregular by design, and `data_quality` is `fresh` throughout.

The engine reports the ratio itself: the `freshness` validation check warns when
more than half of all periods are padding. `missing` means the period had no data
*and* no prior state — the stream began after `startTime` — so those periods carry
no market information at all.

`last_event_timestamp` is never later than `timestamp`; the difference is
`data_age_ms`. Both exist so a script can tell a real measurement from padding
without inferring it from the numbers.


### What is not in the payload

Every event type the engine models is serialized and reachable: `Bar`, `Trade`,
`Quote`, `OrderBookUpdate` and `OrderBookSnapshot` in their own lists, and every
producer-defined event — `Funding`, `Liquidation`, `Auction` and any other — in
`custom_events` under `custom_type`. Set `scriptExposeEvents` to also get one
combined ordered `events` list. Consequences:

- There is no cumulative state: `volume`, `trade_count` and `quote_count` are
  strictly per-period, and `last_price`, `bid_depth` and `ask_depth` are
  instantaneous. The engine keeps rolling windows internally (for features) and
  hands the last N periods to the script via `history`, but it does not maintain
  an unbounded series. A script that wants more than `scriptHistoryPeriods`
  periods must accumulate it itself, as `docs/examples/python/sma_cross.py` does.
- A custom event's `data` dict is passed through as-is, so its keys are whatever
  the producing source chose. Read it with `.get()`.
- A `null` field is dropped at the boundary rather than sent as `None`, so a key
  being absent is how "not published" is expressed. This applies to nested dicts
  too, hence `row.get("order_count")`.


### `open`, `high`, `low` and `close`

These four are the only keys the payload builder post-processes, and it does
**not** simply copy `Observation`:

- `open` — the **first** event's price if it is a trade, its mid if it is a
  quote, its open if it is a bar, otherwise `0`
- `close` — the same switch applied to the **last** event
- `high` / `low` — max / min over the period's **trade** prices and **bar**
  highs/lows only; quotes contribute nothing
- then, for any of the four still `<= 0`, a fallback to the market state's
  `last_price`

So the fallback usually rescues a period that opens with a book update:
`observation["open"]` is `0.0` only when the boundary event was not a
trade/quote/bar **and** `last_price` is `0` — which is the case for a
quote-only or book-only feed that has never seen a trade, or a null state.

Two consequences still hold:

- `open`/`close` may be a quote mid while `high`/`low` are trade prices, so
  `low <= open <= high` is not guaranteed.
- because of the fallback, `observation["open"]` can differ from the raw
  `Observation.OpenPrice` the engine validates. A period the `observation`
  validation check reports as `open = 0` may still show a non-zero `open` here.
  Trust the payload, not the finding, when they disagree.

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
  `QuantConnect.pythonnet`, pinned in `Common`/`Research.Engine`).

  Most distributions ship a shared `libpythonX.Y.so` next to the interpreter, but
  pythonnet cannot find it on its own: it looks for a library named after the
  running process and otherwise fails with a bare "Failed to initialize the
  Python runtime". The engine therefore asks the `python3` on `PATH` where its
  shared library is and sets it automatically, so python jobs work without any
  environment setup.

  `PYTHONNET_PYDLL` still wins when set, because an explicit choice should not
  be second-guessed. Set it to point at a different interpreter:

  ```bash
  PYTHONNET_PYDLL=/usr/lib/x86_64-linux-gnu/libpython3.12.so.1.0 quantlab run --job job.json
  ```

  ```powershell
  $env:PYTHONNET_PYDLL = "C:\Users\KONZA\AppData\Local\Programs\Python\Python313\python313.dll"
  ```

  A statically linked interpreter genuinely has no shared library; there
  discovery finds nothing, `PYTHONNET_PYDLL` is the way in. Without a usable
  runtime the job fails with a clear `StrategyScriptException`; the engine and
  all non-python experiments are unaffected. To use a virtual environment, set
  `PYTHONNET_PYDLL` to the base install and activate it from `initialize`
  (see `QuantConnect.Python.PythonInitializer.ActivatePythonVirtualEnvironment`).

- **Return values are JSON-serialized** on the way back. Return plain Python
  values only (`float`/`int`/`str`/`bool`/`None`/`list`/`dict`). numpy scalars
  must be unwrapped with `.item()`.

  `NaN` and `Infinity` are rejected before serialization, because neither is
  valid JSON and both would otherwise reach the output as a silently corrupt
  value: returning `float('nan')` raises `StrategyScriptException` naming the
  offending key path, e.g. `rows[1].close: NaN`. Guard divisions yourself and
  return `None` or an explicit sentinel instead.

  This only applies to what the script returns. A Python value of `nan` that
  stays inside the script is fine.

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