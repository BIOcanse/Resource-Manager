[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/maintenance/GeneratedWorkspaceRetention.psm1'
Import-Module $modulePath -Force -DisableNameChecking

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:AssertionCount++
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Message)
    try { & $Action }
    catch { $script:AssertionCount++; return }
    throw "Expected failure: $Message"
}

function New-TestWorkspace {
    param([string]$RootPath, [string]$Name, [string[]]$References = @())
    $path = Join-Path $RootPath $Name
    $null = New-GeneratedWorkspace -RootPath $RootPath -WorkspacePath $path -Purpose 'retention test fixture' -References $References -Policy $script:Policy -ReferenceRoots @($RootPath)
    return $path
}

function Complete-TestWorkspace {
    param([string]$RootPath, [string]$WorkspacePath, [ValidateSet('success', 'failed')][string]$Outcome = 'success')
    $null = Complete-GeneratedWorkspace -RootPath $RootPath -WorkspacePath $WorkspacePath -Outcome $Outcome -Policy $script:Policy -ReferenceRoots @($RootPath)
}

function Write-FixtureBytes {
    param([string]$Path, [int]$Count)
    $bytes = [byte[]]::new($Count)
    for ($index = 0; $index -lt $Count; $index++) { $bytes[$index] = [byte](($index % 251) + 1) }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Remove-TestFixtureTreeBounded {
    param([Parameter(Mandatory)][string]$Path)

    $root = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    $expectedTempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $root.StartsWith($expectedTempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($root) -notmatch '^generated-workspace-retention-[a-f0-9]{32}$') {
        throw "Refusing to remove an unexpected fixture path: $root"
    }
    if (-not [System.IO.Directory]::Exists($root)) { return }
    $rootInfo = Get-Item -LiteralPath $root -Force
    if (($rootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to clean a reparse-point fixture root: $root"
    }

    $maxNodes = 100000
    $maxMilliseconds = 30000
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $files = [System.Collections.Generic.List[string]]::new()
    $directories = [System.Collections.Generic.List[string]]::new()
    $reparsePoints = [System.Collections.Generic.List[object]]::new()
    $pending.Push($root)
    $nodes = 0
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in ([System.IO.DirectoryInfo]::new($current)).EnumerateFileSystemInfos()) {
            $nodes++
            if ($nodes -gt $maxNodes -or $timer.ElapsedMilliseconds -gt $maxMilliseconds) {
                throw "Fixture cleanup scan exceeded its bounds (nodes=$nodes/$maxNodes, elapsedMs=$($timer.ElapsedMilliseconds)/$maxMilliseconds)."
            }
            $entry.Refresh()
            $entryPath = [System.IO.Path]::GetFullPath($entry.FullName)
            if (-not $entryPath.StartsWith(($root + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Fixture cleanup found a path outside its root: $entryPath"
            }
            $isDirectory = ($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $reparsePoints.Add([pscustomobject]@{ Path = $entryPath; IsDirectory = $isDirectory })
            }
            elseif ($isDirectory) {
                $directories.Add($entryPath)
                $pending.Push($entryPath)
            }
            else {
                $files.Add($entryPath)
            }
        }
    }

    foreach ($filePath in $files) {
        if ($timer.ElapsedMilliseconds -gt $maxMilliseconds) {
            throw "Fixture cleanup occupancy check exceeded its time bound ($($timer.ElapsedMilliseconds)/$maxMilliseconds ms)."
        }
        $stream = [System.IO.File]::Open($filePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        $stream.Dispose()
    }

    foreach ($filePath in $files) {
        if ($timer.ElapsedMilliseconds -gt $maxMilliseconds) {
            throw "Fixture cleanup file deletion exceeded its time bound ($($timer.ElapsedMilliseconds)/$maxMilliseconds ms)."
        }
        if (-not $filePath.StartsWith(($root + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Fixture cleanup file escaped its root: $filePath"
        }
        [System.IO.File]::Delete($filePath)
    }
    foreach ($entry in $reparsePoints) {
        if ($timer.ElapsedMilliseconds -gt $maxMilliseconds) {
            throw "Fixture cleanup reparse deletion exceeded its time bound ($($timer.ElapsedMilliseconds)/$maxMilliseconds ms)."
        }
        if (-not $entry.Path.StartsWith(($root + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Fixture cleanup reparse point escaped its root: $($entry.Path)"
        }
        if ($entry.IsDirectory) { [System.IO.Directory]::Delete($entry.Path, $false) }
        else { [System.IO.File]::Delete($entry.Path) }
    }
    foreach ($directoryPath in ($directories | Sort-Object { $_.Length } -Descending)) {
        if ($timer.ElapsedMilliseconds -gt $maxMilliseconds) {
            throw "Fixture cleanup directory deletion exceeded its time bound ($($timer.ElapsedMilliseconds)/$maxMilliseconds ms)."
        }
        if (-not $directoryPath.StartsWith(($root + [System.IO.Path]::DirectorySeparatorChar), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Fixture cleanup directory escaped its root: $directoryPath"
        }
        [System.IO.Directory]::Delete($directoryPath, $false)
    }
    [System.IO.Directory]::Delete($root, $false)
}

function New-FixturePolicy {
    param([string[]]$RootPaths)
    $roots = [System.Collections.Generic.List[object]]::new()
    $index = 0
    foreach ($path in $RootPaths) {
        $index++
        $roots.Add([pscustomobject]@{ id = "fixture-$index"; path = $path; excludedNames = @() })
    }
    return [pscustomobject]@{
        schemaVersion = 1
        enabled = $true
        referenceRoots = @()
        defaults = [pscustomobject]@{
            maxRecentCompleted = 10
            maxBytes = [long]::MaxValue
            maxNodes = 100000
            maxMilliseconds = 60000
        }
        roots = @($roots.ToArray())
    }
}

$tempBase = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) ('generated-workspace-retention-' + [guid]::NewGuid().ToString('N'))))
$script:AssertionCount = 0
$rootPaths = @(
    (Join-Path $tempBase 'count-root'),
    (Join-Path $tempBase 'size-root'),
    (Join-Path $tempBase 'state-root'),
    (Join-Path $tempBase 'locked-root'),
    (Join-Path $tempBase 'reparse-root'),
    (Join-Path $tempBase 'reference-source-root'),
    (Join-Path $tempBase 'reference-target-root'),
    (Join-Path $tempBase 'escape-root'),
    (Join-Path $tempBase 'concurrent-root'),
    (Join-Path $tempBase 'keep-count-root'),
    (Join-Path $tempBase 'keep-size-root'),
    (Join-Path $tempBase 'marker-size-root'),
    (Join-Path $tempBase 'reference-budget-root')
)

try {
    foreach ($rootPath in $rootPaths) { [System.IO.Directory]::CreateDirectory($rootPath) | Out-Null }
    $script:Policy = New-FixturePolicy -RootPaths $rootPaths
    $script:ReferenceRoots = $rootPaths

    # Count retention keeps the two newest completed workspaces and is idempotent.
    $countRoot = $rootPaths[0]
    $script:Policy.defaults.maxRecentCompleted = 2
    $script:Policy.defaults.maxBytes = [long]::MaxValue
    $countOne = New-TestWorkspace -RootPath $countRoot -Name 'count-one'
    $countOneMarker = [System.IO.File]::ReadAllText((Join-Path $countOne '.generated-workspace.json'))
    $countOneMarkerState = $countOneMarker | ConvertFrom-Json
    Assert-True ($null -ne $countOneMarkerState.references -and $countOneMarkerState.references -is [Array] -and $countOneMarkerState.references.Count -eq 0) 'new workspaces persist an empty references array rather than null.'
    $countOneState = Get-GeneratedWorkspaceState -RootPath $countRoot -WorkspacePath $countOne
    Assert-True ($countOneState.references -is [Array] -and $countOneState.references.Count -eq 0) 'state reads preserve empty references as a typed empty array.'
    Write-FixtureBytes -Path (Join-Path $countOne 'payload.bin') -Count 8
    Complete-TestWorkspace -RootPath $countRoot -WorkspacePath $countOne
    Start-Sleep -Milliseconds 30
    $countTwo = New-TestWorkspace -RootPath $countRoot -Name 'count-two'
    Write-FixtureBytes -Path (Join-Path $countTwo 'payload.bin') -Count 8
    Complete-TestWorkspace -RootPath $countRoot -WorkspacePath $countTwo
    Start-Sleep -Milliseconds 30
    $countThree = New-TestWorkspace -RootPath $countRoot -Name 'count-three'
    Write-FixtureBytes -Path (Join-Path $countThree 'payload.bin') -Count 8
    Complete-TestWorkspace -RootPath $countRoot -WorkspacePath $countThree
    Assert-True (-not (Test-Path -LiteralPath $countOne)) 'count policy deletes the oldest completed workspace.'
    Assert-True (Test-Path -LiteralPath $countTwo) 'count policy keeps the second newest workspace.'
    Assert-True (Test-Path -LiteralPath $countThree) 'count policy keeps the newest workspace.'
    $repeat = Invoke-GeneratedWorkspaceRetention -RootPath $countRoot -Policy $script:Policy -ReferenceRoots @($countRoot) -Apply
    Assert-True (@($repeat.deleted).Count -eq 0) 'repeating retention makes no additional deletions.'

    # Size pressure can evict the oldest workspace even when it is among the newest three.
    $sizeRoot = $rootPaths[1]
    $script:Policy.defaults.maxRecentCompleted = 3
    $script:Policy.defaults.maxBytes = [long]::MaxValue
    $sizeOne = New-TestWorkspace -RootPath $sizeRoot -Name 'size-one'
    Write-FixtureBytes -Path (Join-Path $sizeOne 'payload.bin') -Count 4096
    Complete-TestWorkspace -RootPath $sizeRoot -WorkspacePath $sizeOne
    Start-Sleep -Milliseconds 30
    $sizeTwo = New-TestWorkspace -RootPath $sizeRoot -Name 'size-two'
    Write-FixtureBytes -Path (Join-Path $sizeTwo 'payload.bin') -Count 4096
    Complete-TestWorkspace -RootPath $sizeRoot -WorkspacePath $sizeTwo
    Start-Sleep -Milliseconds 30
    $sizeThree = New-TestWorkspace -RootPath $sizeRoot -Name 'size-three'
    Write-FixtureBytes -Path (Join-Path $sizeThree 'payload.bin') -Count 4096
    Complete-TestWorkspace -RootPath $sizeRoot -WorkspacePath $sizeThree
    $retentionModule = Get-Module GeneratedWorkspaceRetention
    $sizeCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy } $sizeRoot $script:Policy
    $sizeCandidate = @($sizeCatalog.Items | Where-Object { [string]::Equals($_.Path, $sizeOne, [System.StringComparison]::OrdinalIgnoreCase) })[0]
    $sizeFileMap = $sizeCandidate.Snapshot.FileEntries
    Assert-True ($sizeCandidate.Snapshot.DeletionSnapshotCaptured -and $sizeFileMap.GetType().Name -eq 'Dictionary`2' -and $sizeFileMap.ContainsKey((Join-Path $sizeOne 'payload.bin')) -and $sizeFileMap[(Join-Path $sizeOne 'payload.bin')].Item1 -eq 4096) 'eligible deletion snapshots use a case-insensitive dictionary of compact file stamps.'
    $changedPayload = Join-Path $sizeOne 'payload.bin'
    [System.IO.File]::SetLastWriteTimeUtc($changedPayload, [System.IO.File]::GetLastWriteTimeUtc($changedPayload).AddSeconds(2))
    Assert-Throws { & $retentionModule { param($item, $limits) Remove-GWWorkspaceSnapshot -Item $item -Limits $limits } $sizeCandidate $sizeCatalog.Limits } 'a fresh enumeration rejects a timestamp change after planning even when file length is unchanged.'
    Assert-True (Test-Path -LiteralPath $changedPayload) 'snapshot mismatch leaves the changed workspace intact.'
    $sizeBefore = Invoke-GeneratedWorkspaceRetention -RootPath $sizeRoot -Policy $script:Policy -ReferenceRoots @($sizeRoot)
    $script:Policy.defaults.maxBytes = [long]$sizeBefore.totalBytes - 1
    $sizeResult = Invoke-GeneratedWorkspaceRetention -RootPath $sizeRoot -Policy $script:Policy -ReferenceRoots @($sizeRoot) -Apply
    Assert-True (@($sizeResult.deleted | Where-Object { $_.path -eq $sizeOne }).Count -eq 1) 'size policy evicts the oldest of the newest three.'
    Assert-True (-not (Test-Path -LiteralPath $sizeOne)) 'oldest recent workspace was removed under size pressure.'
    Assert-True ($sizeResult.projectedBytes -eq ($sizeBefore.totalBytes - [long]($sizeResult.deleted | Measure-Object removedBytes -Sum).Sum)) 'post-apply accounting subtracts successful deletions instead of adding them back.'
    Assert-True ((Test-Path -LiteralPath $sizeTwo) -and (Test-Path -LiteralPath $sizeThree)) 'newer recent workspaces survive size pressure.'

    # Running, Keep, unknown legacy, and failed completion lifecycle states.
    $stateRoot = $rootPaths[2]
    $script:Policy.defaults.maxRecentCompleted = 0
    $script:Policy.defaults.maxBytes = 0
    $legacy = Join-Path $stateRoot 'unknown-legacy'
    [System.IO.Directory]::CreateDirectory($legacy) | Out-Null
    Write-FixtureBytes -Path (Join-Path $legacy 'legacy.bin') -Count 16
    $stateCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy } $stateRoot $script:Policy
    $legacyItem = @($stateCatalog.Items | Where-Object { [string]::Equals($_.Path, $legacy, [System.StringComparison]::OrdinalIgnoreCase) })[0]
    Assert-True ($legacyItem.Bytes -eq 16 -and $legacyItem.SizeKnown -and $null -eq $legacyItem.Snapshot.FileEntries -and @($legacyItem.Snapshot.Files).Count -eq 0 -and @($legacyItem.Snapshot.Directories).Count -eq 0) 'protected target workspaces are byte-accounted without retaining per-file or directory entries.'
    $running = New-TestWorkspace -RootPath $stateRoot -Name 'still-running'
    Write-FixtureBytes -Path (Join-Path $running 'payload.bin') -Count 16
    $failed = New-TestWorkspace -RootPath $stateRoot -Name 'failed-terminal'
    Write-FixtureBytes -Path (Join-Path $failed 'payload.bin') -Count 16
    $script:Policy.enabled = $false
    $createdBeforeComplete = (Get-GeneratedWorkspaceState -RootPath $stateRoot -WorkspacePath $failed).createdUtc
    Complete-TestWorkspace -RootPath $stateRoot -WorkspacePath $failed -Outcome failed
    $failedState = Get-GeneratedWorkspaceState -RootPath $stateRoot -WorkspacePath $failed
    Assert-True ($failedState.state -eq 'completed' -and $failedState.outcome -eq 'failed') 'failed workspaces can be explicitly completed.'
    Assert-True ($failedState.createdUtc -ceq $createdBeforeComplete -and $failedState.createdUtc -match '^\d{4}-\d{2}-\d{2}T.*Z$' -and $failedState.completedUtc -match '^\d{4}-\d{2}-\d{2}T.*Z$') 'lifecycle timestamp copies remain stable UTC round-trip text.'
    $failedMarker = [System.IO.File]::ReadAllText((Join-Path $failed '.generated-workspace.json'))
    $failedMarkerState = $failedMarker | ConvertFrom-Json
    Assert-True ($failedMarkerState.references -is [Array] -and $failedMarkerState.references.Count -eq 0) 'state copies preserve empty references arrays.'
    $script:Policy.enabled = $true
    $stateResult = Invoke-GeneratedWorkspaceRetention -RootPath $stateRoot -Policy $script:Policy -ReferenceRoots @($stateRoot) -Apply
    Assert-True (Test-Path -LiteralPath $running) 'running workspace remains protected.'
    Assert-True (Test-Path -LiteralPath $legacy) 'unknown legacy workspace remains protected.'
    Assert-True (-not (Test-Path -LiteralPath $failed)) 'explicitly completed failed workspace is eligible for retention.'
    Assert-True ($stateResult.overBudget) 'running and unknown workspaces can leave the root over budget.'
    Assert-Throws { New-GeneratedWorkspace -RootPath $stateRoot -WorkspacePath $legacy -Purpose 'must reject existing legacy' -Policy $script:Policy -ReferenceRoots @($stateRoot) } 'New rejects an unknown existing workspace.'

    $adopted = Adopt-GeneratedWorkspace -RootPath $stateRoot -WorkspacePath $legacy -Purpose 'verified legacy fixture' -Policy $script:Policy -ReferenceRoots @($stateRoot)
    Assert-True ($adopted.workspace.state -eq 'running') 'Adopt defaults to running and does not infer completion.'
    $script:Policy.enabled = $false
    Complete-TestWorkspace -RootPath $stateRoot -WorkspacePath $legacy -Outcome success
    $legacyState = Get-GeneratedWorkspaceState -RootPath $stateRoot -WorkspacePath $legacy
    Assert-True ($legacyState.state -eq 'completed') 'adopted workspace transitions only after explicit completion.'
    $script:Policy.enabled = $true

    # Keep excludes a completed workspace from both count and size candidate plans.
    $countKeepRoot = $rootPaths[9]
    $script:Policy.enabled = $false
    $script:Policy.defaults.maxRecentCompleted = 0
    $script:Policy.defaults.maxBytes = [long]::MaxValue
    $countKeepWorkspace = New-TestWorkspace -RootPath $countKeepRoot -Name 'kept-count-candidate'
    Write-FixtureBytes -Path (Join-Path $countKeepWorkspace 'payload.bin') -Count 16
    Complete-TestWorkspace -RootPath $countKeepRoot -WorkspacePath $countKeepWorkspace
    $null = Set-GeneratedWorkspaceKeep -RootPath $countKeepRoot -WorkspacePath $countKeepWorkspace -Keep $true -Policy $script:Policy -ReferenceRoots @($countKeepRoot)
    $script:Policy.enabled = $true
    $countKeepResult = Invoke-GeneratedWorkspaceRetention -RootPath $countKeepRoot -Policy $script:Policy -ReferenceRoots @($countKeepRoot) -Apply
    Assert-True (@($countKeepResult.planned).Count -eq 0) 'count retention does not plan an old kept workspace.'
    Assert-True (@($countKeepResult.retained | Where-Object { $_.path -eq $countKeepWorkspace -and $_.reason -match 'Keep' }).Count -eq 1) 'count retention reports the kept workspace as retained.'
    Assert-True (@($countKeepResult.failures).Count -eq 0) 'count retention does not defer Keep protection to a deletion failure.'

    $sizeKeepRoot = $rootPaths[10]
    $script:Policy.enabled = $false
    $script:Policy.defaults.maxRecentCompleted = 2
    $script:Policy.defaults.maxBytes = [long]::MaxValue
    $sizeKeepWorkspace = New-TestWorkspace -RootPath $sizeKeepRoot -Name 'kept-size-candidate'
    Write-FixtureBytes -Path (Join-Path $sizeKeepWorkspace 'payload.bin') -Count 16
    Complete-TestWorkspace -RootPath $sizeKeepRoot -WorkspacePath $sizeKeepWorkspace
    $null = Set-GeneratedWorkspaceKeep -RootPath $sizeKeepRoot -WorkspacePath $sizeKeepWorkspace -Keep $true -Policy $script:Policy -ReferenceRoots @($sizeKeepRoot)
    Start-Sleep -Milliseconds 30
    $sizeNormalWorkspace = New-TestWorkspace -RootPath $sizeKeepRoot -Name 'ordinary-size-candidate'
    Write-FixtureBytes -Path (Join-Path $sizeNormalWorkspace 'payload.bin') -Count 16
    Complete-TestWorkspace -RootPath $sizeKeepRoot -WorkspacePath $sizeNormalWorkspace
    $script:Policy.enabled = $true
    $sizeKeepBefore = Invoke-GeneratedWorkspaceRetention -RootPath $sizeKeepRoot -Policy $script:Policy -ReferenceRoots @($sizeKeepRoot)
    $script:Policy.defaults.maxBytes = [long]$sizeKeepBefore.totalBytes - 1
    $sizeKeepResult = Invoke-GeneratedWorkspaceRetention -RootPath $sizeKeepRoot -Policy $script:Policy -ReferenceRoots @($sizeKeepRoot) -Apply
    Assert-True (@($sizeKeepResult.planned | Where-Object { $_.path -eq $sizeKeepWorkspace }).Count -eq 0) 'size retention does not plan a kept workspace.'
    Assert-True (@($sizeKeepResult.retained | Where-Object { $_.path -eq $sizeKeepWorkspace -and $_.reason -match 'Keep' }).Count -eq 1) 'size retention reports the kept workspace as retained.'
    Assert-True (@($sizeKeepResult.failures).Count -eq 0) 'size retention reports Keep protection without a failed deletion.'
    Assert-True ((Test-Path -LiteralPath $sizeKeepWorkspace) -and -not (Test-Path -LiteralPath $sizeNormalWorkspace)) 'size pressure removes the ordinary candidate while preserving Keep.'

    # Marker reads and writes share a one MiB hard limit.
    $markerSizeRoot = $rootPaths[11]
    $script:Policy.enabled = $false
    $longReferencePrefix = Join-Path $tempBase ('oversized-reference-' + ('x' * 110))
    $longReferenceList = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt 6000; $index++) { $longReferenceList.Add($longReferencePrefix + '-' + $index.ToString('D5')) }
    $oversizedReferences = [string[]]$longReferenceList.ToArray()
    $oversizedNewPath = Join-Path $markerSizeRoot 'oversized-new'
    Assert-Throws { New-GeneratedWorkspace -RootPath $markerSizeRoot -WorkspacePath $oversizedNewPath -Purpose 'oversized marker new' -References $oversizedReferences -Policy $script:Policy -ReferenceRoots @($markerSizeRoot) } 'New rejects a marker payload larger than one MiB.'
    Assert-True (-not (Test-Path -LiteralPath $oversizedNewPath)) 'oversized New leaves no unregistered workspace directory.'

    $referencesWorkspace = New-TestWorkspace -RootPath $markerSizeRoot -Name 'references-writer'
    Assert-Throws { Set-GeneratedWorkspaceReferences -RootPath $markerSizeRoot -WorkspacePath $referencesWorkspace -References $oversizedReferences -Policy $script:Policy -ReferenceRoots @($markerSizeRoot) } 'References rejects a marker payload larger than one MiB.'
    $referencesState = Get-GeneratedWorkspaceState -RootPath $markerSizeRoot -WorkspacePath $referencesWorkspace
    Assert-True (@($referencesState.references).Count -eq 0) 'oversized References leaves the last valid marker unchanged.'

    $oversizedMarkerWorkspace = New-TestWorkspace -RootPath $markerSizeRoot -Name 'oversized-existing-marker'
    $oversizedMarkerPath = Join-Path $oversizedMarkerWorkspace '.generated-workspace.json'
    $oversizedMarkerStream = [System.IO.File]::Open($oversizedMarkerPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try { $oversizedMarkerStream.SetLength(1048577) }
    finally { $oversizedMarkerStream.Dispose() }
    $oversizedMarkerRead = Get-GeneratedWorkspaceState -RootPath $markerSizeRoot -WorkspacePath $oversizedMarkerWorkspace
    Assert-True (-not $oversizedMarkerRead.registered -and $oversizedMarkerRead.reason -match '1 MiB limit') 'oversized marker reads are rejected before JSON parsing.'

    # The catalog can pass its own limit while bounded graph planning safely refuses deletion.
    $referenceBudgetRoot = $rootPaths[12]
    $referenceBudgetTarget = New-TestWorkspace -RootPath $referenceBudgetRoot -Name 'target'
    $referenceBudgetTargetFile = Join-Path $referenceBudgetTarget 'payload.bin'
    Write-FixtureBytes -Path $referenceBudgetTargetFile -Count 8
    Start-Sleep -Milliseconds 30
    $referenceBudgetSource = New-TestWorkspace -RootPath $referenceBudgetRoot -Name 'source' -References @($referenceBudgetTargetFile)
    $script:Policy.defaults.maxNodes = 100000
    $script:Policy.defaults.maxRecentCompleted = 10
    $script:Policy.defaults.maxBytes = [long]::MaxValue
    Complete-TestWorkspace -RootPath $referenceBudgetRoot -WorkspacePath $referenceBudgetTarget
    Start-Sleep -Milliseconds 30
    Complete-TestWorkspace -RootPath $referenceBudgetRoot -WorkspacePath $referenceBudgetSource
    $script:Policy.enabled = $true
    $script:Policy.defaults.maxNodes = 6
    $script:Policy.defaults.maxRecentCompleted = 1
    $script:Policy.defaults.maxBytes = 0
    $retentionModule = Get-Module GeneratedWorkspaceRetention
    $referenceBudgetCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy } $referenceBudgetRoot $script:Policy
    Assert-True ($referenceBudgetCatalog.Complete -and $referenceBudgetCatalog.GraphComplete) 'the low-budget fixture catalog completes within its configured node limit.'
    $referenceBudgetResult = Invoke-GeneratedWorkspaceRetention -RootPath $referenceBudgetRoot -Policy $script:Policy -ReferenceRoots @($referenceBudgetRoot) -Apply
    Assert-True ($referenceBudgetResult.safeToPrune -eq $false) 'reference planning reports unsafe when its shared budget is exceeded.'
    Assert-True (@($referenceBudgetResult.planned).Count -eq 0 -and @($referenceBudgetResult.deleted).Count -eq 0) 'reference planning budget exhaustion returns no plan and performs no deletion.'
    Assert-True ((Test-Path -LiteralPath $referenceBudgetTarget) -and (Test-Path -LiteralPath $referenceBudgetSource)) 'all workspaces remain after reference planning exceeds its bound.'
    $script:Policy.defaults.maxNodes = 100000
    $script:Policy.defaults.maxRecentCompleted = 10
    $script:Policy.defaults.maxBytes = [long]::MaxValue

    # Completed workspaces in a non-target reference root are survivors and protect targets transitively.
    $sourceRoot = $rootPaths[5]
    $targetRoot = $rootPaths[6]
    $script:Policy.enabled = $false
    $source = New-TestWorkspace -RootPath $sourceRoot -Name 'old-source'
    $middle = New-TestWorkspace -RootPath $targetRoot -Name 'middle-target'
    Write-FixtureBytes -Path (Join-Path $middle 'middle.bin') -Count 4
    $leaf = New-TestWorkspace -RootPath $targetRoot -Name 'leaf-target'
    Write-FixtureBytes -Path (Join-Path $leaf 'leaf.bin') -Count 4
    $null = Set-GeneratedWorkspaceReferences -RootPath $sourceRoot -WorkspacePath $source -References @((Join-Path $middle 'middle.bin')) -Policy $script:Policy -ReferenceRoots @($sourceRoot, $targetRoot)
    $null = Set-GeneratedWorkspaceReferences -RootPath $targetRoot -WorkspacePath $middle -References @((Join-Path $leaf 'leaf.bin')) -Policy $script:Policy -ReferenceRoots @($sourceRoot, $targetRoot)
    Complete-TestWorkspace -RootPath $targetRoot -WorkspacePath $leaf
    Complete-TestWorkspace -RootPath $targetRoot -WorkspacePath $middle
    Complete-TestWorkspace -RootPath $sourceRoot -WorkspacePath $source
    $script:Policy.enabled = $true
    $script:Policy.defaults.maxRecentCompleted = 0
    $script:Policy.defaults.maxBytes = 0
    $sourceMetadataCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy -MetadataOnly } $sourceRoot $script:Policy
    $sourceMetadataItem = @($sourceMetadataCatalog.Items | Where-Object { [string]::Equals($_.Path, $source, [System.StringComparison]::OrdinalIgnoreCase) })[0]
    Assert-True ($sourceMetadataCatalog.Complete -and $sourceMetadataCatalog.GraphComplete -and $null -eq $sourceMetadataItem.Snapshot -and @($sourceMetadataItem.References).Count -eq 1 -and $sourceMetadataItem.References[0] -eq (Join-Path $middle 'middle.bin')) 'non-target reference catalogs read lifecycle edges without scanning workspace trees.'
    $referenced = Invoke-GeneratedWorkspaceRetention -RootPath $targetRoot -Policy $script:Policy -ReferenceRoots @($sourceRoot) -Apply
    Assert-True ((Test-Path -LiteralPath $middle) -and (Test-Path -LiteralPath $leaf)) 'an old completed workspace in a non-target root protects a referenced file owner and its transitive dependency.'
    Assert-True (@($referenced.retained | Where-Object { $_.path -eq $middle -or $_.path -eq $leaf }).Count -eq 2) 'reference report contains both protected workspaces.'

    $script:Policy.enabled = $false
    $null = Set-GeneratedWorkspaceKeep -RootPath $sourceRoot -WorkspacePath $source -Keep $true -Policy $script:Policy -ReferenceRoots @($targetRoot)
    $script:Policy.enabled = $true
    $keptReference = Invoke-GeneratedWorkspaceRetention -RootPath $targetRoot -Policy $script:Policy -ReferenceRoots @($sourceRoot) -Apply
    Assert-True ((Test-Path -LiteralPath $middle) -and (Test-Path -LiteralPath $leaf)) 'a Keep workspace also protects its referenced dependency chain.'
    Assert-True (@($keptReference.retained | Where-Object { $_.path -eq $middle -or $_.path -eq $leaf }).Count -eq 2) 'Keep-protected references remain visible in the retention report.'

    # Candidate workspaces do not protect their own outgoing dependency chain.
    $script:Policy.enabled = $false
    $null = Set-GeneratedWorkspaceKeep -RootPath $sourceRoot -WorkspacePath $source -Keep $false -Policy $script:Policy -ReferenceRoots @($targetRoot)
    $null = Set-GeneratedWorkspaceReferences -RootPath $sourceRoot -WorkspacePath $source -References @() -Policy $script:Policy -ReferenceRoots @($targetRoot)
    $script:Policy.enabled = $true
    $unreferenced = Invoke-GeneratedWorkspaceRetention -RootPath $targetRoot -Policy $script:Policy -ReferenceRoots @($sourceRoot) -Apply
    Assert-True (-not (Test-Path -LiteralPath $middle) -and -not (Test-Path -LiteralPath $leaf)) 'completed candidates do not preserve their own outgoing dependency chain.'

    # When managed roots nest, a referenced file belongs to its most specific workspace.
    $nestedParentRoot = Join-Path $tempBase 'nested-parent-root'
    $nestedSourceRoot = Join-Path $tempBase 'nested-reference-source-root'
    [System.IO.Directory]::CreateDirectory($nestedParentRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($nestedSourceRoot) | Out-Null
    $nestedInnerRoot = Join-Path (Join-Path $nestedParentRoot 'outer-task') '.validation/batches'
    $script:Policy.roots = @($script:Policy.roots) + @(
        [pscustomobject]@{ id = 'nested-parent'; path = $nestedParentRoot; excludedNames = @() },
        [pscustomobject]@{ id = 'nested-inner'; path = $nestedInnerRoot; excludedNames = @() },
        [pscustomobject]@{ id = 'nested-source'; path = $nestedSourceRoot; excludedNames = @() }
    )
    $script:Policy.enabled = $false
    $outerTask = New-TestWorkspace -RootPath $nestedParentRoot -Name 'outer-task'
    [System.IO.Directory]::CreateDirectory($nestedInnerRoot) | Out-Null
    $nestedBatch = New-TestWorkspace -RootPath $nestedInnerRoot -Name 'batch-one'
    $nestedPayload = Join-Path $nestedBatch 'payload.bin'
    Write-FixtureBytes -Path $nestedPayload -Count 8
    $nestedSource = New-TestWorkspace -RootPath $nestedSourceRoot -Name 'source-one'
    $null = Set-GeneratedWorkspaceReferences -RootPath $nestedSourceRoot -WorkspacePath $nestedSource -References @($nestedPayload) -Policy $script:Policy -ReferenceRoots @($nestedParentRoot, $nestedInnerRoot, $nestedSourceRoot)
    Complete-TestWorkspace -RootPath $nestedParentRoot -WorkspacePath $outerTask
    Complete-TestWorkspace -RootPath $nestedInnerRoot -WorkspacePath $nestedBatch
    Complete-TestWorkspace -RootPath $nestedSourceRoot -WorkspacePath $nestedSource
    $script:Policy.enabled = $true
    $retentionModule = Get-Module GeneratedWorkspaceRetention
    $parentCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy } $nestedParentRoot $script:Policy
    $innerCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy } $nestedInnerRoot $script:Policy
    $sourceCatalog = & $retentionModule { param($rootPath, $policy) Get-GWRootCatalog -RootPath $rootPath -Policy $policy } $nestedSourceRoot $script:Policy
    $nestedItems = @($parentCatalog.Items) + @($innerCatalog.Items) + @($sourceCatalog.Items)
    $nestedOwnerBudget = & $retentionModule { param($limits) New-GWScanBudget -Limits $limits } $sourceCatalog.Limits
    $nestedOwnerCheck = & $retentionModule {
        param([object[]]$Items, [string]$ReferencePath, [string]$SourcePath, [string]$RootPath, [object]$Budget)
        $owner = Get-GWItemOwner -ReferencePath $ReferencePath -Items $Items -Budget $Budget -RootPath $RootPath
        $caseOwner = Get-GWItemOwner -ReferencePath $ReferencePath.ToUpperInvariant() -Items $Items -Budget $Budget -RootPath $RootPath
        $siblingOwner = Get-GWItemOwner -ReferencePath ($Items[1].Path + '-sibling\payload.bin') -Items $Items -Budget $Budget -RootPath $RootPath
        $sourceItem = @($Items | Where-Object { [string]::Equals($_.Path, $SourcePath, [System.StringComparison]::OrdinalIgnoreCase) })[0]
        $closure = Get-GWReferenceClosure -Items $Items -SeedItems @($sourceItem) -Budget $Budget -RootPath $RootPath
        [pscustomobject]@{ OwnerPath = $owner.Path; CaseOwnerPath = $caseOwner.Path; SiblingOwnerPath = $siblingOwner.Path; Closure = $closure; ParentPath = $Items[0].Path; InnerPath = $Items[1].Path }
    } $nestedItems $nestedPayload $nestedSource $nestedSourceRoot $nestedOwnerBudget
    Assert-True ($nestedOwnerCheck.OwnerPath -eq $nestedBatch) 'nested reference ownership chooses the inner batch after the parent root item.'
    Assert-True ($nestedOwnerCheck.CaseOwnerPath -eq $nestedBatch -and $nestedOwnerCheck.SiblingOwnerPath -eq $outerTask) 'indexed ownership is case-insensitive and does not confuse a sibling path prefix with the nested workspace.'
    Assert-True ($nestedOwnerCheck.Closure.ReferencedKeys.Contains($nestedOwnerCheck.InnerPath.ToUpperInvariant()) -and -not $nestedOwnerCheck.Closure.ReferencedKeys.Contains($nestedOwnerCheck.ParentPath.ToUpperInvariant())) 'nested file references protect the inner batch workspace without marking its parent as owner.'

    # A completed workspace that holds a running batch registered by a nested root is not reclaimed
    # (a sibling task's retention once deleted a completed ledger together with its running child batch).
    $holderRoot = Join-Path $tempBase 'nested-running-root'
    [System.IO.Directory]::CreateDirectory($holderRoot) | Out-Null
    $holderPolicyRecent = $script:Policy.defaults.maxRecentCompleted
    $holderPolicyBytes = $script:Policy.defaults.maxBytes
    $script:Policy.defaults.maxBytes = [long]::MaxValue
    $script:Policy.enabled = $false
    $script:Policy.roots = @($script:Policy.roots) + @([pscustomobject]@{ id = 'nested-running'; path = $holderRoot; excludedNames = @() })
    $oldHolder = New-TestWorkspace -RootPath $holderRoot -Name 'old-ledger'
    Complete-TestWorkspace -RootPath $holderRoot -WorkspacePath $oldHolder
    $script:Policy.roots = @($script:Policy.roots) + @([pscustomobject]@{ id = 'nested-running-inner'; path = $oldHolder; excludedNames = @() })
    $runningChild = New-TestWorkspace -RootPath $oldHolder -Name 'fix2'
    Write-FixtureBytes -Path (Join-Path $runningChild 'payload.bin') -Count 8
    foreach ($name in 'newer-one', 'newer-two') {
        Start-Sleep -Milliseconds 30
        $newer = New-TestWorkspace -RootPath $holderRoot -Name $name
        Complete-TestWorkspace -RootPath $holderRoot -WorkspacePath $newer
    }
    $script:Policy.enabled = $true
    $script:Policy.defaults.maxRecentCompleted = 1
    # As in the incident, the sibling's retention does not know the nested root.
    $holderResult = Invoke-GeneratedWorkspaceRetention -RootPath $holderRoot -Policy $script:Policy -ReferenceRoots @($holderRoot) -Apply
    Assert-True ((Test-Path -LiteralPath $runningChild) -and @($holderResult.deleted | Where-Object { $_.path -eq $oldHolder }).Count -eq 0) 'a completed workspace holding a running nested batch is not deleted.'
    Assert-True (@($holderResult.retained | Where-Object { $_.path -eq $oldHolder -and $_.reason -like '*nested workspace that is running*' }).Count -eq 1) 'the retained holder reports the running nested batch as its reason.'
    $script:Policy.enabled = $false
    Complete-TestWorkspace -RootPath $oldHolder -WorkspacePath $runningChild
    $script:Policy.enabled = $true
    $holderAfter = Invoke-GeneratedWorkspaceRetention -RootPath $holderRoot -Policy $script:Policy -ReferenceRoots @($holderRoot) -Apply
    Assert-True (-not (Test-Path -LiteralPath $oldHolder) -and @($holderAfter.deleted | Where-Object { $_.path -eq $oldHolder }).Count -eq 1) 'once its nested batch is completed, the old holder follows the normal count policy.'

    # Only the root that registered a workspace reclaims it, even when another root names the same directory.
    $sharedRoot = Join-Path $tempBase 'shared-path-root'
    [System.IO.Directory]::CreateDirectory($sharedRoot) | Out-Null
    $script:Policy.enabled = $false
    $script:Policy.roots = @($script:Policy.roots) + @([pscustomobject]@{ id = 'shared-owner'; path = $sharedRoot; excludedNames = @() })
    $foreignOwned = New-TestWorkspace -RootPath $sharedRoot -Name 'owned-elsewhere'
    Complete-TestWorkspace -RootPath $sharedRoot -WorkspacePath $foreignOwned
    $otherPolicy = $script:Policy | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    foreach ($root in $otherPolicy.roots) { if ($root.path -eq $sharedRoot) { $root.id = 'shared-intruder' } }
    $otherPolicy.enabled = $true
    $otherPolicy.defaults.maxRecentCompleted = 0
    $foreignResult = Invoke-GeneratedWorkspaceRetention -RootPath $sharedRoot -Policy $otherPolicy -ReferenceRoots @($sharedRoot) -Apply
    Assert-True ((Test-Path -LiteralPath $foreignOwned) -and @($foreignResult.retained | Where-Object { $_.path -eq $foreignOwned -and $_.reason -like "*Registered under managed root 'shared-owner'*" }).Count -eq 1) 'a workspace registered by another root id is retained with that reason.'
    $script:Policy.enabled = $true
    $script:Policy.defaults.maxRecentCompleted = $holderPolicyRecent
    $script:Policy.defaults.maxBytes = $holderPolicyBytes

    # File locks are checked before any workspace content is removed.
    $lockedRoot = $rootPaths[3]
    $locked = New-TestWorkspace -RootPath $lockedRoot -Name 'locked'
    Write-FixtureBytes -Path (Join-Path $locked 'first.bin') -Count 8
    Write-FixtureBytes -Path (Join-Path $locked 'locked.bin') -Count 8
    $script:Policy.enabled = $false
    Complete-TestWorkspace -RootPath $lockedRoot -WorkspacePath $locked
    $script:Policy.enabled = $true
    $script:Policy.defaults.maxRecentCompleted = 0
    $script:Policy.defaults.maxBytes = 0
    $busyStream = [System.IO.File]::Open((Join-Path $locked 'locked.bin'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $busyResult = Invoke-GeneratedWorkspaceRetention -RootPath $lockedRoot -Policy $script:Policy -ReferenceRoots @($lockedRoot) -Apply
        Assert-True (Test-Path -LiteralPath (Join-Path $locked 'first.bin')) 'a locked file prevents partial workspace deletion.'
        Assert-True (Test-Path -LiteralPath (Join-Path $locked 'locked.bin')) 'the locked file remains.'
        Assert-True (Test-Path -LiteralPath $locked) 'a workspace with a busy file remains intact.'
        Assert-True (@($busyResult.retained | Where-Object { $_.path -eq $locked }).Count -eq 1) 'locked workspace is reported as retained.'
    }
    finally { $busyStream.Dispose() }

    # Reparse points make a workspace ineligible for deletion.
    $reparseRoot = $rootPaths[4]
    $reparseWorkspace = New-TestWorkspace -RootPath $reparseRoot -Name 'with-junction'
    $junctionTarget = Join-Path $tempBase 'junction-target'
    [System.IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    $junctionPath = Join-Path $reparseWorkspace 'junction'
    $junctionCreated = $false
    try {
        New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget -ErrorAction Stop | Out-Null
        $junctionCreated = $true
    }
    catch [System.ComponentModel.Win32Exception] {
        if ($_.Exception.NativeErrorCode -ne 5) { throw }
        Write-Warning 'Reparse-point fixture skipped because this environment denied junction creation.'
    }
    if ($junctionCreated) {
        $script:Policy.enabled = $false
        Complete-TestWorkspace -RootPath $reparseRoot -WorkspacePath $reparseWorkspace
        $script:Policy.enabled = $true
        $reparseResult = Invoke-GeneratedWorkspaceRetention -RootPath $reparseRoot -Policy $script:Policy -ReferenceRoots @($reparseRoot) -Apply
        Assert-True (Test-Path -LiteralPath $junctionPath) 'reparse point is preserved and not traversed.'
        Assert-True (Test-Path -LiteralPath $reparseWorkspace) 'workspace containing a reparse point is retained.'
        Assert-True (@($reparseResult.retained | Where-Object { $_.path -eq $reparseWorkspace }).Count -eq 1) 'reparse workspace is reported as retained.'
    }

    # Managed path checks and root-wide operation locking reject unsafe races.
    $escapeRoot = $rootPaths[7]
    Assert-Throws { New-GeneratedWorkspace -RootPath $escapeRoot -WorkspacePath (Join-Path $tempBase 'outside-workspace') -Purpose 'escape' -Policy $script:Policy } 'workspace path outside the managed root is rejected.'
    Assert-Throws { New-GeneratedWorkspace -RootPath $escapeRoot -WorkspacePath (Join-Path (Join-Path $escapeRoot 'nested') 'child') -Purpose 'nested' -Policy $script:Policy } 'nested path is not a direct managed unit.'

    $concurrentRoot = $rootPaths[8]
    $concurrent = New-TestWorkspace -RootPath $concurrentRoot -Name 'must-stay'
    Write-FixtureBytes -Path (Join-Path $concurrent 'payload.bin') -Count 8
    $script:Policy.enabled = $false
    Complete-TestWorkspace -RootPath $concurrentRoot -WorkspacePath $concurrent
    $script:Policy.enabled = $true
    $lockPath = Join-Path $concurrentRoot '.generated-workspace-retention.lock'
    $externalLock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        Assert-Throws { Invoke-GeneratedWorkspaceRetention -RootPath $concurrentRoot -Policy $script:Policy -Apply } 'concurrent root operation is rejected by the OS file lock.'
        Assert-True (Test-Path -LiteralPath $concurrent) 'root lock conflict does not change a workspace.'
    }
    finally { $externalLock.Dispose() }

    # Explicit selection still reads references from every non-selected owner.
    $selectedRoot = Join-Path $tempBase 'selected-cleanup-root'
    [System.IO.Directory]::CreateDirectory($selectedRoot) | Out-Null
    $script:Policy.roots += [pscustomobject]@{ id = 'selected-cleanup'; path = $selectedRoot; excludedNames = @() }
    $script:Policy.enabled = $false
    $selectedTask = New-TestWorkspace -RootPath $selectedRoot -Name 'selected'
    Write-FixtureBytes -Path (Join-Path $selectedTask 'payload.bin') -Count 8
    Complete-TestWorkspace -RootPath $selectedRoot -WorkspacePath $selectedTask
    $unselectedTask = New-TestWorkspace -RootPath $selectedRoot -Name 'unselected' -References @($selectedTask)
    Complete-TestWorkspace -RootPath $selectedRoot -WorkspacePath $unselectedTask
    $script:Policy.enabled = $true
    $selectedBlocked = Invoke-GeneratedWorkspaceRetention -RootPath $selectedRoot -Policy $script:Policy -WorkspacePaths @($selectedTask) -Apply
    Assert-True ($selectedBlocked.safeToPrune -and @($selectedBlocked.deleted).Count -eq 0 -and (Test-Path -LiteralPath $selectedTask)) 'a non-selected completed owner still protects its selected dependency.'
    $script:Policy.enabled = $false
    $null = Set-GeneratedWorkspaceReferences -RootPath $selectedRoot -WorkspacePath $unselectedTask -References @() -Policy $script:Policy
    $script:Policy.enabled = $true
    $selectedResult = Invoke-GeneratedWorkspaceRetention -RootPath $selectedRoot -Policy $script:Policy -WorkspacePaths @($selectedTask) -Apply
    Assert-True (@($selectedResult.deleted).Count -eq 1 -and -not (Test-Path -LiteralPath $selectedTask) -and (Test-Path -LiteralPath $unselectedTask)) 'selected cleanup deletes only its eligible target and retains the non-selected owner.'
    Assert-True ($selectedResult.overBudget -and $selectedResult.accountingScope -like 'selected*') 'selected byte accounting explicitly reports the unknown whole-root budget.'
    Assert-Throws { Invoke-GeneratedWorkspaceRetention -RootPath $selectedRoot -Policy $script:Policy -WorkspacePaths @($tempBase) -Apply } 'selection cannot escape its configured management root.'

    # Prepare only sizes the completed pool; live/unknown payload stays outside it.
    $poolRoot = Join-Path $tempBase 'completed-pool-root'
    [System.IO.Directory]::CreateDirectory($poolRoot) | Out-Null
    $script:Policy.roots += [pscustomobject]@{ id = 'completed-pool'; path = $poolRoot; excludedNames = @() }
    $script:Policy.enabled = $false
    $poolRetired = New-TestWorkspace -RootPath $poolRoot -Name 'retired'
    Write-FixtureBytes -Path (Join-Path $poolRetired 'payload.bin') -Count 8
    Complete-TestWorkspace -RootPath $poolRoot -WorkspacePath $poolRetired
    $poolDependency = New-TestWorkspace -RootPath $poolRoot -Name 'dependency'
    Complete-TestWorkspace -RootPath $poolRoot -WorkspacePath $poolDependency
    $poolLive = New-TestWorkspace -RootPath $poolRoot -Name 'live' -References @($poolDependency)
    $poolUnknown = Join-Path $poolRoot 'unknown'
    [System.IO.Directory]::CreateDirectory($poolUnknown) | Out-Null
    Write-FixtureBytes -Path (Join-Path $poolUnknown 'input.bin') -Count 8
    $script:Policy.enabled = $true
    $script:Policy.defaults.maxRecentCompleted = 0
    $poolResult = Invoke-GeneratedWorkspaceRetention -RootPath $poolRoot -Policy $script:Policy -CompletedOnly -Apply
    Assert-True ($poolResult.safeToPrune -and $poolResult.selectedWorkspaceCount -eq 2) 'completed-only Prepare selects completed metadata under the root locks.'
    Assert-True (-not (Test-Path -LiteralPath $poolRetired) -and (Test-Path -LiteralPath $poolDependency)) 'completed-only cleanup retires the eligible batch while live references still protect completed dependencies.'
    Assert-True ((Test-Path -LiteralPath $poolLive) -and (Test-Path -LiteralPath $poolUnknown) -and -not $poolResult.wholeRootBytesKnown) 'live and unknown payload stay protected, with unknown whole-root accounting.'
    Assert-Throws { Invoke-GeneratedWorkspaceRetention -RootPath $poolRoot -Policy $script:Policy -CompletedOnly -WorkspacePaths @($poolDependency) } 'completed pool and exact selection cannot be mixed.'

    # Native occupancy/deletion handles must cover real generated paths over MAX_PATH.
    $script:Policy.enabled = $false
    $longTask = New-TestWorkspace -RootPath $selectedRoot -Name 'long-path'
    $longDirectory = $longTask
    while ($longDirectory.Length -lt 280) { $longDirectory = Join-Path $longDirectory 'generated-identity-layout' }
    [System.IO.Directory]::CreateDirectory($longDirectory) | Out-Null
    $longFile = Join-Path $longDirectory 'payload.bin'
    Write-FixtureBytes -Path $longFile -Count 8
    Complete-TestWorkspace -RootPath $selectedRoot -WorkspacePath $longTask
    $longHandle = [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Open($longFile, 0)
    try { Assert-True ([ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Length($longHandle) -eq 8) 'native guard opens and measures a path over MAX_PATH.' }
    finally { $longHandle.Dispose() }
    $script:Policy.enabled = $true
    $longResult = Invoke-GeneratedWorkspaceRetention -RootPath $selectedRoot -Policy $script:Policy -WorkspacePaths @($longTask) -Apply
    Assert-True (@($longResult.deleted).Count -eq 1 -and -not [System.IO.Directory]::Exists($longTask)) 'official maintenance deletes an eligible long-path workspace after both snapshots and occupancy checks.'

    # The single CLI resolves a policy root and returns a read-only inspection result.
    $cliPolicyPath = Join-Path $tempBase 'cli-policy.json'
    $cliPolicy = [pscustomobject]@{
        schemaVersion = 1
        enabled = $true
        referenceRoots = @()
        defaults = $script:Policy.defaults
        roots = @([pscustomobject]@{ id = 'cli-fixture'; path = $concurrentRoot; excludedNames = @() })
    }
    $cliPolicy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cliPolicyPath -Encoding utf8
    $cliPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/maintenance/Manage-GeneratedWorkspace.ps1'
    $cliOutput = & $cliPath -Action Inspect -RootId 'cli-fixture' -PolicyPath $cliPolicyPath
    $cliResult = ($cliOutput -join "`n") | ConvertFrom-Json
    Assert-True ($cliResult.safeToPrune -eq $true) 'unified CLI inspects the selected managed root.'

    "Generated workspace retention fixture checks passed (assertions=$script:AssertionCount)."
}
finally {
    Remove-TestFixtureTreeBounded -Path $tempBase
}
