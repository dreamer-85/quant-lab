# install-gcloud-windows.ps1
# Installs the Google Cloud SDK on Windows (gcloud + gsutil), then configures
# a service account for these scripts. Run in a PowerShell window:
#
#   powershell -ExecutionPolicy Bypass -File deploy\cloud\install-gcloud-windows.ps1
#
# Uses the official installer: https://cloud.google.com/sdk/docs/install-sdk
# Skips re-download if gcloud is already on PATH.

$ErrorActionPreference = "Stop"

function Is-GcloudInstalled {
    return [bool](Get-Command gcloud -ErrorAction SilentlyContinue)
}

if (Is-GcloudInstalled) {
    Write-Host "gcloud already installed:"
    & gcloud --version
    exit 0
}

# Prefer winget if available; fall back to the SDK installer zip.
if (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host "==> Installing 'Google Cloud SDK' via winget"
    winget install -e --id Google.CloudSDK --accept-package-agreements --accept-source-agreements
    if (Get-Command gcloud -ErrorAction SilentlyContinue) {
        $install = (Get-Command gcloud).Source
    } else {
        $install = "$env:LOCALAPPDATA\Google\Cloud SDK\google-cloud-sdk\bin\gcloud.cmd"
    }
} else {
    $version  = "521.0.0"
    $zip      = "$env:TEMP\google-cloud-sdk.zip"
    $dest     = "$env:USERPROFILE\google-cloud-sdk"
    Write-Host "==> Downloading Google Cloud SDK $version (no winget found)"
    Invoke-WebRequest -Uri "https://dl.google.com/dl/cloudsdk/channels/rapid/downloads/google-cloud-sdk-$version-windows-x86_64.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $env:USERPROFILE -Force
    Remove-Item $zip
    $install = "$dest\bin\gcloud.cmd"
}

Write-Host ""
Write-Host "gcloud installed at: $install"
Write-Host "Adding to PATH (current session + user)"
$script:cloudRootBin = Split-Path -Parent $install
$env:Path = "$script:cloudRootBin;$env:Path"
$existing = [Environment]::GetEnvironmentVariable("Path", "User")
if ($existing -notlike "*$script:cloudRootBin*") {
    [Environment]::SetEnvironmentVariable("Path", "$existing;$script:cloudRootBin", "User")
}

Write-Host ""
Write-Host "==> Done. Next steps (pick one):"
Write-Host "  1) Use your own Google account:"
Write-Host "       gcloud init --console-only"
Write-Host "     then:"
Write-Host "       gcloud auth application-default login"
Write-Host ""
Write-Host "  2) Or use a service-account key for automation:"
Write-Host "       gcloud auth activate-service-account --key-file=<path-to-sa-key.json>"
Write-Host "       gcloud config set project <PROJECT>"
Write-Host ""
Write-Host "Then configure the CLI (edit deploy\cloud\environment from environment.example):"
Write-Host "       python -m quantlab env --show"