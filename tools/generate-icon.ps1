# Generate the Windows icon and PNG preview from the project's simple SVG shapes.
# Uses only Windows GDI+; no external drawing tools or downloaded artwork required.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$assetsDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/PotDanmaku.App/Assets'
$source = [xml](Get-Content -LiteralPath (Join-Path $assetsDirectory 'PotDanmaku.svg') -Raw)
$sizes = @(256, 128, 64, 48, 40, 32, 24, 20, 16)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $renderSize = $size * 4
    $canvas = [System.Drawing.Bitmap]::new($renderSize, $renderSize)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $outputGraphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $png = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($renderSize / 256.0, $renderSize / 256.0)
        foreach ($shape in $source.DocumentElement.ChildNodes) {
            if ($shape.LocalName -eq 'title') { continue }
            $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($shape.GetAttribute('fill')))
            try {
                switch ($shape.LocalName) {
                    'rect' {
                        $x = [float]$shape.GetAttribute('x'); $y = [float]$shape.GetAttribute('y')
                        $width = [float]$shape.GetAttribute('width'); $height = [float]$shape.GetAttribute('height')
                        $radius = [float]$shape.GetAttribute('rx'); $diameter = 2 * $radius
                        $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
                        $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
                        $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
                        $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
                        $path.CloseFigure()
                    }
                    'polygon' {
                        $points = [System.Drawing.PointF[]]@($shape.GetAttribute('points').Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {
                            $coordinates = $_.Split(',')
                            [System.Drawing.PointF]::new([float]$coordinates[0], [float]$coordinates[1])
                        })
                        $path.AddPolygon($points)
                    }
                    default { throw ('Unsupported icon shape: ' + $shape.LocalName) }
                }
                $graphics.FillPath($brush, $path)
            } finally { $brush.Dispose(); $path.Dispose() }
        }
        $outputGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $outputGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $outputGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $outputGraphics.DrawImage($canvas, [System.Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $renderSize, $renderSize, [System.Drawing.GraphicsUnit]::Pixel)
        $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($png.ToArray())
        if ($size -eq 256) {
            [System.IO.File]::WriteAllBytes((Join-Path $assetsDirectory 'PotDanmaku.png'), $png.ToArray())
        }
    } finally { $png.Dispose(); $outputGraphics.Dispose(); $bitmap.Dispose(); $graphics.Dispose(); $canvas.Dispose() }
}
$iconPath = Join-Path $assetsDirectory 'PotDanmaku.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
Write-Output ('Generated icon: ' + $iconPath + ' (' + ($sizes -join ', ') + ' px)')
