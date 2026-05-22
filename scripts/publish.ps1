# Self-contained single-file publish for win-x64.
# Run from repo root: .\scripts\publish.ps1

$ErrorActionPreference = "Stop"

$version = "2.0.0"
$outputDir = "publish/win-x64"

if (Test-Path $outputDir) {
    Remove-Item $outputDir -Recurse -Force
}

dotnet publish src/CapyBro `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:Version=$version `
    -p:AssemblyVersion=$version `
    -p:FileVersion=$version `
    -p:AssemblyTitle="CapyBro" `
    -p:Product="CapyBro" `
    -p:Copyright="(c) 2025" `
    -o $outputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$exePath = Join-Path $outputDir "CapyBro.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "Expected output not found: $exePath"
    exit 1
}

$sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
Write-Host ""
Write-Host "Published: $exePath ($sizeMB MB)" -ForegroundColor Green
