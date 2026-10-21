[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# The caller supplies its registered task directory; fixtures never enumerate or
# prune real project worksets. Repeated runs reuse this bounded small fixture.
$out=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'producer-isolated-validation'
[void][IO.Directory]::CreateDirectory($out)
$workspace=Join-Path $out 'workspace'
[void][IO.Directory]::CreateDirectory($workspace)
$batches=Join-Path $workspace '.validation/batches'
$policyPath=Join-Path $out 'policy.json'
$policy=[pscustomobject]@{schemaVersion=1;enabled=$true;referenceRoots=@();defaults=@{maxRecentCompleted=3;maxBytes=8388608;maxNodes=5000;maxMilliseconds=15000};roots=@(@{id='fixture-batches';path=$batches;excludedNames=@()})}
$policy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $policyPath -Encoding utf8
$script=Join-Path $repo 'scripts/validation/Manage-ValidationWorkset.ps1'
$cache=Join-Path $workspace '.validation/shared-artifacts/managed-cache'
[void][IO.Directory]::CreateDirectory($cache)
$global:RMProducerFixtureCreationFailure=$true
function New-Item {
    [CmdletBinding()]param([string]$ItemType,[string]$Path,[switch]$Force)
    if($global:RMProducerFixtureCreationFailure -and $Path -eq (Join-Path $cache 'nuget')) {throw 'Injected scratch-creation failure after registration'}
    Microsoft.PowerShell.Management\New-Item @PSBoundParameters
}
$passed=[Collections.Generic.List[string]]::new()
$threw=$false
try { & $script -Mode Prepare -WorkspaceRoot $workspace -BatchId 'fresh-failure-v2' -ConsumerId c1 -ExpectedGrowthGiB 0 -ReserveFreeGiB 0 -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)|Out-Null }
catch {$threw=$true; $failureMessage=$_.Exception.Message}
if(-not $threw){throw 'Prepare should fail at the blocked scratch path'}
if(-not [IO.File]::Exists((Join-Path $batches 'fresh-failure-v2/.generated-workspace.json'))){throw $failureMessage}
$state=Get-Content -LiteralPath (Join-Path $batches 'fresh-failure-v2/.generated-workspace.json') -Raw|ConvertFrom-Json
if($state.state -ne 'completed' -or $state.outcome -ne 'failed' -or @($state.references).Count -ne 0){throw 'New failed Prepare was not concluded as failed'}
$passed.Add('Fresh Prepare post-registration failure is completed/failed without dependencies')
$global:RMProducerFixtureCreationFailure=$false
$null=& $script -Mode Prepare -WorkspaceRoot $workspace -BatchId 'reused-running' -ConsumerId c1 -ExpectedGrowthGiB 0 -ReserveFreeGiB 0 -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)
$threw=$false
try { & $script -Mode Prepare -WorkspaceRoot $workspace -BatchId 'reused-running' -ConsumerId c1 -ExpectedGrowthGiB 0 -ReserveFreeGiB 0 -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)|Out-Null }
catch {$threw=$true}
$state=Get-Content -LiteralPath (Join-Path $batches 'reused-running/.generated-workspace.json') -Raw|ConvertFrom-Json
if(-not $threw -or $state.state -ne 'running'){throw 'A duplicate consumer request altered the existing running batch'}
$passed.Add('Reused running batch is not completed by a duplicate-consumer failure')
$policy.defaults.maxRecentCompleted=0
$policy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $policyPath -Encoding utf8
$threw=$false
try { & $script -Mode Prepare -Apply -WorkspaceRoot $workspace -BatchId 'invalid-apply' -ConsumerId c1 -ExpectedGrowthGiB 0 -ReserveFreeGiB 0 -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)|Out-Null }
catch {$threw=$true}
if(-not $threw -or -not [IO.Directory]::Exists((Join-Path $batches 'fresh-failure-v2')) -or [IO.Directory]::Exists((Join-Path $batches 'invalid-apply'))){throw 'Invalid Apply invocation reached mutation'}
$passed.Add('Invalid Apply rejects before retention and batch creation')
$threw=$false
try { & $script -Mode Prepare -WorkspaceRoot $workspace -BatchId '../invalid' -ConsumerId c1 -ExpectedGrowthGiB 0 -ReserveFreeGiB 0 -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)|Out-Null }
catch {$threw=$true}
if(-not $threw -or -not [IO.Directory]::Exists((Join-Path $batches 'fresh-failure-v2'))){throw 'Invalid BatchId invocation reached mutation'}
$passed.Add('Invalid BatchId rejects before retention')
# Clean only the synthetic batches through the official entry, with no real
# reference roots. This is also an integration check of post-failure cleanup.
$null=& $script -Mode Complete -WorkspaceRoot $workspace -BatchId 'reused-running' -Outcome success -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)
if([IO.Directory]::Exists((Join-Path $batches 'fresh-failure-v2')) -or [IO.Directory]::Exists((Join-Path $batches 'reused-running'))){throw 'Official zero-recent cleanup did not remove fixture batches'}
$passed.Add('Official zero-recent completion cleans both isolated fixture batches')
# Close the one synthetic batch left by the initial fixture setup experiment.
$oldFixture=Join-Path $batches 'fresh-failure/.generated-workspace.json'
if([IO.File]::Exists($oldFixture)) {
    $oldState=Get-Content -LiteralPath $oldFixture -Raw|ConvertFrom-Json
    if($oldState.state -eq 'running') {
        $null=& $script -Mode Complete -WorkspaceRoot $workspace -BatchId 'fresh-failure' -Outcome failed -RetentionPolicyPath $policyPath -ReferenceRoots @($batches)
    }
}
Remove-Variable -Name RMProducerFixtureCreationFailure -Scope Global
Remove-Item -LiteralPath Function:\New-Item

