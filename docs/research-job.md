# Research Jobs

A research job is a plain JSON object serialized from `ResearchJob`
(`Research\Engine\Jobs\ResearchJob.cs`). The runner deserializes it
case-insensitively, resolves physical data/output roots, and executes it.

## Schema

```jsonc
{
  // Logical identity / content
  "jobId": "my-job",                        // default: random 8-char id
  "dataset": "crypto",
  "symbols": ["BTCUSDT"],
  "assetClass": "crypto",                    // crypto | fx | equity | future | cfd | index | option | cryptofuture
  "venue": "bybit",                          // bybit | binance | coinbase | oanda | usa | cme; else derived from asset class
  "resolution": "Minute",                    // Lean resolution (minute data arranged per day)
  "startTime": "2022-12-13T00:00:00",
  "endTime": "2022-12-13T23:59:59",

  // Replay / observation
  "eventTypes": ["Trade", "Quote"],          // see MarketEventType
  "observationInterval": "00:01:00",         // TimeSpan "c" format; null = event-driven (one obs per event)
  "fillForward": true,                       // emit periods that received no events, carrying state forward
  "maxObservations": 0,                      // cap on observations per symbol; 0 = unlimited
  "gridAnchor": null,                        // grid phase; inferred from the run's first chunk when null
  "maxEvents": 0,                            // 0 = unlimited
  "reorder": "FullSort",                     // FullSort | InOrderStreaming

  // Features to compute per observation (see features.md)
  "features": ["mid_price", "spread", "trade_volume"],

  // Extra measurement columns appended to every observation row, and handed to a python
  // script as observation["raw"]. Names come from the raw-field namespace (see features.md);
  // matched case-insensitively. Default [].
  "rawFields": ["bid_price", "ask_price", "depth", "trade_flow"],

  // Experiment + delayed labels
  "experimentName": "dry-run",               // REQUIRED (validation fails if empty)
                                             //   dry-run | liquidity_trend | hypothesis | python_strategy
                                             //   or a comma list -> runs all over one replay (composite)
  "experimentConfig": {},
  "strategyScript": "",                      // REQUIRED for python_strategy (absolute/.py path)
  "horizons": ["5m"],                        // optional delayed-label horizons (time-based)

  // Python payload contract (python_strategy only; see python-strategies.md)
  "scriptHistoryPeriods": 0,                 // previous periods per symbol as observation["history"]; 0 = off
  "scriptExposeEvents": false,               // also supply observation["events"], one ordered list of all types

  // Output
  "outputFormat": "csv",                     // csv | json | parquet
  "outputLocation": "",                      // physical root; resolved by runner if blank
  "engineVersion": "1.0.0",
  "enableCheckpointing": true,
  "checkpointDirectory": ""
}
```

## Validation rules

Failing any of these throws `InvalidOperationException` (`Validate`):

- `dataset` non-empty
- at least one symbol
- `startTime < endTime`
- `observationInterval > 0`
- `maxObservations` is `0` (no cap) or positive
- `gridAnchor` requires `observationInterval` — without a grid there is no phase to anchor
- `experimentName` non-empty
- `strategyScript` non-empty when `experimentName` is `python_strategy` (full contract in
  [python-strategies.md](python-strategies.md))

These are structural: they catch a job that cannot run at all. Whether the
result is *correct* is a separate question, answered by the guardrail checks in
[validation.md](validation.md), configured through `experimentConfig`:

```json
"experimentConfig": {
  "validation.mode": "warn",
  "validation.checks": "config,degenerate"
}
```

Two cases abort regardless of `validation.mode`, because no useful output can
exist: a `features` or `rawFields` entry that does not exist, and a
`hypothesis` condition that measures something no feature can provide. Both
report the closest valid name and the change to make.

## The observation grid

`observationInterval`, `fillForward` and `maxObservations` describe one thing:
what the clock does when the data is not evenly spaced. They only make sense
together, so they are documented as a set.

