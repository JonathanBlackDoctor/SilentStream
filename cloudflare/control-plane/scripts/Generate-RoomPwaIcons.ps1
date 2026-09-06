[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$controlPlaneRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $controlPlaneRoot 'public\room\icons'
$rooms = [ordered]@{
    'm111' = 'M111'
    'm112' = 'M112'
    'm113' = 'M113'
    'baek202' = '백202'
}

function New-RoundedRectanglePath {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-RoomIcon {
    param(
        [string]$Room,
        [string]$Label,
        [int]$Size,
        [string]$FileName
    )

    $roomDirectory = Join-Path $outputRoot $Room
    New-Item -ItemType Directory -Path $roomDirectory -Force | Out-Null
    $outputPath = Join-Path $roomDirectory $FileName
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        $canvas = [System.Drawing.RectangleF]::new(0, 0, $Size, $Size)
        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            $canvas,
            [System.Drawing.ColorTranslator]::FromHtml('#183c5d'),
            [System.Drawing.ColorTranslator]::FromHtml('#08131f'),
            135
        )
        try { $graphics.FillRectangle($gradient, $canvas) } finally { $gradient.Dispose() }

        $borderPath = New-RoundedRectanglePath ($Size * .045) ($Size * .045) ($Size * .91) ($Size * .91) ($Size * .15)
        $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(78, 115, 181, 255), [Math]::Max(2, $Size * .012))
        try { $graphics.DrawPath($borderPen, $borderPath) } finally { $borderPen.Dispose(); $borderPath.Dispose() }

        $centerX = $Size * .5
        $centerY = $Size * .285
        foreach ($ring in @(
            @{ Radius = .165; Alpha = 54; Width = .026 },
            @{ Radius = .105; Alpha = 128; Width = .030 },
            @{ Radius = .052; Alpha = 230; Width = .034 }
        )) {
            $radius = $Size * $ring.Radius
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb($ring.Alpha, 115, 181, 255), [Math]::Max(2, $Size * $ring.Width))
            try { $graphics.DrawEllipse($pen, $centerX - $radius, $centerY - $radius, $radius * 2, $radius * 2) } finally { $pen.Dispose() }
        }

        $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#b5dcff'))
        $dotRadius = $Size * .025
        try { $graphics.FillEllipse($dotBrush, $centerX - $dotRadius, $centerY - $dotRadius, $dotRadius * 2, $dotRadius * 2) } finally { $dotBrush.Dispose() }

        try { $fontFamily = [System.Drawing.FontFamily]::new('Noto Sans KR') }
        catch { $fontFamily = [System.Drawing.FontFamily]::new('Malgun Gothic') }
        $fontSize = if ($Label.Length -ge 5) { $Size * .175 } else { $Size * .215 }
        $font = [System.Drawing.Font]::new($fontFamily, $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#f4f9ff'))
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        try {
            $textBox = [System.Drawing.RectangleF]::new($Size * .09, $Size * .47, $Size * .82, $Size * .29)
            $graphics.DrawString($Label, $font, $textBrush, $textBox, $format)
        }
        finally {
            $format.Dispose()
            $textBrush.Dispose()
            $font.Dispose()
            $fontFamily.Dispose()
        }

        $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#73b5ff'))
        $accentPath = New-RoundedRectanglePath ($Size * .34) ($Size * .82) ($Size * .32) ($Size * .025) ($Size * .0125)
        try { $graphics.FillPath($accentBrush, $accentPath) } finally { $accentBrush.Dispose(); $accentPath.Dispose() }

        $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

foreach ($room in $rooms.Keys) {
    New-RoomIcon -Room $room -Label $rooms[$room] -Size 192 -FileName 'icon-192.png'
    New-RoomIcon -Room $room -Label $rooms[$room] -Size 512 -FileName 'icon-512.png'
    New-RoomIcon -Room $room -Label $rooms[$room] -Size 512 -FileName 'icon-maskable.png'
    New-RoomIcon -Room $room -Label $rooms[$room] -Size 180 -FileName 'apple-touch-icon.png'
}

Write-Host "Generated room PWA icons under $outputRoot"