$fixtureTask=Join-Path $out 'task-policy-fixture'
[void][IO.Directory]::CreateDirectory($fixtureTask)
$project=Get-Content -LiteralPath (Join-Path $repo 'scripts/maintenance/generated-workspace-policy.json') -Raw|ConvertFrom-Json
$oldTaskPolicy=@{schemaVersion=1;enabled=$false;referenceRoots=@();defaults=@{maxRecentCompleted=99;maxBytes=999999999;maxNodes=100;maxMilliseconds=10};roots=@(@{id='stale-rm-local';path='{RepositoryRoot}\.local';excludedNames=@();maxRecentCompleted=99},@{id='fixture-batches';path=$batches;excludedNames=@()})}
$oldTaskPolicy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $fixtureTask 'retention-policy.json') -Encoding utf8
$effective=& (Join-Path $repo 'scripts/validation/ProductSdk/Get-ProductRetentionPolicy.ps1') -TaskRoot $fixtureTask
if(-not $effective.policy.enabled -or ($effective.policy.defaults|ConvertTo-Json -Compress) -cne ($project.defaults|ConvertTo-Json -Compress)){throw 'Old disabled task policy still overrides current project defaults'}
$passed.Add('Product Prepare and Complete share enabled current project limits')
$matchingRoot=@($effective.policy.roots|Where-Object { $_.path -eq '{RepositoryRoot}\.local' })
if($matchingRoot.Count -ne 1 -or $matchingRoot[0].id -eq 'stale-rm-local' -or @($effective.policy.roots|Where-Object id -eq 'fixture-batches').Count -ne 1){throw 'Root merge lost project precedence or task-specific batch root'}
$passed.Add('Current project root rules win and task-specific roots remain visible')
# Execute the production reference-selection statements on distinct old/current
# fixture paths, without invoking its lifecycle or cleanup operations.
$completeSource=Get-Content -LiteralPath (Join-Path $repo 'scripts/validation/ProductSdk/Complete-Validation.ps1') -Raw
$referenceStart=$completeSource.IndexOf('$references=@(')
$referenceEnd=$completeSource.IndexOf('# Use current finite project limits', $referenceStart)
$referenceCode=$completeSource.Substring($referenceStart,$referenceEnd-$referenceStart)
$selected=& {
    $taskState=[pscustomobject]@{references=@('F:\historical-unused-batch')}
    $build=[pscustomobject]@{artifacts='F:\current-build'}
    $acceptance=[pscustomobject]@{packages=@(@{path='F:\current-package'});consumerDependencies=@(@{deps='F:\current-dependency'});retainedOutputs=@('F:\current-native')}
    . ([scriptblock]::Create($referenceCode))
    @($references)
}
if(@($selected).Count -ne 4 -or 'F:\historical-unused-batch' -in $selected -or 'F:\current-native' -notin $selected){throw 'Closure inherited historical references or lost current outputs'}
$passed.Add('Closure retains only four current accepted outputs/dependencies, excluding historical references')
$normalizer=Join-Path $repo 'scripts/validation/ConvertTo-ValidationLifecycleReferences.ps1'
$parent=Join-Path $out 'reference-owners'
$nested=Join-Path $parent 'current/.validation/batches'
$referencePolicy=[pscustomobject]@{roots=@(@{path=$parent},@{path=$nested})}
$selected=@(& $normalizer -References @((Join-Path $nested 'actual/scratch/file.dll'),(Join-Path $nested 'actual/scratch/other.obj'),(Join-Path $nested 'ACTUAL/scratch/third.dll')) -Policy $referencePolicy)
if($selected.Count -ne 1 -or $selected[0] -ine (Join-Path $nested 'actual')){throw 'Nested actual batch owner was not selected/deduplicated'}
$passed.Add('Most specific managed batch owner collapses per-file references and case variants')
$sourceReferences=@('src','tests','scripts','sdk','docs'|ForEach-Object {Join-Path $repo ($_+'/nested/input.ext')})
$selected=@(& $normalizer -References $sourceReferences -Policy $referencePolicy)
if($selected.Count -ne 5 -or @($selected|Where-Object {$_ -match 'nested'}).Count -ne 0){throw 'Permanent source directory folding failed'}
$passed.Add('Five permanent project source roots replace individual source-file lifecycle references')
# Paths only: this does not create a directory or file under the real SDK tree.
$managedSdkRoot=Join-Path $repo 'sdk/lifecycle-owner-fixture'
$sdkPolicy=[pscustomobject]@{roots=@(@{path=$managedSdkRoot})}
$selected=@(& $normalizer -References @((Join-Path $managedSdkRoot 'current/cache/input.bin'),$managedSdkRoot) -Policy $sdkPolicy)
if($selected.Count -ne 2 -or $selected[0] -ine (Join-Path $managedSdkRoot 'current') -or $selected[1] -ine $managedSdkRoot){throw 'Permanent SDK folding displaced its more specific managed owner or root'}
$passed.Add('A managed root inside repo/sdk takes precedence over source folding, including the root itself')
$unowned=Join-Path $out 'unowned/input.txt'
$selected=@(& $normalizer -References @($nested,$unowned) -Policy $referencePolicy)
if($selected.Count -ne 2 -or $selected[0] -ine $nested -or $selected[1] -ine $unowned){throw 'Root itself or unowned path was changed'}
$passed.Add('An exact management root and an unowned path remain unchanged')
$externalRoot=Join-Path $out 'extra-owners'
$selected=@(& $normalizer -References @((Join-Path $externalRoot 'task/cache/file.bin')) -Policy $referencePolicy -ReferenceRoots @($externalRoot))
if($selected.Count -ne 1 -or $selected[0] -ine (Join-Path $externalRoot 'task')){throw 'Explicit ReferenceRoots were not mapped'}
$passed.Add('Explicit reference management roots participate without filesystem scanning')
foreach($bad in @('relative/file.txt','F:\')) {
    $rejected=$false
    try {$null=& $normalizer -References @($bad) -Policy $referencePolicy} catch {$rejected=$true}
    if(-not $rejected){throw 'Lifecycle normalization weakened the existing absolute/non-volume boundary'}
}
$passed.Add('Relative paths and volume-root references remain rejected')
$outputHelper=Join-Path $repo 'scripts/validation/ProductSdk/Save-AppTestOutputEvidence.ps1'
$evidenceFixture=Join-Path $out 'app-output-evidence'
$compiled=Join-Path $evidenceFixture 'compiled'
$buildRun=Join-Path $evidenceFixture 'build'
$runtimeRun=Join-Path $evidenceFixture ('runtime-unchanged-'+$PSVersionTable.PSEdition)
$changedRun=Join-Path $evidenceFixture 'runtime-changed'
$corruptRun=Join-Path $evidenceFixture 'runtime-corrupt-archive'
foreach($directory in @($compiled,$buildRun,$runtimeRun,$changedRun,$corruptRun)){[void][IO.Directory]::CreateDirectory($directory)}
foreach($suffix in @('.dll','.pdb','.deps.json','.runtimeconfig.json')) {
    [IO.File]::WriteAllText((Join-Path $compiled ('App'+$suffix)),('original fixture bytes '+$suffix))
}
$target=Join-Path $compiled 'App.dll'
$buildRows=@(& $outputHelper -TargetPath $target -Label build -RunDirectory $buildRun)
if(@($buildRows|Where-Object {$_.exists -and -not $_.reusedBuildArchive}).Count -ne 4){throw 'Build did not seal four original output files'}
$passed.Add('Build seals one actual copy of each of its four output files')
$buildEvidence=Join-Path $buildRun 'test-output-build.json'
foreach($label in @('before-tests','after-tests')) {
    $runtimeRows=@(& $outputHelper -TargetPath $target -Label $label -RunDirectory $runtimeRun -ReuseEvidencePath $buildEvidence)
    if(@($runtimeRows|Where-Object {$_.exists -and $_.reusedBuildArchive}).Count -ne 4 -or [IO.Directory]::Exists((Join-Path $runtimeRun ('app-test-output-'+$label)))){throw 'Unchanged runtime outputs were copied again'}
    foreach($row in $runtimeRows){if($row.archive -ine @($buildRows|Where-Object path -eq $row.path)[0].archive){throw 'Runtime proof lost its original Build archive'}}
}
$passed.Add('Both runtime checks hash and reference the original Build files without any duplicate output directory')
[IO.File]::WriteAllText((Join-Path $compiled 'App.pdb'),'changed fixture PDB bytes')
$changedRows=@(& $outputHelper -TargetPath $target -Label after-tests -RunDirectory $changedRun -ReuseEvidencePath $buildEvidence)
$changed=@($changedRows|Where-Object {-not $_.reusedBuildArchive})
if($changed.Count -ne 1 -or [IO.Path]::GetExtension($changed[0].path) -ne '.pdb' -or
    (Get-Content -LiteralPath $changed[0].archive -Raw) -cne 'changed fixture PDB bytes' -or
    @(Get-ChildItem -LiteralPath (Join-Path $changedRun 'app-test-output-after-tests') -File).Count -ne 1){throw 'Changed output evidence retained too much or lost the anomalous bytes'}
$passed.Add('A changed output preserves only its own anomalous bytes; unchanged outputs still reuse Build')
$originalDll=@($buildRows|Where-Object {$_.path -eq $target})[0].archive
$originalBytes=[IO.File]::ReadAllBytes($originalDll)
[IO.File]::WriteAllText($originalDll,'corrupted archive')
$rejected=$false
try {$null=& $outputHelper -TargetPath $target -Label before-tests -RunDirectory $corruptRun -ReuseEvidencePath $buildEvidence} catch {$rejected=$true} finally {[IO.File]::WriteAllBytes($originalDll,$originalBytes)}
if(-not $rejected -or [IO.Directory]::Exists((Join-Path $corruptRun 'app-test-output-before-tests'))){throw 'Corrupt Build archive was silently replaced'}
$passed.Add('A corrupt original Build archive fails rather than being hidden by another runtime copy')
# Exercise the actual canonical SharedWorkspace entry with synthetic registered
# roots only. No real repository/companion roots enter this fixture policy.
$closureFixture=Join-Path $out ('canonical-shared-'+$PSVersionTable.PSEdition)
$taskCatalog=Join-Path $closureFixture 'tasks'
$workspaceCatalog=Join-Path $closureFixture 'workspaces'
$consumerCatalog=Join-Path $closureFixture 'consumers'
foreach($directory in @($taskCatalog,$workspaceCatalog,$consumerCatalog)){[void][IO.Directory]::CreateDirectory($directory)}
$shared=Join-Path $workspaceCatalog 'shared'
$sharedBatches=Join-Path $shared '.validation/batches'
$closureTask=Join-Path $taskCatalog 'accepted-task'
$rejectedTask=Join-Path $taskCatalog 'foreign-mapping-task'
$closurePolicy=[pscustomobject]@{schemaVersion=1;enabled=$true;referenceRoots=@();defaults=@{maxRecentCompleted=0;maxBytes=8388608;maxNodes=10000;maxMilliseconds=15000};roots=@(@{id='fixture-tasks';path=$taskCatalog;excludedNames=@()},@{id='fixture-workspaces';path=$workspaceCatalog;excludedNames=@()},@{id='fixture-consumers';path=$consumerCatalog;excludedNames=@()},@{id='fixture-shared-batches';path=$sharedBatches;excludedNames=@()})}
$closurePolicyPath=Join-Path $closureFixture 'policy.json'
$closurePolicy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $closurePolicyPath -Encoding utf8
$null=New-GeneratedWorkspace -RootPath $workspaceCatalog -WorkspacePath $shared -Purpose 'isolated synthetic shared workspace' -Policy $closurePolicy
[void][IO.Directory]::CreateDirectory($sharedBatches)
$closureRoots=@($taskCatalog,$workspaceCatalog,$consumerCatalog,$sharedBatches)
$null=New-GeneratedWorkspace -RootPath $taskCatalog -WorkspacePath $closureTask -Purpose 'isolated accepted task' -Policy $closurePolicy -ReferenceRoots $closureRoots
$null=New-GeneratedWorkspace -RootPath $taskCatalog -WorkspacePath $rejectedTask -Purpose 'isolated rejected mapping task' -Policy $closurePolicy -ReferenceRoots $closureRoots
$consumer=Join-Path $consumerCatalog 'current-consumer'
$null=New-GeneratedWorkspace -RootPath $consumerCatalog -WorkspacePath $consumer -Purpose 'current consumer keeps accepted task' -References @($closureTask) -Policy $closurePolicy -ReferenceRoots $closureRoots
$warmPaths=@((Join-Path $sharedBatches 'shared-cache-one'),(Join-Path $sharedBatches 'shared-cache-two'))
foreach($warm in $warmPaths){$null=New-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $warm -Purpose 'synthetic shared warm' -Policy $closurePolicy -ReferenceRoots $closureRoots}
$acceptedBatch=Join-Path $sharedBatches 'owned-accepted'
$obsoleteBatch=Join-Path $sharedBatches 'owned-obsolete'
$legacyBatch=Join-Path $sharedBatches 'owned-legacy-completed'
foreach($batch in @($acceptedBatch,$obsoleteBatch,$legacyBatch)){
    $null=New-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $batch -Purpose 'synthetic owned stage' -References @($closureTask) -Policy $closurePolicy -ReferenceRoots $closureRoots
    [void][IO.Directory]::CreateDirectory((Join-Path $batch 'scratch'))
}
$acceptedOutput=Join-Path $acceptedBatch 'scratch/result.bin'
[IO.File]::WriteAllText($acceptedOutput,'accepted synthetic output')
$null=Set-GeneratedWorkspaceReferences -RootPath $taskCatalog -WorkspacePath $closureTask -References @($legacyBatch) -Policy $closurePolicy -ReferenceRoots $closureRoots
function Write-SyntheticRun([string]$Task,[string]$Batch,[string]$ManifestTask) {
    $run=Join-Path $Task ('runs/'+[IO.Path]::GetFileName($Batch))
    $job=Join-Path $run 'job'
    [void][IO.Directory]::CreateDirectory($job)
    @{allowedReceiptRoot=$run;runDirectory=$job;environmentVariables=@{RM_REVIEW_SCRATCH=(Join-Path $Batch 'scratch')};arguments=@('-TaskRoot',$ManifestTask,'-RunDirectory',$run)}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $run 'job-manifest.json') -Encoding utf8
    @{accepted=$true;exitCode=0;normalDrain=$true;runDirectory=$run;workset=(Join-Path $Batch 'scratch')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'bounded-result.json') -Encoding utf8
    $events=@(@{kind='final-job';data=@{empty=$true;reason='normal'}},@{kind='exit';data=@{reason='normal'}},@{kind='owned-job-closed';data=@{assigned=$true;killOnCloseConfigured=$true;reason='normal'}})
    $events|ForEach-Object {$_|ConvertTo-Json -Compress}|Set-Content -LiteralPath (Join-Path $job 'receipts.jsonl') -Encoding utf8
}
Write-SyntheticRun $closureTask $acceptedBatch $closureTask
Write-SyntheticRun $closureTask $obsoleteBatch $closureTask
Write-SyntheticRun $closureTask $legacyBatch $closureTask
# Reproduce the old registration-only terminal record with a reverse task edge.
$legacyPolicy=($closurePolicy|ConvertTo-Json -Depth 8)|ConvertFrom-Json
$legacyPolicy.enabled=$false
$null=Complete-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $legacyBatch -Outcome success -References @($closureTask) -Policy $legacyPolicy -ReferenceRoots $closureRoots
Write-SyntheticRun $rejectedTask $warmPaths[1] (Join-Path $taskCatalog 'another-owner')
foreach($task in @($closureTask,$rejectedTask)) {
    $closurePolicy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $task 'retention-policy.json') -Encoding utf8
    @{artifacts=$warmPaths[0]}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $task 'build-context.json') -Encoding utf8
    $proof=Join-Path $task 'scope-proof.json'
    @{accepted=$true;syntheticFixture=$true}|ConvertTo-Json|Set-Content -LiteralPath $proof -Encoding utf8
    @{scopeAccepted=$true;scope='synthetic shared closure only';stages=@(@{receipt=$proof;sha256=(Get-FileHash -LiteralPath $proof).Hash});packages=@();consumerDependencies=@();retainedOutputs=@($acceptedOutput)}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $task 'acceptance.json') -Encoding utf8
}
$canonical=Join-Path $repo 'scripts/validation/ProductSdk/Complete-Validation.ps1'
$rejected=$false
try {$null=& $canonical -TaskRoot $rejectedTask -WorkspaceRoot $shared -SharedWorkspace -ProjectPolicyPath $closurePolicyPath} catch {$rejected=$true}
if(-not $rejected -or (Get-Content -LiteralPath (Join-Path $warmPaths[1] '.generated-workspace.json') -Raw|ConvertFrom-Json).state -cne 'running' -or
    (Get-Content -LiteralPath (Join-Path $acceptedBatch '.generated-workspace.json') -Raw|ConvertFrom-Json).state -cne 'running'){throw 'A foreign Job mapping closed a shared stage'}
