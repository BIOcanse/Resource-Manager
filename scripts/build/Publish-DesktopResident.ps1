#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Zig = 'zig'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repo 'scripts/dev/MachineRoots.psm1') -DisableNameChecking
Assert-RmRunningWorkspace -Path $WorkRoot
$work = [IO.Path]::GetFullPath($WorkRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($work.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Desktop publication output must be inside its running WorkRoot.'
}
$zigPath = (Get-Command $Zig -ErrorAction Stop).Source
if (-not $env:JOBQUEUE_JOB_ID) {
    $queue = Join-Path (Get-MachineRoot -Name ToolsRoot) 'JobQueue/JobQueue.ps1'
    & $queue -Action Run -Label 'Desktop ReleaseSafe publication' -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-WorkRoot', $work, '-OutputDirectory', $output, '-Zig', $zigPath) -WorkingDirectory $repo
    if ($LASTEXITCODE -ne 0) { throw "Desktop ReleaseSafe publication failed: $LASTEXITCODE" }
    return
}
& (Join-Path $PSScriptRoot 'Generate-DesktopPoints.ps1') -WorkRoot $work
& (Join-Path $PSScriptRoot 'Generate-DesktopLedger.ps1') -WorkRoot (Join-Path $work 'desktop-ledger')
& (Join-Path $PSScriptRoot 'Generate-DesktopText.ps1')
& (Join-Path $PSScriptRoot 'Generate-DesktopFrontendDefaults.ps1')
& (Join-Path $PSScriptRoot 'Generate-DesktopLayout.ps1')
$arguments = @('build', '-Doptimize=ReleaseSafe', '-Dstate-names=false',
    "-Dpoints-root=$work/desktop-points", "-Dledger-root=$work/desktop-ledger",
    '--cache-dir', (Join-Path $work 'zig-cache'),
    '--prefix', $output, '-j1')
$previousGlobalCache = $env:ZIG_GLOBAL_CACHE_DIR
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $work 'zig-global-cache'
Push-Location (Join-Path $repo 'src/UI/Desktop')
try { & $zigPath @arguments; if ($LASTEXITCODE -ne 0) { throw "Desktop ReleaseSafe publication failed: $LASTEXITCODE" } }
finally {
    Pop-Location
    $env:ZIG_GLOBAL_CACHE_DIR = $previousGlobalCache
}
