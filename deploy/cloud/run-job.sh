#!/usr/bin/env bash
# run-job.sh
# Builds the Runner (if needed) and executes a ResearchEngine job on the VM.
#
# Job files: deploy/cloud/jobs/*.json (see the sample bybit-btcusdt.json).
# Outputs land under $QUANTLAB_OUTPUT_ROOT/<jobId>/ including manifest.json.
# Exit code 0 = success, 1 = job failed, 2 = usage error.
#
# Usage: source environment && ./run-job.sh jobs/bybit-btcusdt.json
set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/environment" ]; then
    set -a; source "${SCRIPT_DIR}/environment"; set +a
fi

JOB_FILE="${1:-}"
[ -n "${JOB_FILE}" ] || { echo "usage: run-job.sh <job.json>"; exit 2; }
[ -f "${JOB_FILE}" ] || { echo "job file not found: ${JOB_FILE}"; exit 2; }

REPO_DIR="${QUANTLAB_REPO_DIR:-${HOME}/myEngine}"

# Build only if the artifacts are stale or missing.
RUNNER_DLL="${REPO_DIR}/Research/Runner/bin/Release/net10.0/QuantConnect.Research.Runner.dll"
if [ ! -f "${RUNNER_DLL}" ]; then
    echo "==> Building Runner (Release)"
    dotnet build "${REPO_DIR}/Research/Runner/QuantConnect.Research.Runner.csproj" -c Release
fi

DATA_ROOT="${QUANTLAB_DATA_ROOT:?QUANTLAB_DATA_ROOT must be set (see environment.example)}"
OUTPUT_ROOT="${QUANTLAB_OUTPUT_ROOT:?QUANTLAB_OUTPUT_ROOT must be set (see environment.example)}"

mkdir -p "${DATA_ROOT}" "${OUTPUT_ROOT}"

echo "==> Executing job: ${JOB_FILE}"
echo "    data   : ${DATA_ROOT}"
echo "    output : ${OUTPUT_ROOT}"
echo "    time   : $(date -u +%FT%TZ)"

RESULTS=""
MONITOR_LOG="$(mktemp)"
if command -v /usr/bin/time >/dev/null 2>&1; then
    /usr/bin/time -v dotnet "${RUNNER_DLL}" \
        --job-file "${JOB_FILE}" \
        --data-dir "${DATA_ROOT}" \
        --output-dir "${OUTPUT_ROOT}" 2> >(tee "${MONITOR_LOG}" >&2)
else
    dotnet "${RUNNER_DLL}" \
        --job-file "${JOB_FILE}" \
        --data-dir "${DATA_ROOT}" \
        --output-dir "${OUTPUT_ROOT}"
fi
EXIT_CODE=$?

grep -E 'Maximum resident|Elapsed \(wall' "${MONITOR_LOG}" 2>/dev/null || true

exit ${EXIT_CODE}