$passed.Add('Canonical closure rejects foreign Job-to-batch ownership before any lifecycle mutation')
$parentHash=(Get-FileHash -LiteralPath (Join-Path $shared '.generated-workspace.json')).Hash
$warmHashes=@($warmPaths|ForEach-Object {(Get-FileHash -LiteralPath (Join-Path $_ '.generated-workspace.json')).Hash})
$oldReferences=@((Get-Content -LiteralPath (Join-Path $closureTask '.generated-workspace.json') -Raw|ConvertFrom-Json).references)
if($acceptedOutput -in $oldReferences -or $acceptedBatch -in $oldReferences){throw 'Fixture wrongly pre-protects the newly accepted output'}
$closure=(& $canonical -TaskRoot $closureTask -WorkspaceRoot $shared -SharedWorkspace -ProjectPolicyPath $closurePolicyPath)|ConvertFrom-Json
if($acceptedBatch -notin @($closure.acceptedReferenceRegistration.workspace.references) -or -not [IO.File]::Exists($acceptedOutput)){throw 'Canonical closure did not register new accepted dependencies before child retention'}
$passed.Add('Canonical closure protects newly accepted output before child pruning, with no old task reference to that output')
$acceptedState=Get-Content -LiteralPath (Join-Path $acceptedBatch '.generated-workspace.json') -Raw|ConvertFrom-Json
if(-not $closure.sharedWorkspace -or $acceptedState.state -cne 'completed' -or @($acceptedState.references).Count -ne 0 -or
    (Get-FileHash -LiteralPath (Join-Path $shared '.generated-workspace.json')).Hash -cne $parentHash){throw 'Shared closure modified the parent or retained a reverse task edge'}
