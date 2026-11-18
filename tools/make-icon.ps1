<#
.SYNOPSIS
    Genera el icono oficial de Selbar (src/Selbar/Assets/app.ico) sin dependencias externas.

.DESCRIPTION
    Dibuja el isotipo Dual-Bar S (Opción 2) de Selbar sobre squircle jade (#286473 -> #12353F)
    con barra superior blanca ("Sel") y barra inferior cian ("Bar", #94EFFA -> #4FD0E1) en
    16, 24, 32, 48, 64, 128 y 256 px, empaquetado como .ico con entradas DIB de 32 bits
    (compatibles con cualquier versión de Windows y con System.Drawing.Icon).
#>
[CmdletBinding()]
param(
    [string]$OutFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Selbar\Assets\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectF {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
    $d = $Radius * 2.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-QuadBezier {
    param(
        [System.Drawing.Drawing2D.GraphicsPath]$Path,
        [float]$X0, [float]$Y0, [float]$CX, [float]$CY, [float]$X1, [float]$Y1
    )
    $c1x = $X0 + (2.0 / 3.0) * ($CX - $X0)
    $c1y = $Y0 + (2.0 / 3.0) * ($CY - $Y0)
    $c2x = $X1 + (2.0 / 3.0) * ($CX - $X1)
    $c2y = $Y1 + (2.0 / 3.0) * ($CY - $Y1)
    $Path.AddBezier($X0, $Y0, $c1x, $c1y, $c2x, $c2y, $X1, $Y1)
}

function New-TopBarPath {
    param([float]$K)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine(25.0 * $K, 11.6 * $K, 46.2 * $K, 11.6 * $K)
    $p.AddArc((46.2 - 4.8) * $K, 11.6 * $K, 9.6 * $K, 9.6 * $K, 270, 180)
    $p.AddLine(46.2 * $K, 21.2 * $K, 25.0 * $K, 21.2 * $K)
    $p.AddArc((25.0 - 3.0) * $K, 21.2 * $K, 6.0 * $K, 6.0 * $K, 270, -180)
    $p.AddLine(25.0 * $K, 27.2 * $K, 31.8 * $K, 27.2 * $K)
    Add-QuadBezier -Path $p -X0 (31.8 * $K) -Y0 (27.2 * $K) -CX (33.0 * $K) -CY (27.2 * $K) -X1 (32.6 * $K) -Y1 (28.3 * $K)
    $p.AddLine(32.6 * $K, 28.3 * $K, 29.9 * $K, 35.7 * $K)
    Add-QuadBezier -Path $p -X0 (29.9 * $K) -Y0 (35.7 * $K) -CX (29.5 * $K) -CY (36.8 * $K) -X1 (28.3 * $K) -Y1 (36.8 * $K)
    $p.AddLine(28.3 * $K, 36.8 * $K, 25.0 * $K, 36.8 * $K)
    $p.AddArc((25.0 - 12.6) * $K, 11.6 * $K, 25.2 * $K, 25.2 * $K, 90, 180)
    $p.CloseFigure()
    return $p
}

function New-BottomBarPath {
    param([float]$K)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddLine(39.0 * $K, 52.4 * $K, 17.2 * $K, 52.4 * $K)
    $p.AddArc((17.2 - 4.8) * $K, 42.8 * $K, 9.6 * $K, 9.6 * $K, 90, 180)
    $p.AddLine(17.2 * $K, 42.8 * $K, 39.0 * $K, 42.8 * $K)
    $p.AddArc((39.0 - 3.0) * $K, 36.8 * $K, 6.0 * $K, 6.0 * $K, 90, -180)
    $p.AddLine(39.0 * $K, 36.8 * $K, 32.2 * $K, 36.8 * $K)
    Add-QuadBezier -Path $p -X0 (32.2 * $K) -Y0 (36.8 * $K) -CX (31.0 * $K) -CY (36.8 * $K) -X1 (31.4 * $K) -Y1 (35.7 * $K)
    $p.AddLine(31.4 * $K, 35.7 * $K, 34.1 * $K, 28.3 * $K)
    Add-QuadBezier -Path $p -X0 (34.1 * $K) -Y0 (28.3 * $K) -CX (34.5 * $K) -CY (27.2 * $K) -X1 (35.7 * $K) -Y1 (27.2 * $K)
    $p.AddLine(35.7 * $K, 27.2 * $K, 39.0 * $K, 27.2 * $K)
    $p.AddArc((39.0 - 12.6) * $K, 27.2 * $K, 25.2 * $K, 25.2 * $K, 270, 180)
    $p.CloseFigure()
    return $p
}

function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([UInt32]40)
        $writer.Write([Int32]$size)
        $writer.Write([Int32]($size * 2))
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]0)
        $writer.Write([UInt32]($size * $size * 4))
        $writer.Write([Int32]0)
        $writer.Write([Int32]0)
        $writer.Write([UInt32]0)
        $writer.Write([UInt32]0)

        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $color = $Bitmap.GetPixel($x, $y)
                $writer.Write([byte]$color.B)
                $writer.Write([byte]$color.G)
                $writer.Write([byte]$color.R)
                $writer.Write([byte]$color.A)
            }
        }

        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $row = New-Object byte[] $maskStride
            for ($x = 0; $x -lt $size; $x++) {
                if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                    $byteIdx = [int][Math]::Floor($x / 8)
                    $bitMask = [byte](0x80 -shr ($x % 8))
                    $row[$byteIdx] = [byte]($row[$byteIdx] -bor $bitMask)
                }
            }
            $writer.Write($row)
        }

        $writer.Flush()
        return $stream.ToArray()
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = New-Object System.Collections.Generic.List[object]

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $k = [float]$size / 64.0
            $inset = if ($size -le 24) { 1.0 * $k } else { 2.0 * $k }
            $box = [float]$size - 2.0 * $inset
            $rx = if ($size -le 24) { 14.0 * $k } else { 15.0 * $k }

            $bgPath = New-RoundedRectF -X $inset -Y $inset -Width $box -Height $box -Radius $rx
            $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                (New-Object System.Drawing.PointF((6.0 * $k), (4.0 * $k))),
                (New-Object System.Drawing.PointF((58.0 * $k), (60.0 * $k))),
                [System.Drawing.Color]::FromArgb(0x28, 0x64, 0x73),
                [System.Drawing.Color]::FromArgb(0x12, 0x35, 0x3F))
            try { $graphics.FillPath($bgBrush, $bgPath) } finally { $bgBrush.Dispose(); $bgPath.Dispose() }

            if ($size -ge 24) {
                $bInset = $inset + 0.75 * $k
                $bBox = [float]$size - 2.0 * $bInset
                $bPath = New-RoundedRectF -X $bInset -Y $bInset -Width $bBox -Height $bBox -Radius ($rx - 0.75 * $k)
                $bPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 0x7C, 0xE5, 0xF2), [Math]::Max(1.0, 1.5 * $k))
                try { $graphics.DrawPath($bPen, $bPath) } finally { $bPen.Dispose(); $bPath.Dispose() }
            }

            $topBar = New-TopBarPath -K $k
            try { $graphics.FillPath([System.Drawing.Brushes]::White, $topBar) } finally { $topBar.Dispose() }

            $botBar = New-BottomBarPath -K $k
            $cyanBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                (New-Object System.Drawing.PointF((12.0 * $k), (27.0 * $k))),
                (New-Object System.Drawing.PointF((52.0 * $k), (53.0 * $k))),
                [System.Drawing.Color]::FromArgb(0x94, 0xEF, 0xFA),
                [System.Drawing.Color]::FromArgb(0x4F, 0xD0, 0xE1))
            try { $graphics.FillPath($cyanBrush, $botBar) } finally { $cyanBrush.Dispose(); $botBar.Dispose() }
        } finally {
            $graphics.Dispose()
        }

        $images.Add([pscustomobject]@{ Size = $size; Bytes = (ConvertTo-IconDib -Bitmap $bitmap) })
    } finally {
        $bitmap.Dispose()
    }
}

$directory = Split-Path -Parent $OutFile
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

$file = [System.IO.File]::Create($OutFile)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)

    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([Byte]$dimension)
        $writer.Write([Byte]$dimension)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
    $writer.Flush()
} finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host ("Icono Selbar generado: {0} ({1:N0} bytes)" -f $OutFile, (Get-Item $OutFile).Length)
