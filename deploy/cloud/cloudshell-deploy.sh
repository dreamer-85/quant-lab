#!/usr/bin/env bash
# cloudshell-deploy.sh
# One-shot deploy of the QuantLab ResearchEngine to a Cloud Run Job — runs
# entirely in Cloud Shell (or any machine with gcloud). No local files needed.
#
#   bash deploy/cloud/cloudshell-deploy.sh
#
# It: configures the project, provisions the runtime service account + IAM,
# creates (or updates) the Cloud Run Job with the two GCS volume mounts,
# and optionally uploads the job file. It does NOT stage the dataset — see
# stage-bucket.sh (workstation) or the notes at the bottom.

set -euo pipefail

# ---------------------------------------------------------------- config ----
# Baked-in values (edit here if your infra differs)
PROJECT_ID="project-603dc9c7-b4e5-45c5-9b5"
REGION="europe-west1"
JOB_NAME="quant-lab"
SA_NAME="quantlab-runner"
GCS_BUCKET="quant-lab"
IMAGE="europe-west1-docker.pkg.dev/${PROJECT_ID}/cloud-run-source-deploy/quant-lab/quant-lab:d9c8914a286bec056ab87e10be89a12c390c3d97"

DATA_ROOT="/quantlab/data"
OUTPUT_ROOT="/quantlab/output"
CACHE_ROOT="/quantlab/cache"
TEMP_ROOT="/tmp"
CPU="4"
MEMORY="16Gi"
TIMEOUT="24h"
# ---------------------------------------------------------------------------

SA_EMAIL="${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"
ENV_VARS="QUANTLAB_DATA_ROOT=${DATA_ROOT},QUANTLAB_OUTPUT_ROOT=${OUTPUT_ROOT},QUANTLAB_CACHE_ROOT=${CACHE_ROOT},QUANTLAB_TEMP_ROOT=${TEMP_ROOT}"
JOB_ARGS="--job-file=${DATA_ROOT}/job.json,--data-dir=${DATA_ROOT},--output-dir=${OUTPUT_ROOT}"

echo "==> Project: ${PROJECT_ID}  Region: ${REGION}"

# --- 0. Baseline -------------------------------------------------------------
gcloud config set project "${PROJECT_ID}"
gcloud config set run/region "${REGION}"
gcloud config set builds/region "${REGION}"

# --- 1. Runtime service account + bucket permissions --------------------------
gcloud iam service-accounts create "${SA_NAME}" --project="${PROJECT_ID}" || true

gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
  --member="serviceAccount:${SA_EMAIL}" \
  --role=roles/storage.objectAdmin

gcloud projects add-iam-policy-binding "${PROJECT_ID}" \
  --member="serviceAccount:${SA_EMAIL}" \
  --role=roles/run.invoker

# --- 2. Cloud Run Job (create, or update if it already exists) ----------------
FLAGS=(
  --project="${PROJECT_ID}"
  --region="${REGION}"
  --image="${IMAGE}"
  --cpu="${CPU}"
  --memory="${MEMORY}"
  --task-timeout="${TIMEOUT}"
  --service-account="${SA_EMAIL}"
  --add-volume name=data,type=cloud-storage,bucket="${GCS_BUCKET}",readonly=true
  --add-volume-mount volume=data,mount-path="${DATA_ROOT}"
  --add-volume name=output,type=cloud-storage,bucket="${GCS_BUCKET}",readonly=false
  --add-volume-mount volume=output,mount-path="${OUTPUT_ROOT}"
  --set-env-vars="${ENV_VARS}"
  --args="${JOB_ARGS}"
)

if gcloud run jobs describe "${JOB_NAME}" --project="${PROJECT_ID}" --region="${REGION}" >/dev/null 2>&1; then
  echo "==> Updating Cloud Run Job ${JOB_NAME}"
  gcloud run jobs update "${JOB_NAME}" "${FLAGS[@]}"
else
  echo "==> Creating Cloud Run Job ${JOB_NAME}"
  gcloud run jobs create "${JOB_NAME}" "${FLAGS[@]}"
fi

echo ""
echo "==> Deployed. Job: ${JOB_NAME}  Image: ${IMAGE}"
echo "    Volumes:  ${GCS_BUCKET} (RO) -> ${DATA_ROOT}   (RW) -> ${OUTPUT_ROOT}"
echo ""
echo "    Before first run, the bucket root needs the Lean layout AND job.json:"
echo "      gs://${GCS_BUCKET}/market-hours/  symbol-properties/  crypto/  job.json"
echo "    (workstation: bash deploy/cloud/stage-bucket.sh --data-root <lean> --job ... --bucket gs://${GCS_BUCKET})"
echo ""
echo "    Then run the job:"
echo "      gcloud run jobs execute ${JOB_NAME} --region=${REGION} --wait"