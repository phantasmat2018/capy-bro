# Full release pipeline: publish + NSIS installer + (optionally) sign.
# Run from repo root: .\scripts\release.ps1

$ErrorActionPreference = "Stop"

# 1. Publish
Write-Host "=== Step 1/3: Publish ===" -ForegroundColor Cyan
& "$PSScriptRoot\publish.ps1"

# 2. NSIS installer
Write-Host ""
Write-Host "=== Step 2/3: NSIS installer ===" -ForegroundColor Cyan
$makensis = $null
$candidates = @(
    "C:\Program Files (x86)\NSIS\Bin\makensis.exe",
    "C:\Program Files\NSIS\Bin\makensis.exe",
    "C:\Program Files (x86)\NSIS\makensis.exe",
    "C:\Program Files\NSIS\makensis.exe"
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $makensis = $c; break }
}

if (-not $makensis) {
    Write-Warning "makensis.exe not found in standard locations — skipping installer build."
    Write-Warning "Install NSIS from https://nsis.sourceforge.io/Download and re-run."
} else {
    & $makensis "installer\installer.nsi"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "makensis failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "Built: CapyBro-Setup-2.0.0.exe" -ForegroundColor Green
}

# 3. Sign (optional — only if cert env vars are set)
Write-Host ""
Write-Host "=== Step 3/3: Sign ===" -ForegroundColor Cyan
if ($env:CERT_PATH -and $env:CERT_PASSWORD) {
    & "$PSScriptRoot\..\installer\sign.ps1"
} else {
    Write-Warning "CERT_PATH / CERT_PASSWORD not set — skipping signing."
    Write-Warning "Without a signed binary, SmartScreen will warn until reputation accrues."
}

Write-Host ""
Write-Host "Release pipeline complete." -ForegroundColor Green
