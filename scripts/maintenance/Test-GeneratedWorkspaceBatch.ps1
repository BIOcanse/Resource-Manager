[CmdletBinding()]
param([string]$WorkRoot = 'F:\code\Resource-Manager-Development\d-space-recovery')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'GeneratedWorkspaceRetention.psm1'
Import-Module $modulePath -Force -DisableNameChecking
$module = Get-Module GeneratedWorkspaceRetention
$fixture = Join-Path $WorkRoot ('batch-entry-fixture-' + [Guid]::NewGuid().ToString('N'))
$rootA = Join-Path $fixture 'a'
$rootB = Join-Path $fixture 'b'
[void][IO.Directory]::CreateDirectory($rootA)
[void][IO.Directory]::CreateDirectory($rootB)
$policy = [pscustomobject]@{ schemaVersion=1; enabled=$false; referenceRoots=@(); defaults=[pscustomobject]@{maxRecentCompleted=0;maxBytes=0;maxNodes=750000;maxMilliseconds=300000}; roots=@([pscustomobject]@{id='a';path=$rootA;excludedNames=@()},[pscustomobject]@{id='b';path=$rootB;excludedNames=@()}) }
$roots = @($rootA,$rootB)
$checks = [Collections.Generic.List[string]]::new()
function Assert-Check([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message }; $checks.Add($Message) }
function Add-Workspace([string]$Root,[string]$Name,[string]$State='completed',[string[]]$References=@()) {
    $path = Join-Path $Root $Name
    $null = New-GeneratedWorkspace -RootPath $Root -WorkspacePath $path -Purpose 'Isolated batch-entry regression fixture' -Policy $policy -References $References -ReferenceRoots $roots
    [IO.File]::WriteAllText((Join-Path $path 'payload.bin'), ('x'*257))
    if ($State -eq 'completed') { $null = Complete-GeneratedWorkspace -RootPath $Root -WorkspacePath $path -Outcome success -References $References -Policy $policy -ReferenceRoots $roots }
    return $path
}
$eligibleA = Add-Workspace $rootA 'eligible'
# Read-only files must still be deleted: git object files in test fixtures are read-only (10-27 retention failures).
[IO.File]::SetAttributes((Join-Path $eligibleA 'payload.bin'), [IO.FileAttributes]::ReadOnly)
$running = Add-Workspace $rootA 'running' 'running'
$unknown = Join-Path $rootA 'unknown'
[void][IO.Directory]::CreateDirectory($unknown)
[IO.File]::WriteAllText((Join-Path $unknown 'unique.txt'),'unknown')
$kept = Add-Workspace $rootA 'keep'
$null = Set-GeneratedWorkspaceKeep -RootPath $rootA -WorkspacePath $kept -Keep $true -Policy $policy -ReferenceRoots $roots
$referencedA = Add-Workspace $rootA 'referenced'
$unselectedProtected = Add-Workspace $rootA 'unselected-protected'
$eligibleB = Add-Workspace $rootB 'eligible'
$referencedB = Add-Workspace $rootB 'referenced'
$unselected = Add-Workspace $rootA 'unselected-owner' 'completed' @($unselectedProtected,$referencedB)
$ownerB = Add-Workspace $rootB 'running-owner' 'running' @($referencedA)
$busy = Add-Workspace $rootA 'busy'
$busyStream = [IO.File]::Open((Join-Path $busy 'payload.bin'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
$selectedA = @($eligibleA,$running,$unknown,$kept,$referencedA,$unselectedProtected,$busy)
$selectedB = @($eligibleB,$referencedB)
$groups = @([pscustomobject]@{RootPath=$rootA;WorkspacePaths=$selectedA},[pscustomobject]@{RootPath=$rootB;WorkspacePaths=$selectedB})
$policy.enabled=$true
try {
    $singleA = Invoke-GeneratedWorkspaceRetention -RootPath $rootA -Policy $policy -ReferenceRoots $roots -WorkspacePaths $selectedA
    $singleB = Invoke-GeneratedWorkspaceRetention -RootPath $rootB -Policy $policy -ReferenceRoots $roots -WorkspacePaths $selectedB
    $legacy = & $module {
        param($RootA,$RootB,$Policy,$SelectedA)
        $selection=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $SelectedA) { [void]$selection.Add($path) }
        $target=Get-GWRootCatalog -RootPath $RootA -Policy $Policy -WorkspaceSelection $selection
        $reference=Get-GWRootCatalog -RootPath $RootB -Policy $Policy -MetadataOnly
        Invoke-GWCatalogRetention -RootFull $RootA -TargetCatalog $target -Catalogs @($target,$reference) -RootPaths @($RootA,$RootB) -Selection $selection
    } $rootA $rootB $policy $selectedA
    Assert-Check (($legacy.planned.path -join '|') -eq ($singleA.planned.path -join '|') -and ($legacy.retained.reason -join '|') -eq ($singleA.retained.reason -join '|')) 'Original full catalogue and shared-metadata hydration produce identical plans and protection reasons.'
    & $module {
        $script:CatalogReads=0
        $script:OriginalCatalog=(Get-Command Get-GWRootCatalog).ScriptBlock
        function script:Get-GWRootCatalog {
            param([string]$RootPath,[object]$Policy,[switch]$MetadataOnly,[AllowNull()][Collections.Generic.HashSet[string]]$WorkspaceSelection)
            $script:CatalogReads++
            & $script:OriginalCatalog @PSBoundParameters
        }
    }
    $batch = @(Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups $groups -Policy $policy -ReferenceRoots $roots)
    Assert-Check ($batch.Count -eq 2) 'Batch returns one result per root.'
    Assert-Check ((& $module { $script:CatalogReads }) -eq 2) 'Two roots read metadata exactly once each for the whole batch.'
    Assert-Check (($singleA.planned.path -join '|') -eq ($batch[0].planned.path -join '|')) 'Single and batch A plans match.'
    Assert-Check (($singleB.planned.path -join '|') -eq ($batch[1].planned.path -join '|')) 'Single and batch B plans match.'
    Assert-Check (@($batch[0].planned).Count -eq 1 -and $batch[0].planned[0].path -eq $eligibleA) 'Running, unknown, Keep, busy, cross-root references and unselected dependencies are protected.'
    Assert-Check (@($batch[1].planned).Count -eq 1 -and $batch[1].planned[0].path -eq $eligibleB) 'Non-target root outgoing references protect the other target.'
    $script:lockObserved=$false
    $applied = @(Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups $groups -Policy $policy -ReferenceRoots $roots -Apply | ForEach-Object {
        if (-not $script:lockObserved) {
            try { $null=Set-GeneratedWorkspaceKeep -RootPath $rootB -WorkspacePath $ownerB -Keep $true -Policy $policy -ReferenceRoots $roots; throw 'Expected root lock contention.' }
            catch { if ($_.Exception.Message -notmatch 'already holds the root lock') { throw }; $script:lockObserved=$true }
        }
        $_
    })
    Assert-Check $script:lockObserved 'All root locks remain held when the first per-root result is streamed.'
    Assert-Check (-not (Test-Path -LiteralPath $eligibleA) -and -not (Test-Path -LiteralPath $eligibleB)) 'Apply deletes only both eligible targets through the official deletion core.'
    foreach ($path in @($running,$unknown,$kept,$referencedA,$unselectedProtected,$referencedB,$unselected,$ownerB,$busy)) { Assert-Check (Test-Path -LiteralPath $path) ('Protected fixture survives: ' + [IO.Path]::GetFileName($path)) }
}
finally { $busyStream.Dispose() }
# Produce a real race after occupancy validation: lock the last ordinary file.
# The official deletion core must account for earlier actual removals and continue
# with the next root without reporting the partially removed workspace as deleted.
$policy.enabled=$false
$partial = Add-Workspace $rootA 'partial'
[IO.File]::WriteAllText((Join-Path $partial 'second.bin'),'second')
$afterFailure = Add-Workspace $rootB 'after-failure'
$policy.enabled=$true
& $module {
    param($Partial)
    $script:PartialPath=$Partial
    $script:PartialCalls=0
    $script:PartialLock=$null
    $script:OriginalOccupancy=(Get-Command Test-GWFilesUnoccupied).ScriptBlock
    function script:Test-GWFilesUnoccupied {
        param([string[]]$FilePaths,[string]$RootPath,[object]$Limits)
        $result = & $script:OriginalOccupancy @PSBoundParameters
        if ($RootPath -eq $script:PartialPath) {
            $script:PartialCalls++
            if ($script:PartialCalls -eq 2 -and $result.Unoccupied) {
                $ordinary = @($FilePaths | Where-Object { [IO.Path]::GetFileName($_) -ne $script:GeneratedWorkspaceMarkerName })
                $script:PartialLock = [IO.File]::Open($ordinary[-1],[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
            }
        }
        return $result
    }
} $partial
try {
    $partialResults = @(Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @([pscustomobject]@{RootPath=$rootA;WorkspacePaths=@($partial)},[pscustomobject]@{RootPath=$rootB;WorkspacePaths=@($afterFailure)}) -Policy $policy -ReferenceRoots $roots -Apply)
    Assert-Check (@($partialResults[0].deleted).Count -eq 0 -and @($partialResults[0].failures).Count -eq 1) 'A partial deletion is reported as a failure, never a fully deleted workspace.'
    Assert-Check ($partialResults[0].failures[0].removedFiles -gt 0 -and $partialResults[0].failures[0].removedBytes -gt 0) 'Partial failure reports actual earlier deleted files and bytes.'
    Assert-Check (@($partialResults[1].deleted).Count -eq 1 -and -not (Test-Path -LiteralPath $afterFailure)) 'A later root still completes after an accounted deletion failure.'
}
finally { & $module { if ($null -ne $script:PartialLock) { $script:PartialLock.Dispose() } } }
# Fresh module removes all test hooks before budget and CLI checks.
Import-Module $modulePath -Force -DisableNameChecking
$policy.enabled=$false
$bounded=Add-Workspace $rootB 'bounded'
for ($i=0;$i -lt 20;$i++) { [IO.File]::WriteAllText((Join-Path $bounded ('f'+$i)), 'x') }
$policy.enabled=$true
$policy.roots[1] | Add-Member -NotePropertyName maxNodes -NotePropertyValue 15
$limited=Invoke-GeneratedWorkspaceRetention -RootPath $rootB -Policy $policy -ReferenceRoots $roots -WorkspacePaths @($bounded) -Apply
Assert-Check (-not $limited.safeToPrune -and (Test-Path -LiteralPath $bounded)) 'Metadata and target snapshot share the original root node budget; over-bound work is not deleted.'
$policy.roots[1].PSObject.Properties.Remove('maxNodes')
# Both selected CLI roots are protected, so this read-only check cannot mutate fixtures.
$policyFile=Join-Path $fixture 'policy.json'
$groupsFile=Join-Path $fixture 'groups.json'
$policy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $policyFile -Encoding utf8
@([pscustomobject]@{rootId='a';workspacePaths=@($running)},[pscustomobject]@{rootId='b';workspacePaths=@($ownerB)}) | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $groupsFile -Encoding utf8
$cli=@(& (Join-Path $PSScriptRoot 'Manage-GeneratedWorkspace.ps1') -Action Inspect -WorkspaceGroups $groupsFile -PolicyPath $policyFile)
Assert-Check ($cli.Count -eq 2) 'Official CLI emits exactly one JSON line per root in batch mode.'
Assert-Check (($cli[0] | ConvertFrom-Json).rootPath -eq $rootA -and ($cli[1] | ConvertFrom-Json).rootPath -eq $rootB) 'Both streamed CLI receipts are complete independently parseable JSON.'
# Invalid references anywhere in the locked graph block all deletion.
$policy.enabled=$false
$graphCandidate=Add-Workspace $rootA 'graph-candidate'
$bad=Join-Path $rootB 'bad-marker'
[void][IO.Directory]::CreateDirectory($bad)
$markerName=& (Get-Module GeneratedWorkspaceRetention) { $script:GeneratedWorkspaceMarkerName }
[IO.File]::WriteAllText((Join-Path $bad $markerName),'{invalid')
$policy.enabled=$true
$invalidGraph=Invoke-GeneratedWorkspaceRetention -RootPath $rootA -Policy $policy -ReferenceRoots $roots -WorkspacePaths @($graphCandidate) -Apply
Assert-Check (-not $invalidGraph.safeToPrune -and (Test-Path -LiteralPath $graphCandidate)) 'Invalid lifecycle metadata in an unselected reference root blocks all deletion.'
# This file was created only as malformed fixture input; removing it repairs
# metadata, without deleting a workspace outside the official maintenance core.
[IO.File]::Delete((Join-Path $bad $markerName))
$badSelectionObserved=$false
try {
    $null=Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @([pscustomobject]@{RootPath=$rootA;WorkspacePaths=@($graphCandidate)},[pscustomobject]@{RootPath=$rootB;WorkspacePaths=@($rootA)}) -Policy $policy -ReferenceRoots $roots -Apply
}
catch { $badSelectionObserved=$true }
Assert-Check ($badSelectionObserved -and (Test-Path -LiteralPath $graphCandidate)) 'Every group is validated before any deletion; an invalid second group leaves the first intact.'
$emptySelectionObserved=$false
try { $null=Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @([pscustomobject]@{RootPath=$rootA;WorkspacePaths=@()}) -Policy $policy -ReferenceRoots $roots -Apply }
catch { $emptySelectionObserved=$true }
Assert-Check $emptySelectionObserved 'Public batch entry rejects empty selections instead of scanning an entire root.'
$tooManyGroupsObserved=$false
try { $null=Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups (@([pscustomobject]@{RootPath=$rootA;WorkspacePaths=@($running)}) * 4097) -Policy $policy -ReferenceRoots $roots }
catch { $tooManyGroupsObserved=$_.Exception.Message -match '4096' }
Assert-Check $tooManyGroupsObserved 'Batch rejects more than 4096 groups before catalogue or mutation.'
$tooManyPathsObserved=$false
try { $null=Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @([pscustomobject]@{RootPath=$rootA;WorkspacePaths=(@($running)*750001)}) -Policy $policy -ReferenceRoots $roots }
catch { $tooManyPathsObserved=$_.Exception.Message -match '750000' }
Assert-Check $tooManyPathsObserved 'Batch rejects more than 750000 total exact paths before path validation or mutation.'
$timeBoundObserved=$false
$policy.defaults.maxMilliseconds=1
try { $null=Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @([pscustomobject]@{RootPath=$rootA;WorkspacePaths=(@($running)*1000)}) -Policy $policy -ReferenceRoots $roots }
catch { $timeBoundObserved=$_.Exception.Data.Contains('GeneratedWorkspaceRetentionBudgetExceeded') }
finally { $policy.defaults.maxMilliseconds=300000 }
Assert-Check $timeBoundObserved 'Batch input path validation obeys the original policy time bound.'
# Retire only this run's generated fixture workspaces through the official APIs.
$policy.enabled=$false
$cleanupGroups=[Collections.Generic.List[object]]::new()
foreach ($root in $roots) {
    $paths=[Collections.Generic.List[string]]::new()
    foreach ($directory in ([IO.DirectoryInfo]::new($root)).EnumerateDirectories()) {
        $state=Get-GeneratedWorkspaceState -RootPath $root -WorkspacePath $directory.FullName
        if ($null -ne $state.PSObject.Properties['registered'] -and -not $state.registered) {
            $null=Adopt-GeneratedWorkspace -RootPath $root -WorkspacePath $directory.FullName -Purpose 'Verified generated disposable batch-entry fixture' -CompletedUtc ([DateTime]::UtcNow.ToString('o')) -Policy $policy -ReferenceRoots $roots
        }
        else {
            $null=Set-GeneratedWorkspaceKeep -RootPath $root -WorkspacePath $directory.FullName -Keep $false -Policy $policy -ReferenceRoots $roots
            if ($state.state -eq 'running') { $null=Complete-GeneratedWorkspace -RootPath $root -WorkspacePath $directory.FullName -Outcome success -Policy $policy -ReferenceRoots $roots }
            else { $null=Set-GeneratedWorkspaceReferences -RootPath $root -WorkspacePath $directory.FullName -References @() -Policy $policy -ReferenceRoots $roots }
        }
        $paths.Add($directory.FullName)
    }
    $cleanupGroups.Add([pscustomobject]@{RootPath=$root;WorkspacePaths=@($paths.ToArray())})
}
$policy.enabled=$true
$cleanup=@(Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @($cleanupGroups.ToArray()) -Policy $policy -ReferenceRoots $roots -Apply)
Assert-Check (@($cleanup | ForEach-Object {$_.failures}).Count -eq 0) 'Disposable test workspaces are retired through the same official batch entry.'
$fixturePolicy=[pscustomobject]@{schemaVersion=1;enabled=$false;referenceRoots=@();defaults=$policy.defaults;roots=@([pscustomobject]@{id='batch-fixtures';path=$WorkRoot;excludedNames=@()})}
$null=Adopt-GeneratedWorkspace -RootPath $WorkRoot -WorkspacePath $fixture -Purpose 'Closed disposable batch-entry regression fixture; minimal result stored outside' -CompletedUtc ([DateTime]::UtcNow.ToString('o')) -Policy $fixturePolicy -ReferenceRoots @($WorkRoot)
$fixturePolicy.enabled=$true
$fixtureRetirement=Invoke-GeneratedWorkspaceRetention -RootPath $WorkRoot -WorkspacePathS @($fixture) -Policy $fixturePolicy -ReferenceRoots @($WorkRoot) -Apply
Assert-Check (@($fixtureRetirement.deleted).Count -eq 1 -and -not (Test-Path -LiteralPath $fixture)) 'The entire completed fixture is retired officially; only the compact result remains.'
$report=[pscustomobject]@{passed=$checks.Count;checks=@($checks.ToArray());fixture=$fixture;fixtureRetired=$true;results=$applied;partialResults=$partialResults;budgetResult=$limited}
$report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $WorkRoot ('batch-entry-test-results-' + $PSVersionTable.PSEdition + '-' + $PSVersionTable.PSVersion.Major + '.json')) -Encoding utf8
Write-Output ('Passed ' + $checks.Count + ' checks; fixture ' + $fixture)