for($i=0;$i -lt $warmPaths.Count;$i++) {
    if((Get-FileHash -LiteralPath (Join-Path $warmPaths[$i] '.generated-workspace.json')).Hash -cne $warmHashes[$i]){throw 'Shared closure changed a warm owner'}
}
$passed.Add('Canonical SharedWorkspace leaves the parent and both unrelated warm owners running and unchanged')
if([IO.Directory]::Exists($obsoleteBatch) -or -not [IO.File]::Exists($acceptedOutput) -or
    (Get-Content -LiteralPath (Join-Path $closureTask '.generated-workspace.json') -Raw|ConvertFrom-Json).state -cne 'completed' -or -not $closure.batchRetention.enabled){throw 'Canonical enabled retention did not retire unconsumed owned output while retaining the accepted output'}
$passed.Add('Completed child has no task back-reference; enabled canonical retention removes obsolete output and preserves the current consumer output')
if($closure.closedAttempts -lt 2 -or [IO.Directory]::Exists($legacyBatch)){throw 'A legacy completed batch retained its historical reverse edge'}
$passed.Add('Canonical closure retires a legacy completed child and its historical reverse edge')


# Run the actual Invoke finally body, selected structurally from its AST, against
# synthetic registered roots. No preflight, controller or external Job is run.
# This covers the changed lifecycle seam; it is not a full Invoke execution.
$invokePath=Join-Path $repo 'scripts/validation/ProductSdk/Invoke-Validation.ps1'
$parseTokens=$null;$parseErrors=$null
$invokeAst=[Management.Automation.Language.Parser]::ParseFile($invokePath,[ref]$parseTokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Invoke source does not parse'}
$executionTry=@($invokeAst.FindAll({param($node) $node -is [Management.Automation.Language.TryStatementAst] -and $node.Body.Extent.Text.Contains('$taskPreflight=&')},$true))
if($executionTry.Count -ne 1){throw 'Expected one original Invoke execution scope'}
$finallyText=$executionTry[0].Finally.Extent.Text
$actualFinally=[scriptblock]::Create($finallyText.Substring(1,$finallyText.Length-2))
$noJobPolicy=($closurePolicy|ConvertTo-Json -Depth 8)|ConvertFrom-Json
$noJobPolicy.defaults.maxRecentCompleted=3
$noJobPolicyPath=Join-Path $closureFixture 'no-job-policy.json'
$noJobPolicy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $noJobPolicyPath -Encoding utf8
foreach($case in @('preflight','input-failure','declared','workset-persistence-failure','completion-failure','success')) {
    $taskId='no-job-'+$case
    $taskExpectedBatch=Join-Path $sharedBatches $taskId
    $taskRun=Join-Path $rejectedTask ('runs/'+$taskId)
    [void][IO.Directory]::CreateDirectory($taskRun)
    $taskWorkset=(& $script -Mode Prepare -WorkspaceRoot $shared -BatchId $taskId -ConsumerId rm-product-sdk -ExpectedGrowthGiB 0 -ReserveFreeGiB 0 -RetentionPolicyPath $noJobPolicyPath -ReferenceRoots $closureRoots)|ConvertFrom-Json
    $taskRepo=$repo;$taskWorkspace=$shared;$taskRetention=[pscustomobject]@{referenceRoots=$closureRoots}
    $taskPolicy=if($case -eq 'completion-failure'){Join-Path $closureFixture 'missing-policy.json'}else{$noJobPolicyPath}
    $Stage='Build';$taskTestProject='synthetic';$TestCompileManifest='';$taskPartitionHash=$null;$NativeContextPath='';$taskRuntimeSelection=$null
    $taskCode=if($case -eq 'success'){0}else{1};$taskNormal=$case -eq 'success';$taskSame=$case -eq 'success'
    $taskWorksetRecorded=$case -ne 'workset-persistence-failure';$taskJobDeclared=$case -eq 'declared'
    if($taskWorksetRecorded){$taskWorkset|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $taskRun 'workset.json') -Encoding utf8}
    $taskFailure=if($case -eq 'success'){$null}else{[Management.Automation.ErrorRecord]::new([InvalidOperationException]::new(('Injected '+$case+' refusal')),'SyntheticPreJobFailure',[Management.Automation.ErrorCategory]::OperationStopped,$null)}
    $caught=$null
    try {$null=& $actualFinally} catch {$caught=$_}
    $leaf=Get-Content -LiteralPath (Join-Path $taskExpectedBatch '.generated-workspace.json') -Raw|ConvertFrom-Json
    $receipt=Get-Content -LiteralPath (Join-Path $taskRun 'bounded-result.json') -Raw|ConvertFrom-Json
    if($case -in @('preflight','input-failure')) {
        if($caught -or $leaf.state -cne 'completed' -or $leaf.outcome -cne 'failed' -or @($leaf.references).Count){throw 'Exact unstarted leaf did not complete failed'}
    } elseif($case -eq 'completion-failure') {
        if($leaf.state -cne 'running' -or $null -eq $caught -or $caught.Exception -isnot [AggregateException] -or $caught.Exception.InnerExceptions.Count -ne 2 -or $caught.Exception.InnerExceptions[0].Message -cne 'Injected completion-failure refusal'){throw 'Completion failure lost its original exception or silently concluded'}
    } elseif($caught -or $leaf.state -cne 'running'){throw 'A declared/possibly-started, unrecorded-workset or reusable success was closed by the unstarted path'}
    if($case -eq 'workset-persistence-failure' -and [IO.File]::Exists((Join-Path $taskRun 'workset.json'))){throw 'Fixture unexpectedly persisted the missing workset evidence'}
    if($case -ne 'success' -and ($receipt.accepted -or $receipt.normalDrain -or $receipt.exitCode -ne 1 -or $receipt.sourceInputsUnchangedExceptDeclaredPartitionRefresh)){throw 'Failure became a success receipt'}
    if($leaf.state -eq 'running'){$null=Complete-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $taskExpectedBatch -Outcome failed -References @() -Policy $noJobPolicy -ReferenceRoots $closureRoots}
    $passed.Add('Actual Invoke finalizer no-Job seam: '+$case)
}
# Probe only existing terminal metadata. Each bad case must reject before any
# lifecycle change; valid failed/no-Job must work while recent retention keeps it.
foreach($case in @('running','success-marker','nonempty-references','wrong-workset','accepted-result','declared-manifest','controller-log','job-directory','valid-failed')) {
    $guardTask=Join-Path $taskCatalog ('unstarted-'+$case);$guardBatch=Join-Path $sharedBatches ('unstarted-'+$case)
    $null=New-GeneratedWorkspace -RootPath $taskCatalog -WorkspacePath $guardTask -Purpose 'isolated unstarted guard task' -Policy $noJobPolicy -ReferenceRoots $closureRoots
    $null=New-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $guardBatch -Purpose 'isolated unstarted leaf' -Policy $noJobPolicy -ReferenceRoots $closureRoots
    $guardRun=Join-Path $guardTask ('runs/'+[IO.Path]::GetFileName($guardBatch));[void][IO.Directory]::CreateDirectory($guardRun)
    $scratch=Join-Path $guardBatch 'scratch';[void][IO.Directory]::CreateDirectory($scratch)
    @{Mode='Prepare';WorkspaceRoot=$(if($case -eq 'wrong-workset'){$closureFixture}else{$shared});BatchId=[IO.Path]::GetFileName($guardBatch);ConsumerId='rm-product-sdk';IsolatedScratchRoot=$scratch}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $guardRun 'workset.json') -Encoding utf8
    @{accepted=($case -eq 'accepted-result');exitCode=1;normalDrain=$false;sourceInputsUnchangedExceptDeclaredPartitionRefresh=$false;runDirectory=$guardRun;workset=$scratch}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $guardRun 'bounded-result.json') -Encoding utf8
    if($case -eq 'declared-manifest'){[IO.File]::WriteAllText((Join-Path $guardRun 'job-manifest.json'),'{}')}
    if($case -eq 'controller-log'){[IO.File]::WriteAllText((Join-Path $guardRun 'controller.log'),'possible controller')}
    if($case -eq 'job-directory'){[void][IO.Directory]::CreateDirectory((Join-Path $guardRun 'job'))}
    if($case -ne 'running') {
        $outcome=if($case -eq 'success-marker'){'success'}else{'failed'}
        $refs=if($case -eq 'nonempty-references'){@($warmPaths[0])}else{@()}
        $null=Complete-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $guardBatch -Outcome $outcome -References $refs -Policy $noJobPolicy -ReferenceRoots $closureRoots
    }
    $noJobPolicy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $guardTask 'retention-policy.json') -Encoding utf8
    @{artifacts=$warmPaths[0]}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $guardTask 'build-context.json') -Encoding utf8
    @{scopeAccepted=$true;scope='isolated no-Job leaf handling';stages=@();packages=@();consumerDependencies=@();retainedOutputs=@()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $guardTask 'acceptance.json') -Encoding utf8
    $beforeGuard=(Get-FileHash -LiteralPath (Join-Path $guardBatch '.generated-workspace.json')).Hash
    $guardFailure=$null
    try {$null=& $canonical -TaskRoot $guardTask -WorkspaceRoot $shared -SharedWorkspace -ProjectPolicyPath $noJobPolicyPath} catch {$guardFailure=$_}
    if($case -eq 'valid-failed') {
        if($guardFailure -or (Get-Content -LiteralPath (Join-Path $guardTask '.generated-workspace.json') -Raw|ConvertFrom-Json).state -cne 'completed'){throw 'Canonical rejected a retained failed no-Job leaf'}
    } else {
        if($null -eq $guardFailure -or (Get-FileHash -LiteralPath (Join-Path $guardBatch '.generated-workspace.json')).Hash -cne $beforeGuard){throw ('Invalid no-Job attempt accepted/mutated: '+$case)}
        $null=Complete-GeneratedWorkspace -RootPath $taskCatalog -WorkspacePath $guardTask -Outcome failed -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots
    }
    if([IO.Directory]::Exists($guardBatch)) {
        $state=Get-Content -LiteralPath (Join-Path $guardBatch '.generated-workspace.json') -Raw|ConvertFrom-Json
        if($state.state -eq 'running'){$null=Complete-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $guardBatch -Outcome failed -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots}
    }
    $passed.Add('Canonical retained no-Job guard: '+$case)
}
$null=Invoke-GeneratedWorkspaceRetention -RootPath $taskCatalog -Policy $closurePolicy -ReferenceRoots $closureRoots -Apply
$null=Invoke-GeneratedWorkspaceRetention -RootPath $sharedBatches -Policy $closurePolicy -ReferenceRoots $closureRoots -Apply


