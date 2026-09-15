#!/usr/bin/env bash
# stage-data.sh
# Downloads the dataset bundle from GCS and lays it out as a Lean-compatible
# data folder on the VM ($QUANTLAB_DATA_ROOT).
#
# The bundle is a tar.gz produced locally with:
#   python -m quantlab bundle build --data-root <lean-data> --out bybit.tgz
#
# GCS object: gs://<QUANTLAB_GCS_BUCKET>/<QUANTLAB_GCS_PREFIX>/<bundle>.
# Auth: on the VM the attached service account + storage-rw scope are used
# (no key file needed); on a workstation gsutil uses the gcloud user account.
#
# No ports are involved - this only reads from GCS over IAP-free service auth.
# Usage: source environment && ./stage-data.sh <bundle-object> [--verify]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/environment" ]; then
    set -a; source "${SCRIPT_DIR}/environment"; set +a
fi

BUNDLE="${1:?usage: stage-data.sh <bundle-object> [--verify]}"
VERIFY="${2:-}"
DATA_ROOT="${QUANTLAB_DATA_ROOT:?QUANTLAB_DATA_ROOT must be set (see environment.example)}"
GCS_BUCKET="${QUANTLAB_GCS_BUCKET:?QUANTLAB_GCS_BUCKET must be set}"
GCS_PREFIX="${QUANTLAB_GCS_PREFIX:-quantlab}"
OBJECT="gs://${GCS_BUCKET#gs://}/${GCS_PREFIX}/${BUNDLE}"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

umask 022

if [ -n "${QUANTLAB_PROJECT:-}" ]; then
    gcloud config set project "${QUANTLAB_PROJECT}" >/dev/null 2>&1 || true
fi

echo "==> Creating data root ${DATA_ROOT}"
sudo mkdir -p "${DATA_ROOT}"
sudo chown "${USER}" "${DATA_ROOT}"

echo "==> Downloading ${OBJECT}"
gsutil -m cp "${OBJECT}" "${WORK_DIR}/bundle.tgz" || {
    echo "ERROR: download failed (bucket/prefix or credentials wrong?)";
    echo "   Trying a path listing to help diagnose:";
    gsutil ls "gs://${GCS_BUCKET#gs://}/${GCS_PREFIX}/" || true;
    exit 1;
}

echo "==> Extracting into ${DATA_ROOT}"
tar -xzf "${WORK_DIR}/bundle.tgz" -C "${DATA_ROOT}"

if [ -n "${VERIFY}" ]; then
    echo "==> Verifying expected layout"
    FAILED=0
    for p in \
        "market-hours/market-hours-database.json" \
        "symbol-properties/symbol-properties-database.csv" \
        "symbol-properties/security-database.csv"; do
        if [ ! -f "${DATA_ROOT}/${p}" ]; then
            echo "MISSING: ${p}"
            FAILED=1
        fi
    done
    if find "${DATA_ROOT}/crypto" -maxdepth 1 -type d -empty 2>/dev/null | grep -q .; then
        echo "WARNING: no dataset under ${DATA_ROOT}/crypto (dataset folders should not be empty)"
        FAILED=1
    fi
    [ "${FAILED}" -eq 0 ] || { echo "VERIFY FAILED"; exit 1; }
    echo "VERIFY OK"
fi

echo "==> Data staged."
echo "    data root : ${DATA_ROOT}"
echo "    contents  :"
du -sh "${DATA_ROOT}"/* 2>/dev/null | head -20