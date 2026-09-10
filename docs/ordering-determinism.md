# Event Ordering and Determinism

## Ordering primitive

Every replayed event is a `MarketEvent` and orders against another via
`MarketEvent.CompareEvents`:

1. **Timestamp** (ascending).
2. **SequenceNumber** — only when BOTH events carry one (per-stream Lean
   sequence numbers, e.g. from certain data formats).
3. **OrderOrdinal** — a stable insertion ordinal assigned when the event is
   materialized, guaranteeing total order and determinism even when two events
   share a timestamp and neither has a sequence number.

The grunt of ordering is type-based at the source: raw Lean files are read as
per-file Cartesian/trade sub-streams and k-way merged by
`EventStreamMerger.Merge`, so memory stays bounded while the global order is
preserved.

## FullSort vs InOrderStreaming

`ReorderMode` (`ResearchJob.Reorder`) selects how the merged stream is handed
to the replay engine:

- **FullSort** — the full merged sequence is sorted once (stable), producing a
  globally ordered event stream before replay. Cheap for day-sized inputs; the
  only mode that can correctly reorder arbitrarily out-of-order data.
- **InOrderStreaming** — the per-file sub-streams are merged directly in arrival
  order and replayed as they come. Lowest latency, sub-linear memory, and the
  mode used by the streaming/benchmark pipeline.

### Verified: output is bit-identical

On real Bybit data (`Data\crypto\bybit\minute\btcusdt\20221213`: 1440 trade
bars + 721 quote bars) the two modes were re-run with identical job definitions
differing only in `reorder`:

- events processed: 2161 = 2161
- observations written: 1440 (1-minute grid) = 1440
- produced CSV bytes: **5F625A42D2D008072D41CCB5C0CC0D02A981AB389298238E6C51F95D651AF7A3**
  (SHA-256) — identical for both modes.

This is expected because Lean per-day minute files are already ordered within
each file, so in-arrival merging yields the same total order as an explicit
stable sort.

## Why total order matters

The pipeline is deterministic end-to-end:

- feature columns depend only on reconstructed `MarketState` at the observation
  grid point;
- market state depends on the exact event sequence;
- the observation grid is anchored to the job `StartTime` and `ObservationInterval`;
- delayed-label resolution (`DelayedLabelResolver`) keys off observation
  timestamps only.

As long as the event order is reproducible, the CSV/Parquet output and every
checkpoint are reproducible — which the resume machinery leans on to resume an
interrupted run with bit-identical output (`checkpointing-resume.md`).