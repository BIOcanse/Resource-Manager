[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$ModulePath,
    [string]$ManagerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$taskRoot = [System.IO.Path]::GetFullPath($WorkRoot)
if (-not [System.IO.Directory]::Exists($taskRoot)) { throw 'WorkRoot must be an existing managed test workspace.' }
$candidateRoot = Join-Path $taskRoot 'candidate-adoption'
if ([string]::IsNullOrWhiteSpace($ModulePath)) {
    $ModulePath = Join-Path $repositoryRoot 'scripts/maintenance/GeneratedWorkspaceRetention.psm1'
}
if ([string]::IsNullOrWhiteSpace($ManagerPath)) {
    $ManagerPath = Join-Path (Split-Path -Parent $ModulePath) 'Manage-GeneratedWorkspace.ps1'
}
Import-Module $modulePath -Force -DisableNameChecking
$module = Get-Module GeneratedWorkspaceRetention
$runId = [Guid]::NewGuid().ToString('N')
$fixturesRoot = Join-Path $candidateRoot ('fixturesRoot-' + $runId)
$referenceRoot = Join-Path $candidateRoot ('referenceRoot-' + $runId)
[void][System.IO.Directory]::CreateDirectory($fixturesRoot)
[void][System.IO.Directory]::CreateDirectory($referenceRoot)
$policy = [pscustomobject]@{
    schemaVersion = 1
    enabled = $false
    referenceRoots = @($fixturesRoot, $referenceRoot)
    defaults = [pscustomobject]@{ maxRecentCompleted = 0; maxBytes = 268435456; maxNodes = 750000; maxMilliseconds = 300000 }
    roots = @(
        [pscustomobject]@{ id = 'adoption-fixtures'; path = $fixturesRoot; excludedNames = @() },
        [pscustomobject]@{ id = 'adoption-reference-fixtures'; path = $referenceRoot; excludedNames = @() }
    )
}
$script:AssertionCount = 0
$script:Checks = [Collections.Generic.List[string]]::new()

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:AssertionCount++
    $script:Checks.Add($Message)
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Message, [string]$Pattern = '')
    try { & $Action }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($Pattern) -and $_.Exception.Message -notmatch $Pattern) {
            throw "Expected '$Pattern' for $Message; received '$($_.Exception.Message)' instead."
        }
        $script:AssertionCount++
        $script:Checks.Add($Message)
        return
    }
    throw "Expected failure: $Message"
}

function New-UnregisteredWorkspace {
    param([Parameter(Mandatory)][string]$RootPath, [Parameter(Mandatory)][string]$Name)
    $path = Join-Path $RootPath $Name
    [void][System.IO.Directory]::CreateDirectory($path)
    [System.IO.File]::WriteAllText((Join-Path $path 'payload.txt'), 'bounded adoption fixture')
    return $path
}

function New-Adoption {
    param([Parameter(Mandatory)][string]$WorkspacePath, [string]$Purpose = 'closed adoption regression fixture', [string[]]$References = @(), [string]$CompletedUtc = ([DateTime]::UtcNow.ToString('o')))
    return [pscustomobject]@{ WorkspacePath = $WorkspacePath; Purpose = $Purpose; CompletedUtc = $CompletedUtc; References = @($References) }
}

function Remove-FixturesThroughMaintenance {
    param([Parameter(Mandatory)][string[]]$Roots)
    $script:Policy.enabled = $false
    $script:Policy.defaults.maxRecentCompleted = 0
    foreach ($root in $Roots) {
        foreach ($directory in ([System.IO.DirectoryInfo]::new($root)).EnumerateDirectories()) {
            $state = Get-GeneratedWorkspaceState -RootPath $root -WorkspacePath $directory.FullName
            if ($null -eq $state.PSObject.Properties['registered'] -or -not $state.registered) {
                $marker = Join-Path $directory.FullName '.generated-workspace.json'
                if ([System.IO.File]::Exists($marker)) { [System.IO.File]::Delete($marker) }
                $null = Adopt-GeneratedWorkspace -RootPath $root -WorkspacePath $directory.FullName -Purpose 'closed disposable batch-adoption test fixture' -CompletedUtc ([DateTime]::UtcNow.ToString('o')) -Policy $script:Policy -ReferenceRoots $Roots
            }
            else {
                $null = Set-GeneratedWorkspaceKeep -RootPath $root -WorkspacePath $directory.FullName -Keep $false -Policy $script:Policy -ReferenceRoots $Roots
                if ($state.state -eq 'running') {
                    $null = Complete-GeneratedWorkspace -RootPath $root -WorkspacePath $directory.FullName -Outcome success -References @() -Policy $script:Policy -ReferenceRoots $Roots
                }
                elseif (@($state.references).Count -gt 0) {
                    $null = Set-GeneratedWorkspaceReferences -RootPath $root -WorkspacePath $directory.FullName -References @() -Policy $script:Policy -ReferenceRoots $Roots
                }
            }
        }
    }
    $script:Policy.enabled = $true
    $groups = [Collections.Generic.List[object]]::new()
    foreach ($root in $Roots) {
        $paths = @(([System.IO.DirectoryInfo]::new($root)).EnumerateDirectories() | ForEach-Object { $_.FullName })
        if ($paths.Count -gt 0) { $groups.Add([pscustomobject]@{ RootPath = $root; WorkspacePaths = $paths }) }
    }
    if ($groups.Count -gt 0) {
        $result = @(Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @($groups.ToArray()) -Policy $script:Policy -ReferenceRoots $Roots -Apply)
        if (@($result | ForEach-Object { $_.failures }).Count -gt 0) { throw 'Official maintenance failed to retire one or more test fixtures.' }
    }
}

