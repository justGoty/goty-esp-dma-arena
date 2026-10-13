[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Label,

    [string]$Details = '',
    [string]$OutputRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot 'diagnostics'
}

$activeSessionPath = Join-Path ([System.IO.Path]::GetFullPath($OutputRoot)) 'active-session.json'
if (-not (Test-Path -LiteralPath $activeSessionPath -PathType Leaf)) {
    throw "No active Arena diagnostic session. Start monitor-live-diagnostics.ps1 first."
}

$session = Get-Content -LiteralPath $activeSessionPath -Raw -Encoding UTF8 | ConvertFrom-Json
$monitor = Get-Process -Id ([int]$session.MonitorProcessId) -ErrorAction SilentlyContinue
if (-not $monitor) {
    throw "The Arena diagnostic session is stale (monitor PID $($session.MonitorProcessId) is not running)."
}

function Convert-ToCsvField([AllowNull()][object]$Value) {
    if ($null -eq $Value) { return '' }
    $text = [string]$Value
    if ($text.Contains('"')) { $text = $text.Replace('"', '""') }
    if ($text.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) { return '"' + $text + '"' }
    return $text
}

$now = Get-Date
$startUtc = [DateTimeOffset]::Parse([string]$session.StartTimeUtc)
$elapsed = ([DateTimeOffset]::UtcNow - $startUtc).TotalSeconds.ToString('0.000', [Globalization.CultureInfo]::InvariantCulture)
$row = @(
    (Convert-ToCsvField $now.ToString('o')),
    (Convert-ToCsvField $now.ToUniversalTime().ToString('o')),
    $elapsed,
    (Convert-ToCsvField $Label),
    (Convert-ToCsvField $Details)
) -join ','

[System.IO.File]::AppendAllText([string]$session.MarkersPath, "$row`r`n", [Text.UTF8Encoding]::new($false))
Write-Host "Arena marker added at ${elapsed}s: $Label"
