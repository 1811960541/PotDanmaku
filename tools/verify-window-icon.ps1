param([string]$PackageDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $projectRoot 'artifacts/PotDanmaku-win-x64' }
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/window-icon-verification' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
foreach ($name in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'System.Drawing.Common.dll')) {
    Add-Type -Path (Join-Path $packageRoot $name)
}

# Read the published ICO without opening or controlling any desktop windows.
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $packageRoot 'PotDanmaku.App.dll'))
$resourceStream = $assembly.GetManifestResourceStream('PotDanmaku.App.g.resources')
$reader = [Resources.ResourceReader]::new($resourceStream)
$icoPath = Join-Path $outputRoot 'embedded-window-icon.ico'
$found = $false
try {
    $resources = $reader.GetEnumerator()
    while ($resources.MoveNext()) {
        if ($resources.Key -eq 'assets/potdanmaku.ico') {
            $output = [IO.File]::Create($icoPath)
            try { $resources.Value.CopyTo($output) } finally { $output.Dispose() }
            $found = $true
        }
    }
} finally { $reader.Dispose(); $resourceStream.Dispose() }
if (-not $found) { throw 'Published window icon resource is missing.' }

# Exercise the real XAML resource definition with the same published icon bytes.
# A file URI avoids dependence on PowerShell's entry assembly in pack URI resolution.
$appXaml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/PotDanmaku.App/App.xaml') -Raw)
$resourceNode = $appXaml.DocumentElement.ChildNodes | Where-Object { $_.LocalName -eq 'Application.Resources' }
$uri = [Uri]::new($icoPath)
$resourceXaml = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' +
    $resourceNode.InnerXml.Replace('pack://application:,,,/Assets/PotDanmaku.ico', $uri.AbsoluteUri) + '</ResourceDictionary>'
$dictionary = [Windows.Markup.XamlReader]::Parse($resourceXaml)
$image = $dictionary['AppIcon']
if ($image -isnot [Windows.Media.Imaging.BitmapFrame] -or $image.Decoder -isnot [Windows.Media.Imaging.IconBitmapDecoder]) {
    throw 'Window icon lost its multi-frame ICO decoder.'
}

# Reproduce alpha.5, then compare native HICON artwork bounds through WPF's own conversion.
# This internal API is used only by the diagnostic tool, never by the product.
$oldImage = [Windows.Media.Imaging.BitmapImage]::new()
$oldImage.BeginInit(); $oldImage.UriSource = $uri
$oldImage.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad; $oldImage.EndInit()
$helper = [Windows.Window].Assembly.GetType('MS.Internal.AppModel.IconHelper')
$method = $helper.GetMethod('CreateIconHandleFromImageSource', [Reflection.BindingFlags]'Public, Static')
function Measure-Icon($source, [int]$size, [string]$previewName) {
    $handle = $method.Invoke($null, @($source, [Windows.Size]::new($size, $size)))
    $icon = [Drawing.Icon]::FromHandle($handle.DangerousGetHandle())
    $bitmap = $icon.ToBitmap()
    try {
        $minX = $bitmap.Width; $minY = $bitmap.Height; $maxX = -1; $maxY = -1
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                if ($bitmap.GetPixel($x, $y).A -ge 128) {
                    $minX = [Math]::Min($minX, $x); $minY = [Math]::Min($minY, $y)
                    $maxX = [Math]::Max($maxX, $x); $maxY = [Math]::Max($maxY, $y)
                }
            }
        }
        if ($previewName) { $bitmap.Save((Join-Path $outputRoot $previewName), [Drawing.Imaging.ImageFormat]::Png) }
        [pscustomobject]@{ RequestedSize = $size; HandleWidth = $bitmap.Width; HandleHeight = $bitmap.Height; ArtworkWidth = $maxX - $minX + 1; ArtworkHeight = $maxY - $minY + 1 }
    } finally { $bitmap.Dispose(); $icon.Dispose(); $handle.Dispose() }
}
$before = Measure-Icon $oldImage 32 'before-32.png'
$checks = foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
    $check = Measure-Icon $image $size ('after-' + $size + '.png')
    if ($check.HandleWidth -ne $size -or $check.HandleHeight -ne $size -or
        $check.ArtworkWidth -lt $size * 0.85 -or $check.ArtworkHeight -lt $size * 0.85) {
        throw ('Icon artwork is too small for the requested size: ' + $size)
    }
    $check
}
if ($before.ArtworkWidth -ge 24) { throw 'The alpha.5 small-icon regression was not reproduced.' }
$executableProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageRoot 'PotDanmaku.App.exe')).ProductVersion
$report = [ordered]@{
    Version = ($executableProductVersion -split '\+', 2)[0]; ExecutableProductVersion = $executableProductVersion
    SourceResourceType = $image.GetType().Name; Decoder = $image.Decoder.GetType().Name
    FrameSizes = @($image.Decoder.Frames.PixelWidth); Alpha5Reproduced = $true
    BeforeAt32 = $before; FixedIconChecks = @($checks); Passed = $true
    Notes = 'Headless native icon checks; does not replace desktop screenshots or cross-monitor DPI acceptance.'
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'verification.json') -Encoding utf8
$report | ConvertTo-Json -Depth 5
