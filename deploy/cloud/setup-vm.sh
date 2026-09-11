#!/usr/bin/env bash
# setup-vm.sh
# One-time VM bootstrap: installs dependencies, clones the repo, builds the
# Runner, runs the ResearchEngine test suite.
#
# Usage: source environment && ./setup-vm.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "${SCRIPT_DIR}/environment" ]; then
    set -a; source "${SCRIPT_DIR}/environment"; set +a
fi

WORKDIR="${QUANTLAB_WORKDIR:-${HOME}}"
REPO_URL="${QUANTLAB_REPO_URL:?QUANTLAB_REPO_URL must be set (see environment.example)}"
REPO_DIR="${QUANTLAB_REPO_DIR:-${WORKDIR}/myEngine}"

# 1. OS + tooling
"${SCRIPT_DIR}/install-dependencies.sh"

# 2. Clone (or refresh) the repository
if [ ! -d "${REPO_DIR}/.git" ]; then
    echo "==> Cloning ${REPO_URL}"
    if [ -n "${GIT_PAT:-}" ]; then
        AUTH_URL="${REPO_URL/https:\/\//https:\/\/${GIT_PAT}@}"
        git clone "${AUTH_URL}" "${REPO_DIR}"
    else
        git clone "${REPO_URL}" "${REPO_DIR}"
    fi
else
    echo "==> Refreshing ${REPO_DIR}"
    git -C "${REPO_DIR}" fetch --all --prune
    git -C "${REPO_DIR}" checkout main
    git -C "${REPO_DIR}" pull --ff-only
fi

# 3. Build (Release)
echo "==> Building Runner (Release)"
dotnet build "${REPO_DIR}/Research/Runner/QuantConnect.Research.Runner.csproj" \
    -c Release

# 4. Run the engine test suite
echo "==> Running ResearchEngine tests"
dotnet test "${REPO_DIR}/Tests/Research/EngineTests/QuantConnect.Research.Engine.Tests.csproj" \
    -c Release

# 5. Smoke test the Runner binary
RUNNER_DLL="${REPO_DIR}/Research/Runner/bin/Release/net10.0/QuantConnect.Research.Runner.dll"
echo "==> Synthetic benchmark smoke test (100k events)"
dotnet "${RUNNER_DLL}" --synthetic-benchmark 100000 || true

echo "==> VM setup complete."
echo "    Next: ./stage-data.sh   then   ./run-job.sh jobs/<job>.json"