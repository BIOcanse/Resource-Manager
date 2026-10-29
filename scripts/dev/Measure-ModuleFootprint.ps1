#Requires -Version 7
<#
.SYNOPSIS
Read-only module CPU, thread-entry and image/managed-memory attribution for an x64 Windows target.
.DESCRIPTION
Build MemoryMeasurement/MemoryMeasurement.csproj via JobQueue; pass its DLL as ToolPath or RM_MEMORY_TOOL.
SpeedscopePath accepts an existing Measure-ThreadStacks.ps1 trace; this script never starts a profiler.
No speedscope input means CPU stack attribution is explicitly unavailable; live thread times still run.
OutDir belongs in a lifecycle-registered ledger. Access denied re-runs elevated and waits.
Heap counts stream all objects, with exact integer aggregates (no estimates); passive live walks cannot
guarantee snapshot consistency. Errors or limits discard partial counts. ClrMD and local matching DAC
are optional; no downloads, suspension, dump or target writes. Private image resident bytes are from
QueryWorkingSetEx Shared=0, not a strict dirty measurement. Native private allocation owners unresolved.
Bounds: heap 10M objects/100K types/120s cooperative; modules 100K; threads 8192; committed regions 65536;
speedscope <=256 MiB, 1M frames, 20M events/samples; launcher 0.75 GiB committed process-tree memory.
OwnModuleNames extends ResourceManager prefix + repo project/Zig artifact inventory when needed.
#>
[CmdletBinding(DefaultParameterSetName = 'Name')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Id')][ValidateRange(1, [int]::MaxValue)][int]$ProcessId,
    [Parameter(ParameterSetName = 'Name')][string]$ProcessName = 'ResourceManager',
    [Parameter(Mandatory)][string]$OutDir,
    [Alias('Speedscope')][string]$SpeedscopePath,
    [ValidateRange(1, 3600)][int]$Seconds = 30,
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string[]]$OwnModuleNames = @(),
    [string]$ToolPath = $env:RM_MEMORY_TOOL,
    [string]$ClrMdPath,
    [ValidateRange(0.25, 8)][double]$MemoryLimitGiB = 0.75
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-MemoryMeasurement.ps1')
$targetId = Resolve-MeasurementProcess -ProcessId $ProcessId -ProcessName $ProcessName
$arguments = @('footprint', '--pid', "$targetId", '--out', [IO.Path]::GetFullPath($OutDir),
    '--seconds', "$Seconds", '--repo', [IO.Path]::GetFullPath($RepoRoot))
if ($SpeedscopePath) { $arguments += @('--speedscope', [IO.Path]::GetFullPath($SpeedscopePath)) }
if ($ClrMdPath) { $arguments += @('--clrmd', [IO.Path]::GetFullPath($ClrMdPath)) }
if ($OwnModuleNames.Count -gt 0) { $arguments += @('--own', ($OwnModuleNames -join ';')) }
$result = Invoke-MemoryMeasurement -ToolPath $ToolPath -ToolArguments $arguments -MemoryLimitGiB $MemoryLimitGiB
if ($result -eq 5) { Restart-MeasurementElevated -Script $PSCommandPath -Parameters $PSBoundParameters -ProcessId $targetId }
elseif ($result -ne 0) { throw "Module footprint measurement failed (exit $result); output is incomplete." }
