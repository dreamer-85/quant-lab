#!/usr/bin/env bash
# stage-bucket.sh
# Uploads the Lean data layout to the GCS bucket ROOT so the Cloud Run job's
# GCS volume mount (/quantlab/data) lines up with what the engine reads.
#
# The Cloud Run cloud-storage volume mounts the BUCKET ROOT at the mount path,
# so market-hours/, symbol-properties/ and crypto/ must sit at the bucket root
# (NOT under the quant-lab/ prefix used by the .tgz workflow).
#
# Usage:
#   ./stage-bucket.sh --data-root <lean-data-root> --job deploy/cloud/jobs/xxx.json [--verify]
#   ./stage-bucket.sh --bundle <path.tgz>         --job deploy/cloud/jobs/xxx.json [--verify]
#     (--bundle file may be local or a gs:// object; layout is extracted first)
#
# Bucket: --bucket gs://...  >  $QUANTLAB_GCS_BUCKET (deploy/cloud/environment)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/environment" ]; then
    set -a; source "${SCRIPT_DIR}/environment"; set +a
fi

BUCKET=""
DATA_ROOT=""
BUNDLE=""
JOB_FILE=""
VERIFY=0

while [ $# -gt 0 ]; do
    case "$1" in
        --bucket) BUCKET="$2"; shift 2;;
        --data-root) DATA_ROOT="$2"; shift 2;;
        --bundle) BUNDLE="$2"; shift 2;;
        --job) JOB_FILE="$2"; shift 2;;
        --verify) VERIFY=1; shift;;
        *) echo "usage: stage-bucket.sh --data-root <dir>|--bundle <tgz|gs://...> [--job <json>] [--verify] [--bucket gs://...]"; exit 2;;
    esac
done

BUCKET="${BUCKET:-${QUANTLAB_GCS_BUCKET:-}}"
[ -n "${BUCKET}" ] || { echo "error: bucket not set (--bucket or QUANTLAB_GCS_BUCKET)"; exit 2; }
[ -n "${DATA_ROOT}" ] || [ -n "${BUNDLE}" ] || { echo "error: need --data-root or --bundle"; exit 2; }
BUCKET="${BUCKET%/}"   # strip trailing slash

if [ "${BUCKET}" != "gs://quant-lab" ]; then
    echo "warning: bucket is ${BUCKET}; Cloud Run mounts the bucket ROOT at /quantlab/data,"
    echo "         so the Lean layout will be uploaded to the root of ${BUCKET}"
fi

# --- Source layout -----------------------------------------------------------
TMP_DIR=""
SRC="${DATA_ROOT}"
if [ -n "${BUNDLE}" ]; then
    BUNDLE_LOCAL="${BUNDLE}"
    if [[ "${BUNDLE}" == gs://* ]]; then
        BUNDLE_LOCAL="$(mktemp -t quantlab-bundle.XXXXXX.tar.gz)"
        echo "==> Downloading ${BUNDLE}"
        gsutil cp "${BUNDLE}" "${BUNDLE_LOCAL}"
    fi
    TMP_DIR="$(mktemp -d)"
    echo "==> Extracting ${BUNDLE_LOCAL}"
    tar -xzf "${BUNDLE_LOCAL}" -C "${TMP_DIR}"
    SRC="${TMP_DIR}"
    [ -n "${DATA_ROOT}" ] && echo "note: --bundle wins over --data-root"
fi
[ -d "${SRC}/market-hours" ] || { echo "error: no market-hours/ under ${SRC}"; exit 1; }

# --- Verify required DB files (mirror bundle build checks) --------------------
if [ "${VERIFY}" -eq 1 ]; then
    for f in \
        market-hours/market-hours-database.json \
        symbol-properties/symbol-properties-database.csv \
        symbol-properties/security-database.csv; do
        [ -f "${SRC}/${f}" ] || { echo "error: missing ${f} under ${SRC}"; exit 1; }
    done
    echo "==> Verify OK: required DB files present"
fi

# --- Upload to bucket root -----------------------------------------------------
for sub in market-hours symbol-properties crypto; do
    if [ -d "${SRC}/${sub}" ]; then
        echo "==> rsync ${sub}/ -> ${BUCKET}/${sub}/"
        gsutil -m rsync -r "${SRC}/${sub}" "${BUCKET}/${sub}"
    else
        echo "note: no ${sub}/ found, skipping"
    fi
done

# --- Upload the job file to the mount root as job.json -------------------------
if [ -n "${JOB_FILE}" ]; then
    [ -f "${JOB_FILE}" ] || { echo "error: job file not found: ${JOB_FILE}"; exit 1; }
    echo "==> gsutil cp ${JOB_FILE} ${BUCKET}/job.json"
    gsutil cp "${JOB_FILE}" "${BUCKET}/job.json"
fi

[ -n "${TMP_DIR}" ] && rm -rf "${TMP_DIR}"

echo ""
echo "==> Bucket staged. Cloud Run mounts ${BUCKET} RO at /quantlab/data and"
echo "    RW at /quantlab/output; job.json sits at ${BUCKET}/job.json."