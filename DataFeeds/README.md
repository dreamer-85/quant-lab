# DataFeeds

Python tooling to pull market data — crypto **and** forex — into a common,
version-agnostic on-disk layout that the ResearchEngine consumes directly.
Historic ranges are backfilled through REST/WebSocket APIs; live sessions
stream into the same layout.

Supported providers:

| market | provider | mode       | description |
|---|---|---|---|
| crypto | `okx` | historical / live | OKX v5 REST + WebSocket |
| crypto | `bybit` | historical / live | Bybit v5 REST + WebSocket |
| crypto | `binance` | historical / live | Binance REST + WebSocket (spot + usdm) |
| forex | `deriv` | historical / live | Deriv WebSocket API v3 |

> Live Binance spot sessions try `wss://stream.binance.com:9443` and fall back to
> `wss://data-stream.binance.vision` (standard 443) when the non-standard port is blocked.

## Folder layout

- `Historical/<market>/*.py` — one entry script per provider (backfill a date range).
- `Live/<market>/*.py`     — one entry script per provider (stream a live session).
- `datafeeds/core/`        — shared framework (clients, writers, registry, CLI).
- `datafeeds/providers/`   — per-exchange implementations.

Add a new exchange by writing a feed class in
`datafeeds/providers/<market>/<provider>.py`, decorating it with
`@register_feed(market, provider, mode)` and importing it from
`datafeeds/providers/__init__.py`; `python -m datafeeds list` then picks it up
and `Historical|Live/<market>/app_<provider>.py` runs it.

## Setup

Requires Python 3.10+. Live providers additionally need `websocket-client`:

```
python -m venv .venv
.\.venv\Scripts\activate
pip install -r DataFeeds\requirements.txt
```

REST pulls use only the standard library.

## Output layout

Everything lands under a feed root (default `feeds/`, override with `--out`):

```
<feed_root>/crypto/okx/BTC-USDT/bars_60.csv
<feed_root>/crypto/okx/BTC-USDT/trades.csv
<feed_root>/forex/deriv/EURUSD/bars_3600.csv
<feed_root>/forex/deriv/EURUSD/quotes.csv
<feed_root>/crypto/binance/BTCUSDT/book_updates.csv
```

Headers: `timestamp_ms,open,high,low,close,volume` (bars),
`timestamp_ms,price,size,side,trade_id` (trades),
`timestamp_ms,bid_price,bid_size,ask_price,ask_size` (quotes),
`timestamp_ms,side,price,quantity,action` (book_updates; `side` is `bid`|`ask`,
`action` is `add`|`modify`|`remove`, default `add`; quantity is the new absolute
size at the price, a `remove` is a 0-quantity row).
Files are written atomically, deduplicated, and sorted ascending.

Only `book_updates.csv` carries order-book depth, so it is the source for the
engine's depth measurements (`bid_depth`, `ask_depth`, `imbalance`,
`depth_ratio`, ...) — see `docs/research-layer.md`. Feeds that subscribe to a
depth channel should stage it alongside bars/trades/quotes.

## Commands

### Historical backfills

```
# Crypto — OKX spot
python DataFeeds\Historical\Crypto\app_okx.py --symbol BTC-USDT --start 2026-09-01 --end 2026-09-03 --interval 1m --out feeds

# Crypto — Bybit spot (or --category linear for USDT perpetuals)
python DataFeeds\Historical\Crypto\app_bybit.py --symbol BTCUSDT --start 2026-09-01 --end 2026-09-03 --interval 1h --out feeds

# Crypto — Binance spot (or --market-type usdm for futures)
python DataFeeds\Historical\Crypto\app_binance.py --symbol BTCUSDT --start 2026-09-01 --end 2026-09-03 --interval 5m --out feeds

# Forex — Deriv (interval = candle granularity in seconds) — authenticated
python DataFeeds\Historical\Forex\app_deriv.py --symbol EURUSD --start 2026-09-01 --end 2026-09-03 --interval 60 --app-id YOUR_APP_ID --api-token YOUR_TOKEN --out feeds
python DataFeeds\Historical\Forex\app_deriv.py --symbol EURUSD --start 2026-09-01 --end 2026-09-03 --interval 300 --app-id YOUR_APP_ID --api-token YOUR_TOKEN --quotes --out feeds
```

### Live sessions

```
python DataFeeds\Live\Crypto\app_okx.py      --symbol BTC-USDT --interval 1m --duration 60 --out feeds
python DataFeeds\Live\Crypto\app_bybit.py     --symbol BTCUSDT --interval 1m --duration 60 --out feeds
python DataFeeds\Live\Crypto\app_binance.py   --symbol BTCUSDT --interval 1m --duration 60 --out feeds
python DataFeeds\Live\Forex\app_deriv.py      --symbol EURUSD  --interval 60  --duration 60 --app-id YOUR_APP_ID --api-token YOUR_TOKEN --out feeds
```

> **Deriv authentication:** every Deriv WebSocket session is bound to an
> `--app-id` (default `1089`). Pass `--api-token` (a Deriv API token) to also
> authorize the session — the connection sends `authorize` after connect and
> waits for the acknowledge before issuing `ticks_history`/`ticks`/`ohlc`
> requests. Equivalent flags exist on the unified CLI
> (`python -m datafeeds pull --market forex --provider deriv ...`).
> Treat `--api-token` as a secret (never commit job scripts that embed it).

### Unified CLI

Both modes, all providers, from the same command:

```
python -m datafeeds list
python -m datafeeds pull --market crypto --provider okx --mode historical ^
       --symbol BTC-USDT --start 2026-09-01 --end 2026-09-03 --interval 1m --out feeds
python -m datafeeds pull --market forex --provider deriv --mode live ^
       --symbol EURUSD --interval 60 --duration 60 --out feeds
```

Use `--no-bars` / `--no-trades` / `--no-quotes` to disable individual outputs.

## End-to-end with the ResearchEngine

The engine reads the same layout through `LocalFeedDataSource`
(`Research\Engine\Ingest\LocalFeed\`): set `source.mode` to `"feed"` and point
`--data-dir` at the feed root. Symbol directories match the job ticker ignoring
`-`, `_` and `/` (so `BTC-USDT` finds `BTCUSDT`).

Example `feed-job.json`:

```json
{
  "jobId": "feed-btcusdt",
  "dataset": "crypto",
  "symbols": ["BTCUSDT"],
  "assetClass": "crypto",
  "venue": "binance",
  "resolution": "Minute",
  "startTime": "2026-09-01T00:00:00",
  "endTime": "2026-09-03T00:00:00",
  "eventTypes": ["Bar", "Trade"],
  "observationInterval": "00:05:00",
  "features": ["mid_price", "spread", "trade_volume"],
  "experimentName": "dry-run",
  "outputFormat": "csv",
  "source": { "mode": "feed", "provider": "binance" }
}
```

Run it:

```
dotnet build Research\Runner\QuantConnect.Research.Runner.csproj -c Release
dotnet run --project Research\Runner -c Release -- --job-file feed-job.json --data-dir feeds --output-dir .\research\results
```

Results land under `<output-dir>/<jobId>/`. The DataFeeds tests are covered by
`LocalFeedDataSourceTests` in `Tests\Research\EngineTests`.

To run a Python strategy script over pulled data instead of a dry-run, change
`experimentName` to `"python_strategy"` and add `strategyScript` pointing at
your `.py` file (full contract and an example in `docs/python-strategies.md`):

```json
"experimentName": "python_strategy",
"strategyScript": "C:\\strategies\\sma_cross.py",
```

The script receives each observation's raw fields + requested features and can
emit its own signal rows — no C# needed.