$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet test PotDanmaku.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw '测试失败，停止发布。' }
    # Normalize source paths in distributable assemblies.
    dotnet publish src/PotDanmaku.App -c Release -r win-x64 --self-contained true -p:ContinuousIntegrationBuild=true -o artifacts/PotDanmaku-win-x64
    if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
    Get-FileHash -LiteralPath 'artifacts/PotDanmaku-win-x64/PotDanmaku.App.exe' -Algorithm SHA256 |
        Select-Object Algorithm, Hash | ConvertTo-Json | Set-Content -LiteralPath 'artifacts/PotDanmaku-win-x64/checksum.json' -Encoding utf8
    $projectXml = [xml](Get-Content -LiteralPath 'src/PotDanmaku.App/PotDanmaku.App.csproj' -Raw)
    $version = [string]$projectXml.Project.PropertyGroup.Version
    $packageRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/PotDanmaku-win-x64'))
    $zipPath = Join-Path $projectRoot ('artifacts/PotDanmaku-' + $version + '-win-x64.zip')
    $temporaryZip = $zipPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $archive = [System.IO.Compression.ZipFile]::Open($temporaryZip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
                $relative = [System.IO.Path]::GetRelativePath($packageRoot, $file.FullName)
                if ($relative -match '^(data|logs|diagnostics)[\\/]' -or $relative -eq 'portable.flag') { continue }
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
        [System.IO.File]::Move($temporaryZip, $zipPath, $true)
    } finally { if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip } }
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Select-Object Algorithm,Hash |
        ConvertTo-Json | Set-Content -LiteralPath ($zipPath + '.sha256.json') -Encoding utf8
    Write-Output ('已生成压缩包：' + $zipPath)
    Write-Output '已发布到 artifacts/PotDanmaku-win-x64；请保留整个目录。'
} finally { Pop-Location }
