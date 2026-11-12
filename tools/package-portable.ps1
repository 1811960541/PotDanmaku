param([string]$SourceDirectory, [string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SourceDirectory) { $SourceDirectory = Join-Path $projectRoot 'artifacts/PotDanmaku-win-x64' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot 'releases' }
$sourceRoot = [System.IO.Path]::GetFullPath($SourceDirectory)
$outputRootPath = [System.IO.Path]::GetFullPath($OutputRoot)
if ($outputRootPath.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRootPath.StartsWith($sourceRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw '输出目录必须位于发布源目录之外。' }
$sourceExe = Join-Path $sourceRoot 'PotDanmaku.App.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { throw '缺少发布程序，请先运行 tools/publish.ps1。' }
$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExe).ProductVersion
if ($productVersion -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?(?:\+[a-zA-Z0-9.-]+)?$') { throw '发布程序的版本格式无效。' }
$version = ($productVersion -split '\+', 2)[0]
$runtimeConfig = Get-Content -LiteralPath (Join-Path $sourceRoot 'PotDanmaku.App.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks -or -not (Test-Path -LiteralPath (Join-Path $sourceRoot 'coreclr.dll'))) {
    throw '输入必须是 Windows x64 自包含发布目录。'
}
$packageName = 'PotDanmaku-' + $version + '-portable-win-x64'
$packageDirectory = Join-Path $outputRootPath $packageName
$zipPath = Join-Path $outputRootPath ($packageName + '.zip')
if ((Test-Path -LiteralPath $packageDirectory) -or (Test-Path -LiteralPath $zipPath)) {
    throw '该独立版本已存在，请使用 -OutputRoot 指定新目录，避免覆盖正在使用的便携数据。'
}
[System.IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
    $relative = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName)
    if ($relative -match '^(data|logs|diagnostics)[\\/]' -or $relative -eq 'portable.flag' -or $file.Extension -eq '.pdb') { continue }
    # Ship end-user documentation; engineering history stays in the source repository.
    if ($relative -eq '开发计划.md' -or ($relative -match '^docs[\\/]' -and $relative.Replace('\', '/') -ne 'docs/使用说明.md')) { continue }
    $destination = Join-Path $packageDirectory $relative
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    [System.IO.File]::Copy($file.FullName, $destination, $false)
}
$assetsRoot = Join-Path $projectRoot 'packaging/portable'
foreach ($asset in Get-ChildItem -LiteralPath $assetsRoot -File) {
    $destination = Join-Path $packageDirectory $asset.Name
    if ($asset.Extension -in @('.md', '.txt')) {
        $content = [System.IO.File]::ReadAllText($asset.FullName).Replace('@@VERSION@@', $version)
        [System.IO.File]::WriteAllText($destination, $content, [System.Text.UTF8Encoding]::new($false))
    } else { [System.IO.File]::Copy($asset.FullName, $destination, $true) }
}
$releaseInfo = [ordered]@{
    Product = 'PotDanmaku'; Version = $version; ExecutableProductVersion = $productVersion; RuntimeIdentifier = 'win-x64'; Edition = 'portable'
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o'); SelfContained = $true; PortableDefault = $true
    IncludedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    SourceExecutableSha256 = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
}
$releaseInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-info.json') -Encoding utf8
$hashes = foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Sort-Object FullName) {
    $relative = [System.IO.Path]::GetRelativePath($packageDirectory, $file.FullName).Replace('\', '/')
    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
}
[System.IO.File]::WriteAllLines((Join-Path $packageDirectory 'files.sha256'), [string[]]$hashes, [System.Text.UTF8Encoding]::new($false))
$temporaryZip = $zipPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $temporaryZip,
        [System.IO.Compression.CompressionLevel]::Optimal, $true)
    [System.IO.File]::Move($temporaryZip, $zipPath)
} finally { if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip } }
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(($zipPath + '.sha256'), $zipHash + '  ' + [System.IO.Path]::GetFileName($zipPath) + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))
[PSCustomObject]@{
    Version = $version; PackageDirectory = $packageDirectory; ZipPath = $zipPath
    ZipMB = [Math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2); Sha256 = $zipHash
} | ConvertTo-Json