Events are assigned to the first grid point at or after their timestamp, and
the period stays open until the frontier moves past it, so every event landing
in a period belongs to that period and the period is never stamped earlier than
the newest data it contains.

With `fillForward: true` (the default) a period that received no events is still
emitted, carrying the previous period's state forward and stamped
`data_quality=filled`. A period is then a fixed span of wall-clock time, so a
20-period window always covers `20 * observationInterval` no matter how quiet
the feed is. That is what makes a period count interpretable, and it is also
the setting where padding can quietly dominate a result.

With `fillForward: false` only periods containing data are emitted. Every
observation is then a real measurement, but the series is irregular: a
20-period window covers 20 *events*, not 20 intervals of time, so its span
depends on the data. Choose this when period count is a proxy for sample size
rather than for elapsed time.

Set `observationInterval` to null for event-driven mode: one observation per
event, no grid, and the data itself drives the clock.

`maxObservations` bounds the period count when the grid is much finer than the
data and the row count is set by the clock rather than by anything worth
analysing. It is a budget for the whole run, not per segment: a resumed run
spends only what the earlier segment left, so head plus tail still equals the
cap. A run that stops on a limit reports it through the `coverage` check as a
valid prefix, so a truncated file is never mistaken for a complete window.

`gridAnchor` pins the phase of the grid: periods are `gridAnchor + n x
observationInterval`. It is inferred from the effective start of the first chunk,
which is what keeps a resumed run on the same grid as the run it continues; set
it explicitly to align runs that start at different times.

An explicit anchor need not coincide with `startTime`. The first period opens at
the first grid point at or after the start, so no period is emitted before the
anchor and every event falls in the period the anchor defines. Naming
`startTime` explicitly is identical to omitting the field.

Whichever combination you pick, the `freshness` and `coverage` checks report
what the grid actually did rather than leaving you to infer it from the row
count. See [validation.md](validation.md).

### Live runs and quiet markets

In backtest the frontier moves because events arrive. A live feed has no end of
data, and a quiet market produces no events at all, so a frontier that only
advances on events would stall until trading resumed — the live run would
publish nothing during the quiet stretch, and a strategy waiting on the next
period would wait for as long as the market is uninteresting.

Live runs therefore also carry a **clock**: a tick is emitted on every
observation-grid boundary, which closes the periods that have fully elapsed and
publishes them as `data_quality=filled`. Consequences worth knowing:

- A live run over the same events as a backtest produces the same periods with
  the same qualities. The clock changes *when* a period is published, never
  which periods exist or what they contain.
- Ticks are not data. They are not counted as events, never enter an
  observation's event lists, and are never exposed to a strategy — a quiet
  period is padding, and it says so.
- Ticks land exactly on grid boundaries, so a period is never closed early and
  real events are never miscounted as arriving late.
- The clock only exists when there is a grid. With `observationInterval` set to
  null there are no periods for it to close, and a live run goes event-driven.

## Example: real Bybit BTCUSDT day (2161 events → 289 obs)

```json
{
  "jobId": "bybit-btcusdt-20221213",
  "dataset": "crypto",
  "symbols": ["BTCUSDT"],
  "assetClass": "crypto",
  "venue": "bybit",
  "resolution": "Minute",
  "startTime": "2022-12-13T00:00:00",
  "endTime": "2022-12-14T00:00:00",
  "eventTypes": ["Trade", "Quote"],
  "observationInterval": "00:05:00",
  "features": ["mid_price", "spread", "spread_bps", "trade_flow", "trade_intensity", "trade_volume"],
  "experimentName": "dry-run",
  "outputFormat": "csv",
  "reorder": "FullSort"
}
```

Run it:

```
dotnet run --project Research\Runner -- --job-file job.json --data-dir Data --output-dir .\research\results
```

Output layout:

