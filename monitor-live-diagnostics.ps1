[CmdletBinding()]
param(
    [ValidateRange(10, 86400)]
    [int]$DurationSeconds = 900,

    [ValidateRange(1, 60)]
    [int]$SampleIntervalSeconds = 1,

    [string]$Scenario = 'arena-offset-validation',
    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot 'diagnostics'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$activeSessionPath = Join-Path $OutputRoot 'active-session.json'
if (Test-Path -LiteralPath $activeSessionPath -PathType Leaf) {
    try {
        $active = Get-Content -LiteralPath $activeSessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (Get-Process -Id ([int]$active.MonitorProcessId) -ErrorAction SilentlyContinue) {
            throw "Another Arena diagnostic monitor is running (PID $($active.MonitorProcessId))."
        }
    }
    catch {
        if ($_.Exception.Message -like 'Another Arena diagnostic monitor*') { throw }
    }
}

$start = Get-Date
$runName = "ARENA-Live-$($start.ToString('yyyyMMdd-HHmmss'))-$PID"
$runDirectory = Join-Path $OutputRoot $runName
$samplesPath = Join-Path $runDirectory 'process-samples.csv'
$markersPath = Join-Path $runDirectory 'markers.csv'
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null

$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($markersPath, "TimestampLocal,TimestampUtc,ElapsedSeconds,Label,Details`r`n", $utf8)
[IO.File]::WriteAllText($samplesPath, "TimestampLocal,ElapsedSeconds,RadarFound,RadarPid,RadarPath,RadarSha256,ArenaFound,ArenaPid,WorkingSetMB,PrivateMemoryMB,Handles,Threads,Responding,LatestLogBytes,DiagState,DiagAttachGeneration,DiagTargetPid,DiagMapId,DiagRootStage,DiagRootStreak,DiagReason`r`n", $utf8)

function Convert-ToCsvField([AllowNull()][object]$Value) {
    if ($null -eq $Value) { return '' }
    $text = [string]$Value
    if ($text.Contains('"')) { $text = $text.Replace('"', '""') }
    if ($text.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) { return '"' + $text + '"' }
    return $text
}

$session = [ordered]@{
    MonitorProcessId = $PID
    Scenario = $Scenario
    StartTimeLocal = $start.ToString('o')
    StartTimeUtc = $start.ToUniversalTime().ToString('o')
    RunDirectory = $runDirectory
    SamplesPath = $samplesPath
    MarkersPath = $markersPath
}
[IO.File]::WriteAllText($activeSessionPath, ($session | ConvertTo-Json -Depth 4), $utf8)

$appDataRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'eft-dma-radar-arena'
$logRoot = Join-Path $appDataRoot 'logs'
$runtimeDiagPath = Join-Path $appDataRoot 'runtime-diagnostics-latest.json'
$samples = 0
$radarMissing = 0
$arenaMissing = 0
$radarHashPath = ''
$radarHashValue = ''

Write-Host "Arena live diagnostics started: $runName"
Write-Host "Use .\add-diagnostic-marker.ps1 -Label '<event>' during the run."

try {
    while (((Get-Date) - $start).TotalSeconds -lt $DurationSeconds) {
        $now = Get-Date
        $elapsed = ($now - $start).TotalSeconds
        $radar = Get-Process -Name 'arena-dma-radar' -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending | Select-Object -First 1
        $arena = Get-Process -Name 'EscapeFromTarkovArena' -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending | Select-Object -First 1
        if (-not $radar) { ++$radarMissing }
        if (-not $arena) { ++$arenaMissing }

        $latestLog = if (Test-Path -LiteralPath $logRoot -PathType Container) {
            Get-ChildItem -LiteralPath $logRoot -Filter 'arena-*.log' -File |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        } else { $null }

        $radarPath = ''
        if ($radar) {
            try { $radarPath = [string]$radar.Path } catch { $radarPath = '' }
        }
        if (-not [string]::IsNullOrWhiteSpace($radarPath) -and $radarPath -ne $radarHashPath) {
            $radarHashPath = $radarPath
            try { $radarHashValue = (Get-FileHash -LiteralPath $radarPath -Algorithm SHA256).Hash } catch { $radarHashValue = '' }
        }

        $diag = $null
        if (Test-Path -LiteralPath $runtimeDiagPath -PathType Leaf) {
            try { $diag = Get-Content -LiteralPath $runtimeDiagPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $diag = $null }
        }

        $row = @(
            (Convert-ToCsvField $now.ToString('o')),
            (Convert-ToCsvField $elapsed.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture)),
            (Convert-ToCsvField ([bool]$radar)),
            (Convert-ToCsvField $(if ($radar) { $radar.Id } else { '' })),
            (Convert-ToCsvField $radarPath),
            (Convert-ToCsvField $radarHashValue),
            (Convert-ToCsvField ([bool]$arena)),
            (Convert-ToCsvField $(if ($arena) { $arena.Id } else { '' })),
            (Convert-ToCsvField $(if ($radar) { ($radar.WorkingSet64 / 1MB).ToString('0.00', [Globalization.CultureInfo]::InvariantCulture) } else { '' })),
            (Convert-ToCsvField $(if ($radar) { ($radar.PrivateMemorySize64 / 1MB).ToString('0.00', [Globalization.CultureInfo]::InvariantCulture) } else { '' })),
            (Convert-ToCsvField $(if ($radar) { $radar.HandleCount } else { '' })),
            (Convert-ToCsvField $(if ($radar) { $radar.Threads.Count } else { '' })),
            (Convert-ToCsvField $(if ($radar) { $radar.Responding } else { '' })),
            (Convert-ToCsvField $(if ($latestLog) { $latestLog.Length } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Target.State } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Target.AttachGeneration } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Target.ProcessId } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Match.MapId } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Roots.GamePlayerOwnerStage } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Roots.FailureStreak } else { '' })),
            (Convert-ToCsvField $(if ($diag) { $diag.Reason } else { '' }))
        ) -join ','
        [IO.File]::AppendAllText($samplesPath, "$row`r`n", $utf8)
        ++$samples
        Start-Sleep -Seconds $SampleIntervalSeconds
    }
}
finally {
    $summary = @(
        'Escape from Tarkov Arena live diagnostics',
        "Scenario: $Scenario",
        "Started: $($start.ToString('o'))",
        "Finished: $((Get-Date).ToString('o'))",
        "Samples: $samples",
        "Radar-missing samples: $radarMissing",
        "Arena-missing samples: $arenaMissing"
    )
    [IO.File]::WriteAllLines((Join-Path $runDirectory 'monitor-summary.txt'), $summary, $utf8)

    foreach ($name in @('runtime-diagnostics-latest.json', 'runtime-diagnostics-events.jsonl', 'runtime-diagnostics-events.jsonl.previous', 'offset-diagnostics-latest.json', 'il2cpp_offsets.json', 'camera_offsets.json')) {
        $source = Join-Path $appDataRoot $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $runDirectory -Force
        }
    }
    if (Test-Path -LiteralPath $logRoot -PathType Container) {
        $logsOut = Join-Path $runDirectory 'logs'
        New-Item -ItemType Directory -Path $logsOut -Force | Out-Null
        Get-ChildItem -LiteralPath $logRoot -Filter 'arena-*.log' -File |
            Where-Object { $_.LastWriteTime -ge $start.AddMinutes(-1) } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $logsOut -Force }
    }
    if (Test-Path -LiteralPath $activeSessionPath -PathType Leaf) {
        Remove-Item -LiteralPath $activeSessionPath -Force
    }
    Compress-Archive -LiteralPath $runDirectory -DestinationPath "$runDirectory.zip" -CompressionLevel Optimal
}

Write-Host "Arena live diagnostics complete: $runDirectory.zip"
