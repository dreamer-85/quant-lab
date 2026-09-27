# Features

Features are computed per observation by `FeatureEngine`. The engine takes the
job's `features` list and instantiates each named feature via `FeatureRegistry`
(registration keys are case-insensitive).

An unknown feature name is rejected before replay starts, with a "did you mean"
suggestion naming the closest registered feature. Do not rely on the exception:
the job is not run at all.

## Available features

### Price

| Feature       | Output column | Description                                             |
| ------------- | ------------- | ------------------------------------------------------- |
| `mid_price`   | `mid_price`   | Midpoint of best bid/ask; falls back to last trade price |
| `spread`      | `spread`      | Bid-ask spread in price terms                           |
| `spread_bps`  | `spread_bps`  | Bid-ask spread in basis points relative to mid price    |

### Trade / flow

"Flow" features are **notional** (price x quantity) unless stated otherwise,
and are **signed**: buys add, sells subtract. A positive value means net buying
pressure. This matters for thresholds, which are written in quote currency.

| Feature                   | Output column | Description                                    |
| ------------------------- | ------------- | ---------------------------------------------- |
| `trade_flow`              | `trade_flow`  | Signed notional flow for the period (+buy, -sell) |
| `net_flow`                | `net_flow`    | Taker buy notional minus taker sell notional; identical to `trade_flow` |
| `aggressive_buy_volume`   | `aggressive_buy_volume`   | Taker buy notional in the period     |
| `aggressive_sell_volume`  | `aggressive_sell_volume`  | Taker sell notional in the period    |
| `cumulative_flow`         | `cumulative_flow` | Running sum of signed **quantity** (not notional) since the last reset |
| `trade_volume`            | `trade_volume`| Total traded quantity in the period             |
| `trade_intensity`         | `trade_intensity` | Number of trades in the period              |

`trade_flow` and `net_flow` are two names for the same quantity, so selecting
both produces two identical columns. Selecting one is enough.

`cumulative_flow` is stateful: it accumulates across observations and resets via
`FeatureEngine.Reset()`. It is a quantity-based sum, unlike the notional
measures above, so do not mix the two in one threshold.

### Depth (L2 book)

Depth features read reconstructed book state. When the state is not an
`IOrderBookState` they return `0` rather than failing, so a job that does not
subscribe to book data produces columns of zeros instead of an error. The
validation check `config` reports this as an error, because a constant-zero
depth column is almost always a misconfigured job rather than a real result.

| Registration key        | Output column (defaults)          | Description                                     |
| ----------------------- | --------------------------------- | ----------------------------------------------- |
| `depth`                 | `depth`                           | Total book depth (bid + ask)                    |
| `bid_depth`             | `bid_depth`                       | Total depth on the bid side                     |
| `ask_depth`             | `ask_depth`                       | Total depth on the ask side                     |
| `imbalance`             | `imbalance`                       | Normalized imbalance in [-1, 1]                 |
| `depth_ratio`           | `depth_ratio`                     | Bid depth divided by ask depth                  |
| `structural_imbalance`  | `structural_imbalance_10bps`      | Imbalance within `bps_band` bps of mid           |
| `liquidity_wall`        | `liquidity_wall_3x`               | Distance in bps from mid to the nearest wall    |
| `resistance`            | `resistance_2x`                   | Distance in bps above mid to the nearest large ask level |
| `liquidity_depletion`   | `liquidity_depletion_5obs_10bps`  | Change in depth over `lookback_periods` observations |
| `replenishment_rate`    | `replenishment_rate_10obs_10bps`  | Rate of depth change over `window_size` observations |
| `depth_persistence`     | `depth_persistence_20obs`         | Persistence of depth over `window_size` observations |

## Parameterized features and output column names

The depth features listed with a suffixed output column are **parameterized**.
You select them by the base name, and the configured parameters are baked into
the **output column name**. For example, selecting `liquidity_wall` with
`feature.liquidity_wall.wall_threshold_multiplier = 5` emits a column named
`liquidity_wall_5x`, not `liquidity_wall`.

This is deliberate: a run that mixes several parameter settings into one column
would make the output uninterpretable. Read the header to know which settings
produced a column.

Supported parameters, set through `experimentConfig` in the job file:

| Feature                  | Parameter key                                        | Default | Effect                                              |
| ------------------------ | ---------------------------------------------------- | ------- | --------------------------------------------------- |
| `structural_imbalance`   | `feature.structural_imbalance.bps_band`              | `10`    | Band around mid, in basis points                    |
| `liquidity_wall`         | `feature.liquidity_wall.wall_threshold_multiplier`   | `3`     | A level is a wall when qty exceeds this x the book average |
| `resistance`             | `feature.resistance.resistance_factor`              | `2`     | An ask level resists when qty exceeds this x the average ask level |
| `liquidity_depletion`    | `feature.liquidity_depletion.lookback_periods`       | `5`     | Observations to look back over                      |
| `liquidity_depletion`    | `feature.liquidity_depletion.bps_band`               | `10`    | Band around mid, in basis points                    |
| `replenishment_rate`     | `feature.replenishment_rate.window_size`             | `10`    | Observations in the rolling window                  |
| `replenishment_rate`     | `feature.replenishment_rate.bps_band`                | `10`    | Band around mid, in basis points                    |
| `depth_persistence`      | `feature.depth_persistence.window_size`              | `20`    | Observations in the rolling window                  |

An unrecognised `feature.*` key is reported by the `config` validation check
and ignored, so a misspelled parameter fails silently in terms of output. Check
the header row to confirm the value you intended was applied.

## Adding a feature

Implement `IFeature` (see `FeatureBase` for the base class, which provides a
`Name` property and integrates observation context) and register it:

```csharp
FeatureRegistry.Instance.Register("my_feature", () => new MyFeature());
```

For a feature that reads job configuration, register it as parameterized and
return a name that includes the parameters it consumed:

```csharp
FeatureRegistry.Instance.RegisterParameterized("my_feature",
    p => new MyFeature(p.GetDecimal("band", 10m)));
```

The factory pattern keeps the registry decoupled from the engine.

## Column naming and output

Each observation produces a row. The executor then appends `job_id`, `symbol`,
and `timestamp` columns, and `ResearchOutputWriter` writes/streams the row in
the selected format (`csv` | `json` | `parquet`). CSV columns are emitted in
sorted order of column names; missing values are emitted as empty.

`features` (computed values) and `rawFields` (fields read straight off the
observation) both become columns. **If a raw field and a feature share a name,
the feature value wins** and the raw value is not emitted, so a job cannot
silently compute one thing and label it as another. The `config` validation
check reports such collisions.

Example row (real Bybit BTCUSDT, 5-minute grid, FullSort reorder):

```csv
job_id,mid_price,spread,spread_bps,symbol,timestamp,trade_flow,trade_intensity,trade_volume
bybit-btcusdt-20221213,17205,0,0,BTCUSDT,2022-12-13T00:00:00.0000000,0,0,0
bybit-btcusdt-20221213,17205.59,3.16,1.8366124032945106793780390000,BTCUSDT,2022-12-13T00:05:00.0000000,0,0,0
```

For a 1-minute grid over the same day the output is 1440 observation rows from
2161 events (see `ordering-determinism.md` for the byte-identical FullSort vs
InOrderStreaming proof).
