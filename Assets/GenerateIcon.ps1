param(
    [string]$OutputIco = (Join-Path $PSScriptRoot 'NeClipboard.ico'),
    [string]$PreviewPng = (Join-Path $PSScriptRoot 'NeClipboard-icon-preview.png')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconFrame([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 256.0
    $panel = New-RoundedPath (18 * $scale) (18 * $scale) (220 * $scale) (220 * $scale) (58 * $scale)
    $graphics.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#121820')), $panel)
    $graphics.DrawPath([System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#2B3742'), [Math]::Max(1, 8 * $scale)), $panel)
    $ink = [System.Drawing.ColorTranslator]::FromHtml('#98E8D4')
    $lineWidth = [Math]::Max(1.2, 12 * $scale)
    $clipboard = New-RoundedPath (72 * $scale) (76 * $scale) (112 * $scale) (118 * $scale) (18 * $scale)
    $graphics.DrawPath([System.Drawing.Pen]::new($ink, $lineWidth), $clipboard)
    $pen = [System.Drawing.Pen]::new($ink, [Math]::Max(1.2, 11 * $scale))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($pen, 102 * $scale, 64 * $scale, 154 * $scale, 64 * $scale)
    $graphics.DrawLine($pen, 101 * $scale, 118 * $scale, 155 * $scale, 118 * $scale)
    $graphics.DrawLine($pen, 101 * $scale, 151 * $scale, 138 * $scale, 151 * $scale)
    $pen.Dispose(); $clipboard.Dispose(); $panel.Dispose(); $graphics.Dispose()
    return $bitmap
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-IconFrame $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $frames.Add($stream.ToArray())
}

$directoryBytes = 6 + (16 * $frames.Count)
$offset = $directoryBytes
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($OutputIco))
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$frames.Count)
for ($index = 0; $index -lt $frames.Count; $index++) {
    $size = $sizes[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frames[$index].Length); $writer.Write([UInt32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Dispose()

$preview = New-IconFrame 256
$preview.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
