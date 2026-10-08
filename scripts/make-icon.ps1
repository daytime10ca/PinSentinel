<#
.SYNOPSIS
    Regenerates assets\PinSentinel.ico: six pin bars on a dark rounded tile.
    Only needed when the icon design changes; the .ico is committed.
#>
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\assets\PinSentinel.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0

    # Tile
    $r = 14 * $s
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r; $w = $size - 1
    $tile.AddArc(0, 0, $d, $d, 180, 90); $tile.AddArc($w - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $tile.AddArc(0, $w - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0E1014'))
    $g.FillPath($bg, $tile)

    # Six bars, teal at the bottom to sky at the top
    $heights = 30, 38, 34, 40, 36, 32
    $barW = 5.5 * $s; $gap = 2.5 * $s; $left = (64 * $s - (6 * $barW + 5 * $gap)) / 2; $base = 50 * $s
    $rect = New-Object System.Drawing.RectangleF 0, (8 * $s), $size, (44 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.ColorTranslator]::FromHtml('#7DD3FC')), ([System.Drawing.ColorTranslator]::FromHtml('#5EEAD4')), 90
    for ($i = 0; $i -lt 6; $i++) {
        $h = $heights[$i] * $s; $x = $left + $i * ($barW + $gap); $cap = [Math]::Min($barW, $h) / 2
        $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bar.AddArc($x, $base - $h, 2 * $cap, 2 * $cap, 180, 180)
        $bar.AddArc($x, $base - 2 * $cap, 2 * $cap, 2 * $cap, 0, 180)
        $bar.CloseFigure()
        $g.FillPath($grad, $bar)
    }

    # The 9.5 A limit line
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#F87171')), ([Math]::Max(1, 2 * $s))
    $g.DrawLine($pen, 8 * $s, 9 * $s, 56 * $s, 9 * $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = $sizes | ForEach-Object { , (New-Frame $_) }

$stream = [System.IO.File]::Create($out)
$writer = New-Object System.IO.BinaryWriter $stream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Dispose()
Write-Host "Wrote $out"
