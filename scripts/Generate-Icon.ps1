$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '../Assets'
[xml]$svg = Get-Content (Join-Path $assetDirectory 'app.svg') -Raw

# Rasterize the simple SVG shapes from the source asset. Use oversampling for small icons.
function New-IconPng([int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size * 4 / 256.0, $size * 4 / 256.0)
    foreach ($shape in $svg.DocumentElement.ChildNodes) {
        if ($shape.LocalName -eq 'title') { continue }
        $brush = if ($shape.fill) { [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($shape.fill)) }
        try {
            switch ($shape.LocalName) {
                'rect' { $graphics.FillRectangle($brush, [single]$shape.x, [single]$shape.y, [single]$shape.width, [single]$shape.height) }
                'ellipse' { $graphics.FillEllipse($brush, [single]($shape.cx - $shape.rx), [single]($shape.cy - $shape.ry), [single](2 * $shape.rx), [single](2 * $shape.ry)) }
                'circle' { $graphics.FillEllipse($brush, [single]($shape.cx - $shape.r), [single]($shape.cy - $shape.r), [single](2 * $shape.r), [single](2 * $shape.r)) }
                'polygon' {
                    [Drawing.PointF[]]$points = @($shape.points.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object {
                        $xy = $_.Split(',')
                        [Drawing.PointF]::new([single]$xy[0], [single]$xy[1])
                    })
                    $graphics.FillPolygon($brush, $points)
                }
                'line' {
                    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($shape.stroke), [single]$shape.'stroke-width')
                    try {
                        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                        $graphics.DrawLine($pen, [single]$shape.x1, [single]$shape.y1, [single]$shape.x2, [single]$shape.y2)
                    } finally { $pen.Dispose() }
                }
            }
        } finally { if ($brush) { $brush.Dispose() } }
    }
    $output = [Drawing.Bitmap]::new($size, $size)
    $resizer = [Drawing.Graphics]::FromImage($output)
    $resizer.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $resizer.DrawImage($bitmap, 0, 0, $size, $size)
    $stream = [IO.MemoryStream]::new()
    try {
        $output.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose(); $resizer.Dispose(); $output.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @($sizes | ForEach-Object { ,(New-IconPng $_) })
$file = [IO.File]::Create((Join-Path $assetDirectory 'app.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
        $offset += $images[$i].Length
    }
    foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
} finally { $writer.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app.png'), [byte[]]$images[-1])