# Bounded same-root batch completion, using the real public API and isolated
# roots. The previous no-Job15 boundary cases remain unchanged above.
$batchLeaves=@()
foreach($id in @('batch-complete-one','batch-complete-two')){
    $leaf=Join-Path $sharedBatches $id
    $null=New-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $leaf -Purpose 'isolated batch completion' -Policy $noJobPolicy -ReferenceRoots $closureRoots
    $batchLeaves+=$leaf
}
$leafHashes=@($batchLeaves|ForEach-Object{(Get-FileHash -LiteralPath (Join-Path $_ '.generated-workspace.json')).Hash})
foreach($bad in @('duplicate','foreign-root','bad-outcome','bad-reference','disabled-policy','bad-policy-limits')) {
    $items=@([pscustomobject]@{WorkspacePath=$batchLeaves[0];Outcome='success';References=@()},[pscustomobject]@{WorkspacePath=$batchLeaves[1];Outcome='failed';References=@()})
    $selectedPolicy=$noJobPolicy
    switch($bad){
        'duplicate' {$items[1].WorkspacePath=$batchLeaves[0]}
        'foreign-root' {$items[1].WorkspacePath=$rejectedTask}
        'bad-outcome' {$items[1].Outcome='maybe'}
        'bad-reference' {$items[1].References=@('relative/input')}
        'disabled-policy' {$selectedPolicy=($noJobPolicy|ConvertTo-Json -Depth 8)|ConvertFrom-Json;$selectedPolicy.enabled=$false}
        'bad-policy-limits' {$selectedPolicy=($noJobPolicy|ConvertTo-Json -Depth 8)|ConvertFrom-Json;$selectedPolicy.defaults.maxNodes=0;$selectedPolicy.defaults.maxMilliseconds=0}
    }
    $refused=$false
    try {$null=Complete-GeneratedWorkspaceBatch -RootPath $sharedBatches -Completions $items -Policy $selectedPolicy -ReferenceRoots $closureRoots} catch {$refused=$true}
    if(-not $refused){throw ('Invalid batch completion accepted: '+$bad)}
    for($i=0;$i -lt $batchLeaves.Count;$i++){if((Get-FileHash -LiteralPath (Join-Path $batchLeaves[$i] '.generated-workspace.json')).Hash -cne $leafHashes[$i]){throw 'Invalid batch wrote a preceding terminal state'}}
    $passed.Add('Batch Complete prechecks all requests before writes: '+$bad)
}
$items=@([pscustomobject]@{WorkspacePath=$batchLeaves[0];Outcome='success';References=@()},[pscustomobject]@{WorkspacePath=$batchLeaves[1];Outcome='failed';References=@()})
$batchResult=Complete-GeneratedWorkspaceBatch -RootPath $sharedBatches -Completions $items -Policy $noJobPolicy -ReferenceRoots $closureRoots
if(-not $batchResult.allCompleted -or $batchResult.completed.Count -ne 2 -or -not $batchResult.retention.enabled){throw 'Mixed batch completion did not perform enabled retention'}
foreach($row in $batchResult.completed){if($row.state -cne 'completed' -or @($row.references).Count){throw 'Batch terminal state lost exact empty References'}}
$passed.Add('Batch Complete writes mixed real outcomes and runs enabled finite retention')
# Prove registered state preflight, even when the invalid entry follows a valid
# running one. No completed path is silently re-completed by the batch API.
$remaining=Join-Path $sharedBatches 'batch-still-running'
$null=New-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $remaining -Purpose 'isolated running state guard' -Policy $noJobPolicy -ReferenceRoots $closureRoots
$beforeRemaining=(Get-FileHash -LiteralPath (Join-Path $remaining '.generated-workspace.json')).Hash
$refused=$false
try {$null=Complete-GeneratedWorkspaceBatch -RootPath $sharedBatches -Completions @(@{WorkspacePath=$remaining;Outcome='success';References=@()},@{WorkspacePath=$batchLeaves[1];Outcome='failed';References=@()}) -Policy $noJobPolicy -ReferenceRoots $closureRoots} catch {$refused=$true}
if(-not $refused -or (Get-FileHash -LiteralPath (Join-Path $remaining '.generated-workspace.json')).Hash -cne $beforeRemaining){throw 'Invalid later registered state changed a prior running marker'}
$passed.Add('Batch Complete validates every registered running state before the first write')
$null=Complete-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $remaining -Outcome failed -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots


