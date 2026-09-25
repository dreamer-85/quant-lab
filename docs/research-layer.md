# Research Layer

The research layer sits **on top of** the engine: it answers "what can I
measure, when should a signal fire, and does the forward outcome pay off?" while
the engine keeps owning truth — ingestion, normalization, ordering, replay,
state reconstruction, provenance, checkpointing, bounded memory, and the
observation grid. Nothing in `MarketEvent` or `Observation` was changed to carry
research concepts; research is derived data computed **from** observations.

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
- **raw** fields — `RawFieldValues` (mid price, depth, volatility, vwap, ...),
  read directly off the observation / market state.

Each entry is a `MeasurementDescriptor`: `Name`, `Kind` (Raw/Derived), `ValueType`,
`Description`, `Source` and `Dependencies`. Where a name exists in both (e.g.
`mid_price`), the derived feature wins because it is what `FeatureResult.Values`
actually contains. `MeasurementCatalog.Get(name)` throws a helpful
`KeyNotFoundException` listing the available measurements; the catalog never
invents a measurement the observation cannot provide.

## 2. Derived measurements & the dependency model

A derived measurement can declare inputs via `IFeature.Dependencies`
(`FeatureBase` returns an empty list by default). `FeatureEngine` then:

1. expands the job's selected features to their **transitive dependency closure**
   (`FromNames`), so selecting `liquidity_depletion` automatically pulls in
   everything it reads;
2. orders computation with a stable topological sort — dependencies always
   precede their dependents, cycles and references to unselected measurements are
   rejected with a clear error;
3. exposes each computed value on `FeatureContext.CurrentMeasurements`, so a
   derived feature reads its inputs with `HasMeasurement`/`GetMeasurement` instead
   of recomputing the underlying math.

`liquidity_wall`, `resistance` and `structural_imbalance` are the built-in
derived-from-derived examples; compute stays a pure function of already-measured
values, so causality is preserved mechanically.

## 3. Conditions separate "fire" from "measure"

`MeasurementCondition` parses `"measurement operator threshold"`
(e.g. `imbalance < -0.50`) with operators `< <= > >= == !=` and evaluates over a
measurement-value dictionary (`FeatureResult.Values`). A condition silently does
not fire when its measurement is absent — the job must select it via
`job.Features`. It is deliberately a tiny string grammar, not a general
expression engine: hypotheses stay one-decision-per-condition.

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