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
  quantlab run local  ───────────►   (same job, same data → identical manifest)
  gsutil cp bundle.tgz ───────────►  gs://<bucket>/quantlab/bundle.tgz
  quantlab run cloud  ───────────►   scp job → source environment → run-job.sh
  quantlab results download ◄──────  gs output/ → local
  quantlab compare local cloud      (exit 0 = identical)
```

The wiring is the **unified `quantlab` CLI** (`Research/Python/quantlab/`,
`python -m quantlab`). It reads the *same* `deploy/cloud/environment` file as
the bash scripts, so one environment configures both sides. See
`deploy/cloud/README.md` for the full command reference; the walkthrough below
shows the underlying primitives.

## 0. Prerequisites (workstation)

- Python 3.10+.
- `gcloud` / `gsutil` authenticated on your workstation. On Windows, run
  `deploy/cloud/install-gcloud-windows.ps1`, then `gcloud init --console-only`
  and `gcloud auth application-default login`.
- A private repo: `github.com/boo100-hub/myEngine`.
- `deploy/cloud/environment` (from `environment.example`) with
  `QUANTLAB_VM`, `QUANTLAB_ZONE`, `QUANTLAB_PROJECT`, `QUANTLAB_GCS_BUCKET`.
- The CLI needs `Research/Python` on `PYTHONPATH` (or `pip install -e Research/Python`, then `quantlab`; install `[deep]` extras for parquet-level `compare --deep`).

## 1. Provision the GCP resources

`deploy/cloud/provision.sh` creates everything idempotently from the single
`environment` file: the **GCS bucket** (uniform bucket-level access), the
**IAP-SSH firewall rule** (only `tcp:22` from `35.235.240.0/20`), and the
**VM** with its attached service account and `storage-rw` scope.

```bash
# 1a. Fill the environment (project, zone, bucket, SA email, VM geometry)
cp deploy/cloud/environment.example deploy/cloud/environment   # edit values
python -m quantlab env --show

# 1b. Provision (bucket + firewall + VM)
source deploy/cloud/environment && bash deploy/cloud/provision.sh
```

Networking and ports, by design:

- The VM has **no public IP** (`--no-address`) — SSH happens only through the
  Identity-Aware Proxy tunnel (`gcloud compute ssh` enables it automatically).
  Firewall rule `quantlab-allow-iap-ssh` allows `tcp:22` for tagged instances
  from the IAP range.
- The Runner is a **batch CLI**: it reads a job, writes results, exits. It owns
  no listening socket, so no application port is ever opened on the VM.
- If you cannot use IAP, set `QUANTLAB_VM_EXT_IP="ephemeral"` in the environment;
  the VM then gets an external IP, but no firewall rule permits inbound traffic
  to it beyond the IAP-SSH rule.

Manual equivalent (if you provision outside `provision.sh`):

```bash
# Debian 12, 4 vCPU / 16 GB, 200 GB SSD (adjust for dataset size);
# FullSort materializes events in memory, so size by dataset not the tiny sample.
gcloud compute instances create quantlab-vm \
  --project=<PROJECT> \
  --zone=us-central1-a \
  --machine-type=e2-standard-4 \
  --image-family=debian-12 \
  --image-project=debian-cloud \
  --boot-disk-size=200GB \
  --boot-disk-type=pd-ssd \
  --no-address \
  --service-account=<SA-EMAIL> \
  --scopes=storage-rw \
  --tags=quantlab-runner
```

With the attached service account scoped to `storage-rw`, `gsutil`/gcloud work
on the VM with **no key file**. The service-account key JSON is only needed on
the **workstation** (`GOOGLE_APPLICATION_CREDENTIALS`).

## 2. Stage the dataset bundle

On the workstation (using the CLI — produces the same layout as the manual
`tar` command below), from a Lean data root that contains at least:

```
market-hours/market-hours-database.json
symbol-properties/symbol-properties-database.csv
symbol-properties/security-database.csv
crypto/bybit/minute/btcusdt/20221213_trade.zip
crypto/bybit/minute/btcusdt/20221213_quote.zip
```

```bash
python -m quantlab bundle build --data-root <lean-data-root> --out bybit-btcusdt-20221213.tar.gz
python -m quantlab bundle upload bybit-btcusdt-20221213.tar.gz
```

Only the subfolders actually needed must be included; `bundle build` verifies
the required DB files exist before packing, and `stage-data.sh --verify`
re-checks them after extraction.

## 3. Bootstrap the VM

```bash
gcloud compute ssh quantlab-vm     # IAP tunnel; no key upload needed
git clone https://github.com/boo100-hub/myEngine.git
cd myEngine/deploy/cloud
cp environment.example environment     # edit: repo URL, GCS bucket/prefix
source environment

./setup-vm.sh                          # installs .NET/gcloud, clone, build, 64 tests
./stage-data.sh bybit-btcusdt-20221213.tar.gz --verify
```

`setup-vm.sh` is idempotent: it refreshes the clone, rebuilds, re-runs the
engine test suite, and smoke-tests the Runner binary. GCS access on the VM uses
the attached service account (no `gcloud auth activate-service-account`).

## 4. Run a job

The job file is optional — for the common case the CLI infers the whole job
from the Lean data directory layout (`<root>/<asset>/<venue>/<resolution>/
<symbol>/<YYYYMMDD>[_<ticktype>].zip`). To see what it would generate:

```bash
python -m quantlab job create --data-dir <lean-data-root>
# optional overrides: --symbol BTCUSDT --resolution minute --features a,b
#                     --interval 30 --experiment name --horizons 00:05:00
#   -o myjob.json   writes the JSON instead of printing it
```

To run a job directly from the data (no job JSON, no file upload):

```bash
python -m quantlab run local --data-dir <lean-data-root>
```

Or keep using an explicit job file:

```bash
python -m quantlab run local deploy/cloud/jobs/bybit-btcusdt-20221213.json --data-dir <lean-data-root>
```

Exactly the same job without a build on the VM (the CLI scp's the job file and
runs `deploy/cloud/run-job.sh`):

```bash
python -m quantlab run cloud deploy/cloud/jobs/bybit-btcusdt-20221213.json
```

Under the hood, on the VM:

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

# On the workstation (CLI wrapper over gsutil):
python -m quantlab results download <jobId> --dest ./results
```

## Local-vs-cloud equivalence

The engine is deterministic: identical job + identical data ⇒ identical output.
The reference manifest from a local run is the ground truth. To verify a cloud
run, run locally and compare:

```bash
python -m quantlab run local deploy/cloud/jobs/bybit-btcusdt-20221213.json --data-dir <lean-data-root> --output-dir out-local
python -m quantlab run cloud deploy/cloud/jobs/bybit-btcusdt-20221213.json
python -m quantlab results download bybit-btcusdt-20221213 --dest out-cloud
python -m quantlab compare out-local out-cloud        # exit 0 = identical files + manifest
```

`compare` checks every `manifest.json` field (`eventsProcessed`,
`observationsWritten`, `symbolsProcessed`, `elapsedSeconds`, ...) and
byte-compares the output files; pass `--deep` to compare parquet frames with
pandas (`df.equals`). The manual sha256 approach is equivalent:

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