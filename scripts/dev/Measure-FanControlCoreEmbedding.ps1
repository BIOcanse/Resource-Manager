[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProbePath,
    [Parameter(Mandatory)][string]$LegacyProbePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1,30)][int]$Samples = 5
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProbePath = (Resolve-Path -LiteralPath $ProbePath).Path
$LegacyProbePath = (Resolve-Path -LiteralPath $LegacyProbePath).Path
$measurements = @()
for ($sample = 1; $sample -le $Samples; $sample++) {
    $before = & $ProbePath --measure-stdio $LegacyProbePath
    if ($LASTEXITCODE -ne 0) { throw "Legacy stdio measurement failed: $LASTEXITCODE" }
    $after = & $ProbePath --measure-embedded
    if ($LASTEXITCODE -ne 0) { throw "Embedded measurement failed: $LASTEXITCODE" }
    $measurements += [pscustomobject]@{ sample=$sample; before=($before | ConvertFrom-Json); after=($after | ConvertFrom-Json) }
}
function Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle-1] + $sorted[$middle]) / 2
}
$removed = Median @($measurements | ForEach-Object { $_.before.childPrivateBytes })
$added = Median @($measurements | ForEach-Object { $_.after.addedPrivateBytes })
$summary = [ordered]@{
    schema='rm/fan-control-core-embedding-measurement/v1'
    scope='Windows x64 .NET 10, fresh fixture processes, same fake backend, no hardware IO; private commit, forced GC at memory boundaries'
    baselineSource='FanControlCore cf0dad2'
    probePath=$ProbePath
    legacyProbePath=$LegacyProbePath
    samples=$measurements
    median=[ordered]@{
        removedProcessPrivateBytes=$removed
        addedLibraryPrivateBytes=$added
        savedPrivateBytes=$removed-$added
        savedAgainstUser27MiBBytes=(27*1MB)-$added
        beforeFirstDescribeMs=(Median @($measurements | ForEach-Object { $_.before.firstDescribeMs }))
        afterFirstDescribeMs=(Median @($measurements | ForEach-Object { $_.after.firstDescribeMs }))
    }
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$summary.median | ConvertTo-Json
