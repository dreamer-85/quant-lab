# Checkpointing and Resume

## Model

With `enableCheckpointing = true`, the executor snapshots market state during a
run so an interrupted job can be resumed exactly:

- **In-progress snapshots** are written every 25,000 observations (and at job
  end) as `<outputRoot>/<jobId>/checkpoints/<SYMBOL>.json`.
- Each snapshot (`ReplayCheckpoint`) stores:
  - the job `configurationHash`,
  - `lastObservationTimestamp` (the run boundary),
  - `observationsWritten` / `eventsProcessed`,
  - `completed` (false while running, true at the end),
  - `stateJson` — the full reconstructed `MarketState` at the boundary.
- On a later run, the executor loads any in-progress checkpoint for the symbol,
  validates the configuration hash, and **resumes from the boundary** instead of
  replaying from scratch.

## What a checkpoint contains and why that matters

A persisted `stateJson` **includes** every event up to and including the
boundary timestamp (the state snapshot is taken after applying the boundary
event). Therefore a resumed run must NOT re-apply boundary events — doing so
double-counts them (book/trade volumes, spreads, etc. would be wrong).

This differs from the **chunking** machinery used for chunk-invariance tests:
a chunk boundary assigns the boundary-timestamp events to the NEXT chunk, so
the carried state intentionally EXCLUDES boundary events and the engine
re-applies them exactly once.

## Resume semantics

When resuming from `lastObservationTimestamp` (boundary `B`):

1. `StartTime = B + 1 tick` — events at `ts <= B` are skipped entirely (they are
   already folded into the restored state).
2. `InitialState = restored market state` — replay begins with the saved
   snapshot.
3. `InitialNextObservationTime = next grid point strictly after B`, anchored to
   the original job `StartTime` and `ObservationInterval` (helper
   `ResumeNextObservationTime`). The first emitted row is the observation at
   that next grid point — the boundary observation is NOT re-emitted.
4. `InitialEventsProcessed = saved eventsProcessed` so counts stay continuous.
5. Replaying resumes the ordinary loop; output continues from where the
   interrupted run stopped.

The final (completed) checkpoint is written at the end of the resumed run, just
like a normal run.

## Guarantee

The combination of skip-boundary + grid-aligned continuation produces output
**bit-identical to an uninterrupted run**. Verified by tests:

- `Resume_SerializationRoundTrip_PreservesAllState`
- `Resume_SeededContinuation_MatchesFullRun_Tail` — seeded state + main-state
  tail equality against the reference run.
- `Resume_ExecutorResume_HeadPlusTail_EqualsFull` — executor-level check: head
  result + resumed tail == full-run result (same event totals, same rows after
  the boundary, first tail row exactly at `boundary + interval`).

## Reuse shortcut

If a checkpoint is already `completed` and the output file exists, the executor
prints `Reusing completed checkpoint for <SYMBOL>...` and counts it as a
`symbolsReused` in the manifest — a cheap no-op rerun. For experiments/benchmarks
this can silently skip work:

- **Tests** set a fresh per-`OutputLocation` root per run, or disable
  checkpointing, so the reuse path never hides real work.
- A job whose `outputLocation` is left blank falls back to the shared
  environment output root; a completed checkpoint left there from an earlier
  run can cause "0 observations" runs. Always point jobs at an isolated output
  root.

## Configuration hash and resume validation

`GetConfigurationHash()` excludes `outputLocation`, `outputFormat`,
`enableCheckpointing`, `checkpointDirectory`, `jobId`, `maxEvents`, and
`experimentConfig`. That lets a checkpoint survive being moved to a new output
root, while still invalidating on any change that alters replay ground truth
(dataset, symbols, times, resolution, event types, observation interval,
features, experiment name, horizons, engine version, reorder mode).