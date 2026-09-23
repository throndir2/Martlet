#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Icon generation uses the Windows WPF renderer.' }
Add-Type -AssemblyName PresentationCore, WindowsBase
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Martlet.Desktop\Assets'
[xml]$svg = Get-Content -LiteralPath (Join-Path $assets 'Martlet.svg') -Raw
if ($svg.DocumentElement.GetAttribute('viewBox') -cne '0 0 256 256') { throw 'Expected a 256-square icon source.' }

# The original artwork deliberately uses only filled paths shared by SVG and WPF.
$drawing = [Windows.Media.DrawingGroup]::new()
foreach ($node in $svg.DocumentElement.ChildNodes) {
    if ($node.LocalName -in @('title', 'desc')) { continue }
    if ($node.LocalName -ne 'path' -or $node.Attributes.Count -ne 2 -or
        -not $node.HasAttribute('fill') -or -not $node.HasAttribute('d')) {
        throw 'Unsupported artwork element. Use filled paths without transforms or strokes.'
    }
    $geometry = [Windows.Media.Geometry]::Parse($node.GetAttribute('d'))
    $brush = [Windows.Media.BrushConverter]::new().ConvertFromInvariantString($node.GetAttribute('fill'))
    $drawing.Children.Add([Windows.Media.GeometryDrawing]::new($brush, $null, $geometry))
}
$drawing.Freeze()
$frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([Windows.Media.ScaleTransform]::new($size / 64.0, $size / 64.0))
        $context.DrawDrawing($drawing)
        $context.Pop()
    } finally { $context.Close() }
    $render = [Windows.Media.Imaging.RenderTargetBitmap]::new($size * 4, $size * 4, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $render.Render($visual)
    $scaled = [Windows.Media.Imaging.TransformedBitmap]::new($render, [Windows.Media.ScaleTransform]::new(0.25, 0.25))
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    } finally { $stream.Dispose() }
}
$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assets 'Martlet.ico'), $output.ToArray())
    [IO.File]::WriteAllBytes((Join-Path $assets 'Martlet.png'), $frames[-1].Bytes)
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output 'Generated Martlet.ico (16-256 px) and Martlet.png from the original SVG.'
