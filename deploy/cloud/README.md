# Cloud Deployment (ResearchEngine on GCE)

Scripts to provision a Google Compute Engine VM that builds and runs the
ResearchEngine, plus helpers to stage data from Google Cloud Storage and sync
results back.

## Layout

| File | Purpose |
|------|---------|
| `environment.example` | Env-template; copy to `environment`, fill in, never commit |
| `install-dependencies.sh` | Install .NET SDK + git + gcloud/gsutil on Debian/Ubuntu |
| `setup-vm.sh` | One-time VM bootstrap: clone, build, run test suite, smoke test |
| `stage-data.sh` | Download dataset bundle from GCS and lay out `$QUANTLAB_DATA_ROOT` |
| `run-job.sh` | Build (if stale) and run a job; emits `manifest.json` under the output root |
| `sync-results.sh` | Upload output root to GCS (`--up`) or pull a job down (`--down`) |
| `jobs/bybit-btcusdt-20221213.json` | Sample job file (Lean `Resolution`/`ReorderMode` enum strings) |
| `data/` | Holds the bundle files you upload to GCS (git-ignored) |

## Fast path (on a fresh Debian 12 VM)

```bash
git clone https://github.com/boo100-hub/myEngine.git
cd myEngine/deploy/cloud
cp environment.example environment   # edit: repo URL, GCS bucket, SA key path
source environment
./setup-vm.sh
./stage-data.sh bybit-btcusdt-20221213.tar.gz --verify
./run-job.sh jobs/bybit-btcusdt-20221213.json
./sync-results.sh --up
```

## Environment variables

See `environment.example`. The engine reads the `QUANTLAB_*` vars via
`ResearchEnvironment` (resolution order: CLI flag > env var > default).

| Variable | Meaning |
|----------|---------|
| `QUANTLAB_DATA_ROOT` | Lean-compatible data root on the VM |
| `QUANTLAB_OUTPUT_ROOT` | Output root (results + `manifest.json`) |
| `QUANTLAB_CACHE_ROOT` | Scratch/cache (FullSort materializes here) |
| `QUANTLAB_TEMP_ROOT` | Temp root |
| `QUANTLAB_GCS_BUCKET` | `gs://bucket/prefix` receiving data bundles and results |
| `QUANTLAB_GCS_PREFIX` | Subfolder inside the bucket (default `quantlab`) |
| `GOOGLE_APPLICATION_CREDENTIALS` | VM path to the service-account key JSON |

## Data contract

The engine reads **Lean zip files** from `$QUANTLAB_DATA_ROOT`. A bundle must
contain, at minimum:

```
market-hours/market-hours-database.json
symbol-properties/symbol-properties-database.csv
symbol-properties/security-database.csv
crypto/bybit/minute/btcusdt/YYYYMMDD_{trade,quote}.zip   (dataset under crypto/)
```

Build a bundle locally and upload it:

```bash
tar -czf bybit-btcusdt-20221213.tar.gz -C <data-root> market-hours symbol-properties crypto
gsutil cp bybit-btcusdt-20221213.tar.gz gs://<bucket>/quantlab/
```

## Job files

`jobs/*.json` are serialized `ResearchJob`. Field names/values must match the
C# model (`Research/Engine/Jobs/ResearchJob.cs`). Enums are strings:
`Resolution` = `Tick|Second|Minute|Hour|Daily`; `Reorder` =
`FullSort|InOrderStreaming`; `EventTypes` = `Trade|Quote|OrderBookUpdate`.
Time spans use ISO-8601 (`"00:00:30"`). See `docs/research-job.md`.

## Results

Each successful run writes:
```
$QUANTLAB_OUTPUT_ROOT/<jobId>/
  manifest.json          (outcome, event counts, metrics, elapsed)
  <dataset>.*            (observation output, parquet or csv)
  experiment/<...>       (experiment outputs)
  checkpoints/           (resume checkpoints if enabled)
```
`manifest.json` is the machine-readable result; capture it for the
local-vs-cloud equivalence comparison.