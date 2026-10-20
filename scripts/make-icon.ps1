# Generates src/SonarLite/app.ico: an orange disc with a white sound wave, drawn at each size
# (rather than downscaled) and stored as PNG frames, which Windows supports in .ico files.
param([string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SonarLite\app.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $size / 32.0   # design grid is 32x32

    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 106, 0))
    $g.FillEllipse($bg, 1 * $u, 1 * $u, 30 * $u, 30 * $u)

    # Five bars blur together at small sizes (Task Manager, tray at 100 %), so those get three.
    if ($size -le 24) { $xs = 10, 16, 22; $heights = 10, 18, 10; $width = [Math]::Max(2, 3 * $u) }
    else { $xs = 8, 12, 16, 20, 24; $heights = 6, 14, 20, 14, 6; $width = 3 * $u }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $width
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    for ($i = 0; $i -lt $xs.Count; $i++) {
        $x = $xs[$i] * $u
        $g.DrawLine($pen, $x, (16 - $heights[$i] / 2) * $u, $x, (16 + $heights[$i] / 2) * $u)
    }
    $g.Dispose(); $bg.Dispose(); $pen.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Data = $ms.ToArray() }
}

# ICO container: ICONDIR, one ICONDIRENTRY per frame, then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 0 means 256
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($Output, $out.ToArray())
"Wrote $Output ($($frames.Count) sizes, $($out.Length) bytes)"
