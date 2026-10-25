$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'build\app.ico'
$dir = Split-Path -Parent $out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $u = [double]$s / 16.0
    $r = 3.2 * $u

    $rect = New-Object System.Drawing.RectangleF(0, 0, [float]$s, [float]$s)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([float]$rect.X, [float]$rect.Y, [float]($r * 2), [float]($r * 2), 180, 90)
    $path.AddArc([float]($rect.Right - $r * 2), [float]$rect.Y, [float]($r * 2), [float]($r * 2), 270, 90)
    $path.AddArc([float]($rect.Right - $r * 2), [float]($rect.Bottom - $r * 2), [float]($r * 2), [float]($r * 2), 0, 90)
    $path.AddArc([float]$rect.X, [float]($rect.Bottom - $r * 2), [float]($r * 2), [float]($r * 2), 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 23, 42))
    $g.FillPath($bg, $path)
    $bg.Dispose()
    $path.Dispose()

    $penWidth = [float][Math]::Max(1.0, 1.15 * $u)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 237, 242, 247), $penWidth)
    $body = New-Object System.Drawing.RectangleF([float](3.1 * $u), [float](5.9 * $u), [float](8.2 * $u), [float](4.4 * $u))
    $rr = 1.1 * $u
    $bp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bp.AddArc([float]$body.X, [float]$body.Y, [float]($rr * 2), [float]($rr * 2), 180, 90)
    $bp.AddArc([float]($body.Right - $rr * 2), [float]$body.Y, [float]($rr * 2), [float]($rr * 2), 270, 90)
    $bp.AddArc([float]($body.Right - $rr * 2), [float]($body.Bottom - $rr * 2), [float]($rr * 2), [float]($rr * 2), 0, 90)
    $bp.AddArc([float]$body.X, [float]($body.Bottom - $rr * 2), [float]($rr * 2), [float]($rr * 2), 90, 90)
    $bp.CloseFigure()
    $g.DrawPath($pen, $bp)
    $bp.Dispose()
    $pen.Dispose()

    $tipBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 237, 242, 247))
    $g.FillRectangle($tipBrush, [float](11.2 * $u), [float](7.1 * $u), [float](1.5 * $u), [float](2.0 * $u))
    $tipBrush.Dispose()

    $fillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 74, 222, 128))
    $inner = 1.2 * $u
    $w = ($body.Width - $inner * 2) * 0.78
    $g.FillRectangle($fillBrush, [float]($body.X + $inner), [float]($body.Y + $inner), [float]$w, [float]($body.Height - $inner * 2))
    $fillBrush.Dispose()

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , ($ms.ToArray())
    $ms.Dispose()
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frames[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $frames[$i].Length
}

foreach ($f in $frames) { $writer.Write($f) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($out, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "generated $out"
