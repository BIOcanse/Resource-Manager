#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param([switch]$Apply)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptExpectedTempRoot = 'D:\Code\CommonAssets\Tools\Caches\Temp'
$scriptExpectedStaleCount = 466
$scriptExpectedStaleBytes = [long]37678198
$scriptExpectedAbiVersions = [uint32[]]@(262144, 327680, 393216, 458752, 524288)
$scriptCutoffUtc = [DateTime]::new(2026, 10, 8, 0, 0, 0, [DateTimeKind]::Utc)
$scriptMaxRootEntries = 100000
$scriptMaxSnapshotBytes = 1048576
$scriptStateMarkerName = '.generated-workspace.json'
$scriptRetentionLockName = '.generated-workspace-retention.lock'

function Get-IntelUserTempRoot {
    $userTemp = [Environment]::GetEnvironmentVariable('TEMP', 'User')
    if ([string]::IsNullOrWhiteSpace($userTemp)) {
        throw 'The current user TEMP value is not configured.'
    }
    $fullUserTemp = [IO.Path]::GetFullPath($userTemp).TrimEnd([char[]]@('\', '/'))
    $fullExpected = [IO.Path]::GetFullPath($scriptExpectedTempRoot).TrimEnd([char[]]@('\', '/'))
    if (-not [string]::Equals($fullUserTemp, $fullExpected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The user TEMP root differs from the inventoried Intel worker root: $fullUserTemp"
    }
    if (-not [IO.Directory]::Exists($fullUserTemp)) { throw "The user TEMP root is missing: $fullUserTemp" }
    Assert-IntelNoReparsePath -Path $fullUserTemp
    return $fullUserTemp
}

function Assert-IntelNoReparsePath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $volumeRoot = [IO.Path]::GetPathRoot($fullPath)
    $current = $volumeRoot
    foreach ($segment in $fullPath.Substring($volumeRoot.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [IO.Path]::Combine($current, $segment)
        if (-not [IO.File]::Exists($current) -and -not [IO.Directory]::Exists($current)) { continue }
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A path component is a reparse point: $current"
        }
    }
}

function Get-IntelSingleChild {
    param(
        [Parameter(Mandatory)][string]$ParentPath,
        [Parameter(Mandatory)][string]$ExpectedName,
        [Parameter(Mandatory)][bool]$Directory
    )

    $children = [Collections.Generic.List[IO.FileSystemInfo]]::new()
    foreach ($child in ([IO.DirectoryInfo]::new($ParentPath)).EnumerateFileSystemInfos()) {
        $children.Add($child)
        if ($children.Count -gt 2) { throw "Unexpected contents below the inventoried worker tree: $ParentPath" }
    }
    if ($children.Count -ne 1 -or -not [string]::Equals($children[0].Name, $ExpectedName, [StringComparison]::Ordinal)) {
        throw "The inventoried worker path does not have its exact expected child: $ParentPath"
    }
    $child = $children[0]
    $child.Refresh()
    $isDirectory = ($child.Attributes -band [IO.FileAttributes]::Directory) -ne 0
    if ($isDirectory -ne $Directory -or ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The inventoried worker child has an unexpected type or reparse point: $($child.FullName)"
    }
    return $child
}

function Test-IntelOldTimestamp {
    param([Parameter(Mandatory)][IO.FileSystemInfo]$Item)
    $Item.Refresh()
    return $Item.CreationTimeUtc -lt $scriptCutoffUtc -and $Item.LastWriteTimeUtc -lt $scriptCutoffUtc
}

function Read-IntelSnapshotEnvelope {
    param([Parameter(Mandatory)][string]$Path)

    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        return [pscustomobject]@{ Available = $false; Sha256 = $null; AbiVersion = $null; Length = $null }
    }
    try {
        if ($stream.Length -lt 96 -or $stream.Length -gt $scriptMaxSnapshotBytes) {
            throw "Snapshot envelope length is outside the expected bound: $Path"
        }
        $length = [int]$stream.Length
        $bytes = [byte[]]::new($length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw "Snapshot envelope ended during validation: $Path" }
            $offset += $read
        }
        if ($stream.Length -ne $length) { throw "Snapshot envelope changed during validation: $Path" }
        $stream.Dispose()
        $stream = $null

        if (-not [BitConverter]::IsLittleEndian) { throw 'The snapshot envelope validator requires the Windows little-endian format.' }
        $magicBytes = [byte[]](0x48, 0x52, 0x4D, 0x4D, 0x53, 0x4E, 0x50, 0x32)
        for ($i = 0; $i -lt $magicBytes.Length; $i++) {
            if ($bytes[$i] -ne $magicBytes[$i]) { throw "Snapshot magic does not match HRMMSNP2: $Path" }
        }
        if ([BitConverter]::ToUInt32($bytes, 8) -ne 2) { throw "Snapshot envelope version is not 2: $Path" }
        $abiVersion = [BitConverter]::ToUInt32($bytes, 12)
        if ($scriptExpectedAbiVersions -notcontains $abiVersion) { throw "Snapshot ABI version is outside the inventoried set: $Path" }

        $bodyLength = $bytes.Length - 32
        $body = [byte[]]::new($bodyLength)
        [Buffer]::BlockCopy($bytes, 0, $body, 0, $bodyLength)
        $actualDigest = [Security.Cryptography.SHA256]::HashData($body)
        $expectedDigest = [byte[]]::new(32)
        [Buffer]::BlockCopy($bytes, $bodyLength, $expectedDigest, 0, 32)
        if ([Convert]::ToHexString($actualDigest) -cne [Convert]::ToHexString($expectedDigest)) {
            throw "Snapshot envelope checksum is invalid: $Path"
        }
        return [pscustomobject]@{
            Available = $true
            Sha256 = [Convert]::ToHexString($actualDigest)
            AbiVersion = $abiVersion
            Length = [long]$length
        }
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Test-IntelRetentionRootLock {
    param([Parameter(Mandatory)][string]$RootPath)

    $lockPath = Join-Path $RootPath $scriptRetentionLockName
    if (-not [IO.File]::Exists($lockPath) -and -not [IO.Directory]::Exists($lockPath)) { return }
    $attributes = [IO.File]::GetAttributes($lockPath)
    if (($attributes -band [IO.FileAttributes]::Directory) -ne 0 -or
        ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The retention lock path is not a plain file: $lockPath"
    }
    $stream = $null
    try {
        $stream = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch {
        throw "The TEMP root is busy in GeneratedWorkspaceRetention: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Get-IntelWorkerInventory {
    param([Parameter(Mandatory)][string]$RootPath)

    Test-IntelRetentionRootLock -RootPath $RootPath
    $rootEntries = [Collections.Generic.List[IO.FileSystemInfo]]::new()
    $candidateDirectories = [Collections.Generic.List[IO.DirectoryInfo]]::new()
    foreach ($entry in ([IO.DirectoryInfo]::new($RootPath)).EnumerateFileSystemInfos()) {
        $rootEntries.Add($entry)
        if ($rootEntries.Count -gt $scriptMaxRootEntries) { throw 'TEMP root entry limit was exceeded.' }
        $entry.Refresh()
        if (($entry.Attributes -band [IO.FileAttributes]::Directory) -eq 0) { continue }
        if ([regex]::IsMatch($entry.Name, '^intel-msr-worker-[0-9a-f]{32}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "An Intel worker candidate is a reparse point: $($entry.FullName)"
            }
            $candidateDirectories.Add([IO.DirectoryInfo]::new($entry.FullName))
        }
    }

    $stale = [Collections.Generic.List[object]]::new()
    $recent = [Collections.Generic.List[string]]::new()
    foreach ($directory in $candidateDirectories) {
        $directory.Refresh()
        if (-not (Test-IntelOldTimestamp -Item $directory)) {
            $recent.Add($directory.FullName)
            continue
        }
        Assert-IntelNoReparsePath -Path $directory.FullName
        if ([IO.File]::Exists((Join-Path $directory.FullName $scriptStateMarkerName))) {
            throw "A worker candidate already has a GeneratedWorkspaceRetention marker: $($directory.FullName)"
        }

        $userData = Get-IntelSingleChild -ParentPath $directory.FullName -ExpectedName 'UserData' -Directory $true
        $hostManager = Get-IntelSingleChild -ParentPath $userData.FullName -ExpectedName 'HostManager' -Directory $true
        $snapshotDirectory = Get-IntelSingleChild -ParentPath $hostManager.FullName -ExpectedName 'metric-snapshot' -Directory $true
        $snapshotFile = Get-IntelSingleChild -ParentPath $snapshotDirectory.FullName -ExpectedName 'metric-snapshot-v2.bin' -Directory $false
        $treeItems = @($directory, $userData, $hostManager, $snapshotDirectory, $snapshotFile)
        $isOld = $true
        foreach ($treeItem in $treeItems) {
            if (($treeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A worker candidate contains a reparse point: $($treeItem.FullName)"
            }
            if (-not (Test-IntelOldTimestamp -Item $treeItem)) { $isOld = $false }
        }
        if (-not $isOld) {
            $recent.Add($directory.FullName)
            continue
        }

        $snapshotFile.Refresh()
        if ($snapshotFile.Length -lt 96 -or $snapshotFile.Length -gt $scriptMaxSnapshotBytes) {
            throw "Snapshot file length is outside the expected bound: $($snapshotFile.FullName)"
        }
        $envelope = Read-IntelSnapshotEnvelope -Path $snapshotFile.FullName
        $stale.Add([pscustomobject]@{
            Path = $directory.FullName
            Name = $directory.Name
            FilePath = $snapshotFile.FullName
            Length = [long]$snapshotFile.Length
            CompletedUtc = $snapshotFile.LastWriteTimeUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
            Sha256 = $envelope.Sha256
            AbiVersion = $envelope.AbiVersion
            Available = [bool]$envelope.Available
        })
    }

    $staleTotalBytes = [long]0
    foreach ($item in $stale) { $staleTotalBytes += [long]$item.Length }
    if ($stale.Count -ne $scriptExpectedStaleCount -or $staleTotalBytes -ne $scriptExpectedStaleBytes) {
        throw "The stale candidate inventory differs from the reviewed set (count=$($stale.Count), bytes=$staleTotalBytes)."
    }

    $eligible = @($stale | Where-Object Available)
    $excludedNames = [Collections.Generic.List[string]]::new()
    $eligibleNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $eligible) { [void]$eligibleNames.Add([string]$item.Name) }
    foreach ($entry in $rootEntries) {
        if (($entry.Attributes -band [IO.FileAttributes]::Directory) -eq 0) { continue }
        if (-not $eligibleNames.Contains($entry.Name)) { $excludedNames.Add($entry.Name) }
    }
    $identityRows = @($eligible | Sort-Object Name | ForEach-Object { $_.Name + '|' + $_.Length + '|' + $_.CompletedUtc + '|' + $_.Sha256 })
    $identityBytes = [Text.Encoding]::UTF8.GetBytes(($identityRows -join "`n"))
    $identitySha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($identityBytes))

    return [pscustomobject]@{
        RootPath = $RootPath
        RootDirectoryNames = @($rootEntries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::Directory) -ne 0 } | ForEach-Object Name | Sort-Object -CaseSensitive)
        Stale = @($stale.ToArray())
        Eligible = @($eligible)
        Recent = @($recent.ToArray())
        ExcludedNames = @($excludedNames.ToArray() | Sort-Object -CaseSensitive -Unique)
        StaleCount = $stale.Count
        StaleBytes = $staleTotalBytes
        EligibleCount = $eligible.Count
        EligibleBytes = [long](($eligible | Measure-Object -Property Length -Sum).Sum)
        BusyCount = $stale.Count - $eligible.Count
        CandidateSetSha256 = $identitySha
    }
}

function New-IntelRetentionPolicy {
    param([Parameter(Mandatory)][object]$Inventory, [Parameter(Mandatory)][bool]$Enabled)

    return [pscustomobject]@{
        schemaVersion = 1
        enabled = $Enabled
        referenceRoots = @()
        defaults = [pscustomobject]@{
            maxRecentCompleted = 0
            maxBytes = [long]8589934592
            maxNodes = 750000
            maxMilliseconds = 300000
        }
        roots = @([pscustomobject]@{
            id = 'intel-msr-worker-temp-once'
            path = $Inventory.RootPath
            excludedNames = @($Inventory.ExcludedNames)
            maxRecentCompleted = 0
            maxBytes = [long]8589934592
            maxNodes = 750000
            maxMilliseconds = 300000
        })
    }
}

$taskRoot = Get-IntelUserTempRoot
$taskInventory = Get-IntelWorkerInventory -RootPath $taskRoot
$taskPlan = [ordered]@{
    rootPath = $taskInventory.RootPath
    staleCandidateCount = $taskInventory.StaleCount
    staleCandidateBytes = $taskInventory.StaleBytes
    eligibleCount = $taskInventory.EligibleCount
    eligibleBytes = $taskInventory.EligibleBytes
    skippedBusyCount = $taskInventory.BusyCount
    skippedRecentCount = $taskInventory.Recent.Count
    candidateSetSha256 = $taskInventory.CandidateSetSha256
    applied = $false
    retentionEngine = 'scripts/maintenance/GeneratedWorkspaceRetention.psm1'
}

if (-not $Apply) {
    [pscustomobject]$taskPlan
    return
}
if ($taskInventory.EligibleCount -eq 0) {
    [pscustomobject]$taskPlan
    return
}
if (-not $PSCmdlet.ShouldProcess($taskRoot, "Adopt and remove $($taskInventory.EligibleCount) verified Intel worker snapshots")) {
    [pscustomobject]$taskPlan
    return
}

$taskBeforeApply = Get-IntelWorkerInventory -RootPath $taskRoot
if ($taskBeforeApply.CandidateSetSha256 -cne $taskInventory.CandidateSetSha256 -or
    $taskBeforeApply.EligibleCount -ne $taskInventory.EligibleCount -or
    $taskBeforeApply.StaleCount -ne $taskInventory.StaleCount -or
    $taskBeforeApply.StaleBytes -ne $taskInventory.StaleBytes -or
    (@($taskBeforeApply.RootDirectoryNames) -join "`n") -cne (@($taskInventory.RootDirectoryNames) -join "`n")) {
    throw 'The reviewed TEMP inventory changed before apply; no workspace was adopted.'
}

$taskModulePath = Join-Path $PSScriptRoot 'GeneratedWorkspaceRetention.psm1'
Import-Module -Name $taskModulePath -Force -DisableNameChecking
$taskDisabledPolicy = New-IntelRetentionPolicy -Inventory $taskBeforeApply -Enabled $false
foreach ($candidate in $taskBeforeApply.Eligible) {
    $null = Adopt-GeneratedWorkspace -RootPath $taskRoot -WorkspacePath $candidate.Path `
        -Purpose 'Verified completed Intel MSR worker test snapshot' `
        -CompletedUtc $candidate.CompletedUtc -Policy $taskDisabledPolicy -References @() -ReferenceRoots @()
}

$taskEnabledPolicy = New-IntelRetentionPolicy -Inventory $taskBeforeApply -Enabled $true
$taskResult = Invoke-GeneratedWorkspaceRetention -RootPath $taskRoot -Policy $taskEnabledPolicy -ReferenceRoots @() -Apply
$taskPlan.applied = [bool]$taskResult.applied
$taskPlan.retentionSafeToPrune = [bool]$taskResult.safeToPrune
$taskPlan.deletedCount = @($taskResult.deleted).Count
$taskPlan.failureCount = @($taskResult.failures).Count
$taskPlan.remainingCount = [int]$taskResult.remainingCount
$taskPlan.retentionResult = $taskResult
[pscustomobject]$taskPlan

if (-not $taskResult.safeToPrune -or @($taskResult.deleted).Count -ne $taskBeforeApply.EligibleCount -or
    @($taskResult.failures).Count -ne 0) {
    throw 'GeneratedWorkspaceRetention did not remove the complete eligible Intel worker set safely; review its returned result.'
}
