param([Parameter(Mandatory)][string]$Folder, [double[]]$PausedMs = @())
$ErrorActionPreference = 'Stop'
$frames = @(Import-Csv -LiteralPath (Join-Path $Folder 'frames.csv'))
$summary = Get-Content -LiteralPath (Join-Path $Folder 'summary.json') -Raw | ConvertFrom-Json
$intervals = @($frames | Where-Object { [double]$_.submission_interval_ms -gt 0 } | ForEach-Object { [double]$_.submission_interval_ms } | Sort-Object)
$validDxgi = @($frames | Where-Object { $_.dxgi_available -eq 'True' -and [long]$_.sync_qpc -gt 0 -and [long]$_.present_count -gt 0 })
$run = [System.Collections.Generic.List[object]]::new()
$dxgi = @()
foreach ($frame in $frames) {
    $valid = $frame.dxgi_available -eq 'True' -and [long]$frame.sync_qpc -gt 0 -and [long]$frame.present_count -gt 0
    $breaks = -not $valid
    if ($valid -and $run.Count) {
        $previous = $run[$run.Count - 1]
        $breaks = [long]$frame.present_count -lt [long]$previous.present_count -or [long]$frame.sync_qpc -lt [long]$previous.sync_qpc -or
            ([long]$frame.sync_qpc - [long]$previous.sync_qpc) / [System.Diagnostics.Stopwatch]::Frequency -gt 0.25
    }
    if ($breaks) { if ($run.Count -gt $dxgi.Count) { $dxgi = $run.ToArray() }; $run.Clear() }
    if ($valid) { $run.Add($frame) }
}
if ($run.Count -gt $dxgi.Count) { $dxgi = $run.ToArray() }
$rate = $null
$span = $null
if ($dxgi.Count -gt 1) {
    $span = ([long]$dxgi[-1].sync_qpc - [long]$dxgi[0].sync_qpc) / [System.Diagnostics.Stopwatch]::Frequency
    $countDelta = [long]$dxgi[-1].present_count - [long]$dxgi[0].present_count
    if ($span -gt 0 -and $countDelta -gt 0) { $rate = $countDelta / $span }
}
$pauseChecks = foreach ($time in $PausedMs) {
    $matching = @($frames | Where-Object { [Math]::Abs([double]$_.media_ms - $time) -lt 0.001 })
    [PSCustomObject]@{
        MediaMs = $time
        Frames = $matching.Count
        DistinctX = @($matching.x_ref | Sort-Object -Unique).Count
        DistinctFirstSource = @($matching.first_source_index | Sort-Object -Unique).Count
        DistinctActiveCount = @($matching.active_count | Sort-Object -Unique).Count
    }
}
$playingSteps = [System.Collections.Generic.List[double]]::new()
$clockRebuilds = 0
$movingPairs = 0
$frozenMovingPairs = 0
$backwardSteps = 0
$oversizedSteps = 0
for ($index = 1; $index -lt $frames.Count; $index++) {
    $a = $frames[$index - 1]; $b = $frames[$index]
    if ($a.generation -ne $b.generation -or [double]$b.submission_interval_ms -le 0) { continue }
    if ($a.discontinuities -ne $b.discontinuities) { $clockRebuilds++ }
    if ($a.clock_state -notin @('Playing', 'Preview') -or $a.clock_state -ne $b.clock_state -or $a.rate -ne $b.rate) { continue }
    $step = [double]$b.media_ms - [double]$a.media_ms
    $playingSteps.Add($step)
    if ($step -lt -0.001) { $backwardSteps++ }
    $renderElapsedMs = ([double]$b.monotonic_s - [double]$a.monotonic_s) * 1000 + [double]$a.draw_and_present_ms - [double]$b.draw_and_present_ms
    if ($step -gt $renderElapsedMs * [double]$b.rate + 10) { $oversizedSteps++ }
    if ($a.first_source_index -eq $b.first_source_index -and $a.first_mode -in @('Scroll','Reverse')) {
        $movingPairs++
        if ([double]$a.x_physical -eq [double]$b.x_physical) { $frozenMovingPairs++ }
    }
}
$result = [PSCustomObject]@{
    RecordedFrames = $frames.Count
    MaxActive = ($frames | ForEach-Object { [int]$_.active_count } | Measure-Object -Maximum).Maximum
    SubmissionP95Ms = if ($intervals.Count) { $intervals[[Math]::Ceiling($intervals.Count * 0.95) - 1] } else { $null }
    SubmissionMaxMs = if ($intervals.Count) { $intervals[-1] } else { $null }
    ValidDxgiFrames = $validDxgi.Count
    LongestContinuousDxgiFrames = $dxgi.Count
    DxgiSpanSeconds = $span
    AveragePresentCountPerSecond = $rate
    PausedChecks = @($pauseChecks)
    ClockRebuilds = $clockRebuilds
    PlayingFramePairs = $playingSteps.Count
    BackwardPlayingSteps = $backwardSteps
    OversizedPlayingSteps = $oversizedSteps
    MovingCommentPairs = $movingPairs
    UnchangedMovingPositions = $frozenMovingPairs
    LastVisibleHitPassed = $summary.LastVisibleHitPassed
    GraphicsError = $summary.Renderer.Error
    Note = 'Submission and DXGI present counts are not camera measurements of physical display frames.'
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Folder 'analysis.json') -Encoding utf8
$result | ConvertTo-Json -Depth 6
