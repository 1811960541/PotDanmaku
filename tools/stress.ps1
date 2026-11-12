param([int[]]$Counts = @(100000, 500000))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appExe = Join-Path $projectRoot 'src\PotDanmaku.App\bin\Release\net10.0-windows\PotDanmaku.App.exe'
if (-not (Test-Path -LiteralPath $appExe)) { throw '请先执行 dotnet build PotDanmaku.sln -c Release。' }
$stressFolder = Join-Path $projectRoot 'artifacts\stress'
New-Item -ItemType Directory -Path $stressFolder -Force | Out-Null
foreach ($recordCount in $Counts) {
    if ($recordCount -lt 1 -or $recordCount -gt 500000) { throw '压力记录数须在 1～500000 之间。' }
    $inputPath = Join-Path $stressFolder ('xml-' + $recordCount + '.xml')
    $reportPath = Join-Path $stressFolder ('report-' + $recordCount + '.json')
    $writer = [System.IO.StreamWriter]::new($inputPath, $false, [System.Text.UTF8Encoding]::new($false))
    try {
        $writer.WriteLine('<i>')
        for ($recordIndex = 0; $recordIndex -lt $recordCount; $recordIndex++) {
            $mediaTime = ($recordIndex * 0.04).ToString('F2', [System.Globalization.CultureInfo]::InvariantCulture)
            $writer.WriteLine('<d p="' + $mediaTime + ',1,16,16777215">压力弹幕 ' + $recordIndex + '</d>')
        }
        $writer.WriteLine('</i>')
    } finally { $writer.Dispose() }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($appExe)
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
    foreach ($argument in @('--inspect', '--file', $inputPath, '--output', $reportPath)) { $startInfo.ArgumentList.Add($argument) }
    $inspectProcess = [System.Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $inspectProcess.WaitForExit(60000)) { throw '压力检查超过 60 秒，进程仍在运行，请查看报告与任务管理器。' }
        if ($inspectProcess.ExitCode -ne 0) { throw ('压力检查失败：' + $reportPath) }
        $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($result.Import.Accepted -ne $recordCount) { throw ('导入数量不符合预期：' + $reportPath) }
        [pscustomobject]@{
            Records = $recordCount; ParseMs = $result.ParseMs; LayoutMs = $result.LayoutMs
            PeakWorkingSetMB = $result.PeakWorkingSetMB; PrivateAfterLayoutMB = $result.PrivateAfterLayoutMB
            Scheduled = $result.Layout.Accepted; Crowded = $result.Layout.Crowded
        }
    } finally { $inspectProcess.Dispose() }
}
