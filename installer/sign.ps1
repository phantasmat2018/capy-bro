# Authenticode-sign one or more PE files via signtool with timestamp.
# Run after .\scripts\publish.ps1 + makensis installer\installer.nsi.
#
# Required env vars:
#   CERT_PATH       Path to the .pfx (or PVK) file
#   CERT_PASSWORD   Password for the .pfx
#
# Usage:
#   .\installer\sign.ps1                    # signs published exe + installer
#   .\installer\sign.ps1 -Files file1.exe   # signs specific files

param(
    [string[]]$Files = @(
        "publish\win-x64\CapyBro.exe",
        "installer\CapyBro-Setup-2.0.0.exe"
    )
)

$ErrorActionPreference = "Stop"

if (-not $env:CERT_PATH) {
    Write-Error "CERT_PATH environment variable not set. Point it at your .pfx."
    exit 1
}

if (-not $env:CERT_PASSWORD) {
    Write-Error "CERT_PASSWORD environment variable not set."
    exit 1
}

if (-not (Test-Path $env:CERT_PATH)) {
    Write-Error "Certificate file not found: $env:CERT_PATH"
    exit 1
}

# Standard timestamp authority (DigiCert is reliable; Sectigo http://timestamp.sectigo.com works too).
$timestampUrl = "http://timestamp.digicert.com"

foreach ($file in $Files) {
    if (-not (Test-Path $file)) {
        Write-Warning "Skipping (not found): $file"
        continue
    }

    Write-Host "Signing: $file" -ForegroundColor Cyan

    & signtool.exe sign `
        /f $env:CERT_PATH `
        /p $env:CERT_PASSWORD `
        /tr $timestampUrl `
        /td sha256 `
        /fd sha256 `
        $file

    if ($LASTEXITCODE -ne 0) {
        Write-Error "signtool failed for $file with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

Write-Host ""
Write-Host "All files signed." -ForegroundColor Green
