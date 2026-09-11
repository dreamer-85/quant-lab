#!/usr/bin/env bash
# sync-results.sh
# Uploads the VM output root (or a single jobId) to GCS, and optionally pulls
# a job's results back down to a local directory.
#
# Upload:  source environment && ./sync-results.sh --up [jobId]
# Download (run on the workstation):
#         source environment && ./sync-results.sh --down <jobId> <local-dir>
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/environment" ]; then
    set -a; source "${SCRIPT_DIR}/environment"; set +a
fi

MODE="${1:?usage: sync-results.sh (--up|--down) [args]}"
GCS_BUCKET="${QUANTLAB_GCS_BUCKET:?QUANTLAB_GCS_BUCKET must be set}"
GCS_PREFIX="${QUANTLAB_GCS_PREFIX:-quantlab}"
BUCKET_PATH="gs://${GCS_BUCKET#gs://}/${GCS_PREFIX}/output"

case "${MODE}" in
    --up)
        OUTPUT_ROOT="${QUANTLAB_OUTPUT_ROOT:?QUANTLAB_OUTPUT_ROOT must be set}"
        JOB_ID="${2:-}"
        SRC="${OUTPUT_ROOT}"
        if [ -n "${JOB_ID}" ]; then SRC="${OUTPUT_ROOT}/${JOB_ID}"; fi
        echo "==> Uploading ${SRC} -> ${BUCKET_PATH}/"
        gsutil -m cp -r "${SRC}" "${BUCKET_PATH}/"
        ;;

    --down)
        JOB_ID="${2:?usage: sync-results.sh --down <jobId> <local-dir>}"
        LOCAL_DIR="${3:?usage: sync-results.sh --down <jobId> <local-dir>}"
        mkdir -p "${LOCAL_DIR}"
        echo "==> Downloading ${BUCKET_PATH}/${JOB_ID} -> ${LOCAL_DIR}"
        gsutil -m cp -r "${BUCKET_PATH}/${JOB_ID}" "${LOCAL_DIR}/"
        ;;

    *)
        echo "unknown mode: ${MODE} (use --up or --down)"
        exit 2
        ;;
esac

echo "==> Done."