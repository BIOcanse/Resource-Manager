#Requires -Version 7
<#
.SYNOPSIS
Read-only x64 Windows process memory slice comparison. Requires a prebuilt tool-local helper.
.DESCRIPTION
Build MemoryMeasurement/MemoryMeasurement.csproj through JobQueue once; supply the resulting DLL via
-ToolPath (or RM_MEMORY_TOOL). No product package is added. OutDir must be inside a lifecycle-registered
measurement ledger. Access denied re-runs elevated and waits, preserving all parameters.

Bound: 262144 compact sort records (~6 MiB), <=128 merge heads, 1 MiB read buffer, two 64 KiB
comparison buffers, <=65536 regions, <=100 retained groups / 262144 region references. Nonzero index
limit 33554432 slices; <=128 GiB read, 30 minutes, otherwise fail explicitly. Temporary index files use
DeleteOnClose handles; no full memory content is spooled. ClrMD has separate heap metadata limits.
The launcher enforces 0.75 GiB committed memory across its process tree by default. Index storage can
reach 2.25 GiB (sorted runs plus two collision spools), independent of slice length; exhaustion fails explicitly.
SeparateZero defaults on; -SeparateZero:$false includes zero slices in duplicate comparisons.
GC ranges are optional, passive and cache/local-DAC only. Live memory is not an atomic snapshot.
#>
[CmdletBinding(DefaultParameterSetName = 'Name')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Id')][ValidateRange(1, [int]::MaxValue)][int]$ProcessId,
    [Parameter(ParameterSetName = 'Name')][string]$ProcessName = 'ResourceManager',
    [Parameter(Mandatory)][string]$OutDir,
    [ValidateRange(64, 65536)][ValidateScript({ ($_ -band ($_ - 1)) -eq 0 })][int]$SliceBytes = 4096,
    [switch]$SeparateZero = $true,
    [ValidateRange(1, 100)][int]$Top = 30,
    [ValidateRange(0, 65536)][int]$SampleBytes = 96,
    [string]$ToolPath = $env:RM_MEMORY_TOOL,
    [string]$ClrMdPath,
    [ValidateRange(0.25, 8)][double]$MemoryLimitGiB = 0.75
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-MemoryMeasurement.ps1')
$targetId = Resolve-MeasurementProcess -ProcessId $ProcessId -ProcessName $ProcessName
$arguments = @('duplicates', '--pid', "$targetId", '--out', [IO.Path]::GetFullPath($OutDir),
    '--slice', "$SliceBytes", '--separate-zero', "$([bool]$SeparateZero)", '--top', "$Top", '--sample', "$SampleBytes")
if ($ClrMdPath) { $arguments += @('--clrmd', [IO.Path]::GetFullPath($ClrMdPath)) }
$result = Invoke-MemoryMeasurement -ToolPath $ToolPath -ToolArguments $arguments -MemoryLimitGiB $MemoryLimitGiB
if ($result -eq 5) { Restart-MeasurementElevated -Script $PSCommandPath -Parameters $PSBoundParameters -ProcessId $targetId }
elseif ($result -ne 0) { throw "Memory duplicate measurement failed (exit $result); output is incomplete." }