```
<output-dir>/<jobId>/<SYMBOL>/<dataset>.csv
<output-dir>/<jobId>/manifest.json
<output-dir>/<jobId>/checkpoints/<SYMBOL>.json   (when checkpointing enabled)
```

## Horizon strings

`horizons` entries are parsed by `HorizonParser`. Accepted formats:

- unit suffixes: `s`, `m`, `h`, `d` (e.g. `60s`, `5m`, `1h`, `2d`, `0.5h`)
- bare seconds (e.g. `300`)

Invalid/empty/non-positive values throw `FormatException` (the executor fails
the job).

The `hypothesis` experiment waits a fixed number of **observations** instead of
wall-clock time (`outcome_observation_horizons: "1,5,20"`), so it resolves its
own forward labels and needs no `horizons` entry — full config keys in
`research-layer.md`.

## Feed mode ("source.mode": "feed")

`source.mode = "feed"` reads CSV files staged in the DataFeeds layout instead of
the Lean zip store; `--data-dir` is the feed root:

```
<data-dir>/<assetClass>/<provider>/<symbol>/
  bars_<seconds>.csv     timestamp_ms,open,high,low,close,volume
  trades.csv             timestamp_ms,price,size,side,trade_id
  quotes.csv             timestamp_ms,bid_price,bid_size,ask_price,ask_size
  book_updates.csv       timestamp_ms,side,price,quantity,action   (order book levels)
```

`OrderBookUpdate` must be in `eventTypes` to read `book_updates.csv`. Order book
data is what feeds depth measurements (`bid_depth`, `ask_depth`, `imbalance`,
`depth_ratio`, ...); without it those measurements are constant 0 and a
depth-based condition never fires.

No REST endpoint in this toolchain serves historical L2 deltas, so
`book_updates.csv` is produced by a websocket capture rather than a backfill:

```
python -m datafeeds capture --market crypto --provider bybit --symbol BTCUSDT ^
  --duration 600 --depth 50 --out feeds
```

This stages `feeds/crypto/bybit/BTCUSDT/book_updates.csv` in exactly the layout
above. Two properties of the capture are worth knowing, because both would
otherwise produce a file that loads and replays a wrong book rather than
failing:

- **The opening snapshot is kept.** The engine rebuilds the book by applying
  updates to an empty state, so a delta capture without its base would replay a
  book that starts empty and stays wrong until depth happens to accumulate. If
  the capture never receives a snapshot, nothing is staged and the command
  reports an error.
- **Sequence gaps are reported.** A gap means levels changed in frames the
  capture never saw, so the file is still written but the summary carries a
  warning that the book is wrong from the first gap onward.

`--depth` must be at least 50: shallower depths are point-in-time snapshots, not
deltas, and would stage a book that never changes.

A runnable example lives at
`docs/examples/hypothesis/` (`book_updates.csv` made ask-heavy, then flipping
bid-heavy, plus `trades.csv`/`quotes.csv`):

```
dotnet run --project Research\Runner -- --job-file docs\examples\hypothesis\job.json ^
  --data-dir docs\examples\hypothesis\data --output-dir .\research\results
```

## Configuration hash / reproducibility

`GetConfigurationHash()` (SHA-256) covers everything that changes the replay
ground truth: dataset, symbols, asset class, venue, resolution, start/end
times, event types, observation interval, features, `rawFields`, experiment name,
horizons, engine version, reorder mode, `strategyScript`,
`scriptHistoryPeriods` and `scriptExposeEvents` (for `python_strategy`).
`experimentConfig` is NOT hashed because it is filtered through the experiment;
if a value in it feeds the hypothesis, set it in the script itself.

It deliberately EXCLUDES physical/platform concerns that do not affect the
logical outcome:

- `outputLocation`, `outputFormat`, `enableCheckpointing`,
  `checkpointDirectory`, `jobId`, `maxEvents`, `experimentConfig`

This is what lets a checkpoint saved for a long job be resumed after moving the
job to a different output root.