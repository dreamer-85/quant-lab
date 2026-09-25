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
  "observationInterval": "00:01:00",         // TimeSpan "c" format
  "maxEvents": 0,                            // 0 = unlimited
  "reorder": "FullSort",                     // FullSort | InOrderStreaming

  // Features to compute per observation (see features.md)
  "features": ["mid_price", "spread", "trade_volume"],

  // Experiment + delayed labels
  "experimentName": "dry-run",               // REQUIRED (validation fails if empty)
                                             //   dry-run | liquidity_trend | hypothesis | python_strategy
                                             //   or a comma list -> runs all over one replay (composite)
  "experimentConfig": {},
  "strategyScript": "",                      // REQUIRED for python_strategy (absolute/.py path)
  "horizons": ["5m"],                        // optional delayed-label horizons (time-based)

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
- `experimentName` non-empty
- `strategyScript` non-empty when `experimentName` is `python_strategy` (full contract in
  [python-strategies.md](python-strategies.md))

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
depth-based condition never fires. A runnable example lives at
`docs/examples/hypothesis/` (`book_updates.csv` made ask-heavy, then flipping
bid-heavy, plus `trades.csv`/`quotes.csv`):

```
dotnet run --project Research\Runner -- --job-file docs\examples\hypothesis\job.json ^
  --data-dir docs\examples\hypothesis\data --output-dir .\research\results
```

## Configuration hash / reproducibility

`GetConfigurationHash()` (SHA-256) covers everything that changes the replay
ground truth: dataset, symbols, asset class, venue, resolution, start/end
times, event types, observation interval, features, experiment name, horizons,
engine version, reorder mode, and `strategyScript` (for `python_strategy`).
`experimentConfig` is NOT hashed because it is filtered through the experiment;
if a value in it feeds the hypothesis, set it in the script itself.

It deliberately EXCLUDES physical/platform concerns that do not affect the
logical outcome:

- `outputLocation`, `outputFormat`, `enableCheckpointing`,
  `checkpointDirectory`, `jobId`, `maxEvents`, `experimentConfig`

This is what lets a checkpoint saved for a long job be resumed after moving the
job to a different output root.