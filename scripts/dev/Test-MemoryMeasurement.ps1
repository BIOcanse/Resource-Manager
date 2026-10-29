#Requires -Version 7
<# Run only through JobQueue -Action Run. Builds its small helper, starts/owns every target and stops it.
Outputs stay in the supplied already-registered Ledger; no installed RM process is inspected. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Ledger,
    [Parameter(Mandatory)][string]$ToolPath,
    [string]$SpeedscopePath,
    [switch]$SkipBuild,
    [switch]$ClassificationOnly,
    [ValidateRange(1, 500)][int]$Megabytes = 500
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $env:JOBQUEUE_JOB_ID) { throw 'Run this test through JobQueue.ps1 -Action Run.' }
$Ledger = [IO.Path]::GetFullPath($Ledger)
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
$dotnet = (Get-Command dotnet -CommandType Application).Source
$pwsh = (Get-Process -Id $PID).Path
$testClock = [Diagnostics.Stopwatch]::StartNew()
$checks = [Collections.Generic.List[string]]::new()
$targets = [Collections.Generic.List[Diagnostics.Process]]::new()
function Assert-True([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $checks.Add($Name)
}
function Stop-Owned([Diagnostics.Process]$Target) {
    if (-not $Target.HasExited) {
        try { $Target.StandardInput.WriteLine('exit'); $Target.StandardInput.Flush() } catch { }
        if (-not $Target.WaitForExit(5000)) { $Target.Kill(); $Target.WaitForExit() }
    }
}
function Start-Owned([string]$Exe, [string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info); $targets.Add($process)
    $read = $process.StandardOutput.ReadLineAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not $read.IsCompleted -and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        if ($env:JOBQUEUE_STOP_FILE -and (Test-Path -LiteralPath $env:JOBQUEUE_STOP_FILE)) { throw 'JobQueue stop requested.' }
        Start-Sleep -Milliseconds 100
    }
    if (-not $read.IsCompleted -or $read.Result -ne 'ready') { throw "Target failed to become ready; pid=$($process.Id)" }
    return $process
}
function Measure-Owned([Diagnostics.Process]$Target, [string]$Name, [switch]$IncludeZero, [int]$Seconds = 1) {
    $duplicates = Join-Path $Ledger "$Name-duplicates"
    $footprint = Join-Path $Ledger "$Name-footprint"
    & (Join-Path $PSScriptRoot 'Measure-MemoryDuplicates.ps1') -ProcessId $Target.Id -OutDir $duplicates -ToolPath $ToolPath -Top 100 -SeparateZero:(-not $IncludeZero) *> (Join-Path $Ledger "$Name-duplicates.log")
    $parameters = @{ ProcessId=$Target.Id; OutDir=$footprint; ToolPath=$ToolPath; Seconds=$Seconds }
    if ($SpeedscopePath) { $parameters['SpeedscopePath'] = $SpeedscopePath }
    & (Join-Path $PSScriptRoot 'Measure-ModuleFootprint.ps1') @parameters *> (Join-Path $Ledger "$Name-footprint.log")
    return @{
        Duplicates = Get-Content -Raw -LiteralPath (Join-Path $duplicates 'duplicates.json') | ConvertFrom-Json
        Footprint = Get-Content -Raw -LiteralPath (Join-Path $footprint 'footprint.json') | ConvertFrom-Json
    }
}
try {
    foreach ($scriptName in @('Measure-MemoryDuplicates.ps1', 'Measure-ModuleFootprint.ps1', 'Invoke-MemoryMeasurement.ps1', 'Test-MemoryMeasurement.ps1')) {
        $parseTokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $scriptName), [ref]$parseTokens, [ref]$parseErrors)
        Assert-True ($parseErrors.Count -eq 0) "PowerShell 7 syntax: $scriptName"
    }
    if (-not $SkipBuild) {
        $project = Join-Path $PSScriptRoot 'MemoryMeasurement/MemoryMeasurement.csproj'
        $sources = @(Get-ChildItem -LiteralPath (Split-Path -Parent $project) -File) +
            @(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'Measure-MemoryDuplicates.ps1'), (Join-Path $PSScriptRoot 'Measure-ModuleFootprint.ps1'), (Join-Path $PSScriptRoot 'Invoke-MemoryMeasurement.ps1'), $PSCommandPath)
        $sources | ForEach-Object {
            [pscustomobject]@{ Path=$_.FullName; Bytes=$_.Length; Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $Ledger 'inputs.json') -Encoding utf8
        & $dotnet build $project -c Release --disable-build-servers -m:1 "-p:BaseIntermediateOutputPath=$(Join-Path $Ledger 'obj')/" -o (Split-Path -Parent $ToolPath) *> (Join-Path $Ledger 'integration-build.log')
        if ($LASTEXITCODE -ne 0) { throw "Helper build failed: $LASTEXITCODE; see integration-build.log" }
        $checks.Add('tool-local helper built in this JobQueue job')
    }
    & $dotnet $ToolPath selftest (Join-Path $Ledger 'cpu-tests')
    if ($LASTEXITCODE -ne 0) { throw "CPU aggregation self-test failed: $LASTEXITCODE" }
    $checks.Add('CPU sampled/evented aggregation; forced hash collision and changed-content contracts')
    if ($ClassificationOnly) {
        function Read-Classification([Diagnostics.Process]$Target, [string]$Name) {
            $parameters = @{ ProcessId=$Target.Id; OutDir=(Join-Path $Ledger "$Name/footprint"); ToolPath=$ToolPath; Seconds=1 }
            if ($SpeedscopePath) { $parameters['SpeedscopePath'] = $SpeedscopePath }
            & (Join-Path $PSScriptRoot 'Measure-ModuleFootprint.ps1') @parameters *> (Join-Path $Ledger "$Name.log")
            $report = Get-Content -Raw -LiteralPath (Join-Path $parameters.OutDir 'footprint.json') | ConvertFrom-Json
            $runtimeNames = @('coreclr','clrjit','hostfxr','hostpolicy','dotnet','System.Private.CoreLib','System.Private.Xml','System.Management')
            $runtime = @($report.Modules | Where-Object Module -in $runtimeNames)
            Assert-True ($runtime.Count -ge 3) "$Name runtime modules discovered"
            Assert-True (@($runtime | Where-Object Part -ne 'runtime-windows').Count -eq 0) "$Name bundled runtime/host DLLs classified as runtime"
            Assert-True $report.Managed.Complete "$Name exact managed walk completes"
            return $report
        }
        $fixture = Start-Owned $dotnet @($ToolPath, 'fixture', (Join-Path $Ledger 'classification-fixture.json'), '1')
        $fixtureFootprint = Read-Classification $fixture 'classification-fixture'
        $probe = @($fixtureFootprint.Managed.TypeCounts | Where-Object Name -eq 'MemoryMeasurement.ProbeObject')
        Assert-True ($probe.Count -eq 1 -and $probe[0].Count -eq 12345 -and $probe[0].Bytes -eq 395040 -and $probe[0].Part -eq 'own') 'repo probe objects retain exact counts and own attribution'
        Stop-Owned $fixture
        $idle = Start-Owned $pwsh @('-NoProfile','-Command','[Console]::WriteLine("ready"); $null = [Console]::ReadLine()')
        Start-Sleep -Seconds 5
        $pwshFootprint = Read-Classification $idle 'classification-pwsh'
        foreach ($moduleName in @('System.Management.Automation','Newtonsoft.Json','Microsoft.CodeAnalysis')) {
            $modules = @($pwshFootprint.Modules | Where-Object Module -eq $moduleName)
            Assert-True ($modules.Count -gt 0 -and @($modules | Where-Object Part -ne 'external').Count -eq 0) "$moduleName remains an external application/dependency"
        }
        $windows = @($pwshFootprint.Modules | Where-Object { $_.Module -in @('MPCLIENT','MpOav') })
        if ($windows.Count -gt 0) { Assert-True (@($windows | Where-Object Part -ne 'runtime-windows').Count -eq 0) 'Windows product DLLs outside Windows directory classified as Windows' }
        Stop-Owned $idle
        @{ Status='passed'; JobQueueId=$env:JOBQUEUE_JOB_ID; Checks=$checks; ElapsedSeconds=$testClock.Elapsed.TotalSeconds;
            FixtureFootprintSeconds=$fixtureFootprint.ElapsedSeconds; PwshFootprintSeconds=$pwshFootprint.ElapsedSeconds;
            ExternalModules=@($pwshFootprint.Modules | Where-Object Part -eq 'external' | Select-Object -ExpandProperty Module -Unique) } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Ledger 'classification-result.json') -Encoding utf8
        Write-Host "PASS: $($checks.Count) targeted classification checks; JobQueue $env:JOBQUEUE_JOB_ID"
        return
    }
    $fixturePath = Join-Path $Ledger 'fixture.json'
    $fixture = Start-Owned $dotnet @($ToolPath, 'fixture', $fixturePath, "$Megabytes")
    $expected = Get-Content -Raw -LiteralPath $fixturePath | ConvertFrom-Json
    $invalidSliceRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Measure-MemoryDuplicates.ps1') -ProcessId $fixture.Id -OutDir (Join-Path $Ledger 'invalid-slice') -ToolPath $ToolPath -SliceBytes 96
    } catch [Management.Automation.ParameterBindingException] { $invalidSliceRejected = $true }
    Assert-True $invalidSliceRejected 'non-power-of-two slice rejected before measurement'
    $actual = Measure-Owned $fixture 'fixture'
    $group = @($actual.Duplicates.TopGroups | Where-Object { @($_.Regions | Where-Object { $_.Address -eq $expected.DuplicateAddress }).Count -gt 0 })
    Assert-True ($group.Count -eq 1) 'known native duplicate group found'
    Assert-True ($group[0].SliceCount -eq $expected.DuplicateCopies) 'native duplicate count exactly 257 slices'
    Assert-True ($group[0].DuplicateBytes -eq $expected.RedundantBytes) 'native redundant bytes exactly 1048576'
    Assert-True ($group[0].Hex.StartsWith('524D2D4D454D4F52592D5343414E')) 'content sample contains fixture marker'
    $zero = @($actual.Duplicates.Regions | Where-Object { $_.Address -eq $expected.ZeroAddress })
    Assert-True ($zero.Count -eq 1 -and $zero[0].ObservedZeroBytes -eq $expected.ZeroBytes) 'known zero allocation exactly 77824 bytes'
    Assert-True (@($actual.Duplicates.TopGroups | Where-Object AllZero).Count -eq 0) 'SeparateZero excludes whole-slice zero duplicate groups'
    $protected = @($actual.Duplicates.Regions | Where-Object { $_.Address -ge $expected.ProtectedAddress -and $_.Address -lt ($expected.ProtectedAddress + 8192) })
    Assert-True ($protected.Count -eq 2 -and @($protected | Where-Object Readable).Count -eq 0) 'guard and no-access pages skipped'
    foreach ($class in $actual.Duplicates.Classes) {
        Assert-True ($class.CommittedBytes -eq ($class.UniqueBytes + $class.DuplicateBytes + $class.ZeroBytes + $class.UnreadBytes + $class.ChangedBytes)) "accounting reconciles: $($class.Class) $($class.Module)"
    }
    Assert-True $actual.Footprint.Managed.Complete 'managed heap enumeration completes'
    $objects = @($actual.Footprint.Managed.TypeCounts | Where-Object Name -eq $expected.ObjectType)
    Assert-True ($objects.Count -eq 1 -and $objects[0].Count -eq $expected.ObjectCount) 'managed ProbeObject count exactly 12345'
    Assert-True ($objects[0].Bytes -eq 32 * $expected.ObjectCount) 'managed ProbeObject total exactly 395040 bytes'
    Assert-True ($objects[0].Part -eq 'own') 'assembly built from repo dev project is attributed to own code'
    Assert-True (@($actual.Duplicates.Classes | Where-Object Class -eq 'gc-heap').Count -gt 0) 'GC committed segments marked'
    Assert-True (@($actual.Footprint.Threads | Where-Object { $_.StartAddress -and $_.Module -ne 'unresolved' }).Count -gt 0) 'live thread start address maps to a module'
    Assert-True (@($actual.Footprint.Threads | Where-Object { $null -ne $_.UserMilliseconds -and $null -ne $_.KernelMilliseconds }).Count -gt 0) 'live thread CPU deltas measured'
    Assert-True (@($actual.Footprint.Modules | Where-Object { $_.ImageCommittedBytes -gt 0 }).Count -gt 0) 'committed image memory measured'
    Stop-Owned $fixture

    $idleCode = '[Console]::WriteLine("ready"); $null = [Console]::ReadLine()'
    $idle = Start-Owned $pwsh @('-NoProfile', '-Command', $idleCode)
    Start-Sleep -Seconds 5
    $idleActual = Measure-Owned $idle 'owned-pwsh' -Seconds 30
    Assert-True ($idleActual.Duplicates.ProcessId -eq $idle.Id -and $idleActual.Footprint.ProcessId -eq $idle.Id) 'both scripts run against owned long-lived pwsh'
    Assert-True ($idleActual.Duplicates.Classes.Count -gt 0 -and $idleActual.Footprint.Modules.Count -gt 0) 'owned pwsh memory and module outputs populated'
    Stop-Owned $idle

    # A small second target exercises zero grouping without repeating the 500 MiB scan.
    $zeroFixture = Start-Owned $dotnet @($ToolPath, 'fixture', (Join-Path $Ledger 'zero-fixture.json'), '1')
    & (Join-Path $PSScriptRoot 'Measure-MemoryDuplicates.ps1') -ProcessId $zeroFixture.Id -OutDir (Join-Path $Ledger 'zero-included') -ToolPath $ToolPath -SeparateZero:$false -Top 30 *> (Join-Path $Ledger 'zero-included.log')
    $included = Get-Content -Raw -LiteralPath (Join-Path $Ledger 'zero-included/duplicates.json') | ConvertFrom-Json
    Assert-True (@($included.TopGroups | Where-Object AllZero).Count -gt 0) 'SeparateZero false includes confirmed whole-slice zero duplicate group'
    # 64-byte slices over this small target create multiple 262144-record sort runs.
    & (Join-Path $PSScriptRoot 'Measure-MemoryDuplicates.ps1') -ProcessId $zeroFixture.Id -OutDir (Join-Path $Ledger 'small-slices') -ToolPath $ToolPath -SliceBytes 64 -Top 10 -SampleBytes 0 *> (Join-Path $Ledger 'small-slices.log')
    $small = Get-Content -Raw -LiteralPath (Join-Path $Ledger 'small-slices/duplicates.json') | ConvertFrom-Json
    Assert-True ($small.IndexedSlices -gt 262144) '64-byte scan exercises external multi-run merge'
    Assert-True (@($small.TopGroups | Where-Object { $_.Hex.Length -ne 0 }).Count -eq 0) 'SampleBytes zero omits samples'
    foreach ($class in $small.Classes) {
        Assert-True ($class.CommittedBytes -eq ($class.UniqueBytes + $class.DuplicateBytes + $class.ZeroBytes + $class.UnreadBytes + $class.ChangedBytes)) "small-slice accounting: $($class.Class) $($class.Module)"
    }
    & (Join-Path $PSScriptRoot 'Measure-MemoryDuplicates.ps1') -ProcessId $zeroFixture.Id -OutDir (Join-Path $Ledger 'large-slices') -ToolPath $ToolPath -SliceBytes 65536 -Top 100 -SampleBytes 1 *> (Join-Path $Ledger 'large-slices.log')
    $large = Get-Content -Raw -LiteralPath (Join-Path $Ledger 'large-slices/duplicates.json') | ConvertFrom-Json
    $secondExpected = Get-Content -Raw -LiteralPath (Join-Path $Ledger 'zero-fixture.json') | ConvertFrom-Json
    $largeGroup = @($large.TopGroups | Where-Object { $_.SliceBytes -eq 65536 -and @($_.Regions | Where-Object Address -eq $secondExpected.DuplicateAddress).Count -gt 0 })
    Assert-True ($largeGroup.Count -eq 1 -and $largeGroup[0].SliceCount -eq 16 -and $largeGroup[0].DuplicateBytes -eq 983040) '65536-byte duplicate count and region tail handled exactly'
    Assert-True (@($large.Regions | Where-Object { $_.Address -eq $secondExpected.ZeroAddress -and $_.ObservedZeroBytes -eq 77824 }).Count -eq 1) 'zero bytes include a short region tail'
    Assert-True (@($large.TopGroups | Where-Object { $_.Hex.Length -ne 2 -or $_.Utf16.Length -ne 0 }).Count -eq 0) 'one-byte sample has hex and no incomplete UTF-16 unit'
    & (Join-Path $PSScriptRoot 'Measure-ModuleFootprint.ps1') -ProcessId $zeroFixture.Id -OutDir (Join-Path $Ledger 'without-clrmd') -ToolPath $ToolPath -Seconds 1 -ClrMdPath (Join-Path $Ledger 'absent-clrmd.dll') *> (Join-Path $Ledger 'without-clrmd.log')
    $without = Get-Content -Raw -LiteralPath (Join-Path $Ledger 'without-clrmd/footprint.json') | ConvertFrom-Json
    Assert-True ($without.Managed.Status -eq 'unavailable' -and -not $without.Managed.Complete -and $null -eq $without.Managed.ObjectCount -and $without.Managed.TypeCounts.Count -eq 0) 'absent ClrMD reports unavailable without estimated or partial counts'
    Assert-True ($null -eq $without.Cpu -and $without.Modules.Count -gt 0) 'native footprint still works without managed or speedscope data'
    Stop-Owned $zeroFixture
    $result = [ordered]@{ Status='passed'; JobQueueId=$env:JOBQUEUE_JOB_ID; Checks=$checks; Fixture=$expected;
        ElapsedSeconds=$testClock.Elapsed.TotalSeconds;
        Measurements=@{
            FixtureDuplicates=@{ Seconds=$actual.Duplicates.ElapsedSeconds; PeakWorkingSetBytes=$actual.Duplicates.ToolPeakWorkingSetBytes; PeakCommittedBytes=$actual.Duplicates.ToolPeakCommittedBytes }
            FixtureFootprint=@{ Seconds=$actual.Footprint.ElapsedSeconds; PeakWorkingSetBytes=$actual.Footprint.ToolPeakWorkingSetBytes; PeakCommittedBytes=$actual.Footprint.ToolPeakCommittedBytes }
            PwshDuplicates=@{ Seconds=$idleActual.Duplicates.ElapsedSeconds; PeakWorkingSetBytes=$idleActual.Duplicates.ToolPeakWorkingSetBytes; PeakCommittedBytes=$idleActual.Duplicates.ToolPeakCommittedBytes }
            PwshFootprint=@{ Seconds=$idleActual.Footprint.ElapsedSeconds; PeakWorkingSetBytes=$idleActual.Footprint.ToolPeakWorkingSetBytes; PeakCommittedBytes=$idleActual.Footprint.ToolPeakCommittedBytes }
        } }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Ledger 'test-result.json') -Encoding utf8
    Write-Host "PASS: $($checks.Count) checks; JobQueue $env:JOBQUEUE_JOB_ID; target=$Megabytes MiB; elapsed=$($testClock.Elapsed.TotalSeconds.ToString('F2'))s"
} catch {
    @{ Status='failed'; JobQueueId=$env:JOBQUEUE_JOB_ID; Checks=$checks; Error="$_"; Stack=$_.ScriptStackTrace } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Ledger $(if ($ClassificationOnly) { 'classification-result.json' } else { 'test-result.json' })) -Encoding utf8
    throw
} finally {
    foreach ($target in $targets) { try { Stop-Owned $target } finally { $target.Dispose() } }
}
