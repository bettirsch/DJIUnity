param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$referenceDirectory = Join-Path $ProjectRoot 'Assets/AR/ReferenceImages'
$printDirectory = Join-Path $referenceDirectory 'Print'
New-Item -ItemType Directory -Force -Path $printDirectory | Out-Null

$targetPath = Join-Path $referenceDirectory 'BuildingReference.png'
$printPath = Join-Path $printDirectory 'BuildingReference_A4.png'
$targetPixels = 1800
$printWidthPixels = 2480
$printHeightPixels = 3508
$targetPrintPixels = 2126

function New-Pen([int]$gray, [float]$width) {
    return [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb($gray, $gray, $gray), $width)
}

function New-Brush([int]$gray) {
    return [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb($gray, $gray, $gray))
}

function New-Point([int]$x, [int]$y) {
    return [System.Drawing.Point]::new($x, $y)
}

function Draw-Polyline($graphics, $pen, [System.Drawing.Point[]]$points) {
    $graphics.DrawLines($pen, $points)
}

function Draw-PolygonFeature($graphics, [System.Random]$random, [int]$left, [int]$top, [int]$width, [int]$height, [int]$vertices, [int]$gray, [bool]$filled) {
    $points = [System.Collections.Generic.List[System.Drawing.Point]]::new()
    for ($index = 0; $index -lt $vertices; $index++) {
        $points.Add((New-Point ($left + $random.Next(0, $width)) ($top + $random.Next(0, $height))))
    }

    $pointArray = $points.ToArray()
    if ($filled) {
        $brush = New-Brush $gray
        $graphics.FillPolygon($brush, $pointArray)
        $brush.Dispose()
    }
    else {
        $pen = New-Pen $gray (4 + $random.Next(0, 8))
        $graphics.DrawPolygon($pen, $pointArray)
        $pen.Dispose()
    }
}

function Draw-ShortLineCluster($graphics, [System.Random]$random, [int]$left, [int]$top, [int]$width, [int]$height, [int]$count) {
    for ($index = 0; $index -lt $count; $index++) {
        $x = $left + $random.Next(0, $width)
        $y = $top + $random.Next(0, $height)
        $length = $random.Next(28, 115)
        $angle = $random.NextDouble() * [Math]::PI * 2
        $endX = [int]($x + [Math]::Cos($angle) * $length)
        $endY = [int]($y + [Math]::Sin($angle) * $length)
        $gray = [int]@('18', '48', '83', '118', '156')[$random.Next(0, 5)]
        $pen = New-Pen $gray (3 + $random.NextDouble() * 8)
        $graphics.DrawLine($pen, $x, $y, $endX, $endY)
        $pen.Dispose()
    }
}