# Core failure/boundary gates use temporary module-script hooks, restored even
# on assertion failure. Writer faults occur BEFORE calling the real writer:
# these gates do not simulate or qualify an in-progress I/O-corrupted marker.
$coreRoot=Join-Path $closureFixture 'completion-core'
[void][IO.Directory]::CreateDirectory($coreRoot)
$corePolicy=[pscustomobject]@{schemaVersion=1;enabled=$true;referenceRoots=@();defaults=@{maxRecentCompleted=600;maxBytes=67108864;maxNodes=10000;maxMilliseconds=300000};roots=@(@{id='fixture-completion-core';path=$coreRoot;excludedNames=@()})}
$coreModule=Get-Module GeneratedWorkspaceRetention
$coreOriginals=& $coreModule {@((Get-Command Write-GWStateRecord).ScriptBlock,(Get-Command Invoke-GWRetentionAfterLifecycle).ScriptBlock)}
$coreLeaves=[Collections.Generic.List[string]]::new()
# Seed only small synthetic lifecycle markers under one original root lock.
# Private state/payload/writer routines are real; no after-lifecycle hook is
# bypassed on a public lifecycle call. This avoids512 separate setup sweeps.
function New-CompletionCoreLeaves([string]$Prefix,[int]$Count,[long[]]$AfterPayloadTargets=@()) {
    $paths=@(for($i=0;$i -lt $Count;$i++){Join-Path $coreRoot ($Prefix+'-'+$i.ToString('000'))})
    & $coreModule {
        param([string]$Root,[string[]]$Paths,[object]$Policy,[AllowNull()][long[]]$Targets)
        $config=Get-GWPolicyRoot -Policy $Policy -RootPath $Root
        $locks=Enter-GWRootLocks -RootPaths @($Root)
        try {
            for($i=0;$i -lt $Paths.Count;$i++) {
                $path=$Paths[$i]
                $null=Assert-GWManagedWorkspacePath -RootPath $Root -WorkspacePath $path -RootConfig $config
                if(Test-Path -LiteralPath $path){throw 'Core fixture refuses existing seed'}
                $state=New-GWStateRecord -RootPath $Root -WorkspacePath $path -RootId 'fixture-completion-core' -Purpose 'small synthetic completion gate' -State running -References @() -CompletedUtc $null -Outcome $null
                [void][IO.Directory]::CreateDirectory($path)
                & $script:CompletionGateWriter -State $state
                if($null -ne $Targets -and @($Targets).Count) {
                    $record=Get-GWCompletionRecord -RootPath $Root -WorkspacePath $path -Outcome success -References @() -ReplaceReferences $true
                    $actual=[Text.UTF8Encoding]::new($false).GetByteCount((ConvertTo-Json -InputObject $record.after -Depth 12)+[Environment]::NewLine)
                    $needed=[long]$Targets[$i]-$actual+$state.purpose.Length
                    if($needed -lt 1 -or $needed -gt 1048576){throw 'Payload target cannot be represented by this fixture'}
                    $state.purpose='x'*[int]$needed
                    & $script:CompletionGateWriter -State $state
                    $record=Get-GWCompletionRecord -RootPath $Root -WorkspacePath $path -Outcome success -References @() -ReplaceReferences $true
                    $actual=[Text.UTF8Encoding]::new($false).GetByteCount((ConvertTo-Json -InputObject $record.after -Depth 12)+[Environment]::NewLine)
                    if($actual -ne $Targets[$i]){throw 'Fixture did not construct the exact actual JSON payload size'}
                }
            }
        } finally {Exit-GWRootLocks -Streams $locks}
    } $coreRoot $paths $corePolicy $AfterPayloadTargets
    foreach($path in $paths){$coreLeaves.Add($path)}
    return $paths
}
& $coreModule {
    param($Writer,$Sweep)
    $script:CompletionGateWriter=$Writer;$script:CompletionGateSweep=$Sweep
    $script:CompletionGateWrites=0;$script:CompletionGateSweeps=0;$script:CompletionGateRealSweeps=0
    $script:CompletionGateFailWriter=0;$script:CompletionGateFailSweep=$false
    function script:Write-GWStateRecord {
        param([object]$State)
        $script:CompletionGateWrites++
        if($script:CompletionGateFailWriter -eq $script:CompletionGateWrites){throw 'Injected second-writer BEFORE real write'}
        & $script:CompletionGateWriter @PSBoundParameters
    }
    function script:Invoke-GWRetentionAfterLifecycle {
        param([string]$RootPath,[object]$Policy,[string[]]$ReferenceRoots)
        $script:CompletionGateSweeps++
        if(-not $Policy.enabled){throw 'Core gate unexpectedly disabled real retention'}
        if($script:CompletionGateFailSweep){throw 'Injected final retention failure'}
        $script:CompletionGateRealSweeps++
        & $script:CompletionGateSweep @PSBoundParameters
    }
} $coreOriginals[0] $coreOriginals[1]
function Invoke-CompletionCoreGate([object[]]$Items,[int]$FailWriter=0,[bool]$FailSweep=$false) {
    & $coreModule {
        param($WriterAt,$SweepFails)
        $script:CompletionGateWrites=0;$script:CompletionGateSweeps=0;$script:CompletionGateRealSweeps=0
        $script:CompletionGateFailWriter=$WriterAt;$script:CompletionGateFailSweep=$SweepFails
    } $FailWriter $FailSweep
    $value=$null;$failure=$null
    try {$value=Complete-GeneratedWorkspaceBatch -RootPath $coreRoot -Completions $Items -Policy $corePolicy -ReferenceRoots @($coreRoot)} catch {$failure=$_}
    $counts=& $coreModule {[pscustomobject]@{writes=$script:CompletionGateWrites;sweeps=$script:CompletionGateSweeps;realSweeps=$script:CompletionGateRealSweeps}}
    return [pscustomobject]@{value=$value;failure=$failure;counts=$counts}
}
try {
    foreach($case in @('second-writer','retention-only','both')) {
        $paths=@(New-CompletionCoreLeaves -Prefix $case -Count 3)
        $items=@($paths|ForEach-Object{[pscustomobject]@{WorkspacePath=$_;Outcome='success';References=@()}})
        $result=Invoke-CompletionCoreGate -Items $items -FailWriter $(if($case -eq 'retention-only'){0}else{2}) -FailSweep ($case -ne 'second-writer')
        if($null -eq $result.failure -or $result.failure.Exception -isnot [AggregateException]){throw 'Expected explicit batch failure'}
        $data=$result.failure.Exception.Data['completion'];if($null -eq $data){throw 'Failure omitted exact progress'}
        if($result.counts.sweeps -ne 1){throw 'Write-attempt path did not call after-lifecycle exactly once'}
        if($case -eq 'retention-only') {
            if(-not $data.allCompleted -or $data.completed.Count -ne 3 -or @($data.remainingWorkspaces).Count -ne 0 -or $result.counts.writes -ne 3 -or $result.counts.realSweeps -ne 0 -or $data.retentionFailureMessage -cne 'Injected final retention failure'){throw 'Retention failure misreported true terminal writes'}
        } else {
            if($data.allCompleted -or $data.completed.Count -ne 1 -or $data.completed[0].workspacePath -ine $paths[0] -or $data.failedWorkspace -ine $paths[1] -or $data.failedOriginalState.state -cne 'running' -or
                (@($data.remainingWorkspaces)-join '|') -ine (($paths[1],$paths[2])-join '|') -or $result.counts.writes -ne 2){throw 'Partial write progress is inaccurate'}
            if($case -eq 'second-writer' -and ($result.counts.realSweeps -ne 1 -or -not $data.retention.enabled)){throw 'Partial write did not really run enabled finite retention'}
            if($case -eq 'both' -and ($result.counts.realSweeps -ne 0 -or $result.failure.Exception.InnerExceptions.Count -ne 2 -or
                $result.failure.Exception.InnerExceptions[0].Message -cne 'Injected second-writer BEFORE real write' -or $result.failure.Exception.InnerExceptions[1].Message -cne 'Injected final retention failure')){throw 'Dual original errors were lost'}
        }
        for($i=0;$i -lt 3;$i++) {
            $state=Get-Content -LiteralPath (Join-Path $paths[$i] '.generated-workspace.json') -Raw|ConvertFrom-Json
            $wanted=if($case -eq 'retention-only' -or $i -eq 0){'completed'}else{'running'}
            if($state.state -cne $wanted){throw 'A written state rolled back or an unwritten later leaf changed'}
        }
        $passed.Add('Batch core failure and exact one-sweep accounting: '+$case)
    }
    $paths=@(New-CompletionCoreLeaves -Prefix count-limit -Count 512)
    $items=@($paths|ForEach-Object{[pscustomobject]@{WorkspacePath=$_;Outcome='success';References=@()}})
    $beforeFirst=(Get-FileHash -LiteralPath (Join-Path $paths[0] '.generated-workspace.json')).Hash
    $tooMany=@($items)+@([pscustomobject]@{WorkspacePath=(Join-Path $coreRoot 'request-513');Outcome='success';References=@()})
    $rejected=Invoke-CompletionCoreGate -Items $tooMany
    if($null -eq $rejected.failure -or $rejected.counts.writes -ne 0 -or $rejected.counts.sweeps -ne 0 -or
        (Get-FileHash -LiteralPath (Join-Path $paths[0] '.generated-workspace.json')).Hash -cne $beforeFirst){throw '513 requests escaped all-before-write bound'}
    $passed.Add('Batch core513 request bound rejects with zero writes and zero retention')
    $accepted=Invoke-CompletionCoreGate -Items $items
    if($accepted.failure -or $accepted.value.completed.Count -ne 512 -or $accepted.counts.writes -ne 512 -or $accepted.counts.sweeps -ne 1 -or $accepted.counts.realSweeps -ne 1){throw '512 valid requests did not share exactly one real sweep'}
    foreach($path in $paths){if((Get-Content -LiteralPath (Join-Path $path '.generated-workspace.json') -Raw|ConvertFrom-Json).state -cne 'completed'){throw '512 boundary omitted a terminal write'}}
    $passed.Add('Batch core512 real small markers complete with exactly one real enabled sweep')
    # Construct real serialized bytes: every individual marker is valid. Nine
    # requested after-states total exactly8MiB+1, then exactly8MiB after one byte
    # is removed from the final running state's purpose through the real writer.
    $targets=[long[]]@((1..7|ForEach-Object{1048576})+@(1044480,4097))
    $paths=@(New-CompletionCoreLeaves -Prefix payload-total -Count 9 -AfterPayloadTargets $targets)
    $items=@($paths|ForEach-Object{[pscustomobject]@{WorkspacePath=$_;Outcome='success';References=@()}})
    $beforeFirst=(Get-FileHash -LiteralPath (Join-Path $paths[0] '.generated-workspace.json')).Hash
    $rejected=Invoke-CompletionCoreGate -Items $items
    if($null -eq $rejected.failure -or $rejected.failure.Exception.Message -notmatch '8 MiB' -or $rejected.counts.writes -ne 0 -or $rejected.counts.sweeps -ne 0 -or
        (Get-FileHash -LiteralPath (Join-Path $paths[0] '.generated-workspace.json')).Hash -cne $beforeFirst){throw '8MiB+1 aggregate precheck wrote or swept'}
    $passed.Add('Batch actual marker payloads totaling8MiB+1 reject before writes or sweep')
    & $coreModule {
        param($Root,$Path)
        $locks=Enter-GWRootLocks -RootPaths @($Root)
        try {$record=Read-GWStateRecord -RootPath $Root -WorkspacePath $Path;$record.State.purpose=$record.State.purpose.Substring(1);& $script:CompletionGateWriter -State $record.State}
        finally {Exit-GWRootLocks -Streams $locks}
    } $coreRoot $paths[8]
    $accepted=Invoke-CompletionCoreGate -Items $items
    if($accepted.failure -or $accepted.value.completed.Count -ne 9 -or $accepted.counts.writes -ne 9 -or $accepted.counts.sweeps -ne 1 -or $accepted.counts.realSweeps -ne 1){throw 'Exactly8MiB actual payloads did not complete with one real sweep'}
    $passed.Add('Batch actual marker payloads totaling exactly8MiB complete with one real sweep')
    $paths=@(New-CompletionCoreLeaves -Prefix single-payload -Count 1 -AfterPayloadTargets @([long]1048577))
    $items=@([pscustomobject]@{WorkspacePath=$paths[0];Outcome='success';References=@()})
    $beforeFirst=(Get-FileHash -LiteralPath (Join-Path $paths[0] '.generated-workspace.json')).Hash
    $rejected=Invoke-CompletionCoreGate -Items $items
    if($null -eq $rejected.failure -or $rejected.failure.Exception.Message -notmatch '1 MiB' -or $rejected.counts.writes -ne 0 -or $rejected.counts.sweeps -ne 0 -or
        (Get-FileHash -LiteralPath (Join-Path $paths[0] '.generated-workspace.json')).Hash -cne $beforeFirst){throw '1MiB+1 single marker escaped original payload bound'}
    $passed.Add('Original actual single-marker1MiB+1 bound rejects before writes or sweep')
    & $coreModule {
        param($Root,$Path)
        $locks=Enter-GWRootLocks -RootPaths @($Root)
        try {$record=Read-GWStateRecord -RootPath $Root -WorkspacePath $Path;$record.State.purpose=$record.State.purpose.Substring(1);& $script:CompletionGateWriter -State $record.State}
        finally {Exit-GWRootLocks -Streams $locks}
    } $coreRoot $paths[0]
    $accepted=Invoke-CompletionCoreGate -Items $items
    if($accepted.failure -or $accepted.counts.writes -ne 1 -or $accepted.counts.realSweeps -ne 1 -or $accepted.counts.sweeps -ne 1){throw 'Original exact1MiB marker did not write and sweep once'}
    $passed.Add('Original actual single-marker exactly1MiB completes and sweeps once')
}
finally {
    & $coreModule {
        param($Writer,$Sweep)
        Set-Item -LiteralPath Function:script:Write-GWStateRecord -Value $Writer
        Set-Item -LiteralPath Function:script:Invoke-GWRetentionAfterLifecycle -Value $Sweep
        foreach($name in @('CompletionGateWriter','CompletionGateSweep','CompletionGateWrites','CompletionGateSweeps','CompletionGateRealSweeps','CompletionGateFailWriter','CompletionGateFailSweep')){Remove-Variable -Name $name -Scope Script}
    } $coreOriginals[0] $coreOriginals[1]
}
# Official cleanup only after restoring both real functions; never mutate
# unrelated roots or delete fixture directories with a custom traversal.
$unfinished=@(foreach($path in $coreLeaves){
    $state=Get-GeneratedWorkspaceState -RootPath $coreRoot -WorkspacePath $path
    if($state.state -eq 'running'){[pscustomobject]@{WorkspacePath=$path;Outcome='failed';References=@()}}
})
$coreCleanup=($corePolicy|ConvertTo-Json -Depth 8)|ConvertFrom-Json
$coreCleanup.defaults.maxRecentCompleted=0
if($unfinished.Count){$null=Complete-GeneratedWorkspaceBatch -RootPath $coreRoot -Completions $unfinished -Policy $coreCleanup -ReferenceRoots @($coreRoot)}
$null=Invoke-GeneratedWorkspaceRetention -RootPath $coreRoot -Policy $coreCleanup -ReferenceRoots @($coreRoot) -Apply

