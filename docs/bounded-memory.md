# Bounded-Memory Replay and Delayed Labels

## Why bounded memory

The whole point of the streaming pipeline is to process market data larger than
RAM. The design keeps live managed memory effectively constant regardless of
dataset size by never materializing the full dataset:

- Event streams are read lazily per data file and merged k-way
  (`EventStreamMerger`) — memory is a function of the number of *open streams*,
  not the number of *events*.
- Observations are emitted on the grid and written out immediately by
  `ResearchOutputWriter` (streaming; Parquet buffered in fixed 10k-row batches).
- State reconstruction keeps only the current market state.

Replay/observation work itself streams; the two retention points that could
otherwise grow without bound are bounded explicitly:

1. `ExperimentBase` outcome retention (below).
2. `DelayedLabelResolver` pending queue (below).

## Synthetic throughput benchmark

The runner has a built-in benchmark mode that feeds a deterministic synthetic
streaming source through the full pipeline (no disk reads):

```
dotnet run --project Research\Runner -- --synthetic-benchmark 1000000
```

Observed behavior across 100K / 1M / 5M events with a 5-second observation
grid, features `mid_price/spread/trade_volume`, `InOrderStreaming`:

| events   | rate        | live managed heap                 |
| -------- | ----------- | --------------------------------- |
| 100K     | ~M events/s | ~0.2 MB                           |
| 1M       | ~M events/s | ~0.2 MB                           |
| 5M       | ~M events/s | ~0.2 MB                           |

`liveManaged` is measured after `GC.Collect()` post-run; the working-set delta
grows only because .NET does not return RSS during an allocation burst. The
steady-state heap is flat — the pipeline is O(streams) in memory, not O(dataset).

## ExperimentBase outcome retention

`ExperimentBase` (base class for user experiments) exposes:

- `MaxRetainedObservations` (default 100,000) — retained observation history;
  0 = retain nothing.
- `MaxRetainedOutcomes` (default 100,000; 0 = retain nothing) — retained
  `OutcomeData` history.
- `OutcomeCount` — live count.

`OnOutcome` inserts then evicts oldest-first (FIFO) when the cap is exceeded,
so an experiment that only needs summary statistics never accumulates history.
`RecordingExperiment` in the test suite inherits these caps and is used to prove
`OutcomeList` stays at size ≤ cap.

## Delayed labels with bounded state

Delayed labels attach each observation to a *future* outcome (e.g. the
5-minute forward return). Since the label for observation `T` is only
knowable once an observation at `T + horizon` exists, the resolver must carry
pending observations forward — but only the ones whose horizon has not yet
elapsed.

`DelayedLabelResolver` (`Research\Engine\Experiments\DelayedLabelResolver.cs`):

- Input: observations in timestamp order (`OnObservation`); yields resolved
  forward-return labels as `OutcomeData`.
- Pending queue is a **bounded FIFO whose size is a function of the horizon and
  observation interval, not the dataset**: `ceil(horizon / interval) + 1`.
  A 5-minute horizon on a 1-minute grid never holds more than 6 pending
  observations.
- Each resolved label has:
  - `ReferenceTimestamp` / `ReferencePrice` (the labeled observation),
  - `FuturePrice` (the price at `T + horizon`),
  - `ObservedAt` (`DateTime?` — when the label was realized),
  - `Horizon`, `OutcomeReturn = (FuturePrice - ReferencePrice) / ReferencePrice`.
- `Complete()` — called at end of stream — drops any observations whose horizon
  extends past the data end. The executor logs the tally, e.g.
  `...6 unresolved label(s) for BTCUSDT (horizon extends beyond data end)`.

Instruments (`LocalResearchExecutor`):

- Horizons come from `job.Horizons` (parsed by `HorizonParser`, e.g. `"5m"`).
  One resolver is created per horizon, per symbol.
- While horizons are active, resolved outcomes are delivered to the experiment
  per-observation (`experiment.OnOutcome`) and the legacy single
  end-of-run `OnOutcome` is NOT emitted (it would double-count).
- Without horizons, the legacy behavior is unchanged: one final `OnOutcome`
  holding the final state / last observation timestamp.

## Verified by tests

- `DelayedLabelResolver_Pending_BoundedByHorizonWindow` — 1000 observations @
  1-min grid, 5-min horizon → pending cap 5, 995 resolved, 5 unresolved.
- `DelayedLabelResolver_Resolves_ForwardReturn_AtHorizon` — 100 → 110 over 5
  minutes → `OutcomeReturn == 0.1`.
- `ExperimentBase_Outcomes_Capped_ByRetentionLimit` — cap keeps the last N
  outcomes (FIFO).
- `Executor_WithHorizons_DeliversResolvedLabels_BoundedAndDeterministic` —
  400 synthetic events → 68 observations → 62 resolved labels (6 unresolved),
  identical across two isolated runs.