# Convert assets\logo.png into a multi-resolution Windows .ico file.
#
# We hand-roll the .ico header (ICONDIR + N×ICONDIRENTRY + N×PNG payloads)
# the same way Platform/TrayIconRenderer does at runtime, so the produced
# file is identical in shape: PNG-encoded entries supported by the Win11
# shell since Vista.
#
# Run from repo root: pwsh ./scripts/png-to-ico.ps1

param(
    [string]$Source = (Join-Path $PSScriptRoot '..\assets\logo.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '..\assets\logo.ico'),
    [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
)

Add-Type -AssemblyName System.Drawing

$Source = [System.IO.Path]::GetFullPath($Source)
$Destination = [System.IO.Path]::GetFullPath($Destination)

if (-not (Test-Path $Source)) {
    throw "Source PNG not found: $Source"
}

Write-Output "Source : $Source"
Write-Output "Output : $Destination"
Write-Output "Sizes  : $($Sizes -join ', ')"

# Load the source PNG once; resize per target size and capture PNG bytes.
# Note: parameter $Source is typed [string]; using a different variable name
# for the loaded Image avoids PowerShell coercing it back to string.
$sourceImage = [System.Drawing.Image]::FromFile($Source)
$pngs = @()

try {
    foreach ($size in $Sizes) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

        # Use a destination Rectangle covering the whole bitmap so PNG transparency
        # is preserved (vs DrawImage(image, x, y, w, h) which skews aspect handling).
        $g.DrawImage($sourceImage, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,$ms.ToArray()
        $bmp.Dispose()
        $ms.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

# Write .ico: ICONDIR (6 bytes) + ICONDIRENTRY × N (16 bytes each) + payloads.
$fs = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)

try {
    # ICONDIR
    $bw.Write([UInt16]0)            # reserved
    $bw.Write([UInt16]1)            # type=1 icon
    $bw.Write([UInt16]$Sizes.Count) # count

    # ICONDIRENTRY × N
    $payloadOffset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $size = $Sizes[$i]
        $dim = if ($size -ge 256) { 0 } else { $size }   # 0 means 256 in spec
        $bw.Write([byte]$dim)        # width
        $bw.Write([byte]$dim)        # height
        $bw.Write([byte]0)           # colour count
        $bw.Write([byte]0)           # reserved
        $bw.Write([UInt16]1)         # colour planes
        $bw.Write([UInt16]32)        # bits per pixel
        $bw.Write([UInt32]$pngs[$i].Length)
        $bw.Write([UInt32]$payloadOffset)
        $payloadOffset += $pngs[$i].Length
    }

    # Payloads
    foreach ($png in $pngs) {
        $bw.Write($png)
    }
}
finally {
    $bw.Close()
    $fs.Close()
}

$bytes = (Get-Item $Destination).Length
Write-Output "Wrote $Destination ($bytes bytes, $($Sizes.Count) entries)"
