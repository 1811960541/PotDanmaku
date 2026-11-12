param([Parameter(Mandatory)][string]$ZipPath, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$zipFullPath = [System.IO.Path]::GetFullPath($ZipPath)
$zipHash = (Get-FileHash -LiteralPath $zipFullPath -Algorithm SHA256).Hash
$expectedZipHash = ((Get-Content -LiteralPath ($zipFullPath + '.sha256') -Raw).Trim() -split '\s+', 2)[0]
if ($zipHash -ne $expectedZipHash) { throw 'ZIP 校验失败。' }
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipFullPath)
try {
    $entryNames = @($archive.Entries.FullName)
    $roots = @($entryNames | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
    if ($roots.Count -ne 1) { throw '压缩包应只包含一个顶层程序目录。' }
    $packageName = $roots[0]
    if (@($entryNames | Where-Object { $_ -match '/(data|logs|diagnostics)/' -or $_ -match '\.pdb$' }).Count) {
        throw '压缩包包含运行数据或调试符号。'
    }
    foreach ($required in @('PotDanmaku.App.exe', 'coreclr.dll', 'portable.flag', 'README.md', '开始使用.txt', 'release-info.json', 'files.sha256', 'THIRD-PARTY-NOTICES.md')) {
        if (($packageName + '/' + $required) -notin $entryNames) { throw ('缺少文件：' + $required) }
    }
} finally { $archive.Dispose() }
$validationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('PotDanmaku 独立便携验证 ' + [guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory($zipFullPath, $validationRoot)
$packageRoot = Join-Path $validationRoot $packageName
$packagePrefix = $packageRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$verifiedFiles = 0
foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $packageRoot 'files.sha256'))) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw '文件清单格式错误。' }
    $expectedHash = $Matches[1]; $relative = $Matches[2]
    $filePath = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $relative))
    if (-not $filePath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '清单文件超出程序目录。' }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $expectedHash) { throw ('文件校验失败：' + $relative) }
    $verifiedFiles++
}
$releaseInfo = Get-Content -LiteralPath (Join-Path $packageRoot 'release-info.json') -Raw | ConvertFrom-Json
$executable = Join-Path $packageRoot 'PotDanmaku.App.exe'
if ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion -ne $releaseInfo.Version -or
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $releaseInfo.SourceExecutableSha256) {
    throw '版本或程序二进制校验失败。'
}
$loadedCoreClrPath = $null
$checks = foreach ($format in @('xml', 'json', 'ass')) {
    $resultPath = Join-Path $validationRoot ('inspect-' + $format + '.json')
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true; $startInfo.WorkingDirectory = $packageRoot
    foreach ($argument in @('--inspect', '--file', (Join-Path $packageRoot ('samples/demo.' + $format)), '--output', $resultPath)) {
        $startInfo.ArgumentList.Add($argument)
    }
    # Isolate shared-runtime discovery; also inspect the actual loaded native CLR.
    $startInfo.Environment['DOTNET_ROOT'] = Join-Path $validationRoot '不存在的共享运行环境'
    $startInfo.Environment['DOTNET_ROOT_X64'] = $startInfo.Environment['DOTNET_ROOT']
    $startInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (-not $process.HasExited) {
            if (-not $loadedCoreClrPath) {
                try {
                    $process.Refresh()
                    foreach ($module in $process.Modules) {
                        if ($module.ModuleName -eq 'coreclr.dll') { $loadedCoreClrPath = $module.FileName; break }
                    }
                } catch [System.InvalidOperationException] { }
                  catch [System.ComponentModel.Win32Exception] { }
            }
            if ([DateTime]::UtcNow -gt $deadline) { $process.Kill(); throw ('导入超时：' + $format) }
            $process.WaitForExit(10) | Out-Null
        }
        if ($process.ExitCode -ne 0) { throw ('发布程序导入失败：' + $format) }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($result.Import.Accepted -ne 9 -or $result.Import.Invalid -ne 0) { throw ('样本导入计数错误：' + $format) }
        [PSCustomObject]@{ Format = $format; Accepted = $result.Import.Accepted; Invalid = $result.Import.Invalid; ExitCode = $process.ExitCode }
    } finally { $process.Dispose() }
}
$expectedCoreClrPath = Join-Path $packageRoot 'coreclr.dll'
if (-not $loadedCoreClrPath -or -not $loadedCoreClrPath.Equals($expectedCoreClrPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw '未确认程序加载包内的 .NET 运行环境。'
}
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'data/logs/app.log'))) { throw '默认便携模式没有创建本地 data 日志。' }
$report = [ordered]@{
    Version = $releaseInfo.Version; ZipSha256Passed = $true; VerifiedFiles = $verifiedFiles
    SourceExecutableUnchanged = $true; BundledRuntimeLoaded = $true; CoreClrPath = $loadedCoreClrPath
    PortableDataCreated = $true; SpaceAndUnicodePathPassed = $true; ProjectDirectoryNotRequired = $true
    ExtractedDirectory = $packageRoot; FormatChecks = @($checks)
    Notes = '在本机项目目录之外验证，并确认实际加载包内 CLR；未模拟干净 Windows 或重测图形交互。'
}
if ($ReportPath) {
    $reportFullPath = [System.IO.Path]::GetFullPath($ReportPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($reportFullPath)) | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportFullPath -Encoding utf8
}
$report | ConvertTo-Json -Depth 5
