# Cloud Deployment (ResearchEngine on GCE / Cloud Run)

Scripts and tooling to provision Google Compute Engine and Cloud Run resources
that build and run the ResearchEngine, stage data via Google Cloud Storage, run
jobs on a VM, on Cloud Run (job or service), or on the workstation, and pull
results back to compare locally vs cloud.

Everything is driven by **one** environment file — `deploy/cloud/environment`
(copied from `environment.example`) — which is parsed both by the bash
deploy scripts (`source environment`) and by the Python CLI
(`python -m quantlab env`).

## Layout

| File | Purpose |
|------|---------|
| `environment.example` | GCP env-template; copy to `environment`, fill in, never commit |
| `provision.sh` | **Provision GCP resources idempotently**: GCS bucket, IAP-SSH firewall rule, VM (attached SA + storage scope) |
| `install-dependencies.sh` | Install .NET SDK + git + gcloud/gsutil on Debian/Ubuntu (VM) |
| `install-gcloud-windows.ps1` | Install Google Cloud SDK on the Windows workstation |
| `setup-vm.sh` | One-time VM bootstrap: clone, build, run test suite, smoke test |
| `stage-data.sh` | Download dataset bundle from GCS and lay out `$QUANTLAB_DATA_ROOT` |
| `run-job.sh` | Build (if stale) and run a job; emits `manifest.json` under the output root |
| `sync-results.sh` | Upload output root to GCS (`--up`) or pull a job down (`--down`) |
| `jobs/bybit-btcusdt-20221213.json` | Sample job file (Lean `Resolution`/`ReorderMode` enum strings) |

Plus the unified Python client in `Research/Python/quantlab/`, whose CLI
(`python -m quantlab`) wraps these scripts so the same job runs identically
locally or on the VM (see *Unified CLI* below).

## Google Cloud architecture

```
[Workstation]                         [Google Cloud]
  quantlab CLI ──gcloud/gsutil──►     GCS bucket  gs://<bucket>/<prefix>/
  (your laptop, no ports)                 │  {bundles/ , output/}
                                    [GCE VM] quantlab-vm (Debian 12, no public IP)
                                         │  SSH via IAP tunnel (tcp:22 from 35.235.240.0/20)
  gcloud compute ssh ──IAP tunnel──►     │  -> runs run-job.sh (batch CLI, no listening port)
```

- **No application port on the VM.** The runner is a batch CLI it reads a job,
  emits `manifest.json`, and exits — it owns no listening socket, so no HTTP/grpc
  firewall rule exists.
- **SSH only, and only through IAP.** `provision.sh` creates the firewall rule
  `quantlab-allow-iap-ssh` allowing `tcp:22` from the IAP range
  `35.235.240.0/20` against VM tag `${QUANTLAB_VM_TAGS}`. The VM is created with
  `--no-address` (no public IP) unless `QUANTLAB_VM_EXT_IP=ephemeral`.
  `gcloud compute ssh` and `quantlab run cloud` automatically use the IAP tunnel.
- **Keyless GCS on the VM.** The VM is created with an attached service account
  (`QUANTLAB_SA_EMAIL`) and `--scopes=storage-rw`, so `gsutil`/gcloud work
  without any key file on the instance. Set
  `GOOGLE_APPLICATION_CREDENTIALS` only on the **workstation**, pointing at a
  service-account key JSON.

## Quick start (local laptop → cloud VM)

```bash
# 1. On Windows: install the Google Cloud SDK once
powershell -ExecutionPolicy Bypass -File deploy\cloud\install-gcloud-windows.ps1

# 2. Configure the single environment file (project, zone, bucket, SA email, VM)
cp deploy/cloud/environment.example deploy/cloud/environment   # edit values
python -m quantlab env --show                                   # verify

# 3. Provision GCP resources (bucket, IAP-SSH firewall, VM) idempotently
source deploy/cloud/environment && bash deploy/cloud/provision.sh

# 4. Build a dataset bundle from a Lean data root and upload it
python -m quantlab bundle build --data-root <lean-data> --out data/bybit.tgz
python -m quantlab bundle upload data/bybit.tgz

# 5. Run the same job locally
python -m quantlab run local  deploy/cloud/jobs/bybit-btcusdt-20221213.json \
    --data-dir <lean-data>

# 6. Bootstrap the VM, stage data, run there
gcloud compute ssh quantlab-vm --command "cd ~/myEngine && ./deploy/cloud/setup-vm.sh"
python -m quantlab run cloud deploy/cloud/jobs/bybit-btcusdt-20221213.json

# 7. Pull cloud results and compare against the local run
python -m quantlab results download bybit-btcusdt-20221213 --dest out/
python -m quantlab compare out-local/ out-cloud/
```

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

See `environment.example`. Two consumers read it, so the values must be
identical for both sides:

