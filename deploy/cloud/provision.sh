#!/usr/bin/env bash
# provision.sh
# Creates the Google Cloud resources for the ResearchEngine deployment, idempotently:
#   1. GCS bucket (uniform bucket-level access) storing dataset bundles + output.
#   2. Firewall rule allowing IAP SSH (tcp:22 from 35.235.240.0/20) to tagged VMs.
#   3. Compute Engine VM with the configured service account + storage scopes so
#      gsutil works on the instance WITHOUT a key file.
#
# No application port is ever opened: the runner is a batch CLI that owns no
# listening socket. SSH for `gcloud compute ssh` / `quantlab run cloud` goes
# through Identity-Aware Proxy, so the VM needs no public IP.
#
# Requires: gcloud authenticated with permissions to create the above.
# Usage: source environment && ./provision.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/environment" ]; then
    set -a; source "${SCRIPT_DIR}/environment"; set +a
fi

PROJECT="${QUANTLAB_PROJECT:?QUANTLAB_PROJECT must be set (see environment.example)}"
ZONE="${QUANTLAB_ZONE:?QUANTLAB_ZONE must be set}"
REGION="${QUANTLAB_REGION:-$(dirname "${ZONE}")}"
BUCKET="${QUANTLAB_GCS_BUCKET:?QUANTLAB_GCS_BUCKET must be set}"
VM="${QUANTLAB_VM:-quantlab-vm}"
NETWORK="${QUANTLAB_VM_NETWORK:-default}"
SUBNET="${QUANTLAB_VM_SUBNET:-default}"
TAGS="${QUANTLAB_VM_TAGS:-quantlab-runner}"
EXT_IP="${QUANTLAB_VM_EXT_IP:-none}"
SA_EMAIL="${QUANTLAB_SA_EMAIL:-}"
MACHINE_TYPE="${QUANTLAB_VM_MACHINE_TYPE:-e2-standard-4}"
IMAGE_FAMILY="${QUANTLAB_VM_IMAGE_FAMILY:-debian-12}"
IMAGE_PROJECT="${QUANTLAB_VM_IMAGE_PROJECT:-debian-cloud}"
BOOT_DISK_SIZE="${QUANTLAB_VM_BOOT_DISK_SIZE_GB:-200}"
BOOT_DISK_TYPE="${QUANTLAB_VM_BOOT_DISK_TYPE:-pd-ssd}"
SCOPES="${QUANTLAB_VM_SERVICE_ACCOUNT_SCOPES:-storage-rw}"
SCOPES_FLAG="--scopes=${SCOPES}"
[ -n "${SA_EMAIL}" ] && SCOPES_FLAG="${SCOPES_FLAG} --service-account=${SA_EMAIL}"

IAP_CIDR="35.235.240.0/20"
FW_NAME="quantlab-allow-iap-ssh"
TAG_FW_NAME="quantlab-allow-iap-ssh-${TAGS//[^a-z0-9-]/}"

GCLOUD=(gcloud --project "${PROJECT}")

echo "==> Selecting project ${PROJECT}"
gcloud config set project "${PROJECT}"

# --- 1. GCS bucket -----------------------------------------------------------
echo "==> Ensuring bucket gs://${BUCKET}"
if ! gcloud storage buckets describe "gs://${BUCKET}" >/dev/null 2>&1; then
    gcloud storage buckets create "gs://${BUCKET}" \
        --project="${PROJECT}" \
        --location="${REGION}" \
        --default-storage-class=STANDARD \
        --uniform-bucket-level-access
    echo "    created gs://${BUCKET}"
else
    echo "    gs://${BUCKET} already exists"
fi

# --- 2. IAP SSH firewall rule (tcp:22 only) ----------------------------------
# Identity-Aware Proxy requests SSH on port 22 from the IAP CIDR, regardless of
# whether the VM has an external IP. This is the ONLY port allowed on the VM.
echo "==> Ensuring firewall rule ${FW_NAME} (IAP SSH tcp:22)"
if ! gcloud compute firewall-rules describe "${FW_NAME}" --project="${PROJECT}" >/dev/null 2>&1; then
    gcloud compute firewall-rules create "${FW_NAME}" \
        --network="${NETWORK}" \
        --direction=INGRESS \
        --action=ALLOW \
        --rules=tcp:22 \
        --source-ranges="${IAP_CIDR}" \
        --target-tags="${TAGS}" \
        --priority=1000
    echo "    created ${FW_NAME}"
else
    echo "    ${FW_NAME} already exists"
fi

# --- 3. Compute Engine VM -----------------------------------------------------
if gcloud compute instances describe "${VM}" --zone="${ZONE}" --project="${PROJECT}" >/dev/null 2>&1; then
    echo "==> VM ${VM} already exists; skipping create."
    echo "    If you changed VM geometry, delete it and re-run (data under "
    echo "    QUANTLAB_DATA_ROOT must be re-staged via stage-data.sh)."
    exit 0
fi

echo "==> Creating VM ${VM} (${MACHINE_TYPE}, ${BOOT_DISK_SIZE}GB ${BOOT_DISK_TYPE})"
CREATE_ARGS=(
    create "${VM}"
    --project="${PROJECT}"
    --zone="${ZONE}"
    --machine-type="${MACHINE_TYPE}"
    --image-family="${IMAGE_FAMILY}"
    --image-project="${IMAGE_PROJECT}"
    --boot-disk-size="${BOOT_DISK_SIZE}GB"
    --boot-disk-type="${BOOT_DISK_TYPE}"
    --network="${NETWORK}"
    --subnet="${SUBNET}"
    --tags="${TAGS}"
    --scopes="${SCOPES}"
)
if [ -n "${SA_EMAIL}" ]; then
    CREATE_ARGS+=(--service-account="${SA_EMAIL}")
fi
if [ "${EXT_IP}" = "none" ]; then
    CREATE_ARGS+=(--no-address)
else
    CREATE_ARGS+=(--no-restart-on-failure)
    CREATE_ARGS+=(--preemptible=false)
fi

gcloud compute instances "${CREATE_ARGS[@]}"

echo ""
echo "==> Provisioning complete."
echo "    Bucket : gs://${BUCKET}/${QUANTLAB_GCS_PREFIX:-quantlab}/"
echo "    VM      : ${VM} (no public IP; SSH via IAP)"
echo "    Verify : gcloud compute ssh --project=${PROJECT} --zone=${ZONE} ${VM}"
echo "    Next   : ./setup-vm.sh  then  ./stage-data.sh <bundle> --verify"