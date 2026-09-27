# Research Layer

The research layer sits **on top of** the engine: it answers "what can I
measure, when should a signal fire, and does the forward outcome pay off?" while
the engine keeps owning truth — ingestion, normalization, ordering, replay,
state reconstruction, provenance, checkpointing, bounded memory, and the
observation grid. Nothing in `MarketEvent` or `Observation` was changed to carry
research concepts; research is derived data computed **from** observations.

Because the engine owns the observation clock, every research answer inherits it.
A condition measured at period *N* means what it means only if the grid that
produced *N* is trustworthy, so the engine publishes that trust explicitly —
`data_quality` and `data_age_ms` on every row, `last_event_timestamp` in the
Python payload, and the `grid`, `freshness` and `coverage` checks that report
when the clock and the data disagree. A "20 period" window is 20 periods of
wall-clock time, not 20 intervals of data, unless `fillForward` is off; that
distinction is a property of the data, so the engine reports it rather than
leaving each result to assume it. See [validation.md](validation.md) for the
checks and [research-job.md](research-job.md) for the grid settings.

A runnable example lives at `docs/examples/hypothesis/`:

```
docs/examples/hypothesis/
  job.json                  hypothesis job (condition "imbalance < -0.50")
  data/crypto/binance/BTCUSDT/
    book_updates.csv        ask-heavy for 20 min, then bids take over
    trades.csv              rising buys (drives the realized returns)
    quotes.csv              1-min top-of-book snapshots
```

```
dotnet run --project Research\Runner -- --job-file docs\examples\hypothesis\job.json ^
  --data-dir docs\examples\hypothesis\data --output-dir .\research\results
```

The experiment output lands at `<output-dir>/<jobId>/experiment/hypothesis.csv`
(signal rows with `ret_o{1,5,20}` / `resolved_o{1,5,20}`) next to a
`hypothesis_metrics.json` (trigger count, per-horizon count / mean return /
up rate / continuation rate).

### Run layout

```
<output-dir>/<jobId>/
  manifest.json             runner summary: the local/cloud parity surface for `quantlab compare`
  run_metadata.json         what the run consumed and produced (always written)
  validation_report.json    validation findings (only when validation is enabled)
  checkpoints/              replay checkpoints, when enableCheckpointing is on
  BTCUSDT/
    crypto.parquet          observation rows (features + rawFields columns)
  experiment/
    python_strategy.csv           rows returned by the script
    python_strategy_metrics.json  metrics the script returned
```

`manifest.json` and `run_metadata.json` overlap on purpose and are not
interchangeable. `manifest.json` is the small, stable field set that
`quantlab compare` checks between a local and a cloud run, so it holds only
values that must match. `run_metadata.json` is the audit record and deliberately
includes things that *cannot* match — absolute roots, output paths, wall-clock
timing — which is why `quantlab compare` excludes it from byte comparison
alongside `manifest.json`. Do not put run-local detail in `manifest.json`: it
would report a false parity failure.

`run_metadata.json` is written whether the run succeeded or failed. The job file
alone cannot answer "what did this actually consume?", because defaults,
environment roots and the resolved source configuration are settled at run time.
It records:

- `jobId`, `configurationHash`, `succeeded`, `error`, `engineVersion`
- the resolved window: `symbols`, `startTime`, `endTime`, `resolution`,
  `observationIntervalSeconds`, `fillForward`, `maxObservations`, `gridAnchor`,
  `eventTypes`, `features`, `rawFields`, `horizons`
- `dataRoot` / `outputRoot` actually used, and the resolved `source` block
  (`mode`, `provider`, `orderBookDepth`, `pageSize`, endpoints, archive path) —
  which is how you tell a `feed` run from a `live` one after the fact
- `pythonContract` (`historyPeriods`, `exposeEvents`): the payload contract the
  script ran under
- `validation` (enabled, mode, checks, observation count, finding count)
- `stats` (`eventsProcessed`, `observationsWritten`, `symbolsProcessed`,
  `symbolsReused`, `outputFiles`), `experiment` (name, metrics, metadata) and
  `timing`

`schemaVersion` is `1` and will be bumped if the shape changes.

