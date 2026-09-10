# Features

Features are computed per observation by `FeatureEngine`. The engine takes the
job's `features` list and instantiates each named feature via `FeatureRegistry`
(registration keys are case-insensitive). Unknown feature names throw
`KeyNotFoundException`.

## Available features

### Price

| Feature             | Description                                             |
| ------------------- | ------------------------------------------------------- |
| `mid_price`         | Midpoint of best bid/ask; falls back to last trade price |
| `spread`            | Bid-ask spread (absolute)                                |
| `spread_bps`        | Bid-ask spread in basis points relative to mid price     |

### Trade / flow

| Feature            | Description                                          |
| ------------------ | ---------------------------------------------------- |
| `trade_flow`       | Signed trade flow (buy minus sell volume)            |
| `cumulative_flow`  | Running cumulative signed flow                       |
| `trade_intensity`  | Trade count/size per observation window              |
| `trade_volume`     | Total traded volume in the observation window        |

### Depth (L2 book)

| Feature                    | Description                                   |
| -------------------------- | --------------------------------------------- |
| `depth`                    | Total volume in the order book                |
| `bid_depth`                | Total volume on the bid side                  |
| `ask_depth`                | Total volume on the ask side                  |
| `imbalance`                | Bid-ask volume imbalance                      |
| `depth_ratio`              | Ratio of bid to ask depth                     |
| `liquidity_wall`           | Largest single level / wall detection         |
| `resistance`               | Price level resistance proxy                  |
| `structural_imbalance`     | Venue/level structural imbalance              |
| `liquidity_depletion`      | Rate of book depletion                        |
| `replenishment_rate`       | Rate of book replenishment                    |
| `depth_persistence`        | Persistence of depth levels across observations |

Depth features require `OrderBookEvent` data (L2 snapshots) to be meaningful;
the exact column set depends on which features are selected.

## Adding a feature

Implement `IFeature` (see `FeatureBase` for the base class — it provides a
`Name` property and integrates observation context) and register it:

```csharp
FeatureRegistry.Instance.Register("my_feature", () => new MyFeature());
```

The factory pattern means the registry stays decoupled from the engine.

## Column naming and output

Each observation produces a row. The executor then appends `job_id`, `symbol`,
and `timestamp` columns, and `ResearchOutputWriter` writes/streams the row in
the selected format (`csv` | `json` | `parquet`). CSV columns are emitted in
sorted order of column names; missing values are emitted as empty.

Example row (real Bybit BTCUSDT, 5-minute grid, FullSort reorder):

```csv
job_id,mid_price,spread,spread_bps,symbol,timestamp,trade_flow,trade_intensity,trade_volume
bybit-btcusdt-20221213,17205,0,0,BTCUSDT,2022-12-13T00:00:00.0000000,0,0,0
bybit-btcusdt-20221213,17205.59,3.16,1.8366124032945106793780390000,BTCUSDT,2022-12-13T00:05:00.0000000,0,0,0
```

For a 1-minute grid over the same day the output is 1440 observation rows from
2161 events (see `ordering-determinism.md` for the byte-identical FullSort vs
InOrderStreaming proof).