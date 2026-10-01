#Requires -Version 5.1
<#
    Generates src\WindowsDebloater\app.ico (multi-resolution icon) using the
    .NET Framework System.Drawing stack that ships with Windows PowerShell 5.1.
    Run manually whenever the icon needs changing:  powershell -File tools\Make-Icon.ps1
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$outDir = Join-Path $PSScriptRoot '..\src\WindowsDebloater'
$outFile = Join-Path $outDir 'app.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2, [int]($size * 0.22))
    $pad = [Math]::Max(1, [int]($size * 0.06))

    $body = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 33, 38))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $r, $r, 180, 90)
    $path.AddArc($size - $pad - $r, $pad, $r, $r, 270, 90)
    $path.AddArc($size - $pad - $r, $size - $pad - $r, $r, $r, 0, 90)
    $path.AddArc($pad, $size - $pad - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath($body, $path)

    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 76, 194, 255))
    $cx = $size * 0.5
    $cy = $size * 0.44
    $rad = $size * 0.26
    $thick = [Math]::Max(1.5, $size * 0.085)
    $pen = New-Object System.Drawing.Pen ($accent, $thick)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $g.DrawArc($pen, $cx - $rad, $cy - $rad, $rad * 2, $rad * 2, 130, 230)
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF ([Math]::Max($size * 0.32, $thick), $cy)),
        (New-Object System.Drawing.PointF ([Math]::Max($size * 0.44, $thick), $size * 0.68)),
        (New-Object System.Drawing.PointF ([Math]::Min($size * 0.76, $size - $thick), $size * 0.26))
    ))

    $pen.Dispose(); $accent.Dispose(); $body.Dispose(); $path.Dispose()
    return $bmp
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    # The leading comma stops PowerShell from unrolling the byte array into the
    # output stream, which would corrupt the payload.
    return ,$ms.ToArray()
}

# ICONDIR (6 bytes) + ICONDIRENTRY (16 bytes each) + PNG payloads
$entries = New-Object System.Collections.Generic.List[byte[]]
$dirBytes = New-Object System.Collections.Generic.List[byte]

# ICONDIR is 6 bytes: reserved(2), type(2)=1 icon, count(2)
[void]$dirBytes.AddRange([byte[]](0, 0))
[void]$dirBytes.AddRange([byte[]](1, 0))
[void]$dirBytes.AddRange([byte[]]($sizes.Count, 0))
$offset = 6 + (16 * $sizes.Count)

foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $png = ConvertTo-PngBytes $bmp
    $bmp.Dispose()

    $entries.Add($png)

    $w = if ($s -ge 256) { 0 } else { $s }
    $h = if ($s -ge 256) { 0 } else { $s }

    # ICONDIRENTRY, 16 bytes: width(1), height(1), colorCount(1), reserved(1),
    # planes(2), bitCount(2), bytesInRes(4), imageOffset(4).
    [void]$dirBytes.AddRange([byte[]]@($w, $h, 0, 0))
    [void]$dirBytes.AddRange([byte[]]@(1, 0))
    [void]$dirBytes.AddRange([byte[]]@(32, 0))
    [void]$dirBytes.AddRange([BitConverter]::GetBytes([UInt32]$png.Length))
    [void]$dirBytes.AddRange([BitConverter]::GetBytes([UInt32]$offset))

    $offset += $png.Length
}

$stream = [System.IO.File]::Create($outFile)
$stream.Write($dirBytes.ToArray(), 0, $dirBytes.Count)
foreach ($e in $entries) { $stream.Write($e, 0, $e.Length) }
$stream.Close()

Write-Host "Wrote $outFile ($((Get-Item $outFile).Length) bytes, $($sizes.Count) resolutions)"