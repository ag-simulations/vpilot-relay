# Builds the wizard's image assets from the original logos in .\source:
#   logo-badge.png  product logo cropped to a rounded square (welcome page)
#   logo-mark.png   the white mark only, transparent background (sidebar)
#   agsim-dark.png  AG Simulations logo for light backgrounds, downscaled
#   app.ico         Windows icon (rounded badge, 16..256 px)
# Usage: powershell -ExecutionPolicy Bypass -File make-assets.ps1
#
# The original logos (.\source) are AG Simulations trademarks and are not in the public
# repository; the generated files above are committed so the project builds without them.

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$src = Join-Path $here 'source'

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    return $g
}

$logo = [System.Drawing.Bitmap]::FromFile((Join-Path $src 'vpilot-relay-logo.png'))

# The mark sits in the middle of a 1254 px navy square; crop so it fills ~80% of the badge.
$crop = New-Object System.Drawing.Rectangle 165, 165, 924, 924

# ---- logo-badge.png (rounded corners, transparent outside) ----
function New-Badge([int]$size, [double]$radiusRatio) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bmp
    $g.Clear([System.Drawing.Color]::Transparent)
    $path = New-RoundedPath 0 0 ($size - 0.5) ($size - 0.5) ([float]($size * $radiusRatio))
    $g.SetClip($path)
    $g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), $crop, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $bmp
}
$badge = New-Badge 512 0.22
$badge.Save((Join-Path $here 'logo-badge.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$badge.Dispose()

# ---- logo-mark.png (white mark, alpha from brightness over the navy background) ----
$markSize = 512
$scaled = New-Object System.Drawing.Bitmap $markSize, $markSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = New-Graphics $scaled
$g.DrawImage($logo, (New-Object System.Drawing.Rectangle 0, 0, $markSize, $markSize), $crop, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$mark = New-Object System.Drawing.Bitmap $markSize, $markSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bgL = 0.2126 * 0x11 + 0.7152 * 0x22 + 0.0722 * 0x38
for ($y = 0; $y -lt $markSize; $y++) {
    for ($x = 0; $x -lt $markSize; $x++) {
        $c = $scaled.GetPixel($x, $y)
        $l = 0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B
        $t = ($l - $bgL) / (255 - $bgL)
        $a = [Math]::Max(0, [Math]::Min(1, ($t - 0.12) / 0.80))   # drop most of the glow, keep edges smooth
        $mark.SetPixel($x, $y, [System.Drawing.Color]::FromArgb([int]($a * 255), 255, 255, 255))
    }
}
$mark.Save((Join-Path $here 'logo-mark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$mark.Dispose(); $scaled.Dispose()

# ---- agsim-dark.png (already transparent; just make it a sensible size) ----
$ag = [System.Drawing.Bitmap]::FromFile((Join-Path $src 'agsim-dark.png'))
# Trim the transparent margin: find the content box on a 1/4 scale copy (GetPixel is slow), then crop the original.
$q = 4
$tw = [int]($ag.Width / $q); $th = [int]($ag.Height / $q)
$thumb = New-Object System.Drawing.Bitmap $tw, $th, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = New-Graphics $thumb; $g.DrawImage($ag, (New-Object System.Drawing.Rectangle 0, 0, $tw, $th)); $g.Dispose()
$minX = $tw; $minY = $th; $maxX = 0; $maxY = 0
for ($y = 0; $y -lt $th; $y++) { for ($x = 0; $x -lt $tw; $x++) {
    if ($thumb.GetPixel($x, $y).A -gt 40) {
        if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
        if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
    } } }
$thumb.Dispose()
$box = New-Object System.Drawing.Rectangle ([Math]::Max(0, ($minX - 1) * $q)), ([Math]::Max(0, ($minY - 1) * $q)), (($maxX - $minX + 3) * $q), (($maxY - $minY + 3) * $q)
$h = 120; $w = [int]($box.Width * $h / $box.Height)
$agSmall = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = New-Graphics $agSmall
$g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($ag, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), $box, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$agSmall.Save((Join-Path $here 'agsim-dark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$agSmall.Dispose(); $ag.Dispose()

# ---- app.ico (PNG-compressed entries) ----
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = New-Badge $s 0.22
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$wr = New-Object System.IO.BinaryWriter $out
$wr.Write([uint16]0); $wr.Write([uint16]1); $wr.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $wr.Write([byte]$dim); $wr.Write([byte]$dim); $wr.Write([byte]0); $wr.Write([byte]0)
    $wr.Write([uint16]1); $wr.Write([uint16]32)
    $wr.Write([uint32]$len); $wr.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $wr.Write($p) }
$wr.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'app.ico'), $out.ToArray())

$logo.Dispose()
Write-Host "Assets written: logo-badge.png, logo-mark.png, agsim-dark.png, app.ico"
