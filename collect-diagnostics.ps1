[CmdletBinding()]
param(
    [string]$RuntimePath = '',
    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RuntimePath)) {
    $RuntimePath = Join-Path $scriptRoot 'src-arena\bin\Release\net10.0-windows7.0'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot 'diagnostics'
}

$RuntimePath = [System.IO.Path]::GetFullPath($RuntimePath)
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$appDataRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'eft-dma-radar-arena'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = Join-Path $OutputRoot "ARENA-Diagnostics-$timestamp-$PID"
$filesDirectory = Join-Path $runDirectory 'files'
$logsDirectory = Join-Path $runDirectory 'logs'
$zipPath = "$runDirectory.zip"

New-Item -ItemType Directory -Path $filesDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $logsDirectory -Force | Out-Null
$report = [Collections.Generic.List[string]]::new()

function Add-Report([string]$Text = '') { $script:report.Add($Text) | Out-Null }
function Copy-DiagnosticFile([string]$Source, [string]$DestinationDirectory) {
    if (Test-Path -LiteralPath $Source -PathType Leaf) {
        Copy-Item -LiteralPath $Source -Destination $DestinationDirectory -Force
        return $true
    }
    return $false
}

Add-Report 'Escape from Tarkov Arena DMA radar diagnostics'
Add-Report "Captured: $((Get-Date).ToString('o'))"
Add-Report "Runtime: $RuntimePath"
Add-Report "AppData diagnostics: $appDataRoot"
Add-Report "PowerShell: $($PSVersionTable.PSVersion)"
Add-Report ".NET SDK: $((& dotnet --version 2>$null) -join '')"

$receiver = Get-Process -Name 'arena-dma-radar' -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending | Select-Object -First 1
$arena = Get-Process -Name 'EscapeFromTarkovArena' -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending | Select-Object -First 1
Add-Report "Radar process: $(if ($receiver) { 'running pid=' + $receiver.Id } else { 'not running' })"
Add-Report "Arena process: $(if ($arena) { 'running pid=' + $arena.Id } else { 'not running' })"

$runningRadarPath = ''
if ($receiver) {
    try { $runningRadarPath = [string]$receiver.Path } catch { $runningRadarPath = '' }
}
if (-not [string]::IsNullOrWhiteSpace($runningRadarPath) -and (Test-Path -LiteralPath $runningRadarPath -PathType Leaf)) {
    $runningHash = Get-FileHash -LiteralPath $runningRadarPath -Algorithm SHA256
    Add-Report "Running radar path: $runningRadarPath"
    Add-Report "Running radar SHA256: $($runningHash.Hash)"
    Add-Report "Running radar under requested runtime: $($runningRadarPath.StartsWith($RuntimePath, [StringComparison]::OrdinalIgnoreCase))"
}
else {
    Add-Report 'Running radar path/hash: unavailable'
}

foreach ($binaryName in @('arena-dma-radar.exe', 'arena-dma-radar.dll')) {
    $binaryPath = Join-Path $RuntimePath $binaryName
    if (Test-Path -LiteralPath $binaryPath -PathType Leaf) {
        $binary = Get-Item -LiteralPath $binaryPath
        $hash = Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256
        Add-Report "Binary: $($binary.FullName)"
        Add-Report "$binaryName bytes: $($binary.Length)"
        Add-Report "$binaryName SHA256: $($hash.Hash)"
    }
    else {
        Add-Report "${binaryName}: missing"
    }
}

foreach ($name in @('runtime-diagnostics-latest.json', 'runtime-diagnostics-events.jsonl', 'runtime-diagnostics-events.jsonl.previous', 'offset-diagnostics-latest.json', 'il2cpp_offsets.json', 'camera_offsets.json')) {
    $source = Join-Path $appDataRoot $name
    $copied = Copy-DiagnosticFile $source $filesDirectory
    Add-Report "${name}: $(if ($copied) { 'copied' } else { 'missing' })"
}

$logRoot = Join-Path $appDataRoot 'logs'
if (Test-Path -LiteralPath $logRoot -PathType Container) {
    $logs = Get-ChildItem -LiteralPath $logRoot -Filter 'arena-*.log' -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 5
    foreach ($log in $logs) { Copy-Item -LiteralPath $log.FullName -Destination $logsDirectory -Force }
    Add-Report "Logs copied: $($logs.Count)"
}
else {
    Add-Report 'Logs copied: 0 (log directory missing)'
}

[System.IO.File]::WriteAllLines((Join-Path $runDirectory 'report.txt'), $report, [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $runDirectory -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Arena diagnostics: $runDirectory"
Write-Host "Arena diagnostics archive: $zipPath"
