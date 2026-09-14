$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$assets = Join-Path $root "assets"
$iconPath = Join-Path $assets "NetSpeedBall.ico"

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-BrushColor([int]$a, [int]$r, [int]$g, [int]$b) {
    return [System.Drawing.Color]::FromArgb($a, $r, $g, $b)
}

function New-SpeedBitmap([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.Rectangle -ArgumentList @(3, 3, ($size - 7), ($size - 7))
    $ballPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ballPath.AddEllipse($rect)

    $face = New-Object System.Drawing.Drawing2D.PathGradientBrush -ArgumentList @($ballPath)
    $face.CenterPoint = New-Object System.Drawing.PointF -ArgumentList @(
        [single]($rect.X + $rect.Width * 0.46),
        [single]($rect.Y + $rect.Height * 0.34)
    )
    $face.CenterColor = [System.Drawing.Color]::White
    $face.SurroundColors = @((New-BrushColor 255 232 243 251))
    $g.FillPath($face, $ballPath)
    $face.Dispose()

    $glossPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gloss = New-Object System.Drawing.Rectangle -ArgumentList @(
        ($rect.X + [int]($size / 5)),
        ($rect.Y + [int]($size / 9)),
        ($rect.Width - [int]($size * 2 / 5)),
        ([Math]::Max(4, [int]($size / 4)))
    )
    $glossPath.AddEllipse($gloss)
    $glossBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush -ArgumentList @($glossPath)
    $glossBrush.CenterColor = New-BrushColor 148 255 255 255
    $glossBrush.SurroundColors = @((New-BrushColor 0 255 255 255))
    $g.FillPath($glossBrush, $glossPath)
    $glossBrush.Dispose()
    $glossPath.Dispose()

    $ringRect = New-Object System.Drawing.RectangleF -ArgumentList @(
        [single]($rect.X + $size * 0.07),
        [single]($rect.Y + $size * 0.07),
        [single]($rect.Width - $size * 0.14),
        [single]($rect.Height - $size * 0.14)
    )

    $glow = New-Object System.Drawing.Pen -ArgumentList @((New-BrushColor 112 122 181 220), [single]([Math]::Max(1.0, $size / 14.0)))
    $outer = New-Object System.Drawing.Pen -ArgumentList @((New-BrushColor 246 250 253 255), [single]([Math]::Max(1.0, $size / 25.0)))
    $inner = New-Object System.Drawing.Pen -ArgumentList @((New-BrushColor 190 176 211 232), [single]([Math]::Max(1.0, $size / 55.0)))
    $progress = New-Object System.Drawing.Pen -ArgumentList @((New-BrushColor 218 46 136 232), [single]([Math]::Max(1.2, $size / 20.0)))
    $progress.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $progress.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawEllipse($glow, $ringRect)
    $g.DrawEllipse($outer, $rect)
    $innerRing = [System.Drawing.RectangleF]::Inflate($ringRect, [single](-$size * 0.05), [single](-$size * 0.05))
    $progressRing = [System.Drawing.RectangleF]::Inflate($ringRect, [single](-$size * 0.04), [single](-$size * 0.04))
    $g.DrawEllipse($inner, $innerRing)
    $g.DrawArc($progress, $progressRing, 132, 238)
    $glow.Dispose()
    $outer.Dispose()
    $inner.Dispose()
    $progress.Dispose()

    $state = $g.Save()
    $clipPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $clipPath.AddEllipse($rect)
    $g.SetClip($clipPath, [System.Drawing.Drawing2D.CombineMode]::Intersect)
    $scale = [single]($size / 64.0)
    $cloudY = [single]($rect.Bottom - 18.0 * $scale)
    $cloud = New-Object System.Drawing.SolidBrush -ArgumentList @((New-BrushColor 248 252 255 255))
    $shade = New-Object System.Drawing.SolidBrush -ArgumentList @((New-BrushColor 220 235 244 250))
    $g.FillEllipse($cloud, [single]($rect.X + 4.0 * $scale), [single]($cloudY + 5.0 * $scale), [single](19.0 * $scale), [single](15.0 * $scale))
    $g.FillEllipse($cloud, [single]($rect.X + 17.0 * $scale), $cloudY, [single](21.0 * $scale), [single](19.0 * $scale))
    $g.FillEllipse($cloud, [single]($rect.X + 34.0 * $scale), [single]($cloudY + 4.0 * $scale), [single](24.0 * $scale), [single](17.0 * $scale))
    $g.FillRectangle($cloud, [single]($rect.X + 4.0 * $scale), [single]($cloudY + 11.0 * $scale), [single]($rect.Width - 8.0 * $scale), [single](19.0 * $scale))
    $g.FillEllipse($shade, [single]($rect.X + 36.0 * $scale), [single]($cloudY + 12.0 * $scale), [single](23.0 * $scale), [single](11.0 * $scale))
    $cloud.Dispose()
    $shade.Dispose()
    $clipPath.Dispose()
    $g.Restore($state)

    $state = $g.Save()
    $g.TranslateTransform([single]($size * 0.59), [single]($size * 0.35))
    $g.RotateTransform(-34.0)
    $g.ScaleTransform($scale, $scale)

    $flame = New-Object System.Drawing.Drawing2D.GraphicsPath
    $flame.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF -ArgumentList @(-10.0, -3.0)),
        (New-Object System.Drawing.PointF -ArgumentList @(-17.0, 0.0)),
        (New-Object System.Drawing.PointF -ArgumentList @(-10.0, 3.0))
    ))
    $orange = New-Object System.Drawing.SolidBrush -ArgumentList @((New-BrushColor 236 255 151 54))
    $g.FillPath($orange, $flame)
    $orange.Dispose()
    $flame.Dispose()

    $fin = New-Object System.Drawing.SolidBrush -ArgumentList @((New-BrushColor 255 44 132 224))
    $g.FillPolygon($fin, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF -ArgumentList @(-5.0, -3.4)),
        (New-Object System.Drawing.PointF -ArgumentList @(-9.0, -7.2)),
        (New-Object System.Drawing.PointF -ArgumentList @(-2.0, -5.0))
    ))
    $g.FillPolygon($fin, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF -ArgumentList @(-5.0, 3.4)),
        (New-Object System.Drawing.PointF -ArgumentList @(-9.0, 7.2)),
        (New-Object System.Drawing.PointF -ArgumentList @(-2.0, 5.0))
    ))
    $fin.Dispose()

    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.AddBezier(-9.0, -4.8, -3.5, -7.2, 5.0, -4.8, 9.0, 0.0)
    $body.AddBezier(9.0, 0.0, 5.0, 4.8, -3.5, 7.2, -9.0, 4.8)
    $body.CloseFigure()
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList @(
        (New-Object System.Drawing.RectangleF -ArgumentList @(-9.0, -6.0, 19.0, 12.0)),
        [System.Drawing.Color]::White,
        (New-BrushColor 255 202 232 255),
        [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal
    )
    $edge = New-Object System.Drawing.Pen -ArgumentList @((New-BrushColor 192 70 142 220), 1.0)
    $g.FillPath($bodyBrush, $body)
    $g.DrawPath($edge, $body)
    $bodyBrush.Dispose()
    $edge.Dispose()
    $body.Dispose()

    $window = New-Object System.Drawing.SolidBrush -ArgumentList @((New-BrushColor 255 42 137 229))
    $g.FillEllipse($window, 0.4, -2.4, 4.8, 4.8)
    $window.Dispose()
    $g.Restore($state)

    $outline = New-Object System.Drawing.Pen -ArgumentList @((New-BrushColor 178 174 211 231), [single]([Math]::Max(1.0, $size / 58.0)))
    $g.DrawEllipse($outline, $rect)
    $outline.Dispose()

    $ballPath.Dispose()
    $g.Dispose()
    return $bitmap
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$memory = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $memory

$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

$images = @()
$offset = 6 + (16 * $sizes.Count)

foreach ($size in $sizes) {
    $bitmap = New-SpeedBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $images += [PSCustomObject]@{ Size = $size; Bytes = $bytes; Offset = $offset }
    $offset += $bytes.Length
    $stream.Dispose()
    $bitmap.Dispose()
}

foreach ($image in $images) {
    [byte]$width = if ($image.Size -eq 256) { 0 } else { $image.Size }
    [byte]$height = if ($image.Size -eq 256) { 0 } else { $image.Size }
    $writer.Write($width)
    $writer.Write($height)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$image.Bytes.Length)
    $writer.Write([UInt32]$image.Offset)
}

foreach ($image in $images) {
    $writer.Write($image.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($iconPath, $memory.ToArray())
$writer.Dispose()
$memory.Dispose()

Write-Host "Generated $iconPath"
