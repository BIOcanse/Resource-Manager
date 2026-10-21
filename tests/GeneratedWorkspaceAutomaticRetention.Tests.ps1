[CmdletBinding()]
param([string]$WorkRoot='F:\code\Resource-Manager-Development\d-space-recovery')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$modulePath=Join-Path $repo 'scripts/maintenance/GeneratedWorkspaceRetention.psm1'
Import-Module $modulePath -Force -DisableNameChecking
$fixture=Join-Path $WorkRoot ('automatic-retention-fixture-'+[Guid]::NewGuid().ToString('N'))
$root=Join-Path $fixture 'tasks'
$other=Join-Path $fixture 'other'
$empty=Join-Path $fixture 'empty-pool'
$race=Join-Path $fixture 'race'
$roots=@($root,$other,$empty,$race)
foreach($path in $roots){[void][IO.Directory]::CreateDirectory($path)}
$policy=[pscustomobject]@{schemaVersion=1;enabled=$false;referenceRoots=@();defaults=[pscustomobject]@{maxRecentCompleted=1;maxBytes=8388608;maxNodes=500;maxMilliseconds=30000};roots=@([pscustomobject]@{id='tasks';path=$root;excludedNames=@()},[pscustomobject]@{id='other';path=$other;excludedNames=@()},[pscustomobject]@{id='empty';path=$empty;excludedNames=@()},[pscustomobject]@{id='race';path=$race;maxRecentCompleted=0;excludedNames=@()})}
$checks=[Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$checks.Add($Message)}
function Add-Task([string]$Root,[string]$Name,[switch]$Complete,[string[]]$References=@()){
 $path=Join-Path $Root $Name
 $null=New-GeneratedWorkspace -RootPath $Root -WorkspacePath $path -Purpose 'isolated automatic retention fixture' -References $References -Policy $policy -ReferenceRoots $roots
 [IO.File]::WriteAllText((Join-Path $path 'payload.bin'),'fixture')
 if($Complete){$null=Complete-GeneratedWorkspace -RootPath $Root -WorkspacePath $path -Outcome success -References $References -Policy $policy -ReferenceRoots $roots}
 return $path
}
function Fill-ProtectedTree([string]$Path){for($i=0;$i -lt 700;$i++){[IO.File]::WriteAllText((Join-Path $Path ('f'+$i+'.bin')),'x')}}
$unknown=Join-Path $root 'unknown'
[void][IO.Directory]::CreateDirectory($unknown)
Fill-ProtectedTree $unknown
$running=Add-Task $root 'active'
Fill-ProtectedTree $running
$keep=Add-Task $root 'kept' -Complete
$null=Set-GeneratedWorkspaceKeep -RootPath $root -WorkspacePath $keep -Keep $true -Policy $policy -ReferenceRoots $roots
Fill-ProtectedTree $keep
$protected=Add-Task $root 'foreign-reference' -Complete
Start-Sleep -Milliseconds 20
$old=Add-Task $root 'obsolete' -Complete
Start-Sleep -Milliseconds 20
$newest=Add-Task $root 'newest' -Complete
$owner=Add-Task $other 'current-owner' -References @((Join-Path $protected 'payload.bin'))
$unknownEmpty=Join-Path $empty 'unknown'
[void][IO.Directory]::CreateDirectory($unknownEmpty)
Fill-ProtectedTree $unknownEmpty
$raceTarget=Add-Task $race 'was-completed' -Complete
$policy.enabled=$true
$inspect=Invoke-GeneratedWorkspaceRetention -RootPath $root -Policy $policy -ReferenceRoots $roots
Assert-Check (-not $inspect.safeToPrune) 'Full Inspect retains its original traversal budget and safely refuses the oversized unknown tree.'
$trigger=Join-Path $root 'new-active'
$created=New-GeneratedWorkspace -RootPath $root -WorkspacePath $trigger -Purpose 'isolated automatic trigger' -Policy $policy -ReferenceRoots $roots
Assert-Check ($created.retention.safeToPrune -and @($created.retention.deleted|Where-Object path -eq $old).Count -eq 1) 'New automatically prunes an old registered completed task despite protected large trees.'
Assert-Check ((Test-Path -LiteralPath $newest) -and (Test-Path -LiteralPath $protected)) 'Newest completed task and a cross-root referenced old task survive automatic count retention.'
foreach($path in @($unknown,$running,$keep,$owner)){Assert-Check (Test-Path -LiteralPath $path) ('Protected owner survives: '+[IO.Path]::GetFileName($path))}
Assert-Check (-not $created.retention.wholeRootBytesKnown -and $created.retention.overBudget -and $created.retention.accountingScope -match 'registered completed') 'Automatic accounting reports unknown whole-root capacity, never presents the completed pool as the full root.'
Assert-Check ($created.retention.selectedWorkspaceCount -eq 3 -and $created.retention.totalBytes -lt 65536) 'Only non-Keep registered completed payloads were accounted.'
$completed=Complete-GeneratedWorkspace -RootPath $root -WorkspacePath $trigger -Outcome success -References @() -Policy $policy -ReferenceRoots $roots
Assert-Check ($completed.retention.safeToPrune -and -not (Test-Path -LiteralPath $newest) -and (Test-Path -LiteralPath $trigger)) 'Complete uses the same bounded completed-pool count policy.'
Assert-Check ((Get-GeneratedWorkspaceState -RootPath $root -WorkspacePath $running).state -eq 'running' -and (Get-GeneratedWorkspaceState -RootPath $root -WorkspacePath $keep).keep) 'Automatic maintenance does not close a running task or clear Keep.'
$emptyCreated=New-GeneratedWorkspace -RootPath $empty -WorkspacePath (Join-Path $empty 'active') -Purpose 'isolated empty-pool trigger' -Policy $policy -ReferenceRoots $roots
Assert-Check ($emptyCreated.retention.safeToPrune -and $emptyCreated.retention.selectedWorkspaceCount -eq 0 -and @($emptyCreated.retention.deleted).Count -eq 0 -and $emptyCreated.retention.totalBytes -eq 0) 'An empty completed pool is an explicit empty selection and never falls back to a full scan.'
Assert-Check ((Test-Path -LiteralPath (Join-Path $unknownEmpty 'f699.bin')) -and -not $emptyCreated.retention.wholeRootBytesKnown) 'An empty pool preserves the large unknown input and reports unknown root capacity.'
# Simulate a raw out-of-band marker change after locked selection. The official
# lifecycle API cannot race under these locks, and deletion still rechecks state.
$module=Get-Module GeneratedWorkspaceRetention
& $module {
 param($Target)
 $script:RaceTarget=$Target
 $script:RaceChanged=$false
 $script:OriginalExpand=(Get-Command Expand-GWTargetCatalog).ScriptBlock
 function script:Expand-GWTargetCatalog {
  param([object]$MetadataCatalog,[AllowNull()][object]$Selection)
  if(-not $script:RaceChanged -and $Selection.Contains($script:RaceTarget)){
   $read=Read-GWStateRecord -RootPath $MetadataCatalog.RootPath -WorkspacePath $script:RaceTarget
   $read.State.state='running';$read.State.completedUtc=$null;$read.State.outcome=$null
   Write-GWStateRecord -State $read.State
   $script:RaceChanged=$true
  }
  & $script:OriginalExpand @PSBoundParameters
 }
} $raceTarget
try{
 $raced=New-GeneratedWorkspace -RootPath $race -WorkspacePath (Join-Path $race 'trigger') -Purpose 'isolated race trigger' -Policy $policy -ReferenceRoots $roots
 Assert-Check (@($raced.retention.deleted).Count -eq 0 -and @($raced.retention.failures).Count -eq 1 -and (Test-Path -LiteralPath $raceTarget)) 'A completed-to-running change after selection is rejected by final lifecycle revalidation without deleting payload.'
 Assert-Check ($raced.retention.failures[0].removedBytes -eq 0 -and (Get-GeneratedWorkspaceState -RootPath $race -WorkspacePath $raceTarget).state -eq 'running') 'The race keeps running state and reports zero removed bytes.'
}finally{Import-Module $modulePath -Force -DisableNameChecking}
# Finish synthetic states, then retire only this known disposable fixture via the
# official parent selection. No actual repository roots are reference roots.
$policy.enabled=$false
foreach($catalogRoot in $roots){
 foreach($directory in ([IO.DirectoryInfo]::new($catalogRoot)).EnumerateDirectories()){
  $state=Get-GeneratedWorkspaceState -RootPath $catalogRoot -WorkspacePath $directory.FullName
  if($null -ne $state.PSObject.Properties['registered'] -and -not $state.registered){$null=Adopt-GeneratedWorkspace -RootPath $catalogRoot -WorkspacePath $directory.FullName -Purpose 'verified generated disposable fixture' -CompletedUtc ([DateTime]::UtcNow.ToString('o')) -Policy $policy -ReferenceRoots $roots}
  else{
   $null=Set-GeneratedWorkspaceKeep -RootPath $catalogRoot -WorkspacePath $directory.FullName -Keep $false -Policy $policy -ReferenceRoots $roots
   if($state.state -eq 'running'){$null=Complete-GeneratedWorkspace -RootPath $catalogRoot -WorkspacePath $directory.FullName -Outcome success -References @() -Policy $policy -ReferenceRoots $roots}
   else{$null=Set-GeneratedWorkspaceReferences -RootPath $catalogRoot -WorkspacePath $directory.FullName -References @() -Policy $policy -ReferenceRoots $roots}
  }
 }
}
$fixturePolicy=[pscustomobject]@{schemaVersion=1;enabled=$false;referenceRoots=@();defaults=[pscustomobject]@{maxRecentCompleted=0;maxBytes=0;maxNodes=750000;maxMilliseconds=300000};roots=@([pscustomobject]@{id='fixtures';path=$WorkRoot;excludedNames=@()})}
$null=Adopt-GeneratedWorkspace -RootPath $WorkRoot -WorkspacePath $fixture -Purpose 'closed disposable automatic-retention regression' -CompletedUtc ([DateTime]::UtcNow.ToString('o')) -Policy $fixturePolicy -ReferenceRoots @($WorkRoot)
$fixturePolicy.enabled=$true
$retired=Invoke-GeneratedWorkspaceRetention -RootPath $WorkRoot -WorkspacePaths @($fixture) -Policy $fixturePolicy -ReferenceRoots @($WorkRoot) -Apply
Assert-Check (@($retired.deleted).Count -eq 1 -and -not (Test-Path -LiteralPath $fixture)) 'The completed disposable fixture is retired officially; only the compact test result remains.'
$result=[pscustomobject]@{passed=$checks.Count;checks=@($checks.ToArray());powerShellVersion=[string]$PSVersionTable.PSVersion;moduleSha256=(Get-FileHash -LiteralPath $modulePath).Hash;actualProjectRootsTouched=$false;fixtureRetired=$true;newRetention=$created.retention;completedRetention=$completed.retention;emptyRetention=$emptyCreated.retention;raceRetention=$raced.retention}
$result|ConvertTo-Json -Depth 16|Set-Content -LiteralPath (Join-Path $WorkRoot ('automatic-completed-retention-'+$PSVersionTable.PSEdition+'.json')) -Encoding utf8
'Automatic completed-pool fixture checks passed (assertions='+$checks.Count+').'
