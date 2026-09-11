# QuantLab Research Engine (myEngine)

A streaming, bounded-memory market-research pipeline built on the Lean
platform. It replays raw Lean market data (trades, quotes, L1/L2 order book)
as a fast event stream, reconstructs market state observation-by-observation,
computes feature vectors, optionally tags observations with delayed
(future-return) labels, and streams results to disk — CSV/JSON/Parquet —
without ever loading the dataset into memory.

This repository is the **minimal deployment closure**: only the code needed to
build, run, test, and deploy the engine. Raw market data is never committed; it
is staged as `tar.gz` bundles via the cloud tooling.

## Repository layout

```
Common/                   Lean core libraries (transitive build closure)
Compression/              Zip/gzip handling required by the engine
Configuration/            ConfigHash + runtime configuration
Logging/                  Log handler used by the engine
Research/
  Engine/                 The research engine (replay, features, storage, jobs)
  Runner/                 CLI that runs a job from a JSON file
  Python/quantlab/        Unified Python CLI: local + cloud orchestration
Tests/Research/EngineTests/   NUnit test suite (64 tests)
docs/                     Engine documentation
deploy/cloud/             VM provisioning, data staging, results sync (bash + PowerShell)
```

## Highlights

- **Single-pass streaming replay.** Raw Lean data files are streamed, k-way
  merged, byte-order reordered, and replayed. Live managed heap stays bounded.
- **FullSort == InOrderStreaming, bit-identical.** Both reorder modes produce
  the exact same output on real Bybit data (see `docs/ordering-determinism.md`).
- **Deterministic.** Events compare by `(Timestamp, SequenceNumber, OrderOrdinal)`;
  the same job always yields the same output; a configuration hash identifies a replay.
- **Checkpoint and resume.** Market state is snapshotted every 25,000 observations;
  interrupted runs resume bit-identically (see `docs/checkpointing-resume.md`).
- **Pluggable features and experiments.** Features register by name; experiments
  consume observations/outcomes through `IExperiment`.
- **Unified local + cloud CLI.** One environment file drives both the workstation
  and a Google Compute Engine VM.

## Quick start

Build and run the full test suite (64 tests):

```
dotnet test "Tests\Research\EngineTests\QuantConnect.Research.Engine.Tests.csproj" -c Release
```

Run a job from JSON against real data:

```
dotnet run --project Research\Runner -- --job-file job.json --data-dir Data --output-dir out
```

Run a synthetic throughput benchmark (N = number of events):

```
dotnet run --project Research\Runner -- --synthetic-benchmark 1000000
```

For details of the job file format, see `docs/research-job.md`.

## Unified CLI (`python -m quantlab`)

`Research/Python/quantlab` is an orchestration CLI that runs the exact same job
locally or on a GCE VM, stages data bundles, and compares results. It reads the
same `deploy/cloud/environment` file as the bash deploy scripts.

Requires Python 3.10+ (and `gcloud`/`gsutil` for cloud commands). From the repo
root, either add `Research/Python` to `PYTHONPATH` or `pip install -e Research/Python`.

```
quantlab run local <job.json> [--build] [--data-dir] [--output-dir]
quantlab run cloud <job.json> [--vm] [--zone] [--project] [--no-scp]
quantlab bundle build --data-root <lean-data> --out <bundle.tgz>
quantlab bundle upload <bundle.tgz>
quantlab results download <job-id> [--dest DIR]
quantlab compare <local-result> <cloud-result> [--no-files] [--deep]
quantlab env [--show]
```

See `deploy/cloud/README.md` for the full workflow and `docs/cloud-research.md`
for the end-to-end cloud runbook.

## Documentation

| File | Content |
|------|---------|
| `docs/README.md` | Engine architecture, pipeline, memory model, tests |
| `docs/research-job.md` | `ResearchJob` JSON schema and semantics |
| `docs/ordering-determinism.md` | Ordering guarantees, tie-breaks, invariants |
| `docs/checkpointing-resume.md` | Checkpoint/resume design and guarantees |
| `docs/bounded-memory.md` | Streaming memory model and profiler results |
| `docs/features.md` | Feature registry and available features |
| `docs/cloud-research.md` | End-to-end cloud deployment runbook |
| `deploy/cloud/README.md` | Cloud tooling + unified CLI reference |

## Prerequisites

- .NET 10 SDK.
- Python 3.10+ (for the unified CLI).
- `gcloud` / `gsutil` authenticated on the workstation (cloud commands only).
  On Windows: `deploy/cloud/install-gcloud-windows.ps1`.

## License

This repository is derived from [QuantConnect/Lean](https://github.com/QuantConnect/Lean)
(see `LICENSE`).