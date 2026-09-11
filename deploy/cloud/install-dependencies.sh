#!/usr/bin/env bash
# install-dependencies.sh
# Installs the software required to build and run the ResearchEngine on
# Debian/Ubuntu: .NET SDK, git, unzip, and the Google Cloud tools.
#
# Run as a regular user; sudo is prompted for the OS packages.
set -euo pipefail

DOTNET_VERSION="${DOTNET_VERSION:-10.0}"

echo "==> Updating apt and installing OS packages"
sudo apt-get update -y
sudo apt-get install -y --no-install-recommends \
    apt-transport-https \
    ca-certificates \
    curl \
    git \
    unzip \
    zip \
    jq \
    python3 \
    python3-pip

# --- .NET SDK --------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "==> Installing .NET SDK ${DOTNET_VERSION} via the install script"
    curl -sSL https://dot.net/v1/dotnet-install.sh \
        | bash -s -- --channel "${DOTNET_VERSION}" --install-dir "${HOME}/.dotnet"
    echo 'export PATH="$HOME/.dotnet:$PATH"' >> "${HOME}/.bashrc"
    export PATH="$HOME/.dotnet:$PATH"
else
    echo "==> .NET already present: $(dotnet --version)"
fi

# --- Google Cloud SDK (gcloud + gsutil) -------------------------------------
if ! command -v gsutil >/dev/null 2>&1; then
    echo "==> Installing Google Cloud CLI"
    sudo apt-get install -y google-cloud-cli || \
    {
        echo "==> google-cloud-cli not in apt; using the standalone installer"
        curl -sSL https://sdk.cloud.google.com | bash -s -- --disable-prompts
        export PATH="$HOME/google-cloud-sdk/bin:$PATH"
        echo 'export PATH="$HOME/google-cloud-sdk/bin:$PATH"' >> "${HOME}/.bashrc"
    }
else
    echo "==> gsutil already present"
fi

echo "==> Done. Re-login (or source ~/.bashrc) so dotnet/gcloud are on PATH."
echo "    Then: gcloud auth activate-service-account --key-file=sa-key.json"