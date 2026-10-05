# MP-Commander icon generator
#   .\tools\make-icon.ps1   ->  src\MPCommander\Assets\MP-Commander.ico (+ MP-Commander-256.png preview)
# Design: rounded blue square with a 2x2 grid of panes; the top-left pane is orange
# (the app's "active"/"target" colors). Each size is drawn natively so small sizes stay crisp.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'src\MPCommander\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $small = $S -le 24
    # background: rounded square, blue gradient top -> bottom
    $m = if ($small) { 0.5 } else { $S * 0.04 }
    $bg = New-RoundRect $m $m ($S - 2 * $m) ($S - 2 * $m) ($S * ($(if ($small) { 0.16 } else { 0.19 })))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $S),
        ([System.Drawing.Color]::FromArgb(255, 0x34, 0x93, 0xF2)), ([System.Drawing.Color]::FromArgb(255, 0x12, 0x52, 0xA6))
    $g.FillPath($grad, $bg)

    # 2x2 panes
    $pad = if ($small) { [Math]::Round($S * 0.19) } else { $S * 0.17 }
    $gap = if ($small) { 1 } else { [Math]::Max(2, $S * 0.055) }
    $inner = $S - 2 * $pad
    $pw = ($inner - $gap) / 2
    if ($small) { $pw = [Math]::Floor($pw) }
    $radius = if ($small) { 0 } else { $S * 0.035 }

    $white = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)
    $orange = [System.Drawing.Color]::FromArgb(255, 0xF7, 0xA8, 0x2A)
    $lineOnWhite = [System.Drawing.Color]::FromArgb(255, 0xA9, 0xCB, 0xF2)
    $lineOnOrange = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xE2, 0xAE)

    for ($i = 0; $i -lt 4; $i++) {
        $x = $pad + ($i % 2) * ($pw + $gap)
        $y = $pad + [Math]::Floor($i / 2) * ($pw + $gap)
        $fill = if ($i -eq 0) { $orange } else { $white }
        $brush = New-Object System.Drawing.SolidBrush $fill
        if ($radius -gt 0) {
            $path = New-RoundRect $x $y $pw $pw $radius
            $g.FillPath($brush, $path)
        } else {
            $g.FillRectangle($brush, [float]$x, [float]$y, [float]$pw, [float]$pw)
        }

        # 48 px and up: file-list lines inside each pane
        if ($S -ge 48) {
            $lc = if ($i -eq 0) { $lineOnOrange } else { $lineOnWhite }
            $lb = New-Object System.Drawing.SolidBrush $lc
            $lh = [Math]::Max(1.5, $pw * 0.1)
            $lx = $x + $pw * 0.16
            for ($k = 0; $k -lt 3; $k++) {
                $ly = $y + $pw * (0.22 + $k * 0.22)
                $len = $pw * $(if ($k -eq 1) { 0.5 } else { 0.68 })
                $g.FillRectangle($lb, [float]$lx, [float]$ly, [float]$len, [float]$lh)
            }
        }
    }
    $g.Dispose()
    return $bmp
}

# Classic DIB (BMP) entry: BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bpp AND mask.
# Used for sizes up to 48 px so every Windows component (including System.Drawing.Icon) can read them.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $S = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([Math]::Ceiling($S / 32.0) * 4)
    $w.Write([UInt32]40); $w.Write([Int32]$S); $w.Write([Int32]($S * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($S * $S * 4 + $maskStride * $S)); $w.Write([Int32]0); $w.Write([Int32]0)
    $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $S - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $S; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskStride * $S)))   # all 0 = use alpha channel
    $w.Flush()
    return , $ms.ToArray()
}

# 16-48 px: classic DIB (readable by every Windows component); 64-256 px: PNG (compressed, Vista+)
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -ge 64) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($s -eq 256) { $bmp.Save((Join-Path $outDir 'MP-Commander-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        $data = $ms.ToArray()
    } else {
        $data = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    , $data
}

# ICO file: header + directory + image data (DIB up to 48 px, PNG from 64 px)
$ico = Join-Path $outDir 'MP-Commander.ico'
$fs = [System.IO.File]::Create($ico)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $pngs[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $w.Write($data) }
$w.Dispose()
Get-Item $ico, (Join-Path $outDir 'MP-Commander-256.png') | Format-Table Name, Length -AutoSize
