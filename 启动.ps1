$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectXml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\PotDanmaku.App\PotDanmaku.App.csproj') -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
$candidates = @(
    (Join-Path $projectRoot 'artifacts\PotDanmaku-win-x64\PotDanmaku.App.exe'),
    (Join-Path $projectRoot ("releases\PotDanmaku-$version-portable-win-x64\PotDanmaku.App.exe"))
)
$publishedApp = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ($publishedApp) {
    Start-Process -FilePath $publishedApp -WorkingDirectory (Split-Path -Parent $publishedApp) -WindowStyle Hidden
} else {
    Push-Location -LiteralPath $projectRoot
    try { dotnet run --project src/PotDanmaku.App -c Release; if ($LASTEXITCODE -ne 0) { throw "程序退出码：$LASTEXITCODE" } }
    finally { Pop-Location }
}
