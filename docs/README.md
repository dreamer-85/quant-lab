# QuantLab Research Engine

A streaming, bounded-memory market-research pipeline for the Lean engine.
It replays raw Lean market data (trades, quotes, L1/L2 book) as a hyper-fast
event stream, reconstructs market state observation-by-observation, computes
feature vectors, optionally tags each observation with delayed (future-return)
labels, and streams results to disk — CSV/JSON/Parquet — without ever loading
the dataset into memory.

## Highlights

- **Single-pass streaming replay.** Raw Lean data files are streamed via
  `LeanDataEventSource` / `SyntheticStreamingEventSource`, merged (`EventStreamMerger`),
  reordered (FullSort or InOrderStreaming), and replayed by `EventReplayEngine`.
  No full-dataset load; live managed heap stays bounded (see *Memory model*).
- **FullSort == InOrderStreaming, bit-identical.** Both reorder modes reproduce
  the exact same output on real Bybit data (verified byte-for-byte; see
  `ordering-and-determinism.md`).
- **Deterministic and reproducible.** Events compare by
  `(Timestamp, SequenceNumber, OrderOrdinal)`; the same job always yields the
  same CSV/state. A job configuration hash identifies the exact replay.
- **Checkpoint and resume.** In-progress market state is snapshotted every
  25,000 observations; an interrupted run resumes from the boundary with
  bit-identical output to an uninterrupted run (`checkpointing-resume.md`).
- **Delayed labels with bounded state.** `Horizons` (e.g. `"5m"`) attach each
  observation to future-return labels resolved when the horizon elapses — with
  a bounded pending queue, never a dataset-sized buffer.
- **Pluggable features and experiments.** Features register by name in
  `FeatureRegistry`; experiments consume observations/outcomes through
  `IExperiment`.
- **Python strategy scripts.** `python_strategy` experiments run a user-written
  `.py` file (Lean-style `initialize` / `on_observation` / `on_outcome` /
  `finalize` hooks) over the observation/feature stream — strategy logic stays
  entirely in Python (`python-strategies.md`). One-script research bundles the
  whole flow: a `Research` class (or a `quantlab.research.ResearchStrategy`
  subclass you call `.run()` on) declares the exchange/symbols/features, and
  `quantlab research <script.py>` pulls the data, generates the job, and runs
  the strategy; `quantlab run cloud` uploads the strategy script + job to the VM
  for the reusable-library workflow (`docs/examples/python/research_binance.py`,
  `docs/examples/python/research_strategy_api.py`).
- **Research layer on top of the engine.** A measurement catalog unifies the
  feature/raw-field namespaces with metadata, declarative `hypothesis`
  experiments test "condition → signal → forward return after N observations"
  against the existing replay, and `composite` runs multiple experiments over one
  replay (`research-layer.md`). The dependency-ordering machinery is available
  for custom features; no built-in feature declares one.
- **Validation guardrails.** Configurable checks run alongside replay and
  separate a job that went wrong from a hypothesis that was simply not
  supported: a flow identity that catches an unsigned `trade_flow`, a
  constant-column check that catches a condition fed by data the job never
  received, and preflight checks that name the missing feature and the fix. Off,
  warn (default), or fail, per job (`validation.md`).

## Repository layout

```
Research/
  Engine/
    Replay/           EventReplayEngine, ReplayConfiguration (grid, reorder, resume state)
    Events/           MarketEvent + typed events (Trade/Quote/Bar/OrderBook/...)
    MarketState/      state reconstruction + serialization for checkpoints
    Observations/     ObservationEngine (grid snapping)
    Features/         FeatureEngine + FeatureRegistry + MeasurementCatalog (measurement layer)
    Experiments/      IExperiment, ExperimentBase, DelayedLabelResolver, HorizonParser,
                      HypothesisExperiment, CompositeExperiment, MeasurementCondition, Python/
    Execution/        LocalResearchExecutor (orchestrates per-symbol runs)
    LocalData/        LeanDataEventSource, SyntheticStreamingEventSource, EventStreamMerger
    Storage/          LocalFileStore, ResearchOutputWriter, ReplayCheckpointManager
    Infrastructure/   ResearchEnvironment, LeanBootstrap
    Jobs/             ResearchJob (the JSON-able job definition)
  Runner/             CLI: run a job from a JSON file
Tests/Research/EngineTests/   NUnit test suite
docs/                 This documentation
```

