[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanPath,
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$WorkspaceRoot,
    [Parameter(Mandatory)][string]$LedgerBuildRoot,
    [Parameter(Mandatory)][string]$NativeContextPath,
    [string[]]$PartitionIds = @(),
    [string]$ValidationScriptsRoot = $PSScriptRoot,
    [string]$ResumeInvocationPath,
    [switch]$PlanOnly,
    [string[]]$RuntimePartitionIds = @('app-04'),
    [ValidateRange(1,512)][int]$RuntimeMaxMethodsPerGroup = 128,
    [ValidateRange(1073741824,1610612736)][long]$BuildJobMemoryLimitBytes = 1610612736,
    [string]$StopRequestPath,
    [ValidateSet('DOTNET_gcServer=0')][string]$CompilerEnvironment
)
$ValidationScriptsRoot=[IO.Path]::GetFullPath($ValidationScriptsRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$planFile = [IO.Path]::GetFullPath($PlanPath)
$root = [IO.Path]::GetFullPath($TaskRoot)
$plan = Get-Content -LiteralPath $planFile -Raw | ConvertFrom-Json
if ([int]$plan.schemaVersion -ne 1) { throw 'Unsupported test partition plan.' }
$selected = @($plan.partitions | Where-Object { $PartitionIds.Count -eq 0 -or $_.partitionId -in $PartitionIds })
if ($selected.Count -eq 0 -or ($PartitionIds.Count -gt 0 -and $selected.Count -ne @($PartitionIds | Sort-Object -Unique).Count)) {
    throw 'Select existing, unique test partition IDs.'
}
# Resume requires actual successful receipts and stable inputs; a prior green summary alone is insufficient.
$repo=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($plan.projectPath)) '../..'))
$priorStages=@();$hashes=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$reuseWatch=[Diagnostics.Stopwatch]::StartNew();$resumeQualificationActive=$true
function Read-ResumeJson([string]$Path) {
 if(-not[IO.File]::Exists($Path)-or(Get-Item -LiteralPath $Path).Length-gt67108864){throw "Missing or oversized resume evidence: $Path"}
 Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json
}
function Verify-ResumeHash([string]$Path,[string]$Expected) {
 $full=[IO.Path]::GetFullPath($Path);$file=Get-Item -LiteralPath $full
 if($hashes.Count-ge32768-or($resumeQualificationActive-and$reuseWatch.ElapsedMilliseconds-gt300000)){throw 'Resume qualification exceeds original bounded metadata envelope'}
 if(-not$hashes.ContainsKey($full)-or$hashes[$full].length-ne$file.Length-or$hashes[$full].ticks-ne$file.LastWriteTimeUtc.Ticks){
  $hashes[$full]=@{length=$file.Length;ticks=$file.LastWriteTimeUtc.Ticks;sha256=(Get-FileHash -LiteralPath $full).Hash}
 }
 if($hashes[$full].sha256-ine$Expected){throw "Resume input/output changed: $full"}
}
function Check-ResumeStage($Row) {
 $run=[IO.Path]::GetFullPath($Row.result.runDirectory)
 if(-not$run.StartsWith((Join-Path $root 'runs')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Resume run is outside this task'}
 $actual=Read-ResumeJson (Join-Path $run 'bounded-result.json')
 if(-not$actual.accepted-or-not$actual.normalDrain-or-not$actual.sourceInputsUnchangedExceptDeclaredPartitionRefresh-or$actual.exitCode-ne0-or$actual.stage-cne$Row.stage){throw 'Resume only accepts successful stable actual stages'}
 if(($actual|ConvertTo-Json -Depth 16 -Compress)-cne($Row.result|ConvertTo-Json -Depth 16 -Compress)){throw 'Recorded resume receipt differs from the original actual receipt'}
 $part=@($selected|Where-Object partitionId -CEQ $Row.partitionId)
 if($part.Count-ne1-or$actual.testCompileManifest-ine$part[0].manifestPath-or$actual.nativeContextPath-ine$NativeContextPath-or$actual.testProject-ine$plan.projectPath){throw 'Resume stage belongs to another selected input'}
 Verify-ResumeHash $part[0].manifestPath $actual.testCompileManifestSha256
 Verify-ResumeHash (Join-Path $run 'test-compile-manifest.json') $actual.testCompileManifestSha256
 $events=@(Get-Content -LiteralPath (Join-Path $run 'job/receipts.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
 $e=@($events|Where-Object kind -CEQ 'exit');$f=@($events|Where-Object kind -CEQ 'final-job');$c=@($events|Where-Object kind -CEQ 'owned-job-closed')
 if($e.Count-ne1-or$f.Count-ne1-or$c.Count-ne1-or$e[0].data.reason-cne'normal'-or$e[0].data.ExitCode-ne0-or-not$f[0].data.empty-or$f[0].data.reason-cne'normal'-or$c[0].data.reason-cne'normal'-or-not$c[0].data.assigned-or-not$c[0].data.killOnCloseConfigured){throw 'Resume requires a normal empty closed owned Job'}
 $job=Read-ResumeJson (Join-Path $run 'job-manifest.json')
 $ledgerIndex=[array]::IndexOf([object[]]$job.arguments,'-LedgerBuildRoot')
 if($ledgerIndex-lt0-or$ledgerIndex+1-ge$job.arguments.Count-or$job.arguments[$ledgerIndex+1]-ine$LedgerBuildRoot-or-not([IO.Path]::GetFullPath($actual.workset).StartsWith((Join-Path ([IO.Path]::GetFullPath($WorkspaceRoot)) '.validation/batches')+'\',[StringComparison]::OrdinalIgnoreCase))){throw 'Resume Ledger or workspace input differs'}
 $expectedMemory=if($Row.stage-ceq'Build'){$BuildJobMemoryLimitBytes}else{805306368L}
 if($job.jobMemoryLimitBytes-ne$expectedMemory-or$job.cpuRate-ne2500-or$job.activeProcessLimit-ne32-or$job.timeoutSeconds-ne900-or$job.workingDirectory-ine$repo){throw 'Resume Job budget or working directory differs'}
 $before=@(Read-ResumeJson (Join-Path $run 'source-inputs.json'));$after=@(Read-ResumeJson (Join-Path $run 'source-inputs-after.json'))
 if($before.Count-gt32768-or$before.Count-ne$after.Count-or@(Compare-Object $before $after -Property path,sha256).Count){throw 'Resume source capture is not stable'}
 foreach($inputRow in $after){$path=$inputRow.path;if(-not[IO.Path]::IsPathFullyQualified($path)){$path=Join-Path $repo $path};Verify-ResumeHash $path $inputRow.sha256}
 $outputNames=if($Row.stage-ceq'Build'){@('test-output-build.json')}else{@('test-output-before-tests.json','test-output-after-tests.json')}
 $outputSets=@()
 foreach($name in $outputNames){
  $outputs=@(Read-ResumeJson (Join-Path $run $name));if($outputs.Count-ne4){throw 'Resume requires four real archived test outputs'}
  foreach($output in $outputs){if(-not$output.exists){throw 'Missing resume output'};Verify-ResumeHash $output.archive $output.sha256}
  $outputSets+=,@($outputs)
 }
 if($outputSets.Count-eq2-and@(Compare-Object $outputSets[0] $outputSets[1] -Property path,length,sha256).Count){throw 'Test outputs changed during reused runtime'}
 if($Row.stage-ceq'Tests'){
  $trx=[xml](Get-Content -LiteralPath (Join-Path $run 'full-app.trx') -Raw);$cases=@($trx.TestRun.Results.UnitTestResult)
  if($cases.Count-ne[int]$trx.TestRun.ResultSummary.Counters.total-or$cases.Count-eq0-or@($cases|Where-Object {$_.outcome-notin@('Passed','NotExecuted')}).Count){throw 'Resume contains missing or unsuccessful TRX cases'}
  if($actual.PSObject.Properties['testRuntimeManifest']){Verify-ResumeHash $actual.testRuntimeManifest $actual.testRuntimeManifestSha256}
 }
 return $actual
}
if($ResumeInvocationPath){
 $ResumeInvocationPath=[IO.Path]::GetFullPath($ResumeInvocationPath)
 if(-not$ResumeInvocationPath.StartsWith((Join-Path $root 'partition-invocations')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Resume invocation must belong to this task'}
 $oldSummary=Read-ResumeJson (Join-Path $ResumeInvocationPath 'summary.json')
 if($oldSummary.planPath-ine$planFile-or$oldSummary.planSha256-ine(Get-FileHash -LiteralPath $planFile).Hash-or@(Compare-Object @($oldSummary.selectedPartitionIds) @($selected.partitionId)).Count){throw 'Resume must use the same plan and selected partition set'}
 $priorStages=@(Read-ResumeJson (Join-Path $ResumeInvocationPath 'completed-stages.json'))
 if($priorStages.Count-gt4096){throw 'Resume stage count exceeds bound'}
 $unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($row in $priorStages){
  $receipt=Check-ResumeStage $row;$group=if($receipt.PSObject.Properties['testRuntimeGroupId']){[string]$receipt.testRuntimeGroupId}else{''}
  if(-not$unique.Add($row.partitionId+'|'+$row.stage+'|'+$group)){throw 'Duplicate reusable stage'}
 }
}
$resumeQualificationActive=$false
if($PlanOnly){@{planOnly=$true;resumeQualified=$true;reusableStages=$priorStages.Count;independentlyHashedPaths=$hashes.Count;actualValidationExecuted=$false}|ConvertTo-Json -Compress;return}

$invocation = Join-Path $root ('partition-invocations/' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($invocation)
if($ResumeInvocationPath){
 $qualification=@{resumeQualified=$true;sourceInvocation=$ResumeInvocationPath;reusableStages=$priorStages.Count;independentlyHashedPaths=$hashes.Count;tool=@{path=$PSCommandPath;sha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash};plan=@{path=$planFile;sha256=(Get-FileHash -LiteralPath $planFile).Hash};actualValidationExecuted=$false}
 [IO.File]::WriteAllText((Join-Path $invocation 'resume-qualification.json'),($qualification|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
$results = [Collections.Generic.List[object]]::new()
$completedStages = [Collections.Generic.List[object]]::new()
function Invoke-PartitionStage([string]$Stage, $Partition, [string]$RuntimeManifest, [string]$RuntimeGroupId) {
    $matching=@($priorStages|Where-Object {$_.partitionId-ceq$Partition.partitionId-and$_.stage-ceq$Stage-and(($Stage-ceq'Build')-or(-not$RuntimeManifest-and-not$_.result.PSObject.Properties['testRuntimeGroupId'])-or($RuntimeManifest-and$_.result.PSObject.Properties['testRuntimeGroupId']-and$_.result.testRuntimeGroupId-ceq$RuntimeGroupId))})
    if($matching.Count-gt1){throw 'Ambiguous resume stage'}
    if($matching.Count-eq1){
        $receipt=$matching[0].result
        if($RuntimeManifest-and($receipt.testRuntimeManifest-ine$RuntimeManifest-or$receipt.testRuntimeManifestSha256-ine(Get-FileHash -LiteralPath $RuntimeManifest).Hash)){throw 'Resume group manifest differs'}
        $completedStages.Add([pscustomobject]@{partitionId=$Partition.partitionId;stage=$Stage;result=$receipt;reused=$true})
        [IO.File]::WriteAllText((Join-Path $invocation 'completed-stages.json'),($completedStages.ToArray()|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
        Write-Host (@{partition=$Partition.partitionId;stage=$Stage;accepted=$true;reused=$true;run=$receipt.runDirectory}|ConvertTo-Json -Compress)
        return $receipt
    }
    if($Stage-ceq'Tests'){
        $context=Read-ResumeJson (Join-Path $root 'build-context.json')
        $expected=@($completedStages|Where-Object {$_.partitionId-ceq$Partition.partitionId-and$_.stage-ceq'Build'})
        if($expected.Count-ne1-or$context.runDirectory-ine$expected[0].result.runDirectory){throw 'Missing groups require the same still-selected actual Build; no automatic output restore'}
        foreach($output in @(Read-ResumeJson (Join-Path $context.runDirectory 'test-output-build.json'))){Verify-ResumeHash $output.path $output.sha256}
    }

    if ($StopRequestPath -and [IO.File]::Exists($StopRequestPath) -and
        (Get-Content -LiteralPath $StopRequestPath -Raw | ConvertFrom-Json).stop) {
        throw "Explicit stop requested before $($Partition.partitionId) $Stage; completed Job receipts are retained."
    }
    $before = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ([IO.Directory]::Exists((Join-Path $root 'runs'))) {
        foreach ($directory in [IO.Directory]::EnumerateDirectories((Join-Path $root 'runs'))) { [void]$before.Add($directory) }
    }
    $failure = $null
    $runtimeArguments=@{}
    $compilerArguments=@{}
    if($Stage -ceq 'Build' -and $CompilerEnvironment){$compilerArguments=@{CompilerEnvironment=$CompilerEnvironment}}
    $logLabel=$Partition.partitionId+'-'+$Stage.ToLowerInvariant()
    if($RuntimeManifest){$runtimeArguments=@{TestRuntimeManifest=$RuntimeManifest;TestRuntimeGroupId=$RuntimeGroupId};$logLabel+='-'+$RuntimeGroupId}
    try {
        & (Join-Path $ValidationScriptsRoot 'Invoke-Validation.ps1') -Stage $Stage -TaskRoot $root -WorkspaceRoot $WorkspaceRoot `
            -LedgerBuildRoot $LedgerBuildRoot -NativeContextPath $NativeContextPath -TestProject $plan.projectPath `
            -TestCompileManifest $Partition.manifestPath -JobMemoryLimitBytes $(if ($Stage -eq 'Tests') { 805306368 } else { $BuildJobMemoryLimitBytes }) `
            @runtimeArguments @compilerArguments *> (Join-Path $invocation ($logLabel + '.log'))
    } catch {
        $failure = $_.Exception.Message
        [IO.File]::WriteAllText((Join-Path $invocation ($logLabel + '-failure.txt')), $_.ToString(), [Text.UTF8Encoding]::new($false))
    }
    $created = @(if ([IO.Directory]::Exists((Join-Path $root 'runs'))) {
        [IO.Directory]::EnumerateDirectories((Join-Path $root 'runs')) | Where-Object { -not $before.Contains($_) }
    })
    if ($created.Count -ne 1) { throw "Stage did not create exactly one run: $Stage; $failure" }
    $receiptPath = Join-Path $created[0] 'bounded-result.json'
    if (-not [IO.File]::Exists($receiptPath)) { throw "Missing bounded result: $created; $failure" }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if (-not $receipt.normalDrain -or -not $receipt.sourceInputsUnchangedExceptDeclaredPartitionRefresh) {
        throw "Unaccepted partition preparation or undrained/unstable stage: $receiptPath; original failure: $failure"
    }
    $completedStages.Add([pscustomobject]@{ partitionId = $Partition.partitionId; stage = $Stage; result = $receipt })
    [IO.File]::WriteAllText((Join-Path $invocation 'completed-stages.json'), ($completedStages.ToArray() | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Host (@{ partition = $Partition.partitionId; stage = $Stage; accepted = $receipt.accepted; run = $receipt.runDirectory } | ConvertTo-Json -Compress)
    return $receipt
}
function New-RuntimeManifest($Partition,$Build) {
    if($ResumeInvocationPath){
        $existing=Join-Path $ResumeInvocationPath ($Partition.partitionId+'-runtime-plan/runtime-partitions.json')
        $priorRuntime=@($priorStages|Where-Object {$_.partitionId-ceq$Partition.partitionId-and$_.stage-ceq'Tests'-and$_.result.PSObject.Properties['testRuntimeManifest']}|ForEach-Object {$_.result.testRuntimeManifest}|Sort-Object -Unique)
        if($priorRuntime.Count-gt1){throw 'Resume has multiple runtime manifests for one partition'}
        if($priorRuntime.Count-eq1){$existing=$priorRuntime[0]}
        if(Test-Path -LiteralPath $existing){
            $runtime=Read-ResumeJson $existing
            if($runtime.planPath-ine$planFile-or$runtime.planSha256-ine(Get-FileHash -LiteralPath $planFile).Hash-or$runtime.buildRunDirectory-ine$Build.runDirectory-or$runtime.maxMethodsPerGroup-ne$RuntimeMaxMethodsPerGroup-or$runtime.testCompileManifestSha256-ine$Build.testCompileManifestSha256){throw 'Existing runtime grouping belongs to another plan/Build/limit'}
            foreach($tool in $runtime.tools){Verify-ResumeHash $tool.path $tool.sha256}
            Verify-ResumeHash (Join-Path $Build.runDirectory 'partition-build.json') $runtime.partitionBuildSha256
            Verify-ResumeHash $runtime.assemblyArchive $runtime.testAssemblySha256
            return $existing
        }
    }

    $planning=Join-Path $invocation ($Partition.partitionId+'-runtime-plan')
    [void][IO.Directory]::CreateDirectory($planning)
    $manifestPath=Join-Path $planning 'runtime-partitions.json'
    $job=Get-Content -LiteralPath (Join-Path $Build.runDirectory 'job-manifest.json') -Raw|ConvertFrom-Json
    $preflight=& (Join-Path $ValidationScriptsRoot 'Capture-Preflight.ps1') -TaskRoot $root -Scope Product -JobMemoryLimitBytes 805306368
    $job.faultReviewPath=$preflight.faultReviewPath;$job.headroomPath=$preflight.headroomPath
    $job.allowedReceiptRoot=$planning;$job.runDirectory=Join-Path $planning 'job';$job.jobMemoryLimitBytes=805306368
    $job.arguments=@('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $ValidationScriptsRoot 'New-AppTestRuntimePartitions.ps1'),'-PlanPath',$planFile,'-BuildRunDirectory',$Build.runDirectory,'-OutputPath',$manifestPath,'-MaxMethodsPerGroup',[string]$RuntimeMaxMethodsPerGroup)
    $currentCompile=Get-Content -LiteralPath $Partition.manifestPath -Raw|ConvertFrom-Json
    if(@($currentCompile.testDeclarations|Where-Object {$_ -like 'Resource_Manager_APP.Tests|HostManagerTransactionJournalRuntimeTests::AdmissionDisposeWaitsForInflightPayloadOperationBeforeRuntimeDrain(*)'}).Count -gt 0){$job.arguments+=@('-IsolateMethods','Resource_Manager_APP.Tests.HostManagerTransactionJournalRuntimeTests.AdmissionDisposeWaitsForInflightPayloadOperationBeforeRuntimeDrain')}
    $generatorHash=(Get-FileHash -LiteralPath (Join-Path $ValidationScriptsRoot 'New-AppTestRuntimePartitions.ps1')).Hash
    $jobPath=Join-Path $planning 'job-manifest.json'
    [IO.File]::WriteAllText($jobPath,($job|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    & (Join-Path $ValidationScriptsRoot '../../../.local/subscription-core-device-owners-20261007/execution-job-fixed/bin/ExecutionJob/release/ExecutionJob.exe') $jobPath *> (Join-Path $planning 'controller.log')
    $code=$LASTEXITCODE
    $events=@(Get-Content -LiteralPath (Join-Path $job.runDirectory 'receipts.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
    $exit=@($events|Where-Object kind -eq 'exit');$drain=@($events|Where-Object kind -eq 'final-job')
    if($code -ne 0 -or $exit.Count -ne 1 -or $exit[0].data.reason -cne 'normal' -or $drain.Count -ne 1 -or -not $drain[0].data.empty -or $drain[0].data.reason -cne 'normal' -or
        (Get-FileHash -LiteralPath (Join-Path $ValidationScriptsRoot 'New-AppTestRuntimePartitions.ps1')).Hash -cne $generatorHash){throw "Runtime metadata planning did not finish with stable inputs and a normal empty Job: $planning"}
    return $manifestPath
}
function Save-ExecutionSummary {
    $value = [ordered]@{
        schemaVersion = 1; resumedFromInvocation = $ResumeInvocationPath; planPath = $planFile; planSha256 = (Get-FileHash -LiteralPath $planFile).Hash
        selectedPartitionIds = @($selected.partitionId); wholePlanSelected = $selected.Count -eq @($plan.partitions).Count
        updatedUtc = [DateTime]::UtcNow.ToString('o'); results = @($results.ToArray())
        allSelectedAccepted = $results.Count -eq $selected.Count -and @($results | Where-Object { -not $_.completed -or $null -eq $_.build -or -not $_.build.accepted -or $null -eq $_.tests -or @($_.runtimeTests|Where-Object {-not $_.accepted}).Count -gt 0 }).Count -eq 0
        coverageAcceptance = 'Compile/discovery/TRX union must be reconciled separately; this is a stage execution summary.'
    }
    $json = $value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $invocation 'summary.json'), $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'latest-partition-execution.json'), $json, [Text.UTF8Encoding]::new($false))
    return $value
}
$summary = Save-ExecutionSummary
foreach ($partition in $selected) {
    $currentResult = [pscustomobject]@{ partitionId = $partition.partitionId; manifestPath = $partition.manifestPath; build = $null; tests = $null; runtimeTests = @(); counters = $null; completed = $false; failure = $null }
    $results.Add($currentResult)
    try {
        $build = Invoke-PartitionStage 'Build' $partition
        $currentResult.build = $build
        $runtimeTests=[Collections.Generic.List[object]]::new()
        if ($build.accepted) {
            $runtimeManifest=$null
            $groups=@($null)
            if($partition.partitionId -in $RuntimePartitionIds){$runtimeManifest=New-RuntimeManifest $partition $build;$groups=@((Get-Content -LiteralPath $runtimeManifest -Raw|ConvertFrom-Json).groups)}
            $caseCounts=@{total=0;passed=0;failed=0;skipped=0;summaryNotExecuted=0;skipCountSource='Actual UnitTestResult outcomes; VSTest summary notExecuted may remain zero.'}
            foreach($group in $groups) {
            $tests = Invoke-PartitionStage 'Tests' $partition $runtimeManifest $(if($group){[string]$group.groupId}else{$null})
            $runtimeTests.Add($tests)
            $currentResult.tests = $tests
            $currentResult.runtimeTests = @($runtimeTests.ToArray())
            $trxPath = Join-Path $tests.runDirectory 'full-app.trx'
            if ([IO.File]::Exists($trxPath)) {
                $trx = [xml](Get-Content -LiteralPath $trxPath -Raw)
                $counter = $trx.TestRun.ResultSummary.Counters
                $testResults = @($trx.TestRun.Results.UnitTestResult)
                if ($testResults.Count -ne [int]$counter.total) { throw 'TRX result count differs from its declared total.' }
                $counters = @{
                    total = $testResults.Count
                    passed = @($testResults | Where-Object outcome -eq 'Passed').Count
                    failed = @($testResults | Where-Object outcome -eq 'Failed').Count
                    skipped = @($testResults | Where-Object outcome -eq 'NotExecuted').Count
                    summaryNotExecuted = [int]$counter.notExecuted
                    skipCountSource = 'Actual UnitTestResult outcomes; VSTest summary notExecuted may remain zero.'
                }
                foreach($key in @('total','passed','failed','skipped','summaryNotExecuted')){$caseCounts[$key]+=$counters[$key]}
                $currentResult.counters = $caseCounts
            }
            if(-not$tests.accepted){throw "Actual Tests failed: $($tests.runDirectory); completed stages retained"}
            }
        }
        $currentResult.completed = $true
    } catch {
        $currentResult.failure = $_.Exception.Message
        throw
    } finally {
        $summary = Save-ExecutionSummary
    }
}
Write-Output (@{ invocation = $invocation; selected = $selected.Count; allSelectedAccepted = $summary.allSelectedAccepted } | ConvertTo-Json -Compress)
if (-not $summary.allSelectedAccepted) { exit 1 }
