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

## Repository layout

```
Research/
  Engine/
    Replay/           EventReplayEngine, ReplayConfiguration (grid, reorder, resume state)
    Events/           MarketEvent + typed events (Trade/Quote/Bar/OrderBook/...)
    MarketState/      state reconstruction + serialization for checkpoints
    Observations/     ObservationEngine (grid snapping)
    Features/         FeatureEngine + FeatureRegistry (mid_price, spread, depth, ...)
    Experiments/      IExperiment, ExperimentBase, DelayedLabelResolver, HorizonParser
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

Build and run the full test suite (64 tests):

```
dotnet test "Tests\Research\EngineTests\QuantConnect.Research.Engine.Tests.csproj" -c Release
```

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

See `bounded-memory.md` for profiler results (live managed heap stays ~0.2 MB
across 100K/1M/5M events).

## Tests

- `ChunkInvarianceTests` — chunked vs unchunked replay equivalence.
- `OrderingTests` (in `StreamingReplayTests`/resume tests) — determinism and
  tie-break ordering.
- `ResumeTests` — serialization round-trip, seeded continuation tail equality,
  executor resume head+tail == full run.
- `DelayedLabelTests` — horizon parsing, resolver forward-return precision,
  bounded pending queue, tail-drop on `Complete`, outcome retention cap,
  executor end-to-end determinism.