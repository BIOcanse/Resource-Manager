#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$ToolsRoot,
    [Parameter(Mandatory)][string]$FreedomPointFeed,
    [string]$ManagedGate = '',
    [string]$ReuseGate = '',
    [string]$ReuseAnalysisGate = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $env:JOBQUEUE_JOB_ID) { throw 'Run this gate through the shared JobQueue.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputRoot)
$restart = if ($env:JOBQUEUE_RESTART_COUNT) { [int]$env:JOBQUEUE_RESTART_COUNT } else { 0 }
if ($restart -gt 0) { $output += "-restart-$restart" }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; prior evidence is immutable.' }
[void][IO.Directory]::CreateDirectory($output)
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$zig = Join-Path ([IO.Path]::GetFullPath($ToolsRoot)) 'Zig/zig-0.17.0/zig.exe'
$env:PATH = @($PSHOME,(Split-Path -Parent $zig),(Split-Path -Parent $dotnet),(Join-Path $env:SystemRoot 'System32'),$env:SystemRoot) -join ';'
$env:DOTNET_CLI_HOME = Join-Path $output 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $output 'packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$feed = [IO.Path]::GetFullPath($FreedomPointFeed)
# The analyzer restores its pinned Microsoft.CodeAnalysis.CSharp from nuget.org. Sources come from an
# explicit config file: as a dotnet build --source the URL reached NuGet as a local path (NU1301).
$restoreConfig = Join-Path $output 'NuGet.Config'
$steps = [Collections.Generic.List[object]]::new()
$passed = $false
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($restoreConfig, "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<configuration>`n  <packageSources>`n    <clear />`n    <add key=`"freedom-point`" value=`"$feed`" />`n    <add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" protocolVersion=`"3`" />`n  </packageSources>`n</configuration>`n", $utf8)
function Write-Text([string]$Path, [string]$Value) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Path))
    [IO.File]::WriteAllText($Path, $Value, $utf8)
}
function Invoke-Step([string]$Name, [string]$Exe, [string[]]$Arguments, [int]$Expected = 0, [string]$Contains = '') {
    if ($env:JOBQUEUE_STOP_FILE -and (Test-Path -LiteralPath $env:JOBQUEUE_STOP_FILE)) { exit 75 }
    $number = $steps.Count + 1
    $stem = Join-Path $output ('{0:D3}-{1}' -f $number, $Name)
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.WorkingDirectory = $repository
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = [DateTime]::UtcNow
    $stopped = $false
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(200)) {
            if ($env:JOBQUEUE_STOP_FILE -and (Test-Path -LiteralPath $env:JOBQUEUE_STOP_FILE)) {
                $process.Kill($true); $process.WaitForExit(); $stopped = $true; break
            }
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        Write-Text "$stem.stdout" $out
        Write-Text "$stem.stderr" $err
        $row = [ordered]@{name=$Name; file=$Exe; arguments=$Arguments; exit=$process.ExitCode; expected=$Expected; stopped=$stopped; startedUtc=$started; finishedUtc=[DateTime]::UtcNow}
        $steps.Add($row)
        Write-Text "$stem.json" ($row | ConvertTo-Json -Depth 6)
        if ($stopped) { exit 75 }
        if ($process.ExitCode -ne $Expected -or ($Contains -and ($out + $err) -notlike "*$Contains*")) {
            throw "$Name failed: exit=$($process.ExitCode) expected=$Expected`n$($out + $err)"
        }
        Write-Host "PASS $number $Name"
    } finally { $process.Dispose() }
}
function Find-Dll([string]$Root, [string]$Name) {
    $files = @(Get-ChildItem -LiteralPath $Root -Filter $Name -Recurse -File | Where-Object FullName -NotMatch '[\\/]ref(int)?[\\/]')
    if($files.Count -ne 1) { throw "Expected one $Name under $Root, found $($files.Count)." }
    return $files[0].FullName
}
function Get-Inputs {
    # Enumerate bounded SDK source roots without relying on Git in the short PATH.
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach($root in @('sdk/csharp','sdk/zig/src','sdk/native/include','scripts/sdk')) {
        $pending.Push((Join-Path $repository $root))
    }
    $paths = [Collections.Generic.List[string]]::new()
    foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json','NuGet.Config')) {
        $path = Join-Path $repository $name
        if(Test-Path -LiteralPath $path -PathType Leaf) { $paths.Add($path) }
    }
    while($pending.Count) {
        $directory = $pending.Pop()
        foreach($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if($entry.PSIsContainer) {
                if($entry.Name -notin @('bin','obj','.git') -and -not($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    $pending.Push($entry.FullName)
                }
            } elseif($entry.Extension -in @('.cs','.csproj','.zig','.h','.ps1','.json')) { $paths.Add($entry.FullName) }
        }
    }
    return @($paths | Sort-Object -Unique | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($repository,$_).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    })
}
$before = Get-Inputs
Write-Text (Join-Path $output 'inputs-before.json') ($before | ConvertTo-Json -Depth 5)
$reused = @()
if($ReuseGate) {
    $previous = Get-Content -LiteralPath (Join-Path $ReuseGate 'result.json') -Raw | ConvertFrom-Json
    $oldInputs = @(Get-Content -LiteralPath (Join-Path $ReuseGate 'inputs-before.json') -Raw | ConvertFrom-Json)
    $changedTestInputs = @('sdk/zig/src/local_resource_manager/owned/lease_analysis_tests.zig','sdk/zig/src/ledger_lease_analysis_tests.zig','scripts/sdk/ledger-native-source-files.json','scripts/sdk/Test-LedgerScopedLeases.ps1')
    $oldMap = @{}
    foreach($row in $oldInputs) { $oldMap[([string]$row.path).Replace('\','/')] = [string]$row.sha256 }
    $newMap = @{}
    foreach($row in $before) { $newMap[([string]$row.path).Replace('\','/')] = [string]$row.sha256 }
    foreach($path in @(@($oldMap.Keys) + @($newMap.Keys) | Sort-Object -Unique)) {
        if($path -notin $changedTestInputs -and $oldMap[$path] -cne $newMap[$path]) { throw "Cannot reuse changed production/test input: $path" }
    }
    $reused = @('analyzer-and-erasure','reject-flag','reject-capacity-zero','reject-capacity-over','u128-off-zig-regression','u128-off-real-consumer')
    foreach($name in $reused) {
        $row = @($previous.steps | Where-Object name -EQ $name)
        if($row.Count -ne 1 -or $row[0].exit -ne $row[0].expected -or $row[0].stopped) { throw "No accepted prior step to reuse: $name" }
    }
    Write-Text (Join-Path $output 'reused-checks.json') ([ordered]@{from=[IO.Path]::GetFullPath($ReuseGate); checks=$reused; allowedChangedTestInputs=$changedTestInputs; unchangedOtherInputs=$true} | ConvertTo-Json -Depth 5)
}
$passed = $false
if($ReuseAnalysisGate) {
    $previous = Get-Content -LiteralPath (Join-Path $ReuseAnalysisGate 'result.json') -Raw | ConvertFrom-Json
    $priorInputs = @(Get-Content -LiteralPath (Join-Path $ReuseAnalysisGate 'inputs-before.json') -Raw | ConvertFrom-Json)
    $oldMap = @{}
    foreach($row in $priorInputs) { $oldMap[([string]$row.path).Replace('\','/')] = [string]$row.sha256 }
    foreach($row in $before) {
        $path = ([string]$row.path).Replace('\','/')
        if($path -ne 'scripts/sdk/Test-LedgerScopedLeases.ps1' -and $oldMap[$path] -cne [string]$row.sha256) { throw "Cannot reuse changed diagnostics input: $path" }
    }
    $row = @($previous.steps | Where-Object name -EQ 'bounded-native-analysis')
    $log = @(Get-ChildItem -LiteralPath $ReuseAnalysisGate -Filter '*-bounded-native-analysis.stderr')
    if($row.Count -ne 1 -or $row[0].exit -ne 0 -or $row[0].stopped -or $log.Count -ne 1 -or
        [IO.File]::ReadAllText($log[0].FullName) -notmatch 'All 11 tests passed\.') { throw 'No accepted actual diagnostics run to reuse.' }
    $reused += 'bounded-native-analysis'
    Write-Text (Join-Path $output 'reused-analysis.json') ([ordered]@{from=[IO.Path]::GetFullPath($ReuseAnalysisGate); unchangedInputsExceptHarness=$true; actualExit=0; actualPassedTests=11; originalHarnessCountMismatchRetained=$true} | ConvertTo-Json -Depth 5)
}
try {
    Invoke-Step 'dotnet-identity' $dotnet @('--info')
    Invoke-Step 'zig-identity' $zig @('version') 0 '0.17.0'
    $artifacts = Join-Path $output 'dotnet'
    $common = @('-c','Release','--disable-build-servers','-m:1','--artifacts-path',$artifacts,"-p:RestorePackagesPath=$env:NUGET_PACKAGES",'-p:UseSharedCompilation=false','-p:NuGetAudit=false')
    $toolArtifacts = if($ManagedGate){Join-Path $ManagedGate 'dotnet'}elseif($ReuseGate){Join-Path $ReuseGate 'dotnet'}else{$artifacts}
    if($ManagedGate) {
        $managedResult = Get-Content (Join-Path $ManagedGate 'result.json') -Raw | ConvertFrom-Json
        if($managedResult.artifacts) { $toolArtifacts = $managedResult.artifacts }
        $managedInputs = Get-Content (Join-Path $ManagedGate 'inputs-after.json') -Raw | ConvertFrom-Json
        $currentManaged = @($before | Where-Object { $_.path.StartsWith('sdk/csharp') -or $_.path -notlike '*/*' })
        if(-not $managedResult.passed -or ($managedInputs|ConvertTo-Json -Depth 5 -Compress) -cne ($currentManaged|ConvertTo-Json -Depth 5 -Compress)) {throw 'Managed gate is not a passing exact-input match.'}
    }
    if(-not $ReuseGate -and -not $ManagedGate) {
        foreach($project in @('Configuration','Build','Tests')) {
            $path = Join-Path $repository "sdk/csharp/ResourceManager.Ledger.$project/ResourceManager.Ledger.$project.csproj"
            Invoke-Step "build-$project" $dotnet (@('build',$path) + $common + @("-p:RestoreConfigFile=$restoreConfig"))
        }
    }
    $configuration = Find-Dll (Join-Path $toolArtifacts 'bin/ResourceManager.Ledger.Configuration') 'ResourceManager.Ledger.Configuration.dll'
    $build = Find-Dll (Join-Path $toolArtifacts 'bin/ResourceManager.Ledger.Build') 'ResourceManager.Ledger.Build.dll'
    $tests = Find-Dll (Join-Path $toolArtifacts 'bin/ResourceManager.Ledger.Tests') 'ResourceManager.Ledger.Tests.dll'
    if(-not $ReuseGate -and -not $ManagedGate) { Invoke-Step 'analyzer-and-erasure' $dotnet @($tests) 0 'PASS' }
    foreach($invalid in @(@{name='flag'; point='ledger.development_lease_analysis';value=2},@{name='capacity-zero';point='ledger.development_lease_analysis_capacity';value=0},@{name='capacity-over';point='ledger.development_lease_analysis_capacity';value=65537},@{name='pages-zero';point='ledger.development_lease_analysis_write_watch_pages';value=0},@{name='cache-flag';point='ledger.use_window_cache';value=2},@{name='hint-zero';point='ledger.use_window_cache_slots';value=0})) {
        if($ReuseGate -or $ManagedGate) { continue }
        $path = Join-Path $output ("invalid-$($invalid.name).json")
        Write-Text $path (@{$invalid.point=$invalid.value} | ConvertTo-Json)
        Invoke-Step "reject-$($invalid.name)" $dotnet @($configuration,'check','--choices',$path) 3
    }
    $owner = Join-Path $output 'owner.json'
    Write-Text $owner '{"schemaVersion":1,"indexBucketCount":4,"tables":[{"capacity":2,"partitionCapacity":0,"reservedStart":0,"reservedCount":0,"attributes":{"recoverability":"recoverable","recoveryCostCoefficient":1,"accessLossImpact":"unobservable_now"}}],"fixedResources":[]}'
    $structure = Join-Path $output 'structure'
    Invoke-Step 'structure' $dotnet @($configuration,'structure',$structure,'--table-count','2')
    foreach($width in @(128,256)) {
        foreach($enabled in @($false,$true)) {
            if($ReuseGate -and $width -eq 128 -and -not $enabled) { continue }
            $label = "u$width-$(if($enabled){'on'}else{'off'})"
            $root = Join-Path $output $label
            $choices = Join-Path $root 'choices.json'
            Write-Text $choices (@{'ledger.uid_width_bits'=$width;'ledger.development_lease_analysis'=[int]$enabled;'ledger.development_lease_analysis_capacity'=8;'ledger.use_window_cache'=1} | ConvertTo-Json)
            $values = Join-Path $root 'values'
            Invoke-Step "$label-check" $dotnet @($configuration,'check','--table-count','2','--choices',$choices)
            Invoke-Step "$label-values" $dotnet @($configuration,'values',$values,$structure,'--table-count','2','--choices',$choices)
            $valuesPath = Join-Path $values 'ledger_values.zig'
            $generated = Join-Path $root 'generated'
            Invoke-Step "$label-native-generation" $dotnet @($build,'native',$owner,$valuesPath,$generated,"--uid-width=$width")
            $manifest = Join-Path $generated 'ledger-native-build.json'
            Invoke-Step "$label-verify" $dotnet @($build,'verify',$manifest)
            $props = Join-Path $generated 'LedgerOwnerBuild.props'
            $propsBefore = [IO.File]::ReadAllText($props)
            Write-Text $props ($propsBefore + '<!-- stale -->')
            Invoke-Step "$label-reject-stale-props" $dotnet @($build,'verify',$manifest) 2
            Write-Text $props $propsBefore
            Invoke-Step "$label-verify-restored" $dotnet @($build,'verify',$manifest)
            $dll = Join-Path $root 'ledger.dll'
            $nativeArgs = @('build-lib','-dynamic','-mcpu=baseline+cx16','-j1','-fllvm','-flld','-fcompiler-rt','-OReleaseSafe',
                '--dep','ledger_sdk','--dep','ledger_owner_shape',"-Mroot=$(Join-Path $generated 'LedgerNativeEntry.zig')",
                '--dep','ledger_values',"-Mledger_sdk=$(Join-Path $repository 'sdk/zig/src/ledger.zig')",
                '--dep','ledger_sdk','--dep','ledger_values',"-Mledger_owner_shape=$(Join-Path $generated 'LedgerOwnerShape.zig')",
                '--dep','ledger_structure',"-Mledger_values=$valuesPath","-Mledger_structure=$(Join-Path $structure 'ledger_structure.zig')",
                '--cache-dir',(Join-Path $root 'zig-cache'),'--global-cache-dir',(Join-Path $output 'zig-global'),"-femit-bin=$dll")
            Invoke-Step "$label-native-build" $zig $nativeArgs
            if($width -eq 128) {
                $testArgs = @('test','-mcpu=baseline+cx16','-j1','-fllvm','-flld','-OReleaseSafe','--dep','ledger_values',"-Mroot=$(Join-Path $repository 'sdk/zig/src/ledger_lease_tests.zig')",'--dep','ledger_structure',"-Mledger_values=$valuesPath","-Mledger_structure=$(Join-Path $structure 'ledger_structure.zig')",'--cache-dir',(Join-Path $root 'test-cache'),'--global-cache-dir',(Join-Path $output 'zig-global'))
                if (-not $enabled) { Invoke-Step "$label-zig-regression" $zig $testArgs }
                if ($enabled -and -not $ReuseAnalysisGate) {
                    Invoke-Step 'bounded-native-analysis' $zig @('test','-mcpu=baseline+cx16','-j1','-fllvm','-flld','-OReleaseSafe','--test-filter','lease analysis','--dep','ledger_values',"-Mroot=$(Join-Path $repository 'sdk/zig/src/ledger_lease_analysis_tests.zig')",'--dep','ledger_structure',"-Mledger_values=$valuesPath","-Mledger_structure=$(Join-Path $structure 'ledger_structure.zig')",'--cache-dir',(Join-Path $root 'analysis-cache'),'--global-cache-dir',(Join-Path $output 'zig-global')) 0 'All 9 tests passed.'
                }
            }
            $consumer = $tests
            Invoke-Step "$label-real-consumer" $dotnet @($consumer,$dll,$(if($enabled){'on'}else{'off'}),[string]$width,$manifest) 0 'held windows remain active after return'
        }
    }
    $packages = Join-Path $output 'local-feed'
    $runtime = Join-Path $repository 'sdk/csharp/ResourceManager.Ledger/ResourceManager.Ledger.csproj'
    Invoke-Step 'pack-analyzer' $dotnet (@('pack',$runtime) + $common + @('-p:IsPackable=true','-p:PackageId=ResourceManager.Ledger.ScopedCandidate','-p:Version=0.1.0-dev.scoped20261029','-o',$packages,"-p:RestoreConfigFile=$restoreConfig"))
    $package = Join-Path $packages 'ResourceManager.Ledger.ScopedCandidate.0.1.0-dev.scoped20261029.nupkg'
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        if(-not ($archive.Entries | Where-Object FullName -EQ 'analyzers/dotnet/cs/ResourceManager.Ledger.Analyzers.dll')) { throw 'Packed runtime omitted its analyzer.' }
    } finally { $archive.Dispose() }
    $consumerRoot = Join-Path $output 'package-consumer'
    $project = Join-Path $consumerRoot 'Consumer.csproj'
    Write-Text $project '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include="ResourceManager.Ledger.ScopedCandidate" Version="0.1.0-dev.scoped20261029" /></ItemGroup></Project>'
    $source = Join-Path $consumerRoot 'Caller.cs'
    Write-Text $source 'using ResourceManager.Ledger; using ResourceManager.Ledger.Identity; public class Caller { public void Run(LedgerOwner<LedgerUid128> owner, LedgerUid128 uid) { using var lease = owner.Use(uid).Lease; } }'
    Invoke-Step 'package-scoped-positive' $dotnet @('build',$project,'-c','Release','--disable-build-servers','-m:1','-p:UseSharedCompilation=false','-p:NuGetAudit=false',"-p:RestorePackagesPath=$env:NUGET_PACKAGES",'--source',$packages)
    Write-Text $source 'using ResourceManager.Ledger; using ResourceManager.Ledger.Identity; public class Caller { public LedgerLease<LedgerUid128>? Held; }'
    Invoke-Step 'package-field-negative' $dotnet @('build',$project,'-c','Release','--disable-build-servers','-m:1','-p:UseSharedCompilation=false','-p:NuGetAudit=false',"-p:RestorePackagesPath=$env:NUGET_PACKAGES",'--source',$packages) 1 'RML001'
    $after = Get-Inputs
    Write-Text (Join-Path $output 'inputs-after.json') ($after | ConvertTo-Json -Depth 5)
    if(($before | ConvertTo-Json -Depth 5 -Compress) -cne ($after | ConvertTo-Json -Depth 5 -Compress)) { throw 'Source inputs changed during the gate.' }
    $passed = $true
} finally {
    Write-Text (Join-Path $output 'result.json') ([ordered]@{passed=$passed; jobId=$env:JOBQUEUE_JOB_ID; finishedUtc=[DateTime]::UtcNow; reusedChecks=$reused; steps=$steps} | ConvertTo-Json -Depth 8)
}
