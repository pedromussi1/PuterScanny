# Draws PuterScanny's icon (a magnifier over a list of files) at every Windows icon size and packs them into one .ico.
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File build\make-icon.ps1
param([string]$Out = "$PSScriptRoot\..\src\PuterScanny.App\Assets\PuterScanny.ico")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$size) {
    $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded square with a blue gradient
    $r = 56 * $s; $m = 8 * $s; $w = $size - 2 * $m
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90)
    $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90)
    $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF(0, 0)), (New-Object Drawing.PointF($size, $size)),
        [Drawing.Color]::FromArgb(255, 0, 120, 215), [Drawing.Color]::FromArgb(255, 0, 84, 166))
    $g.FillPath($bg, $path)

    # File rows (what was found)
    $white = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(235, 255, 255, 255))
    $soft = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(120, 255, 255, 255))
    $green = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255, 108, 203, 95))
    $rowH = [Math]::Max(1.5, 16 * $s)
    foreach ($i in 0..2) {
        $y = (52 + $i * 34) * $s
        $g.FillRectangle($(if ($i -eq 1) { $green } else { $white }), 40 * $s, $y, 20 * $s, $rowH)
        $g.FillRectangle($soft, 68 * $s, $y, (if2 ($i -eq 2) 70 110) * $s, $rowH)
    }

    # Magnifier
    $lensPen = New-Object Drawing.Pen([Drawing.Color]::White, [Math]::Max(2, 22 * $s))
    $handlePen = New-Object Drawing.Pen([Drawing.Color]::White, [Math]::Max(2.5, 30 * $s))
    $handlePen.StartCap = 'Round'; $handlePen.EndCap = 'Round'
    $cx = 150 * $s; $cy = 150 * $s; $rad = 52 * $s
    $glass = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(70, 255, 255, 255))
    $g.FillEllipse($glass, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad)
    $g.DrawEllipse($lensPen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad)
    $g.DrawLine($handlePen, $cx + $rad * 0.75, $cy + $rad * 0.75, 214 * $s, 214 * $s)

    $g.Dispose()
    return $bmp
}
function if2($c, $a, $b) { if ($c) { $a } else { $b } }

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = Draw-Icon $sz
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO file: header, directory, then PNG images.
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [IO.File]::Create($Out)
$bw = New-Object IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bw.Write([byte]($sz % 256)); $bw.Write([byte]($sz % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
"Wrote $Out ($((Get-Item $Out).Length) bytes)"

# Preview for checking by eye
$preview = Draw-Icon 256
$preview.Save([IO.Path]::ChangeExtension($Out, '.png'), [Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
