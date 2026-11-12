param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$ReportPath,
    [string]$RealPlayerExecutable
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$harnessRoot = Join-Path $projectRoot 'artifacts/PotDanmaku.SessionTests-win-x64'
foreach ($name in @('PotDanmaku.App.dll', 'PotDanmaku.Core.dll', 'PotDanmaku.Formats.dll', 'coreclr.dll', 'PotDanmaku.App.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $name) -PathType Leaf)) { throw ('缺少自包含发布文件：' + $name) }
}
foreach ($name in @('PotDanmaku.SessionTests.exe', 'PotDanmaku.SessionTests.dll', 'PotDanmaku.SessionTests.deps.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $harnessRoot $name) -PathType Leaf)) { throw '请先将 tests/PotDanmaku.SessionTests 自包含发布到 artifacts/PotDanmaku.SessionTests-win-x64。' }
}
$validationRoot = Join-Path ([IO.Path]::GetTempPath()) ('PotDanmaku 发布会话验证 ' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($validationRoot) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName)
    if ($relative -match '^(data|logs|diagnostics)[\\/]' -or $file.Extension -eq '.pdb') { continue }
    $destination = Join-Path $validationRoot $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($file.FullName, $destination, $false)
}
foreach ($name in @('PotDanmaku.SessionTests.exe', 'PotDanmaku.SessionTests.dll', 'PotDanmaku.SessionTests.deps.json')) {
    [IO.File]::Copy((Join-Path $harnessRoot $name), (Join-Path $validationRoot $name), $false)
}
# Keep the test driver, but run the frozen application assemblies and its bundled runtime.
[IO.File]::Copy((Join-Path $packageRoot 'PotDanmaku.App.runtimeconfig.json'),
    (Join-Path $validationRoot 'PotDanmaku.SessionTests.runtimeconfig.json'), $false)
$phases = @([pscustomobject]@{ Name = 'associations-and-switching'; Arguments = @() })
if ($RealPlayerExecutable) {
    $phases += [pscustomobject]@{ Name = 'real-pause-1x-3x'; Arguments = @('--real-pause', [IO.Path]::GetFullPath($RealPlayerExecutable)) }
}
$checks = foreach ($phase in $phases) {
    $casesRoot = Join-Path $validationRoot 'session-cases'
    $before = if (Test-Path -LiteralPath $casesRoot) { @(Get-ChildItem -LiteralPath $casesRoot -Directory).FullName } else { @() }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $validationRoot 'PotDanmaku.SessionTests.exe'))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $validationRoot
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['DOTNET_ROOT'] = Join-Path $validationRoot '不存在的共享运行环境'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']; $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    foreach ($argument in $phase.Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) { $process.Kill(); throw ('会话检查超时：' + $phase.Name) }
        [IO.File]::WriteAllText((Join-Path $validationRoot ($phase.Name + '.stdout.txt')), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $validationRoot ($phase.Name + '.stderr.txt')), $stderr.GetAwaiter().GetResult())
        if (-not (Test-Path -LiteralPath $casesRoot)) { throw ('测试驱动未启动，见 ' + (Join-Path $validationRoot ($phase.Name + '.stderr.txt'))) }
        $newCases = @(Get-ChildItem -LiteralPath $casesRoot -Directory | Where-Object { $_.FullName -notin $before })
        if ($newCases.Count -ne 1) { throw ('未找到唯一的检查报告：' + $phase.Name) }
        $resultPath = Join-Path $newCases[0].FullName 'verification.json'
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or -not $result.Passed) { throw ('发布会话检查失败，见 ' + $resultPath) }
        if ($result.Assemblies.Count -ne 3) { throw '检查报告缺少实际加载的应用程序集。' }
        foreach ($assembly in $result.Assemblies) {
            $expectedLocation = Join-Path $validationRoot ($assembly.Name + '.dll')
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot ($assembly.Name + '.dll')) -Algorithm SHA256).Hash
            if (-not $assembly.Location.Equals($expectedLocation, [StringComparison]::OrdinalIgnoreCase) -or $assembly.Sha256 -ne $sourceHash) {
                throw ('未使用本次发布的程序集：' + $assembly.Name)
            }
        }
        if (-not $result.RuntimeAssembly.Equals((Join-Path $validationRoot 'System.Private.CoreLib.dll'), [StringComparison]::OrdinalIgnoreCase)) {
            throw '会话检查未使用包内运行环境。'
        }
        $metricsPath = Join-Path $newCases[0].FullName 'pause-metrics.json'
        [pscustomobject]@{ Name = $phase.Name; Passed = $true; Cases = @($result.Cases); ResultPath = $resultPath
            PublishedAssembliesVerified = $true; BundledRuntimeLoaded = $true
            PauseMetrics = if (Test-Path -LiteralPath $metricsPath) { Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json } else { $null } }
    } finally { $process.Dispose() }
}
$executableProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageRoot 'PotDanmaku.App.exe')).ProductVersion
$report = [ordered]@{
    Version = ($executableProductVersion -split '\+', 2)[0]; ExecutableProductVersion = $executableProductVersion
    Passed = $true; PackageDirectory = $packageRoot; ValidationDirectory = $validationRoot; Checks = @($checks)
    Notes = '测试驱动在项目外运行，并校验实际加载的 App/Core/Formats 哈希及包内运行环境；没有修改原发布目录。'
}
if ($ReportPath) {
    $fullReportPath = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullReportPath)) | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullReportPath -Encoding utf8
}
$report | ConvertTo-Json -Depth 8
