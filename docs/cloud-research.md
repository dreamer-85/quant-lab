# Running the ResearchEngine on Google Cloud (GCE)

How to run research jobs at scale on a Google Compute Engine VM: from the
minimal repository, through dataset staging, to running jobs and pulling
results back. Linux is the target runtime; only portable .NET code is used.

## Why this exists

The ResearchEngine repo is minimal on purpose — it contains only what is needed
to build and run the engine:

- `Research/Engine`, `Research/Runner` — the engine and CLI.
- `Common`, `Compression`, `Configuration`, `Logging` — the Lean core libraries
  the engine depends on (transitive build closure).
- `Tests/Research/EngineTests` — the complete test suite (64 tests).
- `docs/`, `deploy/cloud/` — documentation and deployment tooling.

Raw market data is **not** committed to the repo. Datasets are packaged as
`tar.gz` bundles, uploaded to Google Cloud Storage, and staged onto the VM's
data root by `deploy/cloud/stage-data.sh`.

## Architecture

```
[Workstation]                         [GCE VM: Debian 12, .NET 10]
  git push  ───────────────────────►  git clone myEngine
  gsutil cp bundle.tgz ───────────►  gs://<bucket>/quantlab/bundle.tgz
                                      ./stage-data.sh  → /opt/quantlab/data (Lean layout)
                                      ./run-job.sh jobs/<job>.json
                                      ./sync-results.sh --up   → gs output/
  gsutil cp -r gs://…/output ─────►  local results (equivalence comparison)
```

## 0. Prerequisites (workstation)

- `gcloud` / `gsutil` authenticated on your workstation.
- A private repo: `github.com/boo100-hub/myEngine`.

## 1. Provision the VM

Create the VM (example sizes for the Bybit BTCUSDT minute dataset — adjust to
your dataset; `FullSort` materializes events in memory, so size the VM by
dataset, not by the tiny reference set):

```bash
# Debian 12, 4 vCPU / 16 GB, 200 GB SSD (adjust for dataset size)
gcloud compute instances create quantlab-vm \
  --project=<PROJECT> \
  --zone=us-central1-a \
  --machine-type=e2-standard-4 \
  --image-family=debian-12 \
  --image-project=debian-cloud \
  --boot-disk-size=200GB \
  --boot-disk-type=pd-ssd \
  --service-account=<SA-EMAIL>
```

Or via the console / Terraform — any Debian-based image works. The scripts
require nothing exotic: apt, curl, `dotnet`, `gsutil`.

Upload the service-account key (for `gsutil`/`gcloud` on the VM):

```bash
gcloud compute scp sa-key.json quantlab-vm:~/
```

## 2. Stage the dataset bundle

On the workstation, from a Lean data root that contains at least:

```
market-hours/market-hours-database.json
symbol-properties/symbol-properties-database.csv
symbol-properties/security-database.csv
crypto/bybit/minute/btcusdt/20221213_trade.zip
crypto/bybit/minute/btcusdt/20221213_quote.zip
```

```bash
tar -czf bybit-btcusdt-20221213.tar.gz -C <lean-data-root> \
    market-hours symbol-properties crypto
gsutil cp bybit-btcusdt-20221213.tar.gz gs://<bucket>/quantlab/
```

Only the subfolders actually needed must be included; `stage-data.sh --verify`
checks the required DB files exist after extraction.

## 3. Bootstrap the VM

```bash
gcloud compute ssh quantlab-vm
git clone https://github.com/boo100-hub/myEngine.git
cd myEngine/deploy/cloud
cp environment.example environment     # edit: repo URL, GCS bucket, SA key path
source environment

gcloud auth activate-service-account --key-file=<path-to-sa-key>.json
gcloud config set project <PROJECT>
./setup-vm.sh                          # installs .NET/gcloud, clone, build, 64 tests
./stage-data.sh bybit-btcusdt-20221213.tar.gz --verify
```

`setup-vm.sh` is idempotent: it refreshes the clone, rebuilds, re-runs the
engine test suite, and smoke-tests the Runner binary.

## 4. Run a job

```bash
./run-job.sh jobs/bybit-btcusdt-20221213.json
echo $?    # 0 = success, 1 = failed job, 2 = usage
```

Outputs land under `$QUANTLAB_OUTPUT_ROOT/<jobId>/`:

```
<output-root>/<jobId>/
  manifest.json        # machine-readable outcome (see below)
  BTCUSDT/bybit.parquet
  ...
```

The `manifest.json` records `succeeded`, `error`, `symbolsProcessed/reused`,
`eventsProcessed`, `observationsWritten`, `outputFiles`, `elapsedSeconds`,
and experiment `metrics` (when an experiment factory is configured).

## 5. Sync results

```bash
# On the VM:
./sync-results.sh --up <jobId>            # upload one job's output

# On the workstation:
source environment
./sync-results.sh --down <jobId> ./results
```

## Local-vs-cloud equivalence

The engine is deterministic: identical job + identical data ⇒ identical output.
The reference manifest from a local run is the ground truth. To verify a cloud
run:

1. Run the same job locally, e.g.:
   ```bash
   dotnet Research/Runner/bin/Release/net10.0/QuantConnect.Research.Runner.dll \
     --job-file deploy/cloud/jobs/bybit-btcusdt-20221213.json \
     --data-dir <lean-data-root> --output-dir out-local
   ```
2. Compare the cloud `manifest.json` fields against the local one
   (`eventsProcessed`, `observationsWritten`, `symbolsProcessed`).
3. Byte-compare the observation outputs:
   ```bash
   diff <(gunzip -c local.parquet | sha256sum) \
        <(gsutil cat gs://<bucket>/quantlab/output/<jobId>/...parquet | sha256sum)
   ```
   (Parquet equality can also be checked by loading both with pandas and
   comparing `df.equals` on the whole frame.)

## Checkpoint/resume on the VM

`ResearchJob.EnableCheckpointing` (default true) snapshots market state every
25,000 observations under `<output-root>/<jobId>/checkpoints/`. Rerunning the
same job resumes from the last checkpoint; output is bit-identical to an
uninterrupted run (see `docs/checkpointing-resume.md`). Interrupted cloud runs
can simply be re-invoked with `./run-job.sh` — the resume is automatic.

## Sizing notes

- `ReorderMode.FullSort` materializes the full filtered event list in memory;
  use `InOrderStreaming` (bit-identical output) for large datasets on small VMs.
- The engine is single-VM by design; scale by VM size, not by distributing.
- Live managed heap stays bounded (container-style streaming); the disk footprint
  is dominated by the staged dataset and the output files.

## Operating notes

- Rotate `GIT_PAT` and SA keys; never commit `deploy/cloud/environment`.
- Use `--synthetic-benchmark N` to validate a fresh VM quickly without data:
  ```bash
  dotnet Research/Runner/bin/Release/net10.0/QuantConnect.Research.Runner.dll \
    --synthetic-benchmark 1000000
  ```
- The engine emits Human-readable `manifest.json`; treat it as the contract for
  any automation (CI, alerting, downstream pipelines).