---

## Responsibilities (what stays where)

| Concern                    | Owned by                                             |
| -------------------------- | ---------------------------------------------------- |
| Market facts / events      | `MarketEvent` + typed events (`Events/`)              |
| Replay ordering, state     | `EventReplayEngine`, `MarketState`                    |
| Provenance, checkpoints    | `DataProvenance`, `ReplayCheckpoint` / `ReplayCheckpointManager` |
| Observation boundary       | `Observation` (timestamp, state, period events)      |
| What CAN be measured       | `MeasurementCatalog` + `MeasurementDescriptor`       |
| How values are computed    | `FeatureRegistry` (derived) + `RawFieldValues` (raw) |
| Dependency ordering        | `FeatureEngine` (topo sort + transitive closure)     |
| "When should I look?"      | `MeasurementCondition` + `HypothesisExperiment`      |
| "What happened later?"     | `ObservationCountLabelResolver` / `DelayedLabelResolver` |
| "Was it worth it?"         | hypothesis evaluation metrics + `ExperimentResult`   |
| Many hypotheses at once    | `CompositeExperiment`                                |

## 1. Measurement discovery

`MeasurementCatalog.Discover()` returns every measurement the research layer can
request today, unified from two existing namespaces:

- **derived** measurements — the feature registry (`job.Features`), computed per
  observation through the feature engine;
- **raw** fields — `RawFieldValues` (mid price, depth, vwap, trade count, ...),
  read directly off the observation / market state.

Each entry is a `MeasurementDescriptor`: `Name`, `Kind` (Raw/Derived), `ValueType`,
`Description`, `Source` and `Dependencies`. Where a name exists in both (e.g.
`mid_price`), the derived feature wins because it is what `FeatureResult.Values`
actually contains. `MeasurementCatalog.Get(name)` throws a helpful
`KeyNotFoundException` listing the available measurements; the catalog never
invents a measurement the observation cannot provide.

The raw namespace is exactly the 19 fields in `RawFieldValues.Descriptors`:
`symbol`, `timestamp`, `mid_price`, `bid_price`, `ask_price`, `last_price`,
`spread`, `spread_bps`, `depth`, `bid_depth`, `ask_depth`, `volume`,
`trade_count`, `vwap`, `open_price`, `high_price`, `low_price`, `close_price`,
`trade_flow`. There is no `volatility` raw field; compute realized volatility
yourself in an experiment if you need it.

Raw field values are types, not just numbers: `trade_count` is an `int` and
`symbol`/`timestamp` are `string`/`DateTime`. Only decimal-valued fields become
numeric output columns.

## 2. Derived measurements & the dependency model

A derived measurement can declare inputs via `IFeature.Dependencies`
(`FeatureBase` returns an empty list by default). `FeatureEngine` then:

1. expands the job's selected features to their **transitive dependency closure**
   (`FromNames`), so selecting a feature that declares dependencies automatically
   pulls in everything it reads;
2. orders computation with a stable topological sort — dependencies always
   precede their dependents, and cycles are rejected with a clear error;
3. exposes each computed value on `FeatureContext.CurrentMeasurements`, so a
   derived feature reads its inputs with `HasMeasurement`/`GetMeasurement` instead
   of recomputing the underlying math.

**No built-in feature currently declares a dependency.** Every feature in the
registry reads the observation and reconstructed market state directly, so
`Dependencies` is empty for all 21 of them and the closure in step 1 is a no-op
for stock jobs. The mechanism exists for custom and future features, and is
covered by `FeatureDependencyTests`, but do not assume selecting a built-in
feature computes some other built-in feature as a side effect.

Where built-in features look related, they are independent computations over the
same state, not a pipeline. `liquidity_wall`, `resistance` and
`structural_imbalance` each walk the book levels themselves; none of them reads
`depth` or `imbalance`.

Data dependencies that *do* exist are about inputs, not features: a depth
feature needs order book data, and without it returns `0` for every observation.
The `config` validation check reports that as an error rather than letting a
constant-zero column pass as a result.

## 3. Conditions separate "fire" from "measure"

