# Requires Windows PowerShell 5.1 with WPF; no additional packages are needed.
# powershell -NoProfile -STA -File .\scripts\Build-Icons.ps1
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this script with powershell -NoProfile -STA -File .\scripts\Build-Icons.ps1'
}
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $repoRoot 'src/EhGalleryDownloader/Assets'
$extensionDirectory = Join-Path $repoRoot 'src/EhGalleryDownloader/BrowserExtension/icons'
$null = New-Item -ItemType Directory -Force -Path $assetsDirectory, $extensionDirectory
$source = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'design/app-icon.xaml'))
$drawing = [Windows.Markup.XamlReader]::Parse($source)
$drawing.Freeze()

function New-IconPng([int] $size) {
    # Supersample the vector so curves and the small toolbar sizes stay crisp.
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($drawing, (New-Object Windows.Rect(0, 0, $size, $size)))
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(($size * 4), ($size * 4), 384, 384, ([Windows.Media.PixelFormats]::Pbgra32))
    $bitmap.Render($visual)
    $scaled = New-Object Windows.Media.Imaging.TransformedBitmap($bitmap, (New-Object Windows.Media.ScaleTransform(0.25, 0.25)))
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
    $stream = New-Object IO.MemoryStream
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    } finally { $stream.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @{}
foreach ($size in $sizes) { $frames[$size] = New-IconPng $size }
[IO.File]::WriteAllBytes((Join-Path $assetsDirectory 'AppIcon.png'), $frames[256])
foreach ($size in @(16, 32, 48, 128)) {
    [IO.File]::WriteAllBytes((Join-Path $extensionDirectory "icon-$size.png"), $frames[$size])
}

$iconPath = Join-Path $assetsDirectory 'AppIcon.ico'
$iconStream = [IO.File]::Create($iconPath)
$writer = New-Object IO.BinaryWriter($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($size in $sizes) {
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$size].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$size].Length
    }
    foreach ($size in $sizes) { $writer.Write([byte[]]$frames[$size]) }
} finally { $writer.Dispose(); $iconStream.Dispose() }
Write-Output "Generated $($sizes.Count) icon sizes from design/app-icon.xaml."