$managedRoot = $fixturesRoot
$roots = @($managedRoot, $referenceRoot)
$referenceOwner = $null
$busyStream = $null
$junctionPath = $null
$writeHookInstalled = $false
$cliPolicyPath = Join-Path $candidateRoot ('adoption-policy-' + $runId + '.json')
$cliManifestPath = Join-Path $candidateRoot ('adoptions-' + $runId + '.json')

try {
    # Two already-closed unknown roots are adopted together. A completed owner
    # in another managed root protects A during the mandatory automatic sweep.
    $closedA = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'closed-a'
    $closedB = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'closed-b'
    $referenceOwner = Join-Path $referenceRoot 'external-owner'
    $null = New-GeneratedWorkspace -RootPath $referenceRoot -WorkspacePath $referenceOwner -Purpose 'external reference fixture' -References @($closedA) -Policy $policy -ReferenceRoots $roots
    $null = Complete-GeneratedWorkspace -RootPath $referenceRoot -WorkspacePath $referenceOwner -Outcome success -Policy $policy -ReferenceRoots $roots
    $policy.enabled = $true
    $batch = Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $closedA), (New-Adoption $closedB)) -Policy $policy -ReferenceRoots $roots
    $retainedA = @($batch.retention.retained | Where-Object { $_.path -eq $closedA })
    Assert-True ($batch.requested -eq 2 -and $batch.adopted.Count -eq 2 -and $batch.allAdopted) 'One batch writes both verified terminal adoptions.'
    Assert-True ((Get-GeneratedWorkspaceState -RootPath $managedRoot -WorkspacePath $closedA).state -eq 'completed') 'Adopted closed workspace A is recorded as completed.'
    Assert-True ($retainedA.Count -eq 1 -and (Test-Path -LiteralPath $closedA) -and -not (Test-Path -LiteralPath $closedB)) 'The mandatory enabled sweep preserves A through an external reference and prunes unreferenced B.'

    # Keep remains an ordinary lifecycle property respected by the same sweep.
    $policy.defaults.maxRecentCompleted = 8
    $keptPath = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'kept'
    $null = Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $keptPath)) -Policy $policy -ReferenceRoots $roots
    $null = Set-GeneratedWorkspaceKeep -RootPath $managedRoot -WorkspacePath $keptPath -Keep $true -Policy $policy -ReferenceRoots $roots
    $policy.defaults.maxRecentCompleted = 0
    $keepSweep = Invoke-GeneratedWorkspaceRetention -RootPath $managedRoot -Policy $policy -ReferenceRoots $roots -WorkspacePaths @($keptPath) -Apply
    Assert-True ((Test-Path -LiteralPath $keptPath) -and @($keepSweep.retained | Where-Object { $_.path -eq $keptPath }).Count -eq 1) 'Keep protects a completed adopted workspace from the finite sweep.'

    # An open exclusive handle also protects a completed adoption during sweep.
    $busyPath = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'busy'
    $busyStream = [System.IO.File]::Open((Join-Path $busyPath 'payload.txt'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    $busyBatch = Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $busyPath)) -Policy $policy -ReferenceRoots $roots
    Assert-True ((Test-Path -LiteralPath $busyPath) -and @($busyBatch.retention.retained | Where-Object { $_.path -eq $busyPath }).Count -eq 1) 'The automatic sweep leaves an adopted workspace in use by an exclusive file handle.'
    $busyStream.Dispose(); $busyStream = $null
    $busyCleanup = Invoke-GeneratedWorkspaceRetention -RootPath $managedRoot -Policy $policy -ReferenceRoots $roots -WorkspacePaths @($busyPath) -Apply
    Assert-True (@($busyCleanup.deleted).Count -eq 1 -and -not (Test-Path -LiteralPath $busyPath)) 'After the handle closes, the existing bounded retention API retires the completed fixture.'

    # Invalid path and duplicate inputs are rejected before the first marker write.
    $validBeforeReject = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'valid-before-reject'
    $outsidePath = Join-Path $referenceRoot '..\outside-managed-root'
    Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $validBeforeReject), (New-Adoption $outsidePath)) -Policy $policy -ReferenceRoots $roots } 'outside-root path is rejected before any write' 'outside its managed root'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $validBeforeReject '.generated-workspace.json'))) 'A later invalid path leaves earlier valid inputs unmarked.'
    $duplicatePath = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'duplicate'
    Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $duplicatePath), (New-Adoption $duplicatePath)) -Policy $policy -ReferenceRoots $roots } 'duplicate paths are rejected' 'Duplicate workspace'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $duplicatePath '.generated-workspace.json'))) 'Duplicate rejection occurs before the first marker write.'

    # Disabled retention, pre-existing markers, and reparse workspaces fail closed.
    $disabledPath = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'disabled'
    $policy.enabled = $false
    Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $disabledPath)) -Policy $policy -ReferenceRoots $roots } 'disabled policy is rejected' 'enabled finite retention'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $disabledPath '.generated-workspace.json'))) 'Disabled policy never writes a batch marker.'
    $policy.enabled = $true
    $existingPath = Join-Path $managedRoot 'existing-marker'
    $policy.enabled = $false
    $null = New-GeneratedWorkspace -RootPath $managedRoot -WorkspacePath $existingPath -Purpose 'existing marker guard fixture' -Policy $policy -ReferenceRoots $roots
    $existingMarker = Join-Path $existingPath '.generated-workspace.json'
    $existingHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $existingMarker).Hash
    $policy.enabled = $true
    Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $existingPath)) -Policy $policy -ReferenceRoots $roots } 'existing marker is refused' 'already has a lifecycle marker'
    Assert-True ((Get-FileHash -Algorithm SHA256 -LiteralPath $existingMarker).Hash -eq $existingHash) 'Existing marker bytes remain unchanged.'
    $junctionPath = Join-Path $managedRoot 'reparse-workspace'
    try {
        $null = New-Item -ItemType Junction -Path $junctionPath -Target $referenceRoot -ErrorAction Stop
        Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $junctionPath)) -Policy $policy -ReferenceRoots $roots } 'reparse workspace is refused' 'reparse point'
        [System.IO.Directory]::Delete($junctionPath)
        $junctionPath = $null
    }
    catch {
        if ($null -ne $junctionPath -and [System.IO.Directory]::Exists($junctionPath)) { [System.IO.Directory]::Delete($junctionPath) }
        $junctionPath = $null
        if ($_.Exception.Message -notmatch 'privilege|not supported|not permitted|Access is denied|requires elevation') { throw }
    }

    # Both the per-marker 1 MiB bound and the aggregate 8 MiB bound are checked
    # while planning, before any marker can be written.
    $oversizeSingle = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'oversize-single'
    Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $oversizeSingle ('x' * 1049000))) -Policy $policy -ReferenceRoots $roots } 'single marker payload is bounded' '1 MiB limit'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $oversizeSingle '.generated-workspace.json'))) 'Single oversized marker rejection happens before writes.'
    $aggregateItems = [Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 9; $index++) {
        $path = New-UnregisteredWorkspace -RootPath $managedRoot -Name ('aggregate-' + $index)
        $aggregateItems.Add((New-Adoption $path ('x' * 940000)))
    }
    Assert-Throws { Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @($aggregateItems.ToArray()) -Policy $policy -ReferenceRoots $roots } 'aggregate marker payload is bounded' '8 MiB bound'
    foreach ($item in $aggregateItems) { Assert-True (-not (Test-Path -LiteralPath (Join-Path $item.WorkspacePath '.generated-workspace.json'))) 'Aggregate-size rejection writes no partial batch markers.' }

    # Inject a one-time marker write failure. The exception must report only
    # paths actually written and stop before the failing and later entries.
    $policy.defaults.maxRecentCompleted = 8
    $partialA = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'partial-a'
    $partialB = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'partial-b'
    $partialC = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'partial-c'
    & $module {
        param($FailPath)
        $script:AdoptionFailPath = $FailPath
        $script:AdoptionOriginalWrite = (Get-Command Write-GWStateRecord).ScriptBlock
        function script:Write-GWStateRecord {
            param([Parameter(Mandatory)][object]$State)
            if ([string]::Equals([string]$State.workspacePath, $script:AdoptionFailPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Injected adoption write failure.'
            }
            & $script:AdoptionOriginalWrite @PSBoundParameters
        }
    } $partialB
    $writeHookInstalled = $true
    $partialError = $null
    try {
        $null = Adopt-GeneratedWorkspaceBatch -RootPath $managedRoot -Adoptions @((New-Adoption $partialA), (New-Adoption $partialB), (New-Adoption $partialC)) -Policy $policy -ReferenceRoots $roots
    }
    catch { $partialError = $_ }
    finally {
        if ($writeHookInstalled) {
            Import-Module $modulePath -Force -DisableNameChecking
            $module = Get-Module GeneratedWorkspaceRetention
            $writeHookInstalled = $false
        }
    }
    $progress = $null
    if ($null -ne $partialError -and $partialError.Exception.Data.Contains('adoption')) { $progress = $partialError.Exception.Data['adoption'] }
    Assert-True ($null -ne $progress -and $progress.adopted.Count -eq 1 -and $progress.adopted[0].workspacePath -eq $partialA -and $progress.failedWorkspace -eq $partialB) 'Partial write failure reports the one successful marker and the first failed path.'
    Assert-True (@($progress.remainingWorkspaces).Count -eq 2 -and $progress.remainingWorkspaces[0] -eq $partialB -and $progress.remainingWorkspaces[1] -eq $partialC) 'Partial progress identifies failed and never-attempted paths for verified retry.'
    Assert-True ((Test-Path -LiteralPath (Join-Path $partialA '.generated-workspace.json')) -and -not (Test-Path -LiteralPath (Join-Path $partialB '.generated-workspace.json')) -and -not (Test-Path -LiteralPath (Join-Path $partialC '.generated-workspace.json'))) 'Batch halts at the first write failure without claiming later markers.'

    # Exercise the candidate unified entry and its exclusive JSON input route.
    $policy.defaults.maxRecentCompleted = 8
    $cliWorkspace = New-UnregisteredWorkspace -RootPath $managedRoot -Name 'cli-adoption'
    $cliPolicyPath = Join-Path $candidateRoot ('adoption-policy-' + $runId + '.json')
    $cliManifestPath = Join-Path $candidateRoot ('adoptions-' + $runId + '.json')
    $policy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cliPolicyPath -Encoding utf8
    $entryJson = ConvertTo-Json -InputObject (New-Adoption $cliWorkspace 'candidate CLI closed fixture' @()) -Depth 8 -Compress
    [System.IO.File]::WriteAllText($cliManifestPath, ('[' + $entryJson + ']'), [System.Text.UTF8Encoding]::new($false))
    $cliOutput = @(& $managerPath -Action Adopt -RootId 'adoption-fixtures' -PolicyPath $cliPolicyPath -AdoptionsPath $cliManifestPath)
    $cliResult = ($cliOutput -join "`n") | ConvertFrom-Json
    Assert-True ($cliResult.requested -eq 1 -and $cliResult.adopted[0].workspacePath -eq $cliWorkspace -and (Test-Path -LiteralPath $cliWorkspace)) 'Candidate unified entry loads the bounded JSON array and returns the batch receipt.'
}
finally {
    if ($null -ne $busyStream) { $busyStream.Dispose() }
    if ($null -ne $junctionPath -and [System.IO.Directory]::Exists($junctionPath)) { [System.IO.Directory]::Delete($junctionPath) }
    if ($writeHookInstalled) { Import-Module $modulePath -Force -DisableNameChecking }
    Remove-FixturesThroughMaintenance -Roots $roots
}

$resultPath = Join-Path $candidateRoot ('adoption-batch-test-results-' + $PSVersionTable.PSEdition + '-' + $PSVersionTable.PSVersion.Major + '.json')
$testResult = [pscustomobject]@{
    passed = $script:AssertionCount
    checks = @($script:Checks.ToArray())
    fixturesRoot = $fixturesRoot
    referenceRoot = $referenceRoot
    fixturesRetiredThroughMaintenance = $true
    cliResult = $cliResult
}
$testResult | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $resultPath -Encoding utf8
Write-Output ("GeneratedWorkspaceAdoptionBatch.Tests passed ($($script:AssertionCount) assertions); results: $resultPath")