## Quick start

Build and run the full test suite (160 tests):

```
dotnet test "Tests\Research\EngineTests\QuantConnect.Research.Engine.Tests.csproj" -c Release
```

> Python-bridge tests skip automatically when the Python runtime is not
> configured; to include them, set `PYTHONNET_PYDLL` to a Python 3.x DLL and use
> pythonnet `2.0.66` (see `python-strategies.md`).

Run a job from JSON against real data:

```
dotnet run --project Research\Runner -- --job-file job.json --data-dir Data --output-dir out
```

Run a synthetic throughput benchmark (N = number of events):

```
dotnet run --project Research\Runner -- --synthetic-benchmark 1000000
```

## CLI reference

`QuantConnect.Research.Runner --job-file <path.json> [--data-dir <path>] [--output-dir <path>]`

| Argument                  | Description                                                        |
| ------------------------- | ------------------------------------------------------------------ |
| `--job-file <path.json>`  | Required. JSON research job (see `research-job.md`).               |
| `--data-dir <path>`       | Root of Lean raw data (default: `QUANTLAB_DATA_ROOT` or Lean Data).|
| `--output-dir <path>`     | Root for results/checkpoints (default: `QUANTLAB_OUTPUT_ROOT` or `%TEMP%\QuantLab`). |
| `--synthetic-benchmark N` | Run the streaming pipeline over a deterministic synthetic source of N events. |

Run as a Cloud Run **service** (HTTP front end) instead of a one-shot CLI:

```
dotnet run --project Research\Runner -- --web [--port 8080] [--data-dir] [--output-dir]
```

| Endpoint       | Description                                                        |
| -------------- | ------------------------------------------------------------------ |
| `GET /healthz` | Startup/health probe target (`200 {"status":"ok"}`).               |
| `GET /`        | Service info JSON (name, mode, port, resolved dirs).               |
| `POST /run`    | Body = job JSON (same schema as a job file). Executes it and returns JSON with `succeeded`, counts, output files, manifest path. |
| `POST /run-job-file` | Body = `{ "jobFile": "...", "dataDir": "...", "outputDir": "..." }`. Runs a job already present on disk. |

The same image is used for Cloud Run jobs and services. If `--job-file` (or
`--synthetic-benchmark`) is present among the arguments the runner ignores
`--web` and runs the CLI; a service spec (no job args) gets the web server.
The web server binds `0.0.0.0:${PORT}` (defaults to `--port` then `PORT` env, then 8080),
which satisfies Cloud Run's startup probe on port 8080.

The job file itself carries only logical content (dataset, symbols, times,
features, horizons, experiment); physical roots are resolved by the runner/environment.
Exit code 0 = success, 1 = failed job, 2 = usage error.
A `manifest.json` is written next to the outputs summarizing
symbols/events/observations/reuse counts and elapsed time.

### Unified orchestration CLI

`Research/Python/quantlab` (`python -m quantlab`) wraps the Runner for local
and cloud execution, and adds bundle staging, GCS upload/download, and
local-vs-cloud comparison. It reads the same `deploy/cloud/environment` file as
the bash deploy scripts:

```
quantlab run local <job.json> [--build] [--data-dir] [--output-dir]
quantlab run cloud <job.json> [--vm] [--zone] [--project] [--no-scp]
quantlab bundle build --data-root <lean-data> --out <bundle.tgz>
quantlab bundle upload <bundle.tgz>
quantlab results download <job-id> [--dest DIR]
quantlab compare <local-result> <cloud-result> [--no-files] [--deep]
quantlab env [--show]
```

