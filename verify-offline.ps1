[CmdletBinding()]
param(
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) {
    $dotnetCommand.Source
} else {
    Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
}

if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw ".NET SDK not found. Expected 'dotnet' in PATH or '$dotnetExe'."
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        Invoke-Checked $dotnetExe @('restore', '.\eft-dma-radar-arena.sln')
    }

    Invoke-Checked $dotnetExe @(
        'build', '.\eft-dma-radar-arena.sln',
        '-c', 'Release', '--no-restore'
    )
    Invoke-Checked $dotnetExe @(
        'run', '--project', '.\tests\Arena.OffsetContracts.Tests\Arena.OffsetContracts.Tests.csproj',
        '-c', 'Release', '--no-build'
    )

    $scripts = @(
        'collect-diagnostics.ps1',
        'monitor-live-diagnostics.ps1',
        'add-diagnostic-marker.ps1',
        'verify-offline.ps1'
    )
    foreach ($script in $scripts) {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $repoRoot $script),
            [ref]$tokens,
            [ref]$errors) | Out-Null
        if ($errors.Count -gt 0) {
            throw "PowerShell parser failed for $script`: $($errors[0].Message)"
        }
        Write-Host "PASS: PowerShell parser $script"
    }

    $releaseExe = Join-Path $repoRoot 'src-arena\bin\Release\net10.0-windows7.0\arena-dma-radar.exe'
    $releaseDll = Join-Path $repoRoot 'src-arena\bin\Release\net10.0-windows7.0\arena-dma-radar.dll'
    if (-not (Test-Path -LiteralPath $releaseExe)) {
        throw "Release executable not found: $releaseExe"
    }
    if (-not (Test-Path -LiteralPath $releaseDll)) {
        throw "Release assembly not found: $releaseDll"
    }

    $exeHash = Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256
    $dllHash = Get-FileHash -LiteralPath $releaseDll -Algorithm SHA256
    Write-Host "PASS: Release executable $releaseExe"
    Write-Host "EXE SHA256: $($exeHash.Hash)"
    Write-Host "DLL SHA256: $($dllHash.Hash)"
}
finally {
    Pop-Location
}
