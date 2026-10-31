[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$modulePath=Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/maintenance/GeneratedWorkspaceRetention.psm1'
Import-Module $modulePath -Force -DisableNameChecking
$module=Get-Module GeneratedWorkspaceRetention
$fixture=Join-Path $WorkRoot ('archive-fixture-'+[Guid]::NewGuid().ToString('N'))
$sourceRoot=Join-Path $fixture 'source'
$coldRoot=Join-Path $fixture 'cold'
$referenceRoot=Join-Path $fixture 'references'
$docs=Join-Path $fixture 'docs'
$roots=@($sourceRoot,$coldRoot,$referenceRoot)
foreach($path in @($roots)+@($docs)){[void][IO.Directory]::CreateDirectory($path)}
$policy=[pscustomobject]@{schemaVersion=1;enabled=$false;referenceRoots=@();defaults=[pscustomobject]@{maxRecentCompleted=0;maxBytes=8388608;maxNodes=750000;maxMilliseconds=300000};archiveLimits=[pscustomobject]@{maxNodes=750000;maxMilliseconds=300000;maxDocumentBytes=8388608;maxDocumentNodes=20000;maxDocumentMilliseconds=300000;flushToDiskPerFile=$false};roots=@([pscustomobject]@{id='source';path=$sourceRoot;excludedNames=@()},[pscustomobject]@{id='cold';path=$coldRoot;archiveOnly=$true;excludedNames=@()},[pscustomobject]@{id='references';path=$referenceRoot;excludedNames=@()})}
$script:checks=0
function Assert-Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:checks++}
function Add-Fixture([string]$Root,[string]$Name,[switch]$Running,[switch]$Keep,[string[]]$References=@()){
 $path=Join-Path $Root $Name
 $null=New-GeneratedWorkspace -RootPath $Root -WorkspacePath $path -Purpose 'isolated archive regression' -References $References -Policy $policy -ReferenceRoots $roots
 [IO.File]::WriteAllText((Join-Path $path 'payload.txt'),'original payload')
 if(-not $Running){$null=Complete-GeneratedWorkspace -RootPath $Root -WorkspacePath $path -Outcome success -References $References -Policy $policy -ReferenceRoots $roots}
 if($Keep){$null=Set-GeneratedWorkspaceKeep -RootPath $Root -WorkspacePath $path -Keep $true -Policy $policy -ReferenceRoots $roots}
 return $path
}
function Invoke-Archive([string]$Path,[switch]$Apply){Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $Path -DestinationRootPath $coldRoot -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots -Apply:$Apply}
function Assert-Rejected([string]$Path,[string]$Pattern){
 try{$null=Invoke-Archive $Path -Apply;throw 'Expected archive refusal'}catch{
  Assert-Check ($_.Exception.Message -match $Pattern) ('Refusal names reason: '+$Pattern)
  Assert-Check (Test-Path -LiteralPath (Join-Path $Path 'payload.txt')) 'Refused source remains present.'
 }
}
$source=Add-Fixture $sourceRoot 'evidence' -Keep
[IO.File]::SetAttributes((Join-Path $source 'payload.txt'),[IO.FileAttributes]::ReadOnly)
$sourceId=(Get-GeneratedWorkspaceState -RootPath $sourceRoot -WorkspacePath $source).workspaceId
$unrelated=Join-Path $fixture 'unrelated-permanent-input'
$owner=Add-Fixture $referenceRoot 'closed-owner' -Keep -References @((Join-Path $source 'payload.txt'),$unrelated)
$running=Add-Fixture $sourceRoot 'running' -Running
$locked=Add-Fixture $sourceRoot 'locked' -Keep
$current=Add-Fixture $sourceRoot 'active-input' -Keep
$activeOwner=Add-Fixture $referenceRoot 'active-owner' -Running -References @($current)
$copyChanged=Add-Fixture $sourceRoot 'changed-after-copy' -Keep
$docFailed=Add-Fixture $sourceRoot 'document-failure' -Keep
$capacity=Add-Fixture $sourceRoot 'capacity' -Keep
$nested=Add-Fixture $sourceRoot 'nested-running' -Keep
$recorded=Add-Fixture $sourceRoot 'recorded-backup' -Keep
$recordedFolder=Join-Path $recorded 'reports';[void][IO.Directory]::CreateDirectory($recordedFolder)
$recordedMarker=Join-Path $recordedFolder '.generated-workspace.json'
$historicalRoot=Join-Path $fixture 'removed-worktree/.local'
@{schemaVersion=1;rootId='old-reports';workspaceId=[Guid]::NewGuid().ToString();rootPath=$historicalRoot;workspacePath=(Join-Path $historicalRoot reports);state='running';keep=$true}|ConvertTo-Json|Set-Content $recordedMarker -Encoding utf8
$recordedOwner=Add-Fixture $referenceRoot 'recorded-owner' -Keep -References @($recordedMarker)
$resumable=Add-Fixture $sourceRoot 'resumable' -Keep
[IO.File]::WriteAllText((Join-Path $resumable second.txt),'second complete payload')
$cleanupResume=Add-Fixture $sourceRoot 'cleanup-resume' -Keep
[IO.File]::WriteAllText((Join-Path $cleanupResume second.txt),'second cleanup payload')
$cleanupReports=Join-Path $cleanupResume reports;[void][IO.Directory]::CreateDirectory($cleanupReports)
$cleanupRecorded=Join-Path $cleanupReports '.generated-workspace.json'
[IO.File]::Copy($recordedMarker,$cleanupRecorded)
$cleanupDeclaration=@{path=$cleanupRecorded;sha256=(Get-FileHash $cleanupRecorded -Algorithm SHA256).Hash}
$closedNested=Add-Fixture $sourceRoot 'closed-nested-owner' -Keep
$closedRoot=Join-Path $closedNested '.validation/batches';$closedChild=Join-Path $closedRoot child;[void][IO.Directory]::CreateDirectory($closedChild)
$closedState=Get-GeneratedWorkspaceState -RootPath $sourceRoot -WorkspacePath $closedNested
$closedState.rootId='old-child-root';$closedState.rootPath=$closedRoot;$closedState.workspacePath=$closedChild;$closedState.workspaceId=[Guid]::NewGuid().ToString();$closedState.references=@($unrelated);$closedState.keep=$false
$closedMarker=Join-Path $closedChild '.generated-workspace.json';$closedState|ConvertTo-Json -Depth 8|Set-Content $closedMarker -Encoding utf8
$closedDeclaration=@{path=$closedMarker;sha256=(Get-FileHash $closedMarker -Algorithm SHA256).Hash;retireCompletedOwner=$true}
$nestedPath=Join-Path $nested 'child';[void][IO.Directory]::CreateDirectory($nestedPath)
$nestedMarker=Join-Path $nestedPath '.generated-workspace.json'
@{state='running';keep=$false}|ConvertTo-Json|Set-Content $nestedMarker -Encoding utf8
$docPath=Join-Path $docs 'evidence.md'
$originalDoc=$source+"`r`n"+$source.Replace('\','/')+'/payload.txt'+"`r`n"+$source+'-different-workspace'
[IO.File]::WriteAllText($docPath,$originalDoc)
$policy.enabled=$true
$preview=Invoke-Archive $source
Assert-Check ($preview.eligible -and -not $preview.applied -and $preview.markerUpdates.Count -eq 1 -and $preview.documentUpdates.Count -eq 1) 'Preview plans a cold move plus exact marker/document rewrites.'
Assert-Check ((Test-Path $source) -and -not (Test-Path (Join-Path $coldRoot evidence)) -and [IO.File]::ReadAllText($docPath) -ceq $originalDoc) 'Preview changes neither data nor references.'
$moved=Invoke-Archive $source -Apply
$destination=Join-Path $coldRoot evidence
Assert-Check ($moved.complete -and $moved.verifiedFiles -eq 1 -and -not (Test-Path $source)) 'Apply verifies payload before removing the source through maintenance.'
Assert-Check ([IO.File]::ReadAllText((Join-Path $destination 'payload.txt')) -ceq 'original payload') 'Cold payload bytes match source.'
$state=Get-GeneratedWorkspaceState -RootPath $coldRoot -WorkspacePath $destination
Assert-Check ($state.workspaceId -eq $sourceId -and $state.rootId -eq 'cold' -and $state.keep -and $state.state -eq 'completed') 'Destination preserves identity and Keep while adopting its configured root.'
$ownerState=Get-GeneratedWorkspaceState -RootPath $referenceRoot -WorkspacePath $owner
Assert-Check ($ownerState.references.Count -eq 2 -and $ownerState.references[0] -eq (Join-Path $destination 'payload.txt')) 'Real completed consumer reference moves with the evidence.'
$rewritten=[IO.File]::ReadAllText($docPath)
Assert-Check ($rewritten.Contains($destination) -and $rewritten.Contains($destination.Replace('\','/')+'/payload.txt') -and $rewritten.Contains($source+'-different-workspace')) 'Docs update slash variants and preserve similarly prefixed workspace names.'
$pruned=Invoke-GeneratedWorkspaceRetention -RootPath $coldRoot -Policy $policy -ReferenceRoots $roots -Apply
Assert-Check (@($pruned.deleted).Count -eq 0 -and (Test-Path $destination)) 'Normal Prune still honors archived Keep.'
[IO.File]::WriteAllText($docPath,$originalDoc)
$repair=Repair-GeneratedWorkspaceArchiveDocuments -RootPath $sourceRoot -DestinationRootPath $coldRoot -ArchiveReceipt $moved -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots
Assert-Check ($repair.plannedDocuments.Count -eq 1 -and [IO.File]::ReadAllText($docPath) -ceq $originalDoc) 'Archive document repair previews a missed docs tree without writes.'
$wrongReceipt=$moved|ConvertTo-Json -Depth 10|ConvertFrom-Json;$wrongReceipt.workspaceId=[Guid]::NewGuid().ToString()
try{$null=Repair-GeneratedWorkspaceArchiveDocuments -RootPath $sourceRoot -DestinationRootPath $coldRoot -ArchiveReceipt $wrongReceipt -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected receipt identity refusal'}catch{Assert-Check ($_.Exception.Message -match 'identity does not match') 'Document repair rejects an unrelated completed archive.'}
$wrongReceipt=$moved|ConvertTo-Json -Depth 10|ConvertFrom-Json;$wrongReceipt.complete=$false
try{$null=Repair-GeneratedWorkspaceArchiveDocuments -RootPath $sourceRoot -DestinationRootPath $coldRoot -ArchiveReceipt $wrongReceipt -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected incomplete receipt refusal'}catch{Assert-Check ($_.Exception.Message -match 'successful applied archive receipt') 'Document repair rejects a failed or unfinished archive receipt.'}
$repair=Repair-GeneratedWorkspaceArchiveDocuments -RootPath $sourceRoot -DestinationRootPath $coldRoot -ArchiveReceipt $moved -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots -Apply
Assert-Check ($repair.updatedDocuments.Count -eq 1 -and [IO.File]::ReadAllText($docPath) -ceq $rewritten -and -not $repair.payloadChanged) 'Document repair reuses exact bounded rewrites without touching payloads or registry state.'
Assert-Rejected $running 'completed workspace'
$held=[IO.File]::Open((Join-Path $locked payload.txt),'Open','Read','None')
try{Assert-Rejected $locked 'occupied|inaccessible'}finally{$held.Dispose()}
Assert-Rejected $current 'running workspace'
Assert-Rejected $nested 'nested lifecycle registrations'
[IO.File]::WriteAllText($nestedMarker,'{"state":"completed","keep":false}')
Assert-Rejected $nested 'nested lifecycle registrations'
[IO.File]::WriteAllText((Join-Path $docs recorded.md),($recordedMarker+"`n"+$recordedMarker.Replace('\','/')+"`n"+$recordedMarker.Replace('\','\\')))
$recordedHash=(Get-FileHash $recordedMarker -Algorithm SHA256).Hash
try{$null=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $recorded -DestinationRootPath $coldRoot -DocumentRoots @($docs) -RecordedMarkers @(@{path=$recordedMarker;sha256=('0'*64)}) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected recorded hash refusal'}catch{Assert-Check ($_.Exception.Message -match 'Recorded marker hash changed') 'Recorded evidence must match its explicit hash-bound declaration.'}
$recordedMove=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $recorded -DestinationRootPath $coldRoot -DocumentRoots @($docs) -RecordedMarkers @(@{path=$recordedMarker;sha256=$recordedHash}) -Policy $policy -ReferenceRoots $roots -Apply
$archivedRecord=Join-Path $coldRoot 'recorded-backup/reports/.generated-workspace.evidence.json'
Assert-Check ($recordedMove.complete -and $recordedMove.recordedMarkers.Count -eq 1 -and -not (Test-Path $recorded)) 'Explicit historical-marker copies relocate while real nested owners remain prohibited.'
Assert-Check ((Get-FileHash $archivedRecord -Algorithm SHA256).Hash -eq $recordedHash -and -not (Test-Path (Join-Path (Split-Path $archivedRecord -Parent) '.generated-workspace.json'))) 'Historical marker bytes are preserved under an evidence filename, never a live registry marker.'
Assert-Check ((Get-GeneratedWorkspaceState -RootPath $referenceRoot -WorkspacePath $recordedOwner).references[0] -eq $archivedRecord -and [IO.File]::ReadAllText((Join-Path $docs recorded.md)) -eq ($archivedRecord+"`n"+$archivedRecord.Replace('\','/')+"`n"+$archivedRecord.Replace('\','\\'))) 'Exact historical marker mapping wins over its workspace prefix in all slash/escaped variants.'
@{schemaVersion=1;rootPath=$nested;workspacePath=$nestedPath;state='completed';keep=$false}|ConvertTo-Json|Set-Content $nestedMarker -Encoding utf8
try{$null=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $nested -DestinationRootPath $coldRoot -RecordedMarkers @(@{path=$nestedMarker;sha256=(Get-FileHash $nestedMarker -Algorithm SHA256).Hash}) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected live owner refusal'}catch{Assert-Check ($_.Exception.Message -match 'live nested owner') 'A real nested lifecycle owner cannot be relabelled as historical evidence.'}
$closedMove=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $closedNested -DestinationRootPath $coldRoot -RecordedMarkers @($closedDeclaration) -Policy $policy -ReferenceRoots $roots -Apply
$closedArchive=Join-Path $coldRoot 'closed-nested-owner'
Assert-Check ($closedMove.complete -and (Get-GeneratedWorkspaceState -RootPath $coldRoot -WorkspacePath $closedArchive).references -contains $unrelated) 'Explicit completed-child retirement carries its real outgoing dependencies into the kept archive parent.'
Assert-Check ((Get-FileHash (Join-Path $closedArchive '.validation/batches/child/.generated-workspace.evidence.json') -Algorithm SHA256).Hash -eq $closedDeclaration.sha256) 'Completed child identity/history is preserved as evidence under the one archive lifecycle.'
@{schemaVersion=1;rootId='nested';workspaceId=[Guid]::NewGuid().ToString();rootPath=$nested;workspacePath=$nestedPath;state='running';keep=$false}|ConvertTo-Json|Set-Content $nestedMarker -Encoding utf8
try{$null=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $nested -DestinationRootPath $coldRoot -RecordedMarkers @(@{path=$nestedMarker;sha256=(Get-FileHash $nestedMarker -Algorithm SHA256).Hash;retireCompletedOwner=$true}) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected running owner refusal'}catch{Assert-Check ($_.Exception.Message -match 'live nested owner') 'Explicit child retirement still cannot move running work.'}
$policy.roots[1]|Add-Member -NotePropertyName maxBytes -NotePropertyValue 1
Assert-Rejected $capacity 'cold-root budget'
$policy.roots[1].PSObject.Properties.Remove('maxBytes')

# Change source bytes while preserving length and write timestamp after copy:
# the old metadata-only deletion check would miss this. The archive must not.
& $module {
 param($Target)
 $script:ArchiveTestTarget=$Target
 $script:OriginalDocumentWriter=(Get-Command Write-GWArchiveDocument).ScriptBlock
 function script:Write-GWArchiveDocument {
  param([object]$Plan)
  $file=Join-Path $script:ArchiveTestTarget payload.txt
  $stamp=[IO.File]::GetLastWriteTimeUtc($file)
  [IO.File]::WriteAllText($file,'changed! payload')
  [IO.File]::SetLastWriteTimeUtc($file,$stamp)
  & $script:OriginalDocumentWriter -Plan $Plan
 }
} $copyChanged
[IO.File]::WriteAllText((Join-Path $docs changed.md),$copyChanged)
try{
 try{$null=Invoke-Archive $copyChanged -Apply;throw 'Expected source hash refusal'}catch{
  Assert-Check ($_.Exception.Message -match 'content changed') 'Source hash recheck catches same-length/timestamp changes before deletion.'
  $failure=$_.Exception.Data['archive']
  Assert-Check ($failure.sourceExists -and $failure.sourceCleanup.RemovedBytes -eq 0) 'Hash refusal reports the retained source and zero removed bytes.'
  $copy=Get-GeneratedWorkspaceState -RootPath $coldRoot -WorkspacePath (Join-Path $coldRoot 'changed-after-copy')
  Assert-Check ($copy.keep -and $copy.state -eq 'completed') 'Verified destination stays protected after source cleanup failure.'
 }
}finally{Import-Module $modulePath -Force -DisableNameChecking;$module=Get-Module GeneratedWorkspaceRetention}

[IO.File]::WriteAllText((Join-Path $docs 'failure-a.md'),$docFailed)
[IO.File]::WriteAllText((Join-Path $docs 'failure-b.md'),$docFailed)
& $module {
 $script:ArchiveTestDocCount=0
 $script:OriginalDocumentWriter=(Get-Command Write-GWArchiveDocument).ScriptBlock
 function script:Write-GWArchiveDocument {
  param([object]$Plan)
  if(++$script:ArchiveTestDocCount -eq 2){throw 'injected document write failure'}
  & $script:OriginalDocumentWriter -Plan $Plan
 }
}
try{
 try{$null=Invoke-Archive $docFailed -Apply;throw 'Expected document refusal'}catch{
  Assert-Check ($_.Exception.Message -match 'document write failure') 'Reference-update failure retains its actual cause.'
  $failure=$_.Exception.Data['archive']
  Assert-Check ($failure.phase -eq 'references' -and $failure.updatedDocuments.Count -eq 1 -and $failure.sourceExists) 'Partial reference failure reports exact progress and keeps the original source.'
  $copy=Get-GeneratedWorkspaceState -RootPath $coldRoot -WorkspacePath (Join-Path $coldRoot 'document-failure')
  Assert-Check $copy.keep 'Verified copy remains protected when only some docs changed.'
 }
}finally{Import-Module $modulePath -Force -DisableNameChecking}

# Simulate interruption after payload copying, before the first reference write.
$module=Get-Module GeneratedWorkspaceRetention
[IO.File]::WriteAllText((Join-Path $docs resume.md),$resumable)
& $module {
 param($Target)
 $script:InterruptedArchiveTarget=$Target
 $script:ArchiveOriginalWriter=(Get-Command Write-GWStateRecord).ScriptBlock
 function script:Write-GWStateRecord {
  param([object]$State)
  if($State.workspacePath -eq $script:InterruptedArchiveTarget -and $State.state -eq 'completed'){throw 'injected precommit interruption'}
  & $script:ArchiveOriginalWriter -State $State
 }
} (Join-Path $coldRoot resumable)
try{
 try{$null=Invoke-Archive $resumable -Apply;throw 'Expected interruption'}catch{Assert-Check ($_.Exception.Message -match 'precommit interruption') 'Interrupted archive preserves its exact cause.'}
}finally{Import-Module $modulePath -Force -DisableNameChecking}
$staging=Join-Path $coldRoot resumable
Assert-Check ((Get-GeneratedWorkspaceState -RootPath $coldRoot -WorkspacePath $staging).state -eq 'running' -and (Test-Path $resumable)) 'Precommit staging copy and original remain protected after interruption.'
[IO.File]::WriteAllText((Join-Path $staging payload.txt),'partial')
[IO.File]::WriteAllText((Join-Path $staging unexpected.txt),'unexpected')
try{$null=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $resumable -DestinationRootPath $coldRoot -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots -Resume -Apply;throw 'Expected staging refusal'}catch{Assert-Check ($_.Exception.Message -match 'Unexpected file') 'Resume never overwrites unrelated staging content.'}
[IO.File]::Delete((Join-Path $staging unexpected.txt))
$resumeResult=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $resumable -DestinationRootPath $coldRoot -DocumentRoots @($docs) -Policy $policy -ReferenceRoots $roots -Resume -Apply
Assert-Check ($resumeResult.complete -and $resumeResult.resumed -and $resumeResult.reusedFiles -eq 1 -and $resumeResult.verifiedFiles -eq 2 -and -not (Test-Path $resumable)) 'Resume hash-reuses only complete files, repairs partial copies and finishes source cleanup.'
Assert-Check ([IO.File]::ReadAllText((Join-Path $staging payload.txt)) -ceq 'original payload') 'Resumed partial payload matches the original bytes.'

# Exercise actual native deletion under a deliberately exhausted deletion budget,
# after payload verification. It removes payloads (including a recorded marker)
# and leaves a valid source marker, exactly like a real interrupted cleanup.
$module=Get-Module GeneratedWorkspaceRetention
& $module {
 $script:OriginalRemoveForBudget=(Get-Command Remove-GWWorkspaceSnapshot).ScriptBlock
 $script:OriginalBudgetForCleanup=(Get-Command New-GWScanBudget).ScriptBlock
 $script:CleanupBudgetCalls=0;$script:InsideCleanup=$false
 function script:New-GWScanBudget {
  param([object]$Limits)
  $b=& $script:OriginalBudgetForCleanup -Limits $Limits
  if($script:InsideCleanup -and ++$script:CleanupBudgetCalls -eq 3){$b.MaxNodes=3}
  $b
 }
 function script:Remove-GWWorkspaceSnapshot {
  param([object]$Item,[object]$Limits,[object]$VerifiedArchive=$null)
  $script:InsideCleanup=$true
  try{& $script:OriginalRemoveForBudget @PSBoundParameters}finally{$script:InsideCleanup=$false}
 }
}
try{
 try{$null=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $cleanupResume -DestinationRootPath $coldRoot -RecordedMarkers @($cleanupDeclaration) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected cleanup bound'}catch{
  $partialFailure=$_.Exception.Data['archive']
  Assert-Check ($partialFailure.sourceCleanup.RemovedFiles -eq 3 -and $partialFailure.referencesCommitted -and (Test-Path (Join-Path $cleanupResume '.generated-workspace.json')) -and -not (Test-Path $cleanupRecorded)) 'Real bounded partial cleanup preserves source identity after deleting already-verified payloads.'
 }
}finally{Import-Module $modulePath -Force -DisableNameChecking}
$finishedCleanup=Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot -WorkspacePath $cleanupResume -DestinationRootPath $coldRoot -RecordedMarkers @($cleanupDeclaration) -Policy $policy -ReferenceRoots $roots -Resume -Apply
Assert-Check ($finishedCleanup.complete -and $finishedCleanup.resumedVerifiedCopy -and -not (Test-Path $cleanupResume)) 'Resume completes a protected verified copy even after source payloads/recorded markers were already removed.'
Assert-Check ((Get-FileHash (Join-Path $coldRoot 'cleanup-resume/reports/.generated-workspace.evidence.json') -Algorithm SHA256).Hash -eq $cleanupDeclaration.sha256) 'Verified cleanup recovery preserves historical marker bytes already removed from source.'

$request=[pscustomobject]@{workspacePath=$owner;retiredReferences=@((Join-Path $destination payload.txt));reason='isolated reference retirement proof';evidence=@(@{path=$docPath;sha256=(Get-FileHash $docPath -Algorithm SHA256).Hash})}
$preview=Retire-GeneratedWorkspaceReferences -RootPath $referenceRoot -Retirements @($request) -Policy $policy -ReferenceRoots $roots
Assert-Check (-not $preview.applied -and $preview.updatedOwners.Count -eq 0 -and -not $preview.retentionInvoked) 'Reference retirement preview is read-only and never implicitly collects the completed pool.'
$retired=Retire-GeneratedWorkspaceReferences -RootPath $referenceRoot -Retirements @($request) -Policy $policy -ReferenceRoots $roots -Apply
$after=Get-GeneratedWorkspaceState -RootPath $referenceRoot -WorkspacePath $owner
Assert-Check ($retired.updatedOwners.Count -eq 1 -and $after.keep -and $after.references.Count -eq 1 -and $after.references[0] -eq $unrelated -and (Test-Path $destination)) 'Retirement changes only audited outgoing edges, preserving owner state, Keep, unrelated input and target bytes.'
try{$null=Retire-GeneratedWorkspaceReferences -RootPath $referenceRoot -Retirements @(@{workspacePath=$activeOwner;retiredReferences=@($current);reason='not allowed';evidence=$request.evidence}) -Policy $policy -ReferenceRoots $roots -Apply;throw 'Expected running owner refusal'}catch{Assert-Check ($_.Exception.Message -match 'completed owners') 'Reference retirement refuses a running consumer.'}

# Retire only this known disposable fixture, using the same official sequence
# as AutomaticRetention.Tests; no custom tree deletion.
$policy.enabled=$false
[IO.File]::WriteAllText($nestedMarker,'{"state":"completed","keep":false}')
foreach($root in $roots){foreach($directory in ([IO.DirectoryInfo]::new($root)).EnumerateDirectories()){
 $state=Get-GeneratedWorkspaceState -RootPath $root -WorkspacePath $directory.FullName
 if($state.keep){$null=Set-GeneratedWorkspaceKeep -RootPath $root -WorkspacePath $directory.FullName -Keep $false -Policy $policy -ReferenceRoots $roots}
 if($state.state -eq 'running'){$null=Complete-GeneratedWorkspace -RootPath $root -WorkspacePath $directory.FullName -Outcome success -References @() -Policy $policy -ReferenceRoots $roots}
}}
$cleanupPolicy=[pscustomobject]@{schemaVersion=1;enabled=$false;referenceRoots=@();defaults=$policy.defaults;roots=@([pscustomobject]@{id='fixtures';path=$WorkRoot;excludedNames=@()})}
$null=Adopt-GeneratedWorkspace -RootPath $WorkRoot -WorkspacePath $fixture -Purpose 'closed disposable archive regression' -CompletedUtc ([DateTime]::UtcNow.ToString('o')) -Policy $cleanupPolicy -ReferenceRoots @($WorkRoot)
$cleanupPolicy.enabled=$true;$cleanupPolicy.defaults.maxBytes=0
$cleanup=Invoke-GeneratedWorkspaceRetention -RootPath $WorkRoot -WorkspacePaths @($fixture) -Policy $cleanupPolicy -ReferenceRoots @($WorkRoot) -Apply
Assert-Check ($cleanup.safeToPrune -and -not (Test-Path $fixture)) 'Official retention retires the fully closed synthetic fixture.'
"GeneratedWorkspaceArchive.Tests passed ($script:checks assertions)."