See `deploy/cloud/README.md` for the workflow and `cloud-research.md` for the
cloud runbook.

## The pipeline

1. **Source** — `IEventDataSource.GetEventStreams(job, symbol)` yields ordered
   sub-streams (one per Lean data file) of raw events, or a synthetic generator.
2. **Merge** — `EventStreamMerger.Merge` k-way merges the sub-streams by
   `CompareEvents` order.
3. **Reorder** — if `ReorderMode.FullSort`, the full merged stream is sorted once
   (stable; timestamps + per-stream sequence when present + insertion ordinal);
   if `ReorderMode.InOrderStreaming`, streams are merged in arrival order. Both
   produce identical output (see `ordering-and-determinism.md`).
4. **Replay** — `EventReplayEngine` walks the ordered stream, reconstructs
   `MarketState` for each event, and emits an observation whenever the engine
   advances to the next grid tick (or past the last data event).
5. **Observe** — `ObservationEngine` snaps each emission to the observation grid;
   `FeatureEngine` computes the requested feature columns.
6. **Output** — each observation row is streamed via `ResearchOutputWriter`
   (CSV/JSON/Parquet), then handed to the experiment (`OnObservation`) and any
   delayed-label resolvers (`OnOutcome`).
7. **Checkpoint** — market state is serialized every 25,000 observations to
   enable exact resume.

## Bounded memory (summary)

The pipeline never holds the dataset in memory:

- Event streams are pulled and merged incrementally (`EventStreamMerger`).
- Observations are emitted at grid cadence and written immediately.
- `ExperimentBase` retains at most `MaxRetainedOutcomes` outcomes (FIFO, default
  100,000); 0 retains nothing.
- `DelayedLabelResolver` keeps only the observations whose horizon has not yet
  elapsed — bounded by `ceil(horizon / observationInterval) + 1` entries, not by
  dataset length.
- Validation findings are aggregated per distinct problem, and the `degenerate` /
  `duplicate` checks keep one counter and one hash per output column — bounded by
  the job's column count, not by dataset size.

See `bounded-memory.md` for profiler results (live managed heap stays ~0.2 MB
across 100K/1M/5M events).

## Documentation

| Document | Covers |
| -------- | ------ |
| [research-job.md](research-job.md)        | Job schema, validation rules, config hash, feed mode |
| [features.md](features.md)                | Every selectable feature, its output column, and its parameters |
| [validation.md](validation.md)            | Guardrail checks, `off`/`warn`/`fail` modes, report format |
| [research-layer.md](research-layer.md)    | Measurement catalog, conditions, outcomes, composite runs |
| [python-strategies.md](python-strategies.md) | The four-hook Python contract and the observation payload |
| [ordering-determinism.md](ordering-determinism.md) | FullSort vs InOrderStreaming equivalence proof |
| [checkpointing-resume.md](checkpointing-resume.md) | Resume semantics and equivalence |
| [bounded-memory.md](bounded-memory.md)    | Memory model and profiler results |
| [cloud-research.md](cloud-research.md)    | Cloud Run deployment |

## Tests

- `ChunkInvarianceTests` — chunked vs unchunked replay equivalence.
- `OrderingTests` (in `StreamingReplayTests`/resume tests) — determinism and
  tie-break ordering.
- `ResumeTests` — serialization round-trip, seeded continuation tail equality,
  executor resume head+tail == full run.
- `DelayedLabelTests` — horizon parsing, resolver forward-return precision,
  bounded pending queue, tail-drop on `Complete`, outcome retention cap,
  executor end-to-end determinism.
- `MeasurementCatalogTests` — measurement discovery/deduplication/metadata.
- `FeatureDependencyTests` — dependency ordering, transitive closure, cycle and
  missing-dependency rejection.
- `HypothesisTests` — condition parsing, count-based label resolution,
  hypothesis metrics/rows, composite fan-out, factory naming.