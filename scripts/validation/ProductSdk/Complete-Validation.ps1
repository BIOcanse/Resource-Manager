[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$WorkspaceRoot,
    [switch]$SharedWorkspace,
    [switch]$KeepTaskRunning,
    [string]$ProjectPolicyPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($KeepTaskRunning -and -not $SharedWorkspace){throw 'KeepTaskRunning requires SharedWorkspace.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$taskPath=[IO.Path]::GetFullPath($TaskRoot)
$workspace=[IO.Path]::GetFullPath($WorkspaceRoot)
$batchRoot=Join-Path $workspace '.validation/batches'
$acceptance=Get-Content (Join-Path $taskPath 'acceptance.json') -Raw|ConvertFrom-Json
if(-not $acceptance.scopeAccepted){throw 'Scope not accepted'}
foreach($proof in $acceptance.stages){
    if((Get-FileHash -LiteralPath $proof.receipt).Hash -ine $proof.sha256){throw 'Accepted stage receipt changed'}
}
$taskState=Get-Content (Join-Path $taskPath '.generated-workspace.json') -Raw|ConvertFrom-Json
$workspaceMarker=Join-Path $workspace '.generated-workspace.json'
$workspaceState=Get-Content -LiteralPath $workspaceMarker -Raw|ConvertFrom-Json
if($taskState.state -cne 'running' -or $workspaceState.state -cne 'running'){throw 'Closure requires the task and its workspace to be running.'}
$workspaceMarkerHash=(Get-FileHash -LiteralPath $workspaceMarker).Hash
$build=Get-Content (Join-Path $taskPath 'build-context.json') -Raw|ConvertFrom-Json
$references=@([string]$build.artifacts)+@($acceptance.packages|ForEach-Object path)+@($acceptance.consumerDependencies|ForEach-Object deps)|Sort-Object -Unique
if($acceptance.PSObject.Properties['retainedOutputs']) {
    # Only current accepted output roots and actual consumer dependencies remain.
    $references=@($references)+@($acceptance.retainedOutputs)|Sort-Object -Unique
}
# Use current finite project limits and the complete registered reference roots.
$effectiveRetention=& (Join-Path $PSScriptRoot 'Get-ProductRetentionPolicy.ps1') -TaskRoot $taskPath -ProjectPolicyPath $ProjectPolicyPath
$policy=$effectiveRetention.policy
$registration=$effectiveRetention.path
$referenceRoots=$effectiveRetention.referenceRoots
$references=@(& (Join-Path $PSScriptRoot '../ConvertTo-ValidationLifecycleReferences.ps1') -References $references -Policy $policy -ReferenceRoots $referenceRoots)
Import-Module (Join-Path $repo 'scripts/maintenance/GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking
function Assert-JobArgument([object]$Manifest,[string]$Name,[string]$Expected) {
    $arguments=@($Manifest.arguments)
    $indices=@(for($i=0;$i -lt $arguments.Count;$i++){if([string]$arguments[$i] -ceq $Name){$i}})
    if($indices.Count -ne 1 -or $indices[0]+1 -ge $arguments.Count -or
        [IO.Path]::GetFullPath([string]$arguments[$indices[0]+1]) -ine $Expected){throw "Job ownership argument mismatch: $Name"}
}
# Verify every owned stage before any lifecycle change. A directory name alone
# never authorizes closing a shared warm batch or another task's result.
$plan=[Collections.Generic.List[object]]::new()
$runCount=0
foreach($run in [IO.Directory]::EnumerateDirectories((Join-Path $taskPath 'runs'))) {
    $runCount++
    if($runCount -gt 512){throw 'Run count exceeds the closure bound.'}
    $id=[IO.Path]::GetFileName($run)
    $batch=Join-Path $batchRoot $id
    if(-not [IO.Directory]::Exists($batch)){continue}
    $state=Get-Content -LiteralPath (Join-Path $batch '.generated-workspace.json') -Raw|ConvertFrom-Json
    if([IO.Path]::GetFullPath([string]$state.workspacePath) -ine $batch -or
        [IO.Path]::GetFullPath([string]$state.rootPath) -ine $batchRoot -or $state.state -cnotin @('running','completed')){throw "Batch ownership mismatch: $id"}
    $manifestPath=Join-Path $run 'job-manifest.json'
    $result=Get-Content -LiteralPath (Join-Path $run 'bounded-result.json') -Raw|ConvertFrom-Json
    if(-not [IO.File]::Exists($manifestPath)) {
        # The finite recent pool may retain this already failed, never-declared
        # leaf. Validate its existing records; do not invent Job drain evidence.
        $workset=Get-Content -LiteralPath (Join-Path $run 'workset.json') -Raw|ConvertFrom-Json
        if($state.state -cne 'completed' -or $state.outcome -cne 'failed' -or $state.keep -or @($state.references).Count -ne 0 -or
            $result.accepted -ne $false -or $result.exitCode -ne 1 -or $result.normalDrain -ne $false -or
            $result.sourceInputsUnchangedExceptDeclaredPartitionRefresh -ne $false -or
            [IO.Path]::GetFullPath([string]$result.runDirectory) -ine $run -or
            [IO.Path]::GetFullPath([string]$result.workset) -ine (Join-Path $batch 'scratch') -or
            $workset.Mode -cne 'Prepare' -or [string]$workset.BatchId -cne $id -or $workset.ConsumerId -cne 'rm-product-sdk' -or
            [IO.Path]::GetFullPath([string]$workset.WorkspaceRoot) -ine $workspace -or
            [IO.Path]::GetFullPath([string]$workset.IsolatedScratchRoot) -ine (Join-Path $batch 'scratch') -or
            (Test-Path -LiteralPath (Join-Path $run 'controller.log')) -or (Test-Path -LiteralPath (Join-Path $run 'job'))){throw "Missing declared Job evidence or invalid unstarted terminal attempt: $run"}
        $plan.Add([pscustomobject]@{id=$id;batch=$batch;outcome='failed'})
        continue
    }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    if([IO.Path]::GetFullPath([string]$manifest.allowedReceiptRoot) -ine $run -or
        [IO.Path]::GetFullPath([string]$manifest.runDirectory) -ine (Join-Path $run 'job') -or
        [IO.Path]::GetFullPath([string]$manifest.environmentVariables.RM_REVIEW_SCRATCH) -ine (Join-Path $batch 'scratch')){throw "Job-to-batch mapping mismatch: $id"}
    Assert-JobArgument $manifest '-TaskRoot' $taskPath
    Assert-JobArgument $manifest '-RunDirectory' $run
    if([IO.Path]::GetFullPath([string]$result.runDirectory) -ine $run -or
        [IO.Path]::GetFullPath([string]$result.workset) -ine (Join-Path $batch 'scratch')){throw "Result-to-batch mapping mismatch: $id"}
    $events=@(Get-Content -LiteralPath (Join-Path $manifest.runDirectory 'receipts.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
    $drain=@($events|Where-Object kind -eq 'final-job')
    $closed=@($events|Where-Object kind -eq 'owned-job-closed')
    $exit=@($events|Where-Object kind -eq 'exit')
    if($drain.Count -ne 1 -or -not $drain[0].data.empty -or $closed.Count -ne 1 -or
        -not $closed[0].data.assigned -or -not $closed[0].data.killOnCloseConfigured -or $exit.Count -ne 1 -or
        $closed[0].data.reason -ne $drain[0].data.reason -or $exit[0].data.reason -ne $drain[0].data.reason){throw "Job not empty/closed: $run"}
    if(($result.accepted -and ($result.exitCode -ne 0 -or -not $result.normalDrain)) -or
        (-not $result.normalDrain -and ($result.exitCode -eq 0 -or $drain[0].data.reason -eq 'normal'))){throw "Inconsistent terminal attempt: $run"}
    $plan.Add([pscustomobject]@{id=$id;batch=$batch;outcome=$(if($result.accepted){'success'}else{'failed'})})
}
# Establish the accepted dependency set while this task is still running and
# before any child completion/reference update can invoke enabled retention.
# A new accepted output need not have appeared in the task's previous marker.
$acceptedReferenceRegistration=Set-GeneratedWorkspaceReferences -RootPath ([string]$taskState.rootPath) -WorkspacePath $taskPath -References $references -Policy $policy -ReferenceRoots $referenceRoots
$closures=[Collections.Generic.List[object]]::new()
$runningCompletions=[Collections.Generic.List[object]]::new()
foreach($row in $plan) {
    if(-not [IO.Directory]::Exists($row.batch)){continue}
    $state=Get-Content -LiteralPath (Join-Path $row.batch '.generated-workspace.json') -Raw|ConvertFrom-Json
    if($state.state -ceq 'running') {
        $runningCompletions.Add([pscustomobject]@{WorkspacePath=$row.batch;Outcome=$row.outcome;References=@()})
    } elseif($state.state -ceq 'completed') {
        # Few existing completed leaves keep their ordinary References path.
        $closures.Add((Set-GeneratedWorkspaceReferences -RootPath $batchRoot -WorkspacePath $row.batch -References @() -Policy $policy -ReferenceRoots $referenceRoots))
    } else {throw 'Batch lifecycle changed during closure.'}
}
if($runningCompletions.Count) {
    try {
        $completedBatch=Complete-GeneratedWorkspaceBatch -RootPath $batchRoot -Completions $runningCompletions.ToArray() -Policy $policy -ReferenceRoots $referenceRoots
    } catch {
        $partial=$_.Exception.Data['completion']
        if($null -ne $partial){$partial|ConvertTo-Json -Depth 20|Set-Content (Join-Path $taskPath 'running-batch-closure.json') -Encoding utf8}
        throw
    }
    $completedBatch|ConvertTo-Json -Depth 20|Set-Content (Join-Path $taskPath 'running-batch-closure.json') -Encoding utf8
    foreach($state in $completedBatch.completed){$closures.Add([pscustomobject]@{workspace=$state})}
}
$closures|ConvertTo-Json -Depth 20|Set-Content (Join-Path $taskPath 'attempt-batch-closures.json') -Encoding utf8
if($SharedWorkspace) {
    if((Get-FileHash -LiteralPath $workspaceMarker).Hash -cne $workspaceMarkerHash){throw 'Shared workspace lifecycle changed during closure.'}
    $workspaceClosure=[pscustomobject]@{shared=$true;state='running';unchanged=$true}
} else {
    $global:LASTEXITCODE=0
    $raw=& (Join-Path $repo 'scripts/maintenance/Manage-GeneratedWorkspace.ps1') -Action Complete -RootId $workspaceState.rootId -WorkspacePath $workspace -Outcome success -References @() -PolicyPath $registration
    if($LASTEXITCODE -ne 0 -or -not $raw){throw 'Official workspace closure failed'}
    $workspaceClosure=$raw|ConvertFrom-Json
}
$workspaceClosure|ConvertTo-Json -Depth 20|Set-Content (Join-Path $taskPath 'workspace-closure.json') -Encoding utf8
if($KeepTaskRunning) {
    $currentTask=Get-Content -LiteralPath (Join-Path $taskPath '.generated-workspace.json') -Raw|ConvertFrom-Json
    if($currentTask.state -cne 'running' -or $currentTask.workspaceId -cne $taskState.workspaceId){throw 'Running task identity changed during leaf closure.'}
    $taskClosure=[pscustomobject]@{state='running';workspaceId=$currentTask.workspaceId;endedAttemptsOnly=$true}
} else {
    $global:LASTEXITCODE=0
    $raw=& (Join-Path $repo 'scripts/maintenance/Manage-GeneratedWorkspace.ps1') -Action Complete -RootId $taskState.rootId -WorkspacePath $taskPath -Outcome success -References $references -PolicyPath $registration
    if($LASTEXITCODE -ne 0 -or -not $raw){throw 'Official task closure failed'}
    $taskClosure=$raw|ConvertFrom-Json
}
# Zero-recent policies may legitimately reclaim the completed task. Do not
# recreate an unregistered directory just to store its own closure receipt.
if([IO.Directory]::Exists($taskPath)) {$taskClosure|ConvertTo-Json -Depth 20|Set-Content (Join-Path $taskPath 'task-closure.json') -Encoding utf8}
$batchRetention=if([IO.Directory]::Exists($batchRoot)) {
    Invoke-GeneratedWorkspaceRetention -RootPath $batchRoot -Policy $policy -ReferenceRoots $referenceRoots -Apply
} else {[pscustomobject]@{rootPath=$batchRoot;removedWithCompletedWorkspace=$true}}
if($SharedWorkspace -and (Get-FileHash -LiteralPath $workspaceMarker).Hash -cne $workspaceMarkerHash){throw 'Shared workspace lifecycle changed during final retention.'}
if([IO.Directory]::Exists($taskPath)) {$batchRetention|ConvertTo-Json -Depth 20|Set-Content (Join-Path $taskPath 'final-batch-retention.json') -Encoding utf8}
[pscustomobject]@{closedAttempts=$closures.Count;scope=$acceptance.scope;broaderBackendProductGoal='running';ledgerParent='unchanged';sharedWorkspace=[bool]$SharedWorkspace;acceptedReferenceRegistration=$acceptedReferenceRegistration;workspaceClosure=$workspaceClosure;taskClosure=$taskClosure;batchRetention=$batchRetention}|ConvertTo-Json -Depth 20
