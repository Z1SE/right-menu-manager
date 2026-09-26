# Generates app.ico for 右键菜单管理器.
# Pure ASCII on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files as ANSI.
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square background
    $r = [Math]::Max(2, [int]($size * 0.20))
    $d = $r * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 15, 108, 189))
    $g.FillPath($brush, $path)

    # three stacked menu lines
    $penWidth = [Math]::Max(1.0, $size * 0.072)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]$penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $x1 = [single]($size * 0.27)
    $x2 = [single]($size * 0.73)
    foreach ($fy in @(0.355, 0.50, 0.645)) {
        $y = [single]($size * $fy)
        $g.DrawLine($pen, $x1, $y, $x2, $y)
    }

    $g.Dispose()
    $pen.Dispose()
    $brush.Dispose()
    $path.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = [byte[]](New-IconPng $s) }

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'app.ico'
$ms = [System.IO.MemoryStream]::new()
$bw = [System.IO.BinaryWriter]::new($ms)

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = [byte[]]$pngs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write([byte[]]$pngs[$s]) }

$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Output "wrote $out ($((Get-Item $out).Length) bytes)"
