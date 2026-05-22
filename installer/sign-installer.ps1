# FZ6-F5 / L34: optional Authenticode signing hook for the CapyBro
# installer .exe.  Run this script AFTER `makensis installer.nsi`
# produces the unsigned installer; it wraps signtool with the
# parameters NSIS itself doesn't manage.
#
# Why this is a separate script:
#   1. NSIS has no built-in sign step — the standard pattern is
#      "build, then sign the produced .exe".
#   2. The signing certificate is sensitive material that must NOT
#      be checked into the repo.  Operators supply it via
#      environment variables OR a Windows certificate-store thumbprint
#      so CI can pass it through without writing it to disk.
#   3. Without a cert, the script is a documented no-op rather than
#      a build failure — local-dev builds continue to work; signing
#      only kicks in when an operator provides the cert material.
#
# Configuration (env vars):
#   CAPYBRO_SIGN_CERT_PATH     — path to a .pfx file (mutually exclusive
#                                with thumbprint).  Pair with
#                                CAPYBRO_SIGN_CERT_PASSWORD for the .pfx
#                                password.
#   CAPYBRO_SIGN_CERT_PASSWORD — .pfx password (only with CERT_PATH).
#   CAPYBRO_SIGN_THUMBPRINT    — SHA1 thumbprint of a cert already
#                                installed in CurrentUser\My (preferred
#                                — never touches disk).
#   CAPYBRO_SIGN_TIMESTAMP_URL — RFC3161 timestamp server, default
#                                http://timestamp.digicert.com.  Free
#                                alternatives: http://timestamp.sectigo.com,
#                                http://timestamp.globalsign.com/tsa/r6advanced1.
#
# Usage (local):
#   $env:CAPYBRO_SIGN_THUMBPRINT = "ABCD…1234"
#   pwsh installer\sign-installer.ps1
#
# Usage (CI):
#   - name: Sign installer
#     env:
#       CAPYBRO_SIGN_CERT_PATH: ${{ secrets.SIGN_CERT_PATH }}
#       CAPYBRO_SIGN_CERT_PASSWORD: ${{ secrets.SIGN_CERT_PASSWORD }}
#     run: pwsh installer/sign-installer.ps1
#
# Exit codes:
#   0 — signed successfully OR skipped because no cert was configured.
#   1 — cert was configured but signing failed (build CI should treat
#       this as failure).

param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'CapyBro-Setup-2.0.0.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $InstallerPath)) {
    Write-Error "Installer not found at $InstallerPath — run 'makensis installer.nsi' first."
    exit 1
}

$thumbprint = $env:CAPYBRO_SIGN_THUMBPRINT
$certPath = $env:CAPYBRO_SIGN_CERT_PATH
$certPassword = $env:CAPYBRO_SIGN_CERT_PASSWORD
$timestampUrl = if ($env:CAPYBRO_SIGN_TIMESTAMP_URL) {
    $env:CAPYBRO_SIGN_TIMESTAMP_URL
} else {
    'http://timestamp.digicert.com'
}

if (-not $thumbprint -and -not $certPath) {
    Write-Host "No signing cert configured (CAPYBRO_SIGN_THUMBPRINT or CAPYBRO_SIGN_CERT_PATH unset). Skipping — SmartScreen will warn until the installer is signed."
    Write-Host "To enable signing, see the header of this script."
    exit 0
}

if ($thumbprint -and $certPath) {
    Write-Error "Both CAPYBRO_SIGN_THUMBPRINT and CAPYBRO_SIGN_CERT_PATH are set — choose one."
    exit 1
}

# signtool ships with the Windows SDK; resolve it via vswhere or PATH.
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    $sdkBase = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkBase) {
        $signtool = Get-ChildItem $sdkBase -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
    }
}

if (-not $signtool) {
    Write-Error "signtool.exe not found. Install the Windows 10/11 SDK and re-run."
    exit 1
}

$signtoolPath = $signtool.Path ?? $signtool.FullName

$args = @(
    'sign'
    '/fd', 'SHA256'
    '/tr', $timestampUrl
    '/td', 'SHA256'
)

if ($thumbprint) {
    $args += @('/sha1', $thumbprint)
} else {
    $args += @('/f', $certPath)
    if ($certPassword) {
        $args += @('/p', $certPassword)
    }
}

$args += $InstallerPath

Write-Host "Signing $InstallerPath …"
& $signtoolPath @args
if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool failed (exit code $LASTEXITCODE)."
    exit 1
}

Write-Host "Verifying signature …"
& $signtoolPath verify /pa /v $InstallerPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "Signature verification failed (exit code $LASTEXITCODE)."
    exit 1
}

Write-Host "OK — installer signed and verified."