function Draw-Target() {
    $bitmap = [System.Drawing.Bitmap]::new($targetPixels, $targetPixels, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $bitmap.SetResolution(254, 254)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::FromArgb(245, 244, 239))
    $random = [System.Random]::new(180180)

    # Orientation cue: deliberately small, asymmetric, and inside the tracking target.
    $headerBrush = New-Brush 20
    $headerFont = [System.Drawing.Font]::new('Arial', 16, [System.Drawing.FontStyle]::Bold)
    $graphics.DrawString('BUILDING REFERENCE', $headerFont, $headerBrush, 120, 72)
    $graphics.DrawString('TOP', $headerFont, $headerBrush, 1540, 72)
    $headerBrush.Dispose()
    $headerFont.Dispose()
    $arrowPen = New-Pen 20 10
    $graphics.DrawLine($arrowPen, 1655, 145, 1655, 85)
    $graphics.DrawLine($arrowPen, 1655, 85, 1628, 116)
    $graphics.DrawLine($arrowPen, 1655, 85, 1682, 116)
    $arrowPen.Dispose()

    # Top-left: angular, mostly filled architectural fragments.
    $darkBrush = New-Brush 28
    [System.Drawing.Point[]]$topLeft = @(
        (New-Point 105 255), (New-Point 338 186), (New-Point 504 292), (New-Point 451 515), (New-Point 205 551), (New-Point 112 424)
    )
    $graphics.FillPolygon($darkBrush, $topLeft)
    $darkBrush.Dispose()
    $lightBrush = New-Brush 210
    $graphics.FillPolygon($lightBrush, [System.Drawing.Point[]]@((New-Point 184 320), (New-Point 327 269), (New-Point 385 354), (New-Point 265 460), (New-Point 173 408)))
    $lightBrush.Dispose()
    $topLeftPen = New-Pen 72 11
    Draw-Polyline $graphics $topLeftPen ([System.Drawing.Point[]]@((New-Point 102 615), (New-Point 236 704), (New-Point 384 586), (New-Point 517 736), (New-Point 650 592)))
    $graphics.DrawLine($topLeftPen, 582, 214, 739, 369)
    $graphics.DrawLine($topLeftPen, 640, 194, 760, 267)
    $topLeftPen.Dispose()
    Draw-PolygonFeature $graphics $random 90 600 600 250 5 52 $true
    Draw-ShortLineCluster $graphics $random 105 215 620 635 18

    # Top-right: distinct arcs, off-centre rings, and a broken diagonal spine.
    $arcPenA = New-Pen 34 14
    $arcPenB = New-Pen 96 9
    $arcPenC = New-Pen 156 7
    $graphics.DrawArc($arcPenA, 1030, 198, 445, 445, 42, 247)
    $graphics.DrawArc($arcPenB, 1150, 287, 290, 290, 165, 166)
    $graphics.DrawArc($arcPenC, 1280, 165, 330, 330, 218, 97)
    $graphics.DrawArc($arcPenA, 1390, 430, 250, 250, 290, 122)
    $arcPenA.Dispose(); $arcPenB.Dispose(); $arcPenC.Dispose()
    $circleBrush = New-Brush 54
    $graphics.FillEllipse($circleBrush, 1515, 247, 112, 112)
    $graphics.FillEllipse($circleBrush, 1090, 628, 72, 72)
    $circleBrush.Dispose()
    $spinePen = New-Pen 45 12
    Draw-Polyline $graphics $spinePen ([System.Drawing.Point[]]@((New-Point 890 650), (New-Point 1005 552), (New-Point 1090 675), (New-Point 1215 572), (New-Point 1338 725), (New-Point 1480 604), (New-Point 1680 690)))
    $spinePen.Dispose()
    Draw-ShortLineCluster $graphics $random 920 170 760 650 20

    # Bottom-left: one irregular zig-zag ribbon with triangles and sparse circles.
    $ribbonBrush = New-Brush 68
    [System.Drawing.Point[]]$ribbon = @(
        (New-Point 115 1000), (New-Point 248 915), (New-Point 347 1042), (New-Point 491 940), (New-Point 605 1092), (New-Point 473 1233), (New-Point 592 1388), (New-Point 414 1518), (New-Point 244 1410), (New-Point 315 1257), (New-Point 132 1163)
    )
    $graphics.FillPolygon($ribbonBrush, $ribbon)
    $ribbonBrush.Dispose()
    $trianglePen = New-Pen 18 10
    $graphics.DrawPolygon($trianglePen, [System.Drawing.Point[]]@((New-Point 697 994), (New-Point 810 1182), (New-Point 586 1178)))
    $graphics.DrawPolygon($trianglePen, [System.Drawing.Point[]]@((New-Point 695 1390), (New-Point 836 1544), (New-Point 560 1570)))
    $trianglePen.Dispose()
    $bottomLeftBrush = New-Brush 110
    $graphics.FillEllipse($bottomLeftBrush, 702, 1280, 96, 96)
    $graphics.FillEllipse($bottomLeftBrush, 210, 1600, 132, 132)
    $bottomLeftBrush.Dispose()
    Draw-PolygonFeature $graphics $random 85 1460 560 250 6 28 $false
    Draw-ShortLineCluster $graphics $random 105 900 710 790 28

    # Bottom-right: compact, varied corner structures rather than a repeated grid.
    $rightPen = New-Pen 25 12
    Draw-Polyline $graphics $rightPen ([System.Drawing.Point[]]@((New-Point 1040 978), (New-Point 1238 938), (New-Point 1330 1070), (New-Point 1220 1197), (New-Point 1032 1140)))
    Draw-Polyline $graphics $rightPen ([System.Drawing.Point[]]@((New-Point 1458 1045), (New-Point 1665 944), (New-Point 1682 1175), (New-Point 1538 1283)))
    $rightPen.Dispose()
    $grayBrush = New-Brush 126
    $graphics.FillRectangle($grayBrush, 1140, 1300, 188, 102)
    $graphics.FillEllipse($grayBrush, 1515, 1450, 155, 88)
    $grayBrush.Dispose()
    $innerBrush = New-Brush 225
    $graphics.FillRectangle($innerBrush, 1180, 1333, 65, 35)
    $innerBrush.Dispose()
    $cornerPen = New-Pen 73 10
    $graphics.DrawLine($cornerPen, 1030, 1538, 1160, 1680)
    $graphics.DrawLine($cornerPen, 1160, 1680, 1278, 1545)
    $graphics.DrawLine($cornerPen, 1362, 1620, 1507, 1712)
    $graphics.DrawLine($cornerPen, 1507, 1712, 1695, 1580)
    $cornerPen.Dispose()
    Draw-PolygonFeature $graphics $random 1010 1205 645 470 7 48 $true
    Draw-ShortLineCluster $graphics $random 950 900 760 800 27

    # Dense asymmetric central feature cluster joins all four visual regions.
    $centerPen = New-Pen 12 15
    Draw-Polyline $graphics $centerPen ([System.Drawing.Point[]]@((New-Point 715 745), (New-Point 853 688), (New-Point 963 768), (New-Point 934 921), (New-Point 786 968), (New-Point 682 874), (New-Point 715 745)))
    $centerPen.Dispose()
    $centerBrush = New-Brush 190
    $graphics.FillPolygon($centerBrush, [System.Drawing.Point[]]@((New-Point 775 774), (New-Point 854 737), (New-Point 914 793), (New-Point 872 884), (New-Point 775 892), (New-Point 736 838)))
    $centerBrush.Dispose()
    $centerArc = New-Pen 70 10
    $graphics.DrawArc($centerArc, 655, 810, 270, 270, 102, 193)
    $centerArc.Dispose()

    # Irregular perimeter details prevent empty regions without becoming a fiducial border.
    for ($index = 0; $index -lt 42; $index++) {
        $edge = $index % 4
        switch ($edge) {
            0 { $x = 55 + $random.Next(0, 1680); $y = 155 + $random.Next(0, 75) }
            1 { $x = 1600 + $random.Next(0, 145); $y = 185 + $random.Next(0, 1450) }
            2 { $x = 80 + $random.Next(0, 1610); $y = 1600 + $random.Next(0, 130) }
            default { $x = 55 + $random.Next(0, 115); $y = 200 + $random.Next(0, 1420) }
        }

        $gray = [int]@('28', '83', '135')[$index % 3]
        $pen = New-Pen $gray (4 + ($index % 5))
        $graphics.DrawLine($pen, $x, $y, $x + $random.Next(-38, 65), $y + $random.Next(-38, 65))
        $pen.Dispose()
    }

    $graphics.Dispose()
    return $bitmap
}

