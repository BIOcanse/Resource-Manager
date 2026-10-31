#requires -Version 7.0
<#
.SYNOPSIS
Runs the unresolved runtime-read decision gate through JobQueue.
.DESCRIPTION
First build the backend normally in the same configuration, with its real Ledger
work root. This preserves usable dependency outputs even when strict analysis of
ReadWriteCore fails, so Core/native and Shared are still checked independently.
No analyzer is disabled and no diagnostic is suppressed. RuntimeReadStrict is a
compiler-visible property: switching from a normal build reruns compilation.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [Parameter(Mandatory)][string]$LedgerWorkRoot,
    # Debug Core includes the Zig/LLVM native build; the measured 4 GiB ceiling
    # exhausted LLVM before analyzer execution. The process tree remains bounded.
    [ValidateRange(1, 8)][int]$MemoryLimitGiB = 8,
    [string]$ReportDirectory = (Join-Path $PSScriptRoot '../../.local/reports'),
    # Defaults to <ToolsRoot>\JobQueue\JobQueue.ps1 (scripts/dev/MachineRoots.psm1).
    [string]$Queue
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if (-not $Queue) {
        Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
        $Queue = Join-Path (Get-MachineRoot -Name ToolsRoot) 'JobQueue\JobQueue.ps1'
    }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $reports = (Resolve-Path -LiteralPath $ReportDirectory).Path
    $ledger = (Resolve-Path -LiteralPath $LedgerWorkRoot).Path
    if (-not (Test-Path -LiteralPath $reports -PathType Container)) { throw 'ReportDirectory must be an existing directory.' }
    if (-not (Test-Path -LiteralPath $ledger -PathType Container)) { throw 'LedgerWorkRoot must be an existing registered work root.' }
    if (-not (Test-Path -LiteralPath $Queue -PathType Leaf)) { throw "Build queue is unavailable: $Queue" }
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $projects = [ordered]@{
        Shared = 'src/Shared/ResourceManager.Shared.csproj'
        ReadWriteCore = 'src/ReadWriteCore/ResourceManager.ReadWriteCore.csproj'
        Core = 'src/Core/ResourceManager.App.csproj'
    }
    $results = [Collections.Generic.List[object]]::new()
    $findings = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($project in $projects.GetEnumerator()) {
        $queueLog = Join-Path $reports ("runtime-read-strict-$($project.Key).queue.log")
        $arguments = @('build', $project.Value, '-c', $Configuration, '--no-restore', '--disable-build-servers', '-m:1',
            '-p:BuildInParallel=false', '-p:BuildProjectReferences=false', '-p:UseSharedCompilation=false',
            '-p:RuntimeReadStrict=true', "-p:ResourceManagerLedgerWorkRoot=$ledger", '-v:minimal')
        Write-Host "RuntimeRead strict: $($project.Key) ($Configuration)"
        & $Queue -Action Run -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $repository `
            -MemoryLimitGiB $MemoryLimitGiB -TimeoutMinutes 120 -Label "runtime-read strict: $($project.Key)" *> $queueLog
        $exitCode = $LASTEXITCODE
        $receipt = Get-Content -LiteralPath $queueLog -Tail 1 | ConvertFrom-Json
        if (-not $receipt.PSObject.Properties['log'] -or -not (Test-Path -LiteralPath $receipt.log)) {
            throw "Missing queue execution log; see $queueLog"
        }
        $log = Join-Path $reports ("runtime-read-strict-$($project.Key).log")
        Copy-Item -LiteralPath $receipt.log -Destination $log
        if ($receipt.PSObject.Properties['errorLog'] -and (Test-Path -LiteralPath $receipt.errorLog)) {
            $stderr = Get-Content -LiteralPath $receipt.errorLog -Raw
            if ($stderr) { Add-Content -LiteralPath $log -Value $stderr -Encoding utf8 }
        }
        foreach ($line in Get-Content -LiteralPath $log) {
            Write-Host $line
            if ($line -match '^(?<path>.+?)(?:\((?<line>\d+),(?<column>\d+)\))?\s*:\s*(?<severity>warning|error) (?<id>RMR\d{3}): (?<message>.*?)(?: \[[^\]]+\])?$') {
                if ($Matches.id -eq 'RMR000') { continue }
                $lineNumber = if ($Matches.ContainsKey('line')) { $Matches['line'] } else { 0 }
                $columnNumber = if ($Matches.ContainsKey('column')) { $Matches['column'] } else { 0 }
                $key = "$($project.Key)|$($Matches.path)|$lineNumber|$columnNumber|$($Matches.id)|$($Matches.message)"
                if ($seen.Add($key)) {
                    $findings.Add([pscustomobject]@{
                        project = $project.Key; path = $Matches.path.Trim(); line = $lineNumber; column = $columnNumber
                        id = $Matches.id; severity = $Matches.severity; message = $Matches.message
                    })
                }
            }
        }
        $results.Add([pscustomobject]@{ project = $project.Key; exitCode = $exitCode; log = $log })
    }
    $json = Join-Path $reports 'runtime-read-strict-findings.json'
    ConvertTo-Json -InputObject @($findings.ToArray()) -Depth 6 | Set-Content -LiteralPath $json -Encoding utf8
    $findings.ToArray() | Export-Csv -LiteralPath (Join-Path $reports 'runtime-read-strict-findings.tsv') -Delimiter "`t" -NoTypeInformation -Encoding utf8
    $unresolved = @($findings | Where-Object id -in @('RMR001', 'RMR003', 'RMR103')).Count
    $proven = @($findings | Where-Object id -in @('RMR005', 'RMR007')).Count
    $unmarked = @($findings | Where-Object id -in @('RMR006', 'RMR101')).Count
    Write-Host "RuntimeRead strict totals: unresolved=$unresolved; proven-repeat=$proven; unmarked-primary=$unmarked; all-findings=$($findings.Count)"
    foreach ($group in ($findings | Group-Object id | Sort-Object Name)) { Write-Host "$($group.Name)=$($group.Count)" }
    Write-Host "Full finding list: $json"
    $results.ToArray() | ConvertTo-Json -Depth 4 | Write-Output
    if (@($results | Where-Object exitCode -ne 0).Count -gt 0 -or $findings.Count -gt 0) { exit 1 }
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 2
}