`MeasurementCondition` parses `"measurement operator threshold"`
(e.g. `imbalance < -0.50`) with operators `< <= > >= == !=` and evaluates over a
measurement-value dictionary (`FeatureResult.Values`). It is deliberately a tiny
string grammar, not a general expression engine: hypotheses stay
one-decision-per-condition.

A condition whose measurement is not in `FeatureResult.Values` never fires, so
the two failure modes are distinct and both matter:

- the job did not select a measurement that **is** a registered feature — a
  mistake, reported by the `config` validation check as an error that names the
  missing feature and the exact job change to make;
- the measurement is a **raw field** (`trade_count`, `vwap`, `open_price`, ...)
  rather than a feature — also an error, because conditions only see feature
  values and a raw field cannot be added to `job.features` to fix it. The check
  says so, instead of suggesting a change that would fail the run.

Before this check existed, both cases produced an empty experiment output and a
successful run, which reads as "the hypothesis was rejected" rather than "the
condition was impossible". See `validation.md`.

## 4. Current observation vs future outcome

The same rule as before holds: a signal row sees **only** its own observation's
measurements. Forward returns are resolved by a separate, bounded mechanism:

- `ObservationCountLabelResolver` resolves a label N **observations** later
  (for count-based outcomes), mirroring `DelayedLabelResolver`'s discipline
  (bounded pending queue sized by the step count, tail dropped at stream end);
- the engine's `DelayedLabelResolver` covers wall-clock horizons (`job.Horizons`
  such as `"5m"`).

Outcome data flows through the same `OnObservation`/`OnOutcome`/`Finalize`
contract, so no future information can leak into a feature or a signal.

## 5. The `hypothesis` experiment

`docs/research-job.md` schema + config keys:

| Config key                     | Meaning                                                  |
| ------------------------------ | -------------------------------------------------------- |
| `condition` (required)         | e.g. `imbalance < -0.50`                                 |
| `signal` (optional)            | label on every row where the condition holds; defaults to a sanitized condition text |
| `outcome_observation_horizons` | `"1,5,20"` — comma list of observation counts            |
| `direction` (optional)         | `up` / `down` / `none` — continuation direction for `*_continuation_rate` |

Rows are emitted **only** for triggering observations, carrying the raw
measurement values plus `ret_o{n}` / `resolved_o{n}` per horizon. Metrics
(namespaced by signal):

- `{signal}_trigger_count`
- `{signal}_o{n}_count`, `_mean_return`, `_up_rate` and (when `direction` set)
  `_continuation_rate`

## 6. Multiple experiments over one replay

`experimentName` accepts a comma-separated list —
`"hypothesis,liquidity_trend"` — and `ExperimentFactory` wraps them in a
`CompositeExperiment`. The executor builds **one** replay and **one** shared
feature engine, then fans every observation/outcome out to each experiment, so
running ten hypotheses costs the same replay as running one. Result rows get an
`experiment` column; metrics are namespaced `experimentName.metricName`.

An unknown name inside a list is rejected (silently dropping an experiment would
void the run); a single unknown name keeps the legacy "experiment disabled"
behavior (`null`).

## 7. Python as a future research interface

The abstractions above are Python-compatible by construction: measurement names
are plain strings that a Python script already receives (`features` dict in
`on_observation`), conditions are strings, descriptors are serializable, and the
experiment contract (`IExperiment`) is the same seam `python_strategy` uses
today. The engine does **not** depend on Python; pythonnet is loaded only by the
experiment that explicitly opts in (`docs/python-strategies.md`).

## Where the research layer lives

```
Research/Engine/
  Features/MeasurementDescriptor.cs    metadata for one measurement
  Features/MeasurementCatalog.cs       discovery + Get + help text
  Features/FeatureEngine.cs            dependency ordering, closure, CurrentMeasurements
  Experiments/MeasurementCondition.cs  "imbalance < -0.50" parser/evaluator
  Experiments/ObservationCountLabelResolver.cs  N-observation forward labels
  Experiments/HypothesisExperiment.cs  condition → signal → outcome → evaluation
  Experiments/CompositeExperiment.cs   fan-out over one replay
  Experiments/ExperimentFactory.cs     "hypothesis,liquidity_trend" → composite
```