$target = Draw-Target
$target.Save($targetPath, [System.Drawing.Imaging.ImageFormat]::Png)

$print = [System.Drawing.Bitmap]::new($printWidthPixels, $printHeightPixels, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$print.SetResolution(300, 300)
$printGraphics = [System.Drawing.Graphics]::FromImage($print)
$printGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$printGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$printGraphics.Clear([System.Drawing.Color]::White)

$targetLeft = [int](($printWidthPixels - $targetPrintPixels) / 2)
$targetTop = 460
$targetRect = [System.Drawing.Rectangle]::new($targetLeft, $targetTop, $targetPrintPixels, $targetPrintPixels)
$printGraphics.DrawImage($target, $targetRect)

$captionFont = [System.Drawing.Font]::new('Arial', 11, [System.Drawing.FontStyle]::Regular)
$rulerFont = [System.Drawing.Font]::new('Arial', 6, [System.Drawing.FontStyle]::Regular)
$captionBrush = New-Brush 25
$captionFormat = [System.Drawing.StringFormat]::new()
$captionFormat.Alignment = [System.Drawing.StringAlignment]::Center
$printGraphics.DrawString('Print at 100% / Actual Size', $captionFont, $captionBrush, [System.Drawing.RectangleF]::new(0, 2670, $printWidthPixels, 65), $captionFormat)
$printGraphics.DrawString('Target size: 180 mm x 180 mm', $captionFont, $captionBrush, [System.Drawing.RectangleF]::new(0, 2735, $printWidthPixels, 65), $captionFormat)

$rulerLeft = [int](($printWidthPixels - 1181) / 2)
$rulerY = 3035
$rulerPen = New-Pen 20 6
$printGraphics.DrawLine($rulerPen, $rulerLeft, $rulerY, $rulerLeft + 1181, $rulerY)
for ($millimeter = 0; $millimeter -le 100; $millimeter++) {
    $x = $rulerLeft + [int]($millimeter * 11.81)
    $tick = if ($millimeter % 10 -eq 0) { 40 } elseif ($millimeter % 5 -eq 0) { 26 } else { 15 }
    $printGraphics.DrawLine($rulerPen, $x, $rulerY - $tick, $x, $rulerY + $tick)
    if ($millimeter % 10 -eq 0) {
        $printGraphics.DrawString("$millimeter", $rulerFont, $captionBrush, [System.Drawing.RectangleF]::new($x - 34, $rulerY + 43, 68, 42), $captionFormat)
    }
}
$printGraphics.DrawString('100 mm verification scale', $captionFont, $captionBrush, [System.Drawing.RectangleF]::new(0, 3145, $printWidthPixels, 65), $captionFormat)

$rulerPen.Dispose()
$captionFormat.Dispose()
$captionBrush.Dispose()
$rulerFont.Dispose()
$captionFont.Dispose()
$printGraphics.Dispose()
$print.Save($printPath, [System.Drawing.Imaging.ImageFormat]::Png)
$print.Dispose()
$target.Dispose()

Write-Host "Generated $targetPath (1800 x 1800 px)"
Write-Host "Generated $printPath (2480 x 3508 px at 300 DPI)"
