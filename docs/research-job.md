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
  "experimentConfig": {},
  "horizons": ["5m"],                        // optional delayed-label horizons

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

## Configuration hash / reproducibility

`GetConfigurationHash()` (SHA-256) covers everything that changes the replay
ground truth: dataset, symbols, asset class, venue, resolution, start/end
times, event types, observation interval, features, experiment name, horizons,
engine version, and reorder mode.

It deliberately EXCLUDES physical/platform concerns that do not affect the
logical outcome:

- `outputLocation`, `outputFormat`, `enableCheckpointing`,
  `checkpointDirectory`, `jobId`, `maxEvents`, `experimentConfig`

This is what lets a checkpoint saved for a long job be resumed after moving the
job to a different output root.