| Variable | Meaning |
|----------|---------|
| `QUANTLAB_PROJECT` / `QUANTLAB_ZONE` | GCP project id + VM zone (`provision.sh`, `run cloud`) |
| `QUANTLAB_REGION` | Region for the bucket (default: zone's region) |
| `QUANTLAB_VM` | GCE instance name used by `run cloud` (default `quantlab-vm`) |
| `QUANTLAB_GCS_BUCKET` | **Bucket name only** (no `gs://`); stores `bundles/` + `output/` |
| `QUANTLAB_GCS_PREFIX` | Subfolder inside the bucket (default `quantlab`) |
| `QUANTLAB_SA_EMAIL` | Service-account email attached to the VM (keyless gsutil) |
| `GOOGLE_APPLICATION_CREDENTIALS` | **Workstation only**: path to the SA key JSON |
| `QUANTLAB_VM_TAGS` / `QUANTLAB_VM_EXT_IP` | Firewall tag for IAP-SSH; `none`\`ephemeral` public IP |
| `QUANTLAB_VM_MACHINE_TYPE`/`BOOT_*` | VM geometry used by `provision.sh` |
| `QUANTLAB_REPO_DIR` | Path of the repo on the VM (default `myEngine`) |
| `QUANTLAB_DATA_ROOT` | Lean-compatible data root on the VM |
| `QUANTLAB_OUTPUT_ROOT` | Output root (results + `manifest.json`) |
| `QUANTLAB_CACHE_ROOT` | Scratch/cache (FullSort materializes here) |
| `QUANTLAB_TEMP_ROOT` | Temp root |

## Unified CLI (`python -m quantlab`)

From the repo root: `PYTHONPATH=Research/Python python -m quantlab ...`

```
quantlab run local <job.json> [--data-dir] [--output-dir] [--build]
quantlab run cloud <job.json> [--vm] [--zone] [--project] [--data-dir] [--output-dir] [--no-scp]
quantlab bundle build --data-root <lean-data> --out <bundle.tgz> [--extra-dir X]
quantlab bundle upload <bundle.tgz> [--no-check]
quantlab results download <job-id> [--dest DIR]
quantlab compare <local-result> <cloud-result> [--no-files] [--deep]
quantlab env [--show]
```

- `bundle build` produces the exact layout `stage-data.sh` expects and verifies
  the required DB files first.
- `bundle upload` / `results download` wrap gsutil.
- `run cloud` scp's the job file to the VM, sources the same `environment`
  file there, and runs `deploy/cloud/run-job.sh` synchronously.
- `compare` byte-compares the output files (or deep-compares parquet frames
  with `--deep`) and field-by-field compares `manifest.json`. Exit code 0
  means identical.

## Troubleshooting

**`Missing --job-file` / `Invalid --job-file: file not found`** (Cloud Run)

The container shares one image between two Cloud Run shapes:

- **Cloud Run Job**: the runner is a batch CLI — it reads a job, emits
  `manifest.json`, and exits. Every arg must come from the job's `--args`
  (`--job-file=...`). Because the job spec always passes `--job-file`, the
  runner detects it and runs in CLI mode even though the image ENTRYPOINT is
  `... --web`.
- **Cloud Run Service** (web mode): same image, **no** `--job-file` arg, so the
  runner starts Kestrel on `0.0.0.0:${PORT:-8080}` and serves the HTTP API
  (`/healthz`, `/`, `/run`, `/run-job-file`). This satisfies the startup probe.

The two GCS volumes mount the **bucket root** at `/quantlab/data` and
`/quantlab/output`, so the runner expects `job.json` at `gs://<bucket>/job.json`.
The `Missing --job-file` / `Invalid --job-file` message means one of:

1. **The job has no `--args`.** Verify:
   ```bash
   gcloud run jobs describe quant-lab --region=<region> \
     --format='value(spec.template.spec.template.spec.containers[0].args)'
   ```
   If empty, the job was created before args existed — re-run `cloudshell-deploy.sh`.
2. **`job.json` isn't at the bucket root.** Verify:
   ```bash
   gsutil ls gs://<bucket>/job.json
   ```
   If absent, stage it (must be the bucket ROOT, not a prefix):
   ```bash
   bash deploy/cloud/stage-bucket.sh --data-root <lean> \
     --job deploy/cloud/jobs/<job>.json --bucket gs://<bucket> --verify
   ```

**`failed to start because the default startup TCP probe on port 8080 was
unsuccessful`** (Cloud Run Service)

This means the service deployed an image that did not listen on port 8080
during startup. If it was a pre-web build of the runner (CLI-only, exits after
the job), Kestrel is never started. Fix: redeploy with the `--web` image (the
post-`--web` Dockerfile ENTRYPOINT) — either through `cloudbuild.yaml` step 4
or `gcloud run deploy quant-lab --image=<image> --port=8080 --allow-unauthenticated`.
The `--web` mode binds `0.0.0.0:$PORT` and serves `/healthz`, so the probe and
any `/run` requests succeed.

## Data contract

The engine reads **Lean zip files** from `$QUANTLAB_DATA_ROOT`. A bundle must
contain, at minimum:

```
market-hours/market-hours-database.json
symbol-properties/symbol-properties-database.csv
symbol-properties/security-database.csv
crypto/bybit/minute/btcusdt/YYYYMMDD_{trade,quote}.zip   (dataset under crypto/)
```

Build and upload a bundle (equivalent manual commands):

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
`manifest.json` is the machine-readable result; `quantlab compare` uses it
(plus the output files) for the local-vs-cloud equivalence check.