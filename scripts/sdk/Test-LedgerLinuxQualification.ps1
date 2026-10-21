<# Binds the independently executed Linux fixture to the SDK source being published. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateManifestPath,
    [Parameter(Mandatory)][string]$LinuxFixtureManifestPath,
    [Parameter(Mandatory)][string]$LinuxRuntimeResultPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 32 }
$candidate = Read-Json $CandidateManifestPath
$fixture = Read-Json $LinuxFixtureManifestPath
$runtime = Read-Json $LinuxRuntimeResultPath
$fixtureHash = Get-Sha $LinuxFixtureManifestPath
if ($runtime.status -cne 'passed' -or $runtime.boundary_gates_passed -ne $true -or
    $runtime.inputs_unchanged -ne $true -or $runtime.manifest_sha256 -cne $fixtureHash -or
    $runtime.candidate -cne $fixture.candidate -or $runtime.page_size -ne 4096 -or
    $runtime.platform -notlike 'Linux-*-x86_64-*') { throw 'Linux runtime evidence does not match the supported fixture scope.' }
$sources = @($fixture.sources)
$binaries = @($fixture.binaries)
if ($sources.Count -lt 1 -or $sources.Count -gt 128 -or $binaries.Count -ne 3 -or @($runtime.cases).Count -ne 5) {
    throw 'Unexpected Linux fixture size.'
}
$root = [IO.Path]::GetFullPath((Split-Path -Parent $LinuxFixtureManifestPath))
if ($root -cne [IO.Path]::GetFullPath([string]$fixture.stageRoot)) { throw 'Linux fixture root differs from its manifest.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($row in @($sources) + @($binaries)) {
    $relative = [string]$row.relativePath
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or !$seen.Add($relative)) {
        throw 'Unsafe or duplicate Linux fixture path.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne [long]$row.length -or
        (Get-Sha $path) -cne [string]$row.sha256) { throw "Linux fixture file changed: $relative" }
    $intake = $runtime.intake.PSObject.Properties[$relative]
    if ($null -eq $intake -or [string]$intake.Value -cne [string]$row.sha256) { throw "Linux intake does not bind $relative" }
}
$sdkSources = @($sources | Where-Object { $_.relativePath.StartsWith('sdk-source/src/', [StringComparison]::Ordinal) })
$packageSources = @($candidate.nativeSource.files | Where-Object { $_.path.StartsWith('src/', [StringComparison]::Ordinal) })
if ($sdkSources.Count -ne $packageSources.Count) { throw 'Linux fixture and package SDK source closures differ.' }
foreach ($row in $packageSources) {
    $matches = @($sdkSources | Where-Object { $_.relativePath -ceq ('sdk-source/' + $row.path) })
    if ($matches.Count -ne 1 -or $matches[0].length -ne $row.length -or $matches[0].sha256 -cne $row.sha256) {
        throw "Linux-tested SDK source differs from package: $($row.path)"
    }
}
foreach ($case in $runtime.cases) {
    if ($case.status -cne 'collected' -or $case.empty_group -ne $true -or $case.group_removed -ne $true -or
        @($case.remaining_pids).Count -ne 0) { throw 'Linux process group was not collected.' }
}
$oom = @($runtime.cases | Where-Object { $_.label -ceq 'memory-negative' })
$timeout = @($runtime.cases | Where-Object { $_.label -ceq 'timeout-negative' })
if ($oom.Count -ne 1 -or $timeout.Count -ne 1 -or $oom[0].exit_code -ne -9 -or $oom[0].memory_events.oom_group_kill -lt 1 -or
    $oom[0].limits.'memory.max' -cne '33554432' -or $timeout[0].timed_out -ne $true -or $timeout[0].max_sampled_processes -lt 2) {
    throw 'Linux hard-memory or whole-group timeout negative gate did not pass.'
}
$resultRoot = Split-Path -Parent $LinuxRuntimeResultPath
$logs = @()
foreach ($binary in $binaries) {
    $label = [string]$binary.relativePath
    $cases = @($runtime.cases | Where-Object { $_.label -ceq $label })
    if ($cases.Count -ne 1) { throw "Linux program result missing: $label" }
    $case = $cases[0]
    if ($case.exit_code -ne 0 -or $case.timed_out -ne $false -or $case.memory_events.oom -ne 0 -or
        $case.memory_events.oom_kill -ne 0 -or $case.limits.'memory.max' -cne '134217728' -or
        $case.limits.'memory.swap.max' -cne '0' -or $case.limits.'memory.oom.group' -cne '1' -or
        $case.limits.'pids.max' -cne '16' -or $case.limits.'cpu.max' -cne '100000 100000' -or $case.deadline_seconds -ne 30) {
        throw "Linux program failed its bounded execution contract: $label"
    }
    $logPath = Join-Path $resultRoot ($label + '.stderr.log')
    $log = Get-Content -LiteralPath $logPath -Raw
    if ($label -ceq 'linux-resource-tests') {
        foreach ($expected in @('25 passed; 1 skipped; 0 failed.',
            'fixed unload keeps registration and update changes the next actual allocation...OK',
            'dynamic allocation requires one explicit value, including zero...OK',
            'Linux payload pages are resident, zeroed and actually unmapped on release...OK')) {
            if (!$log.Contains($expected)) { throw "Linux test output missing: $expected" }
        }
    } else {
        if ($label -notmatch '^linux-native-u(128|256)$') { throw 'Unexpected Linux Native fixture.' }
        $width = $Matches[1]
        foreach ($expected in @("ledger Native lifecycle: pass (UID$width)", "ledger public Tick/loading/discard: pass (UID$width)")) {
            if (!$log.Contains($expected)) { throw "Linux Native output missing: $expected" }
        }
    }
    $logs += [pscustomobject]@{path=[IO.Path]::GetFullPath($logPath);sha256=Get-Sha $logPath}
}
[pscustomobject]@{
    target = 'linux-x64'
    binding = 'static Zig LedgerBinding through public C ABI'
    platform = $runtime.platform
    pageSize = $runtime.page_size
    uidWidthBits = @(128,256)
    scope = 'Small serial WSL2 fixture only; excludes Linux C#, Android, product migration and full-capacity performance.'
    matchedSdkSourceCount = $sdkSources.Count
    fixtureManifest = [pscustomobject]@{path=[IO.Path]::GetFullPath($LinuxFixtureManifestPath);sha256=$fixtureHash}
    runtimeResult = [pscustomobject]@{path=[IO.Path]::GetFullPath($LinuxRuntimeResultPath);sha256=Get-Sha $LinuxRuntimeResultPath}
    logs = $logs
}