# Conclude only these known synthetic fixtures through official lifecycle APIs.
$null=Complete-GeneratedWorkspace -RootPath $taskCatalog -WorkspacePath $rejectedTask -Outcome failed -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots
$null=Complete-GeneratedWorkspace -RootPath $consumerCatalog -WorkspacePath $consumer -Outcome success -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots
$null=Invoke-GeneratedWorkspaceRetention -RootPath $taskCatalog -Policy $closurePolicy -ReferenceRoots $closureRoots -Apply
foreach($warm in $warmPaths){$null=Complete-GeneratedWorkspace -RootPath $sharedBatches -WorkspacePath $warm -Outcome success -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots}
$null=Invoke-GeneratedWorkspaceRetention -RootPath $sharedBatches -Policy $closurePolicy -ReferenceRoots $closureRoots -Apply
$null=Complete-GeneratedWorkspace -RootPath $workspaceCatalog -WorkspacePath $shared -Outcome success -References @() -Policy $closurePolicy -ReferenceRoots $closureRoots
# Nested batch locks are now unnecessary: all their synthetic owners are gone.
$null=Invoke-GeneratedWorkspaceRetention -RootPath $workspaceCatalog -Policy $closurePolicy -ReferenceRoots @($taskCatalog,$consumerCatalog) -Apply
$result=[pscustomobject]@{passed=$passed.Count;cases=@($passed);actualProjectWorksetsTouched=$false;powerShellVersion=[string]$PSVersionTable.PSVersion}
$resultJson=$result|ConvertTo-Json -Depth 6
$resultJson|Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding utf8
$resultJson|Set-Content -LiteralPath (Join-Path $out ('result-'+$PSVersionTable.PSEdition+'.json')) -Encoding utf8
Get-Content -LiteralPath (Join-Path $out 'result.json')
