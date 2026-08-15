param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Icon.ico')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r)
{
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size)
{
    $scale = $size / 256.0
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    function S([double]$value) { return [int][Math]::Round($value * $scale) }

    $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(255, 76, 194, 255),
        [System.Drawing.Color]::FromArgb(255, 0, 103, 192),
        45)
    $g.FillPath($gradient, $tile)

    $highlight = New-RoundedRectPath (S 14) (S 14) (S 228) (S 118) (S 58)
    $g.FillPath(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(18, 255, 255, 255)),
        $highlight)

    $folderWhite = [System.Drawing.Color]::FromArgb(242, 255, 255, 255)
    $folderBack = New-RoundedRectPath (S 46) (S 82) (S 164) (S 106) (S 22)
    $g.FillPath([System.Drawing.SolidBrush]::new($folderWhite), $folderBack)

    $folderFront = New-RoundedRectPath (S 48) (S 98) (S 158) (S 88) (S 20)
    $g.FillPath([System.Drawing.SolidBrush]::new($folderWhite), $folderFront)

    $tabInactive = New-RoundedRectPath (S 76) (S 62) (S 54) (S 38) (S 9)
    $g.FillPath(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255)),
        $tabInactive)

    $tabActive = New-RoundedRectPath (S 138) (S 62) (S 54) (S 38) (S 9)
    $g.FillPath(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 103, 192)),
        $tabActive)

    if ($size -ge 32)
    {
        $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(230, 255, 255, 255))
        $g.FillEllipse($dotBrush, (S 160), (S 76), (S 10), (S 10))
    }

    $g.Dispose()
    return $bmp
}

function Write-Ico([string]$path)
{
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()

    foreach ($size in $sizes)
    {
        $bmp = New-IconBitmap $size
        $stream = [System.IO.MemoryStream]::new()
        $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , ($stream.ToArray())
        $bmp.Dispose()
        $stream.Dispose()
    }

    $file = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($file)
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    for ($i = 0; $i -lt $images.Count; $i++)
    {
        $size = $sizes[$i]
        $data = $images[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$data.Length)
        $writer.Write([uint32]$offset)
        $offset += $data.Length
    }

    foreach ($data in $images)
    {
        $writer.Write($data)
    }

    $writer.Dispose()
    $file.Dispose()
}

Write-Ico $OutputPath
Write-Output "ICON_WRITTEN $OutputPath"
