# Regenerates src/NetLedger.Tray/Resources/NetLedger.ico: an upload and a download arrow on a dark tile.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\NetLedger.Tray\Resources\NetLedger.ico'))

Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 16.0

    # Rounded tile
    $r = 3.5 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($w - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($w - 2 * $r, $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 31, 41, 51))), $path)

    $up = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 124, 195, 74))
    $down = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 242, 163, 58))

    # Upload arrow (left, pointing up)
    $g.FillPolygon($up, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (2.2 * $s), (7.0 * $s)),
        (New-Object System.Drawing.PointF (5.5 * $s), (2.6 * $s)),
        (New-Object System.Drawing.PointF (8.8 * $s), (7.0 * $s))))
    $g.FillRectangle($up, 4.3 * $s, 6.4 * $s, 2.4 * $s, 7.0 * $s)

    # Download arrow (right, pointing down)
    $g.FillRectangle($down, 9.3 * $s, 2.6 * $s, 2.4 * $s, 7.0 * $s)
    $g.FillPolygon($down, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (7.2 * $s), (9.0 * $s)),
        (New-Object System.Drawing.PointF (10.5 * $s), (13.4 * $s)),
        (New-Object System.Drawing.PointF (13.8 * $s), (9.0 * $s))))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($size in $sizes) { , (New-Frame $size) }

$ico = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $bw.Write($frame) }
$bw.Flush()
$Out = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[System.IO.File]::WriteAllBytes($Out, $ico.ToArray())
Write-Host "Wrote $Out"
