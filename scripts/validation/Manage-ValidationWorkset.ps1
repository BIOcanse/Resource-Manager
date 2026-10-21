[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Prepare', 'Prune', 'Complete')]
    [string]$Mode = 'Inspect',

    [Alias('ProjectRoot')]
    [string]$WorkspaceRoot = 'F:\code\Resource-Manager-Development',

    [string]$BatchId,
    [string]$ConsumerId,
    [string]$ManifestPath,
    [switch]$Apply,
    [ValidateSet('success', 'failed')]
    [string]$Outcome = 'success',
    [string[]]$References = @(),
    [string[]]$ReferenceRoots = @(),
    [string]$RetentionPolicyPath,

    [ValidateRange(1, 1024)]
    [double]$MaxWorksetGiB = 16,

    [ValidateRange(0, 1024)]
    [double]$ReserveFreeGiB = 8,

    [ValidateRange(0, 1024)]
    [double]$ExpectedGrowthGiB = 8,

    [ValidateRange(1, 5000000)]
    [int]$MaxAccountingNodes = 750000,

    [ValidateRange(1, 3600000)]
    [int]$MaxAccountingMilliseconds = 300000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonResult {
    param([Parameter(Mandatory)][object]$Value)
    ConvertTo-Json -InputObject $Value -Depth 10 -Compress
}

function Assert-Name {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$ParameterName)
    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw "$ParameterName must be 1-64 letters, digits, dots, underscores, or hyphens and start with a letter or digit."
    }
}

function Get-DriveSnapshot {
    param([Parameter(Mandatory)][string]$Path)
    $driveRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Path))
    if ($driveRoot -notmatch '^[A-Za-z]:\\$') { throw "A local drive-rooted path is required: $Path" }
    $drive = [System.IO.DriveInfo]::new($driveRoot)
    if (-not $drive.IsReady) { throw "The workspace volume is not ready: $driveRoot" }
    [pscustomobject]@{
        Drive = $driveRoot.Substring(0, 2).ToUpperInvariant()
        Root = $driveRoot
        AvailableBytes = [long]$drive.AvailableFreeSpace
        TotalBytes = [long]$drive.TotalSize
    }
}

function Assert-WorkspaceRoot {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Workspace root does not exist: $Path"
    }
    $current = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    $visited = 0
    while ($null -ne $current) {
        $current.Refresh()
        if ($current.Exists -and (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Workspace path contains a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
        $visited++
        if ($visited -gt 128) { throw 'Workspace path has too many components.' }
    }
}

function Get-TreeAccounting {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$IncludeEntries,
        [Parameter(Mandatory)][ValidateRange(1, 5000000)][int]$MaxNodes,
        [Parameter(Mandatory)][ValidateRange(1, 3600000)][int]$MaxMilliseconds
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [pscustomobject]@{
            Bytes = [long]0
            ReparsePaths = @()
            ProtectedPaths = @()
            Files = @()
            Directories = @()
            NodeCount = 0
            ElapsedMilliseconds = 0
        }
    }

    $bytes = [long]0
    $reparsePaths = [System.Collections.Generic.List[string]]::new()
    $protectedPaths = [System.Collections.Generic.List[string]]::new()
    $files = [System.Collections.Generic.List[string]]::new()
    $directories = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $root = [System.IO.DirectoryInfo]::new($Path)
    $root.Refresh()
    if (($root.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        return [pscustomobject]@{
            Bytes = [long]0
            ReparsePaths = @($root.FullName)
            ProtectedPaths = @($root.FullName)
            Files = @()
            Directories = @()
            NodeCount = 1
            ElapsedMilliseconds = 0
        }
    }
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $nodeCount = 1
    $maxNodes = $MaxNodes
    $maxMilliseconds = $MaxMilliseconds
    $protectedDirectoryNames = @('logs', 'log', 'testresults', 'failures', 'failed', 'failure-repro', 'repro', 'reproduction', 'native-prefix', 'native_prefix', 'source', 'sources', 'before', 'before-state', 'before-snapshot')
    $pending.Push($root)

    while ($pending.Count -gt 0) {
        if ($nodeCount -gt $maxNodes -or $timer.ElapsedMilliseconds -gt $maxMilliseconds) {
            throw "Directory accounting exceeded its bound (nodes=$nodeCount/$maxNodes elapsedMs=$($timer.ElapsedMilliseconds)/$maxMilliseconds): $Path"
        }
        $directory = $pending.Pop()
        foreach ($entry in $directory.EnumerateFileSystemInfos()) {
            $entry.Refresh()
            $nodeCount++
            if ($nodeCount -gt $maxNodes -or $timer.ElapsedMilliseconds -gt $maxMilliseconds) {
                throw "Directory accounting exceeded its bound (nodes=$nodeCount/$maxNodes elapsedMs=$($timer.ElapsedMilliseconds)/$maxMilliseconds): $Path"
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $reparsePaths.Add($entry.FullName)
                continue
            }
            if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                if ($protectedDirectoryNames -contains $entry.Name.ToLowerInvariant()) {
                    $protectedPaths.Add($entry.FullName)
                }
                if ($IncludeEntries) { $directories.Add($entry.FullName) }
                $pending.Push([System.IO.DirectoryInfo]::new($entry.FullName))
            }
            else {
                $file = [System.IO.FileInfo]::new($entry.FullName)
                $bytes += [long]$file.Length
                $extension = [System.IO.Path]::GetExtension($entry.Name).ToLowerInvariant()
                if (
                    $extension -in @('.trx', '.log', '.binlog', '.jsonl', '.etl', '.trace', '.dmp', '.mdmp') -or
                    $entry.Name.StartsWith('vstest.diag', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $entry.Name.StartsWith('failure', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $entry.Name.StartsWith('repro', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $entry.Name.StartsWith('stdout', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $entry.Name.StartsWith('stderr', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $entry.Name.StartsWith('before', [System.StringComparison]::OrdinalIgnoreCase) -or
                    $entry.Name.StartsWith('source', [System.StringComparison]::OrdinalIgnoreCase)
                ) {
                    $protectedPaths.Add($entry.FullName)
                }
                if ($IncludeEntries) { $files.Add($entry.FullName) }
            }
        }
    }

    [pscustomobject]@{
        Bytes = $bytes
        ReparsePaths = @($reparsePaths.ToArray())
        ProtectedPaths = @($protectedPaths.ToArray())
        Files = @($files.ToArray())
        Directories = @($directories.ToArray())
        NodeCount = $nodeCount
        ElapsedMilliseconds = [long]$timer.ElapsedMilliseconds
    }
}

function Assert-NoReparseComponents {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + '\'
    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside its validation root: $Path"
    }

    $currentPath = $rootFull
    $rootInfo = [System.IO.DirectoryInfo]::new($currentPath)
    $rootInfo.Refresh()
    if ($rootInfo.Exists -and (($rootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Validation root is a reparse point: $currentPath"
    }

    $relative = $pathFull.Substring($prefix.Length)
    foreach ($segment in $relative.Split('\')) {
        $currentPath = Join-Path $currentPath $segment
        $info = [System.IO.DirectoryInfo]::new($currentPath)
        $info.Refresh()
        if ($info.Exists -and (($info.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Cache path contains a reparse point: $currentPath"
        }
    }
}

function Assert-CacheEntryForDeletion {
    param(
        [Parameter(Mandatory)][string]$EntryPath,
        [Parameter(Mandatory)][string]$CacheRoot,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [switch]$Directory
    )
    $cacheRootFull = [System.IO.Path]::GetFullPath($CacheRoot).TrimEnd('\')
    $entryFull = [System.IO.Path]::GetFullPath($EntryPath)
    $prefix = $cacheRootFull + '\'
    if (-not $entryFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Deletion entry is outside its exact allowlisted cache: $EntryPath"
    }
    Assert-NoReparseComponents -Path $entryFull -Root $ValidationRoot

    if ($Directory) {
        $item = [System.IO.DirectoryInfo]::new($entryFull)
        $item.Refresh()
        if (-not $item.Exists -or (($item.Attributes -band [System.IO.FileAttributes]::Directory) -eq 0) -or
            (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Directory changed or became a reparse point before prune: $entryFull"
        }
    }
    else {
        $item = [System.IO.FileInfo]::new($entryFull)
        $item.Refresh()
        if (-not $item.Exists -or (($item.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) -or
            (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "File changed or became a reparse point before prune: $entryFull"
        }
    }
    $entryFull
}

function Test-CacheFilesUnoccupied {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$FilePaths,
        [Parameter(Mandatory)][string]$CacheRoot,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][System.Diagnostics.Stopwatch]$OperationTimer,
        [Parameter(Mandatory)][ValidateRange(1, 3600000)][int]$MaxMilliseconds
    )
    foreach ($filePath in $FilePaths) {
        if ($OperationTimer.ElapsedMilliseconds -gt $MaxMilliseconds) {
            throw "Cache occupancy checks exceeded their time bound: elapsedMs=$($OperationTimer.ElapsedMilliseconds)/$MaxMilliseconds."
        }
        $stream = $null
        try {
            $verifiedPath = Assert-CacheEntryForDeletion -EntryPath $filePath -CacheRoot $CacheRoot -ValidationRoot $ValidationRoot
            $stream = [System.IO.File]::Open($verifiedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
        }
        catch {
            return [pscustomobject]@{ Unoccupied = $false; Path = $filePath; Reason = $_.Exception.Message }
        }
        finally {
            if ($null -ne $stream) { $stream.Dispose() }
        }
    }
    return [pscustomobject]@{ Unoccupied = $true; Path = $null; Reason = $null }
}
function Remove-VerifiedCacheTree {
    param(
        [Parameter(Mandatory)][object]$Target,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][System.Diagnostics.Stopwatch]$OperationTimer
    )
    $cacheRootFull = [System.IO.Path]::GetFullPath([string]$Target.FullPath)
    Assert-NoReparseComponents -Path $cacheRootFull -Root $ValidationRoot
    $rootInfo = [System.IO.DirectoryInfo]::new($cacheRootFull)
    $rootInfo.Refresh()
    if (-not $rootInfo.Exists -or (($rootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Allowlisted cache root changed or became a reparse point: $cacheRootFull"
    }

    foreach ($filePath in $Target.Accounting.Files) {
        if ($OperationTimer.ElapsedMilliseconds -gt 120000) { throw 'Prune exceeded its 120 second apply bound.' }
        $null = Assert-CacheEntryForDeletion -EntryPath ([string]$filePath) -CacheRoot $cacheRootFull -ValidationRoot $ValidationRoot
    }
    foreach ($directoryPath in $Target.Accounting.Directories) {
        if ($OperationTimer.ElapsedMilliseconds -gt 120000) { throw 'Prune exceeded its 120 second apply bound.' }
        $null = Assert-CacheEntryForDeletion -EntryPath ([string]$directoryPath) -CacheRoot $cacheRootFull -ValidationRoot $ValidationRoot -Directory
    }
    $occupancy = Test-CacheFilesUnoccupied -FilePaths @($Target.Accounting.Files) -CacheRoot $cacheRootFull `
        -ValidationRoot $ValidationRoot -OperationTimer $OperationTimer -MaxMilliseconds 120000
    if (-not $occupancy.Unoccupied) { throw "Cache became busy before prune: $($occupancy.Path) ($($occupancy.Reason))" }

    foreach ($filePath in $Target.Accounting.Files) {
        if ($OperationTimer.ElapsedMilliseconds -gt 120000) { throw 'Prune exceeded its 120 second apply bound.' }
        $verifiedPath = Assert-CacheEntryForDeletion -EntryPath ([string]$filePath) -CacheRoot $cacheRootFull -ValidationRoot $ValidationRoot
        $stream = [System.IO.File]::Open($verifiedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Delete)
        try { [System.IO.File]::Delete($verifiedPath) }
        finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $verifiedPath) { throw "File remained after prune: $verifiedPath" }
    }
    foreach ($directoryPath in ($Target.Accounting.Directories | Sort-Object { $_.Length } -Descending)) {
        if ($OperationTimer.ElapsedMilliseconds -gt 120000) { throw 'Prune exceeded its 120 second apply bound.' }
        $verifiedPath = Assert-CacheEntryForDeletion -EntryPath ([string]$directoryPath) -CacheRoot $cacheRootFull -ValidationRoot $ValidationRoot -Directory
        [System.IO.Directory]::Delete($verifiedPath, $false)
        if (Test-Path -LiteralPath $verifiedPath) { throw "Directory remained after prune: $verifiedPath" }
    }

    Assert-NoReparseComponents -Path $cacheRootFull -Root $ValidationRoot
    $rootInfo = [System.IO.DirectoryInfo]::new($cacheRootFull)
    $rootInfo.Refresh()
    if (-not $rootInfo.Exists -or (($rootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Allowlisted cache root changed before final prune: $cacheRootFull"
    }
    [System.IO.Directory]::Delete($cacheRootFull, $false)
    if (Test-Path -LiteralPath $cacheRootFull) { throw "Cache root remained after prune: $cacheRootFull" }
}

function Get-VerifyEvidence {
    param([Parameter(Mandatory)][object]$Evidence)

    $kindProperty = $Evidence.PSObject.Properties['kind']
    $kind = if ($null -ne $kindProperty) { [string]$kindProperty.Value } else { '' }
    if ([string]::IsNullOrWhiteSpace($kind)) { $kind = 'gpu' }
    if ($kind -notin @('gpu', 'gpu-verify', 'rmd-verify', 'sdk-checks')) { throw "Unsupported prune evidence kind: $kind" }

    $requiredProperties = @('receiptPath', 'receiptSha256', 'jobReceiptPath', 'jobReceiptSha256')
    if ($kind -eq 'sdk-checks') { $requiredProperties += @('officialResultPath', 'officialResultSha256', 'expectedConsumerChecks', 'expectedSpecializationChecks') }
    foreach ($propertyName in $requiredProperties) {
        $property = $Evidence.PSObject.Properties[$propertyName]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "Prune evidence is missing '$propertyName'."
        }
    }

    $receiptPath = [System.IO.Path]::GetFullPath([string]$Evidence.receiptPath)
    $jobReceiptPath = [System.IO.Path]::GetFullPath([string]$Evidence.jobReceiptPath)
    if (
        -not (Test-Path -LiteralPath $receiptPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $jobReceiptPath -PathType Leaf)
    ) {
        throw 'The final stage receipt or its job receipt is missing.'
    }
    if ((Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash -ine [string]$Evidence.receiptSha256) {
        throw 'The final Verify receipt hash does not match the prune manifest.'
    }
    if ((Get-FileHash -LiteralPath $jobReceiptPath -Algorithm SHA256).Hash -ine [string]$Evidence.jobReceiptSha256) {
        throw 'The final stage job receipt hash does not match the prune manifest.'
    }

    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $officialResultPath = $null
    $officialResultSha256 = $null
    if ($kind -in @('gpu', 'gpu-verify', 'rmd-verify')) {
        if (
            $receipt.stage -ne 'Verify' -or
            [int]$receipt.exitCode -ne 0 -or
            $receipt.drained -ne $true -or
            [System.IO.Path]::GetFullPath([string]$receipt.jobReceiptPath) -ine $jobReceiptPath
        ) {
            throw 'Prune requires a successful, normally drained RMD Verify receipt.'
        }
    }
    else {
        if (
            $receipt.stage -ne 'SdkChecks' -or
            [int]$receipt.exitCode -ne 0 -or
            $receipt.normalDrain -ne $true -or
            $receipt.sourceInputsUnchanged -ne $true
        ) {
            throw 'Prune requires successful SDK checks, normal drain, and unchanged source inputs.'
        }
        $runDirectory = [System.IO.Path]::GetFullPath([string]$receipt.runDirectory)
        $expectedJobReceiptPath = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $runDirectory 'job') 'receipts.jsonl'))
        if ($jobReceiptPath -ine $expectedJobReceiptPath) {
            throw 'SDK job receipt path must be the runDirectory/job/receipts.jsonl receipt.'
        }
        $officialResultPath = [System.IO.Path]::GetFullPath([string]$Evidence.officialResultPath)
        if (-not (Test-Path -LiteralPath $officialResultPath -PathType Leaf)) { throw 'The official SDK result.json is missing.' }
        $officialResultSha256 = [string]$Evidence.officialResultSha256
        if ((Get-FileHash -LiteralPath $officialResultPath -Algorithm SHA256).Hash -ine $officialResultSha256) {
            throw 'The official SDK result.json hash does not match the prune manifest.'
        }

        $officialResult = Get-Content -LiteralPath $officialResultPath -Raw | ConvertFrom-Json
        if (
            $officialResult.Passed -ne $true -or
            [int]$officialResult.Consumer.Checks -ne [int]$Evidence.expectedConsumerChecks -or
            [int]$officialResult.Consumer.Failed -ne 0 -or
            [int]$officialResult.Specialization.Checks -ne [int]$Evidence.expectedSpecializationChecks -or
            [int]$officialResult.Specialization.Failed -ne 0 -or
            $officialResult.InputSeal.Passed -ne $true -or
            [string]::IsNullOrWhiteSpace([string]$officialResult.InputSeal.BeforeInputSha256) -or
            [string]$officialResult.InputSeal.BeforeInputSha256 -ine [string]$officialResult.InputSeal.AfterInputSha256 -or
            [int]$officialResult.InputSeal.BeforeFileCount -ne [int]$officialResult.InputSeal.AfterFileCount -or
            @($officialResult.InputSeal.Changes).Count -ne 0
        ) {
            throw 'The official SDK result does not prove the expected passing consumer, specialization, and input-seal checks.'
        }
    }

    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $jobReceiptPath) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $events.Add(($line | ConvertFrom-Json)) }
    }
    $admissions = @($events | Where-Object kind -eq 'admission')
    $configured = @($events | Where-Object kind -eq 'configured')
    $gateStarts = @($events | Where-Object kind -eq 'gate-start')
    $gateAssigned = @($events | Where-Object kind -eq 'gate-assigned')
    $childStarts = @($events | Where-Object kind -eq 'child-start')
    $childExits = @($events | Where-Object kind -eq 'child-exit')
    $finalJobs = @($events | Where-Object kind -eq 'final-job')
    $exitEvents = @($events | Where-Object kind -eq 'exit')
    $closedEvents = @($events | Where-Object kind -eq 'owned-job-closed')
    if (
        $admissions.Count -ne 1 -or $admissions[0].data.probe -ne $false -or
        $configured.Count -ne 1 -or
        $gateStarts.Count -ne 1 -or [long]$gateStarts[0].data.Id -le 0 -or [long]$gateStarts[0].data.StartFileTimeUtc -le 0 -or
        $gateAssigned.Count -ne 1 -or [long]$gateAssigned[0].data.Id -ne [long]$gateStarts[0].data.Id -or
        $childStarts.Count -lt 1 -or
        $finalJobs.Count -ne 1 -or $finalJobs[0].data.reason -ne 'normal' -or $finalJobs[0].data.empty -ne $true -or
        $exitEvents.Count -ne 1 -or [int]$exitEvents[0].data.ExitCode -ne 0 -or $exitEvents[0].data.reason -ne 'normal' -or
        [long]$exitEvents[0].data.Id -ne [long]$gateStarts[0].data.Id -or
        $closedEvents.Count -ne 1 -or $events.Count -eq 0 -or
        $events[$events.Count - 1].kind -ne 'owned-job-closed' -or
        $closedEvents[0].data.reason -ne 'normal' -or $closedEvents[0].data.assigned -ne $true -or
        $closedEvents[0].data.killOnCloseConfigured -ne $true
    ) {
        throw 'The final job receipt lacks successful admission, assignment, child start, normal empty drain, exit, or close evidence.'
    }

    $gateProcessId = [long]$gateStarts[0].data.Id
    $gateStartFileTime = [long]$gateStarts[0].data.StartFileTimeUtc
    $rootChildStarts = @($childStarts | Where-Object { [long]$_.data.pid -eq $gateProcessId -and [long]$_.data.startFileTimeUtc -eq $gateStartFileTime })
    $admissionIndex = [array]::IndexOf($events.ToArray(), $admissions[0])
    $configuredIndex = [array]::IndexOf($events.ToArray(), $configured[0])
    $gateStartIndex = [array]::IndexOf($events.ToArray(), $gateStarts[0])
    $gateAssignedIndex = [array]::IndexOf($events.ToArray(), $gateAssigned[0])
    $rootChildStartIndex = if ($rootChildStarts.Count -gt 0) { [array]::IndexOf($events.ToArray(), $rootChildStarts[0]) } else { -1 }
    $finalJobIndex = [array]::IndexOf($events.ToArray(), $finalJobs[0])
    $exitIndex = [array]::IndexOf($events.ToArray(), $exitEvents[0])
    $closedIndex = [array]::IndexOf($events.ToArray(), $closedEvents[0])
    if (
        $rootChildStarts.Count -ne 1 -or
        -not ($admissionIndex -lt $configuredIndex -and $configuredIndex -lt $gateStartIndex -and
            $gateStartIndex -lt $gateAssignedIndex -and $gateAssignedIndex -lt $rootChildStartIndex -and
            $rootChildStartIndex -lt $finalJobIndex -and $finalJobIndex -lt $exitIndex -and $exitIndex -lt $closedIndex)
    ) {
        throw 'The job receipt does not show the five lifecycle stages and final drain in order for the same process identity.'
    }

    [pscustomobject]@{
        Kind = $kind
        ReceiptPath = $receiptPath
        ReceiptSha256 = [string]$Evidence.receiptSha256
        JobReceiptPath = $jobReceiptPath
        JobReceiptSha256 = [string]$Evidence.jobReceiptSha256
        OfficialResultPath = $officialResultPath
        OfficialResultSha256 = $officialResultSha256
        ConsumerChecks = if ($kind -eq 'sdk-checks') { [int]$Evidence.expectedConsumerChecks } else { $null }
        SpecializationChecks = if ($kind -eq 'sdk-checks') { [int]$Evidence.expectedSpecializationChecks } else { $null }
    }
}

function Assert-LedgerObjectProperties {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string[]]$PropertyNames,
        [Parameter(Mandatory)][string]$Description
    )
    if ($null -eq $Object) { throw "$Description is missing." }
    foreach ($propertyName in $PropertyNames) {
        if ($null -eq $Object.PSObject.Properties[$propertyName]) {
            throw "$Description is missing '$propertyName'."
        }
    }
}
function Assert-LedgerEvidenceFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][string]$Description
    )
    if ($ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw "$Description has an invalid SHA-256 value." }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-NoReparseComponents -Path $fullPath -Root $ValidationRoot
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Description is missing: $fullPath" }
    $item = Get-Item -LiteralPath $fullPath
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Description is a reparse point: $fullPath" }
    if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -ine $ExpectedSha256) {
        throw "$Description SHA-256 does not match the prune manifest."
    }
    return $fullPath
}

function Assert-LedgerExactPath {
    param(
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Description
    )
    $actualFull = [System.IO.Path]::GetFullPath($Actual).TrimEnd('\')
    $expectedFull = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\')
    if ($actualFull -ine $expectedFull) { throw "$Description does not match its bound ledger stage." }
    return $actualFull
}

function Get-LedgerArgumentValue {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Context
    )
    $indices = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        if ($Arguments[$i] -ceq $Name) { $indices.Add($i) }
    }
    if ($indices.Count -ne 1 -or $indices[0] + 1 -ge $Arguments.Count) {
        throw "$Context must contain one complete '$Name' argument."
    }
    return [string]$Arguments[$indices[0] + 1]
}

function Assert-LedgerInputSnapshotStable {
    param(
        [Parameter(Mandatory)][object]$StageManifest,
        [string]$CountProperty = 'ChangedInputCount',
        [string[]]$ChangedArrayProperties = @('ChangedInputs')
    )
    $requiredProperties = @('InputsBefore', 'InputsAfter') + @($ChangedArrayProperties)
    if (-not [string]::IsNullOrWhiteSpace($CountProperty)) { $requiredProperties += $CountProperty }
    foreach ($propertyName in $requiredProperties) {
        if ($null -eq $StageManifest.PSObject.Properties[$propertyName]) {
            throw "The ledger input manifest is missing '$propertyName'."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($CountProperty) -and [int]$StageManifest.$CountProperty -ne 0) {
        throw 'The ledger input manifest reports changed inputs.'
    }
    foreach ($propertyName in $ChangedArrayProperties) {
        if (@($StageManifest.$propertyName).Count -ne 0) { throw 'The ledger input manifest reports changed inputs.' }
    }

    $before = @($StageManifest.InputsBefore)
    $after = @($StageManifest.InputsAfter)
    if ($before.Count -eq 0 -or $before.Count -ne $after.Count) {
        throw 'The ledger stage manifest lacks matching before/after input rows.'
    }
    $beforeByPath = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $beforeOccurrences = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $before) {
        foreach ($propertyName in @('path', 'exists', 'length', 'sha256')) {
            if ($null -eq $row.PSObject.Properties[$propertyName]) {
                throw 'The ledger before-input manifest has an incomplete input row.'
            }
        }
        $rawPath = [string]$row.path
        if ([string]::IsNullOrWhiteSpace($rawPath) -or -not [System.IO.Path]::IsPathRooted($rawPath)) {
            throw 'The ledger before-input manifest has a missing or non-absolute path.'
        }
        $path = [System.IO.Path]::GetFullPath($rawPath)
        $relativePath = if ($null -eq $row.relativePath) { '' } else { [string]$row.relativePath }
        if ($row.exists -eq $true) {
            if ([long]$row.length -lt 0 -or [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw 'The ledger before-input manifest has an invalid existing-input row.'
            }
            $signature = @('true', [string]$row.length, [string]$row.sha256.ToLowerInvariant(), $relativePath) -join "`0"
        }
        elseif ($row.exists -eq $false) {
            if ($null -ne $row.length -or -not [string]::IsNullOrEmpty([string]$row.sha256)) {
                throw 'The ledger before-input manifest has an invalid missing-input row.'
            }
            $signature = @('false', '', '', $relativePath) -join "`0"
        }
        else { throw 'The ledger before-input manifest has an invalid exists flag.' }
        if ($beforeByPath.ContainsKey($path)) {
            if ($beforeByPath[$path] -cne $signature) { throw "The ledger before-input manifest repeats a path with conflicting values: $path" }
            $beforeOccurrences[$path]++
        }
        else {
            $beforeByPath.Add($path, $signature)
            $beforeOccurrences.Add($path, 1)
        }
    }
    $afterOccurrences = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $after) {
        foreach ($propertyName in @('path', 'exists', 'length', 'sha256')) {
            if ($null -eq $row.PSObject.Properties[$propertyName]) {
                throw 'The ledger after-input manifest has an incomplete input row.'
            }
        }
        $rawPath = [string]$row.path
        if ([string]::IsNullOrWhiteSpace($rawPath) -or -not [System.IO.Path]::IsPathRooted($rawPath)) {
            throw 'The ledger after-input manifest has a missing or non-absolute path.'
        }
        $path = [System.IO.Path]::GetFullPath($rawPath)
        $relativePath = if ($null -eq $row.relativePath) { '' } else { [string]$row.relativePath }
        if ($row.exists -eq $true) {
            if ([long]$row.length -lt 0 -or [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw 'The ledger after-input manifest has an invalid existing-input row.'
            }
            $signature = @('true', [string]$row.length, [string]$row.sha256.ToLowerInvariant(), $relativePath) -join "`0"
        }
        elseif ($row.exists -eq $false) {
            if ($null -ne $row.length -or -not [string]::IsNullOrEmpty([string]$row.sha256)) {
                throw 'The ledger after-input manifest has an invalid missing-input row.'
            }
            $signature = @('false', '', '', $relativePath) -join "`0"
        }
        else { throw 'The ledger after-input manifest has an invalid exists flag.' }
        $beforeSignature = $null
        if (-not $beforeByPath.TryGetValue($path, [ref]$beforeSignature)) {
            throw "The ledger after-input manifest adds a path: $path"
        }
        if ($beforeSignature -cne $signature) { throw "The ledger input changed during the stage: $path" }
        if ($afterOccurrences.ContainsKey($path)) { $afterOccurrences[$path]++ }
        else { $afterOccurrences.Add($path, 1) }
    }
    if ($afterOccurrences.Count -ne $beforeByPath.Count) { throw 'The ledger after-input manifest omits before-input paths.' }
    foreach ($path in $beforeByPath.Keys) {
        if ($beforeOccurrences[$path] -ne $afterOccurrences[$path]) {
            throw "The ledger input manifest changed the occurrence count for: $path"
        }
    }
}
function Assert-LedgerExecutionLifecycle {
    param(
        [Parameter(Mandatory)][string]$JobReceiptPath,
        [Parameter(Mandatory)][object]$JobManifest,
        [Parameter(Mandatory)][string]$RunDirectory,
        [Parameter(Mandatory)][string]$AllowedReceiptRoot
    )
    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $JobReceiptPath) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $events.Add(($line | ConvertFrom-Json)) }
    }
    $admissions = @($events | Where-Object kind -eq 'admission')
    $configured = @($events | Where-Object kind -eq 'configured')
    $gateStarts = @($events | Where-Object kind -eq 'gate-start')
    $gateAssigned = @($events | Where-Object kind -eq 'gate-assigned')
    $childStarts = @($events | Where-Object kind -eq 'child-start')
    $childExits = @($events | Where-Object kind -eq 'child-exit')
    $finalJobs = @($events | Where-Object kind -eq 'final-job')
    $exitEvents = @($events | Where-Object kind -eq 'exit')
    $closedEvents = @($events | Where-Object kind -eq 'owned-job-closed')
    if (
        $admissions.Count -ne 1 -or $admissions[0].data.probe -ne $false -or
        $configured.Count -ne 1 -or
        $gateStarts.Count -ne 1 -or [long]$gateStarts[0].data.Id -le 0 -or [long]$gateStarts[0].data.StartFileTimeUtc -le 0 -or
        $gateAssigned.Count -ne 1 -or [long]$gateAssigned[0].data.Id -ne [long]$gateStarts[0].data.Id -or
        $childStarts.Count -lt 1 -or
        $finalJobs.Count -ne 1 -or $finalJobs[0].data.reason -ne 'normal' -or $finalJobs[0].data.empty -ne $true -or
        $exitEvents.Count -ne 1 -or [int]$exitEvents[0].data.ExitCode -ne 0 -or $exitEvents[0].data.reason -ne 'normal' -or
        [long]$exitEvents[0].data.Id -ne [long]$gateStarts[0].data.Id -or
        $closedEvents.Count -ne 1 -or $events.Count -eq 0 -or
        $events[$events.Count - 1].kind -ne 'owned-job-closed' -or
        $closedEvents[0].data.reason -ne 'normal' -or $closedEvents[0].data.assigned -ne $true -or
        $closedEvents[0].data.killOnCloseConfigured -ne $true
    ) { throw 'The ledger ExecutionJob receipt lacks a complete successful lifecycle.' }

    $gateProcessId = [long]$gateStarts[0].data.Id
    $gateStartFileTime = [long]$gateStarts[0].data.StartFileTimeUtc
    $rootChildStarts = @($childStarts | Where-Object {
        [long]$_.data.pid -eq $gateProcessId -and [long]$_.data.startFileTimeUtc -eq $gateStartFileTime
    })
    $rootChildExits = @($childExits | Where-Object {
        [long]$_.data.pid -eq $gateProcessId -and [long]$_.data.observedCreationFileTimeUtc -eq $gateStartFileTime
    })
    $admissionPlan = $admissions[0].data.plan
    $manifestArguments = @($JobManifest.arguments | ForEach-Object { [string]$_ })
    $admissionArguments = @($admissionPlan.Arguments | ForEach-Object { [string]$_ })
    if (
        $rootChildStarts.Count -ne 1 -or $rootChildExits.Count -ne 1 -or [int]$rootChildExits[0].data.exitCode -ne 0 -or
        $manifestArguments.Count -ne $admissionArguments.Count -or
        (($manifestArguments -join "`0") -cne ($admissionArguments -join "`0")) -or
        [System.IO.Path]::GetFullPath([string]$admissionPlan.RunDirectory) -ine $RunDirectory -or
        [System.IO.Path]::GetFullPath([string]$admissionPlan.AllowedReceiptRoot) -ine $AllowedReceiptRoot -or
        [System.IO.Path]::GetFullPath([string]$JobManifest.runDirectory) -ine $RunDirectory -or
        [System.IO.Path]::GetFullPath([string]$JobManifest.allowedReceiptRoot) -ine $AllowedReceiptRoot
    ) { throw 'The ledger ExecutionJob lifecycle does not bind its manifest, job root, and assigned process.' }

    $eventArray = $events.ToArray()
    $admissionIndex = [array]::IndexOf($eventArray, $admissions[0])
    $configuredIndex = [array]::IndexOf($eventArray, $configured[0])
    $gateStartIndex = [array]::IndexOf($eventArray, $gateStarts[0])
    $gateAssignedIndex = [array]::IndexOf($eventArray, $gateAssigned[0])
    $rootChildStartIndex = [array]::IndexOf($eventArray, $rootChildStarts[0])
    $rootChildExitIndex = [array]::IndexOf($eventArray, $rootChildExits[0])
    $finalJobIndex = [array]::IndexOf($eventArray, $finalJobs[0])
    $exitIndex = [array]::IndexOf($eventArray, $exitEvents[0])
    $closedIndex = [array]::IndexOf($eventArray, $closedEvents[0])
    if (-not (
        $admissionIndex -lt $configuredIndex -and $configuredIndex -lt $gateStartIndex -and
        $gateStartIndex -lt $gateAssignedIndex -and $gateAssignedIndex -lt $rootChildStartIndex -and
        $rootChildStartIndex -lt $rootChildExitIndex -and $rootChildExitIndex -lt $finalJobIndex -and
        $finalJobIndex -lt $exitIndex -and $exitIndex -lt $closedIndex
    )) { throw 'The ledger ExecutionJob lifecycle events are out of order.' }
}

function Assert-LedgerUseBaselineArtifacts {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][object]$StageManifest,
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$RootModulePath
    )
    $candidateName = $Candidate.ToLowerInvariant()
    if ($candidateName -notin @('c2', 'b2')) { throw 'UseBaseline cache evidence requires a concrete c2 or b2 candidate.' }

    $expectedInputs = @(
        $RootModulePath,
        (Join-Path ([string]$StageManifest.ValuesRoot) 'ledger_values.zig'),
        (Join-Path (Join-Path $PSScriptRoot '..\..') 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'),
        (Join-Path $StageRoot 'benchmark_options.zig')
    )
    foreach ($expectedInput in $expectedInputs) {
        $expectedFullPath = [System.IO.Path]::GetFullPath($expectedInput)
        $matchingInputs = @($StageManifest.InputsBefore | Where-Object {
            [System.IO.Path]::GetFullPath([string]$_.path) -ieq $expectedFullPath
        })
        if ($matchingInputs.Count -lt 1) { throw "The UseBaseline input snapshot does not pin its compiler module: $expectedFullPath" }
        foreach ($inputRow in $matchingInputs) {
            if ($inputRow.exists -ne $true -or [long]$inputRow.length -lt 0 -or [string]$inputRow.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw "The UseBaseline input snapshot has an invalid compiler-module row: $expectedFullPath"
            }
        }
    }

    $optionsPath = Join-Path $StageRoot 'benchmark_options.zig'
    $optionsText = Get-Content -LiteralPath $optionsPath -Raw
    $candidateOptionPattern = '(?m)^\s*pub\s+const\s+candidate\s*:\s*enum\s*\{\s*both\s*,\s*c2\s*,\s*b2\s*\}\s*=\s*\.' + [regex]::Escape($candidateName) + '\s*;\s*$'
    if ($optionsText -notmatch $candidateOptionPattern) {
        throw 'The UseBaseline benchmark_options.zig does not select its receipt-bound candidate.'
    }

    $logsRoot = Join-Path $StageRoot 'logs'
    if (-not (Test-Path -LiteralPath $logsRoot -PathType Container)) { throw 'The UseBaseline output log directory is missing.' }
    Assert-NoReparseComponents -Path $logsRoot -Root $StageRoot
    $caseKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($caseNumber = 1; $caseNumber -le 60; $caseNumber++) {
        $casePath = Join-Path $logsRoot ("use-case-$caseNumber.log")
        if (-not (Test-Path -LiteralPath $casePath -PathType Leaf)) { throw "The UseBaseline correctness output is missing case $caseNumber." }
        $caseItem = Get-Item -LiteralPath $casePath
        if (($caseItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "The UseBaseline case output is a reparse point: $casePath" }
        $case = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
        Assert-LedgerObjectProperties -Object $case -PropertyNames @(
            'schema', 'candidate', 'uid_width_bits', 'resource_capacity', 'path', 'measurement',
            'state', 'trace', 'run_mode', 'build_mode', 'iterations_per_sample', 'sample_count',
            'cache_mode', 'samples_elapsed_ns'
        ) -Description "UseBaseline correctness output case $caseNumber"
        $uidWidth = [int]$case.uid_width_bits
        $capacity = [int]$case.resource_capacity
        $pathName = [string]$case.path
        $measurement = [string]$case.measurement
        $state = [string]$case.state
        $trace = [string]$case.trace
        if (
            $case.schema -cne 'resource-manager/owner-use-benchmark/v1' -or
            [string]$case.candidate -cne $candidateName -or
            $uidWidth -notin @(128, 256) -or $capacity -notin @(1024, 1048576) -or
            $pathName -notin @('constant_fixed', 'runtime_fixed_uid', 'runtime_dynamic_uid') -or
            $measurement -notin @('owner_use', 'index_only') -or
            $state -notin @('hit', 'notresident', 'loading') -or
            $case.run_mode -cne 'correctness' -or $case.build_mode -cne 'ReleaseFast' -or
            [int]$case.iterations_per_sample -ne 128 -or [int]$case.sample_count -ne 0 -or
            $case.cache_mode -cne 'warm' -or @($case.samples_elapsed_ns).Count -ne 0 -or
            ($pathName -ceq 'constant_fixed' -and $measurement -cne 'owner_use') -or
            ($state -ceq 'hit' -and $trace -cne 'shuffled') -or
            ($state -cne 'hit' -and $trace -cne 'single')
        ) { throw "The UseBaseline case output is outside the pinned correctness matrix: $casePath" }
        $measurements = if ($pathName -ceq 'constant_fixed') { @('owner_use') } else { @('owner_use', 'index_only') }
        if ($measurement -notin $measurements) { throw "The UseBaseline case has an invalid measurement for path $pathName." }
        $key = @($uidWidth, $capacity, $pathName, $measurement, $state, $trace) -join '|'
        if (-not $caseKeys.Add($key)) { throw "The UseBaseline correctness output repeats a matrix case: $key" }
    }

    $expectedCaseKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($uidWidth in @(128, 256)) {
        foreach ($capacity in @(1024, 1048576)) {
            foreach ($pathName in @('constant_fixed', 'runtime_fixed_uid', 'runtime_dynamic_uid')) {
                $measurements = if ($pathName -ceq 'constant_fixed') { @('owner_use') } else { @('owner_use', 'index_only') }
                foreach ($measurement in $measurements) {
                    foreach ($state in @('hit', 'notresident', 'loading')) {
                        $trace = if ($state -ceq 'hit') { 'shuffled' } else { 'single' }
                        $null = $expectedCaseKeys.Add(@($uidWidth, $capacity, $pathName, $measurement, $state, $trace) -join '|')
                    }
                }
            }
        }
    }
    if ($caseKeys.Count -ne $expectedCaseKeys.Count) { throw 'The UseBaseline correctness artifacts do not cover the complete 60-case matrix.' }
    foreach ($expectedCaseKey in $expectedCaseKeys) {
        if (-not $caseKeys.Contains($expectedCaseKey)) { throw "The UseBaseline correctness artifacts omit matrix case: $expectedCaseKey" }
    }

    $refusalArtifacts = [ordered]@{
        'affinity-refusal-candidate-build-mismatch.log' = 'error: CandidateBuildMismatch'
        'affinity-refusal-constant-fixed-index-only.log' = 'error: IndexOnlyRequiresRuntimeUidPath'
        'affinity-refusal-multiple-bits.log' = 'error: AffinityRequiresOneProcessor'
        'affinity-refusal-timing-unpinned.log' = 'error: TimingRequiresExplicitAffinity'
        'affinity-refusal-zero-mask.log' = 'error: AffinityRequiresOneProcessor'
    }
    foreach ($artifactName in $refusalArtifacts.Keys) {
        $artifactPath = Join-Path $logsRoot $artifactName
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw "The UseBaseline refusal output is missing: $artifactName" }
        $artifactItem = Get-Item -LiteralPath $artifactPath
        if (($artifactItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-Content -LiteralPath $artifactPath -Raw).Trim() -cne [string]$refusalArtifacts[$artifactName]) {
            throw "The UseBaseline refusal output is not the expected preserved diagnostic: $artifactName"
        }
    }

    $executablePath = Join-Path $StageRoot 'bin\owned-validation.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw 'The UseBaseline emitted executable is missing.' }
    $executableItem = Get-Item -LiteralPath $executablePath
    if (($executableItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or $executableItem.Length -le 0) {
        throw 'The UseBaseline emitted executable is empty or is a reparse point.'
    }
}

function Assert-LedgerUseBaseline120ColdArtifacts {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][object]$StageManifest,
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$RootModulePath,
        [Parameter(Mandatory)][string]$CommandsPath,
        [Parameter(Mandatory)][string]$ResultsPath,
        [Parameter(Mandatory)][string]$ArtifactManifestPath
    )
    $candidateName = $Candidate.ToLowerInvariant()
    if ($candidateName -notin @('c2', 'b2')) { throw 'UseBaseline 120-case evidence requires a concrete c2 or b2 candidate.' }

    $logsRoot = Join-Path $StageRoot 'logs'
    if (-not (Test-Path -LiteralPath $logsRoot -PathType Container)) { throw 'The UseBaseline 120-case output log directory is missing.' }
    Assert-NoReparseComponents -Path $logsRoot -Root $StageRoot
    foreach ($boundedFile in @(
        @{ Path = $ArtifactManifestPath; MaximumBytes = 1048576; Description = 'artifact manifest' },
        @{ Path = $CommandsPath; MaximumBytes = 1048576; Description = 'command log' },
        @{ Path = $ResultsPath; MaximumBytes = 16777216; Description = 'correctness results' }
    )) {
        $boundedItem = Get-Item -LiteralPath $boundedFile.Path
        if (($boundedItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or [long]$boundedItem.Length -gt [long]$boundedFile.MaximumBytes) {
            throw "The UseBaseline 120-case $($boundedFile.Description) is reparse-backed or exceeds its read bound."
        }
    }

    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $expectedInputs = @(
        $RootModulePath,
        (Join-Path ([string]$StageManifest.ValuesRoot) 'ledger_values.zig'),
        (Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'),
        (Join-Path $StageRoot 'benchmark_options.zig'),
        (Join-Path $repositoryRoot '.local\ledger-owner-implementation\foundation\cache_cold_proposal.zig')
    )
    foreach ($expectedInput in $expectedInputs) {
        $expectedFullPath = [System.IO.Path]::GetFullPath($expectedInput)
        $matchingInputs = @($StageManifest.InputsBefore | Where-Object {
            [System.IO.Path]::GetFullPath([string]$_.path) -ieq $expectedFullPath
        })
        if ($matchingInputs.Count -lt 1) { throw "The UseBaseline 120-case input snapshot does not pin its compiler module: $expectedFullPath" }
        foreach ($inputRow in $matchingInputs) {
            if ($inputRow.exists -ne $true -or [long]$inputRow.length -lt 0 -or [string]$inputRow.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw "The UseBaseline 120-case input snapshot has an invalid compiler-module row: $expectedFullPath"
            }
        }
    }

    $optionsPath = Join-Path $StageRoot 'benchmark_options.zig'
    $optionsText = Get-Content -LiteralPath $optionsPath -Raw
    $candidateOptionPattern = '(?m)^\s*pub\s+const\s+candidate\s*:\s*enum\s*\{\s*both\s*,\s*c2\s*,\s*b2\s*\}\s*=\s*\.' + [regex]::Escape($candidateName) + '\s*;\s*$'
    if ($optionsText -notmatch $candidateOptionPattern) {
        throw 'The UseBaseline 120-case benchmark_options.zig does not select its receipt-bound candidate.'
    }

    $artifactManifest = Get-Content -LiteralPath $ArtifactManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $artifactManifest -PropertyNames @(
        'Candidate', 'Binary', 'BinarySha256', 'Zig', 'ZigSha256', 'Options', 'OptionsSha256', 'ValuesRoot', 'CompilerCommand'
    ) -Description 'UseBaseline 120-case artifact manifest'
    Assert-LedgerObjectProperties -Object $artifactManifest.CompilerCommand -PropertyNames @(
        'name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'output'
    ) -Description 'UseBaseline 120-case artifact compiler command'
    $executablePath = Join-Path $StageRoot 'bin\owned-validation.exe'
    if ([string]$artifactManifest.Candidate -cne $candidateName) { throw 'The UseBaseline 120-case artifact manifest candidate does not match its receipt.' }
    $null = Assert-LedgerExactPath -Actual ([string]$artifactManifest.Binary) -Expected $executablePath -Description 'UseBaseline 120-case artifact binary'
    $null = Assert-LedgerExactPath -Actual ([string]$artifactManifest.Zig) -Expected ([string]$Receipt.Command.executable) -Description 'UseBaseline 120-case artifact compiler'
    $null = Assert-LedgerExactPath -Actual ([string]$artifactManifest.Options) -Expected $optionsPath -Description 'UseBaseline 120-case artifact options'
    $null = Assert-LedgerExactPath -Actual ([string]$artifactManifest.ValuesRoot) -Expected ([string]$StageManifest.ValuesRoot) -Description 'UseBaseline 120-case artifact values root'
    foreach ($hashValue in @($artifactManifest.BinarySha256, $artifactManifest.ZigSha256, $artifactManifest.OptionsSha256)) {
        if ([string]$hashValue -notmatch '^[A-Fa-f0-9]{64}$') { throw 'The UseBaseline 120-case artifact manifest contains an invalid SHA-256 value.' }
    }
    foreach ($hashBinding in @(
        @{ Path = $executablePath; Hash = [string]$artifactManifest.BinarySha256; Description = 'emitted executable' },
        @{ Path = [string]$artifactManifest.Zig; Hash = [string]$artifactManifest.ZigSha256; Description = 'Zig compiler' },
        @{ Path = $optionsPath; Hash = [string]$artifactManifest.OptionsSha256; Description = 'benchmark options' }
    )) {
        if (-not (Test-Path -LiteralPath $hashBinding.Path -PathType Leaf)) { throw "The UseBaseline 120-case $($hashBinding.Description) is missing." }
        $hashItem = Get-Item -LiteralPath $hashBinding.Path
        if (($hashItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            (Get-FileHash -LiteralPath $hashBinding.Path -Algorithm SHA256).Hash -ine $hashBinding.Hash) {
            throw "The UseBaseline 120-case $($hashBinding.Description) does not match the artifact manifest hash."
        }
    }
    $receiptArguments = @($Receipt.Command.arguments | ForEach-Object { [string]$_ })
    $artifactArguments = @($artifactManifest.CompilerCommand.arguments | ForEach-Object { [string]$_ })
    if ([string]$artifactManifest.CompilerCommand.name -cne 'sdk38-owned-validation' -or
        [string]$artifactManifest.CompilerCommand.executable -ine [string]$Receipt.Command.executable -or
        [int]$artifactManifest.CompilerCommand.expectedExitCode -ne 0 -or [int]$artifactManifest.CompilerCommand.exitCode -ne 0 -or
        [string]$artifactManifest.CompilerCommand.output -ine [string]$Receipt.Command.output -or
        ($artifactArguments -join "`0") -cne ($receiptArguments -join "`0")) {
        throw 'The UseBaseline 120-case artifact manifest compiler command does not match the successful Native receipt.'
    }

    $commands = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $CommandsPath) {
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'The UseBaseline 120-case command log contains a blank record.' }
        $commands.Add(($line | ConvertFrom-Json))
    }
    if ($commands.Count -ne 126) { throw 'The UseBaseline 120-case command log must contain one build, five refusals, and 120 correctness invocations.' }
    $commandExecutable = Join-Path $StageRoot 'bin\owned-validation.exe'
    $compileCommand = $commands[0]
    Assert-LedgerObjectProperties -Object $compileCommand -PropertyNames @('name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'output') -Description 'UseBaseline 120-case command log compiler row'
    if ([string]$compileCommand.name -cne 'sdk38-owned-validation' -or
        [string]$compileCommand.executable -ine [string]$Receipt.Command.executable -or
        [int]$compileCommand.expectedExitCode -ne 0 -or [int]$compileCommand.exitCode -ne 0 -or
        [string]$compileCommand.output -ine [string]$Receipt.Command.output -or
        (@($compileCommand.arguments | ForEach-Object { [string]$_ }) -join "`0") -cne ($receiptArguments -join "`0")) {
        throw 'The UseBaseline 120-case command log compiler row does not match its successful Native receipt.'
    }

    $expectedRefusals = [ordered]@{
        'affinity-refusal-zero-mask' = @('AffinityRequiresOneProcessor', @('--candidate', $candidateName, '--uid-bits', '128', '--capacity', '1024', '--path', 'constant-fixed', '--state', 'hit', '--trace', 'shuffled', '--mode', 'correctness', '--cache', 'warm', '--iterations', '1', '--affinity-mask', '0'))
        'affinity-refusal-multiple-bits' = @('AffinityRequiresOneProcessor', @('--candidate', $candidateName, '--uid-bits', '128', '--capacity', '1024', '--path', 'constant-fixed', '--state', 'hit', '--trace', 'shuffled', '--mode', 'correctness', '--cache', 'warm', '--iterations', '1', '--affinity-mask', '3'))
        'affinity-refusal-timing-unpinned' = @('TimingRequiresExplicitAffinity', @('--candidate', $candidateName, '--uid-bits', '128', '--capacity', '1024', '--path', 'constant-fixed', '--state', 'hit', '--trace', 'shuffled', '--mode', 'timing', '--cache', 'warm', '--iterations', '1', '--samples', '1'))
        'affinity-refusal-constant-fixed-index-only' = @('IndexOnlyRequiresRuntimeUidPath', @('--candidate', $candidateName, '--uid-bits', '128', '--capacity', '1024', '--path', 'constant-fixed', '--state', 'hit', '--trace', 'shuffled', '--mode', 'correctness', '--cache', 'warm', '--iterations', '1', '--measurement', 'index-only', '--affinity-mask', '1'))
    }
    $mismatchedCandidate = if ($candidateName -ceq 'c2') { 'b2' } else { 'c2' }
    $expectedRefusals['affinity-refusal-candidate-build-mismatch'] = @('CandidateBuildMismatch', @('--candidate', $mismatchedCandidate, '--uid-bits', '128', '--capacity', '1024', '--path', 'constant-fixed', '--state', 'hit', '--trace', 'shuffled', '--mode', 'correctness', '--cache', 'warm', '--iterations', '1', '--affinity-mask', '1'))
    foreach ($refusalName in $expectedRefusals.Keys) {
        $refusalRows = @($commands | Where-Object { [string]$_.name -ceq $refusalName })
        if ($refusalRows.Count -ne 1) { throw "The UseBaseline 120-case command log must contain one $refusalName row." }
        $refusal = $refusalRows[0]
        $refusalDetails = @($expectedRefusals[$refusalName])
        $expectedError = [string]$refusalDetails[0]
        $expectedArguments = @($refusalDetails[1] | ForEach-Object { [string]$_ })
        $expectedOutput = Join-Path (Join-Path $StageRoot 'logs') ($refusalName + '.log')
        if ([string]$refusal.executable -ine $commandExecutable -or $refusal.expectedNonzeroExitCode -ne $true -or
            [string]$refusal.expectedError -cne $expectedError -or [int]$refusal.exitCode -ne 1 -or
            [string]$refusal.output -ine $expectedOutput -or
            (@($refusal.arguments | ForEach-Object { [string]$_ }) -join "`0") -cne ($expectedArguments -join "`0")) {
            throw "The UseBaseline 120-case refusal command does not match its pinned diagnostic shape: $refusalName"
        }
        if (-not (Test-Path -LiteralPath $expectedOutput -PathType Leaf)) { throw "The UseBaseline 120-case refusal output is missing: $refusalName" }
        $refusalItem = Get-Item -LiteralPath $expectedOutput
        if (($refusalItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or [long]$refusalItem.Length -gt 4096 -or
            (Get-Content -LiteralPath $expectedOutput -Raw).Trim() -cne ('error: ' + $expectedError)) {
            throw "The UseBaseline 120-case refusal output does not match its command record: $refusalName"
        }
    }

    $results = @(Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json)
    if ($results.Count -ne 120) { throw 'The UseBaseline 120-case correctness result file must contain exactly 120 rows.' }
    $caseKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    for ($caseNumber = 1; $caseNumber -le 120; $caseNumber++) {
        $caseCommand = $commands | Where-Object { [string]$_.name -ceq ("owned-use-correctness-{0}" -f $caseNumber) }
        if (@($caseCommand).Count -ne 1) { throw "The UseBaseline 120-case command log is missing or repeats case $caseNumber." }
        $caseCommand = @($caseCommand)[0]
        $casePath = Join-Path $logsRoot ("use-case-{0}.log" -f $caseNumber)
        if (-not (Test-Path -LiteralPath $casePath -PathType Leaf)) { throw "The UseBaseline 120-case correctness output is missing case $caseNumber." }
        $caseItem = Get-Item -LiteralPath $casePath
        if (($caseItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or [long]$caseItem.Length -gt 131072) { throw "The UseBaseline 120-case output is reparse-backed or exceeds its read bound: $casePath" }
        $case = Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
        $resultRow = $results[$caseNumber - 1]
        if ((ConvertTo-Json -InputObject $case -Depth 100 -Compress) -cne (ConvertTo-Json -InputObject $resultRow -Depth 100 -Compress)) {
            throw "The UseBaseline 120-case log and raw results row disagree for case $caseNumber."
        }
        Assert-LedgerObjectProperties -Object $case -PropertyNames @(
            'schema', 'candidate', 'uid_width_bits', 'resource_capacity', 'partition_capacity', 'index_node_capacity',
            'index_home_capacity', 'indexed_entries_after_setup', 'path', 'measurement', 'state', 'trace', 'cache_mode',
            'run_mode', 'build_mode', 'iterations_per_sample', 'sample_count', 'warmup_iterations', 'samples_elapsed_ns',
            'fixture_construction', 'fixture_construction_timed', 'activity_bitmap_width', 'cache_cold_method',
            'cache_cold_scope', 'cache_cold_cache_line_bytes', 'cache_cold_workspace_address_count',
            'cache_cold_workspace_bytes', 'cache_cold_workspace_limit_bytes', 'cache_cold_max_operation_address_count',
            'cache_cold_flush_count', 'cache_cold_flushed_address_count', 'result_counts', 'index_lookup_counts',
            'metadata_bytes', 'same_home_contention_supported', 'affinity'
        ) -Description "UseBaseline 120-case correctness output $caseNumber"

        $uidWidth = [int]$case.uid_width_bits
        $capacity = [int]$case.resource_capacity
        $pathName = [string]$case.path
        $measurement = [string]$case.measurement
        $state = [string]$case.state
        $trace = [string]$case.trace
        $cacheMode = [string]$case.cache_mode
        $expectedTrace = if ($state -ceq 'hit') { 'shuffled' } else { 'single' }
        if (
            [string]$case.schema -cne 'resource-manager/owner-use-benchmark/v2' -or
            [string]$case.candidate -cne $candidateName -or
            $uidWidth -notin @(128, 256) -or $capacity -notin @(1024, 1048576) -or
            [int]$case.partition_capacity -ne 0 -or [int]$case.index_node_capacity -ne $capacity -or
            [int]$case.index_home_capacity -ne (2 * $capacity) -or [int]$case.indexed_entries_after_setup -ne $capacity -or
            $pathName -notin @('constant_fixed', 'runtime_fixed_uid', 'runtime_dynamic_uid') -or
            $measurement -notin @('owner_use', 'index_only') -or
            $state -notin @('hit', 'notresident', 'loading') -or $trace -cne $expectedTrace -or
            $cacheMode -notin @('warm', 'cold') -or
            [string]$case.run_mode -cne 'correctness' -or [string]$case.build_mode -cne 'ReleaseFast' -or
            [int]$case.iterations_per_sample -ne 128 -or [int]$case.sample_count -ne 0 -or
            [int]$case.warmup_iterations -ne 0 -or @($case.samples_elapsed_ns).Count -ne 0 -or
            [string]$case.fixture_construction -cne 'maxdriver_direct_zero_byte_resource_and_index_setup' -or
            $case.fixture_construction_timed -ne $false -or [int]$case.activity_bitmap_width -ne 8 -or
            $case.same_home_contention_supported -ne $false -or
            ($pathName -ceq 'constant_fixed' -and $measurement -cne 'owner_use')
        ) { throw "The UseBaseline 120-case output is outside its pinned warm/cold correctness matrix: $casePath" }

        if ($cacheMode -ceq 'warm') {
            if ($null -ne $case.cache_cold_method -or $null -ne $case.cache_cold_scope -or
                $null -ne $case.cache_cold_cache_line_bytes -or [int]$case.cache_cold_workspace_address_count -ne 0 -or
                [long]$case.cache_cold_workspace_bytes -ne 0 -or [long]$case.cache_cold_workspace_limit_bytes -ne 67108864 -or
                [int]$case.cache_cold_max_operation_address_count -ne 0 -or [int]$case.cache_cold_flush_count -ne 0 -or
                [int]$case.cache_cold_flushed_address_count -ne 0) {
                throw "The UseBaseline warm case reports cache-flush work: $casePath"
            }
        } else {
            $expectedScope = if ($measurement -ceq 'owner_use') {
                'owner-use lookup metadata collected for the selected path'
            } else {
                'index-only timing; collection includes owner header and matching resource row when present as an explicit superset'
            }
            $addressCount = [int]$case.cache_cold_workspace_address_count
            if ([string]$case.cache_cold_method -cne 'collect exact lookup metadata address ranges; execute guarded CLFLUSH+MFENCE before each operation; does not establish an empty cache' -or
                [string]$case.cache_cold_scope -cne $expectedScope -or [int]$case.cache_cold_cache_line_bytes -ne 64 -or
                $addressCount -lt 1 -or [long]$case.cache_cold_workspace_bytes -ne (8 * $addressCount) -or
                [long]$case.cache_cold_workspace_limit_bytes -ne 67108864 -or
                [int]$case.cache_cold_max_operation_address_count -lt 1 -or [int]$case.cache_cold_max_operation_address_count -gt $addressCount -or
                [int]$case.cache_cold_flush_count -ne 128 -or [int]$case.cache_cold_flushed_address_count -lt 128 -or
                [int]$case.cache_cold_flushed_address_count -gt (128 * [int]$case.cache_cold_max_operation_address_count)) {
                throw "The UseBaseline cold case lacks a bounded per-operation cache-flush receipt: $casePath"
            }
        }

        if ($measurement -ceq 'owner_use') {
            if ($null -eq $case.result_counts -or $null -ne $case.index_lookup_counts) { throw "The UseBaseline owner-use case has an invalid result-count shape: $casePath" }
            $expectedCounts = @{ in_memory = 0; not_in_memory = 0; loading = 0 }
            $expectedCountKey = if ($state -ceq 'hit') { 'in_memory' } elseif ($state -ceq 'notresident') { 'not_in_memory' } else { 'loading' }
            $expectedCounts[$expectedCountKey] = 128
            foreach ($countName in @('in_memory', 'not_in_memory', 'loading')) {
                if ([int]$case.result_counts.$countName -ne [int]$expectedCounts[$countName]) { throw "The UseBaseline owner-use case result counts disagree with its state: $casePath" }
            }
        } else {
            if ($null -ne $case.result_counts -or $null -eq $case.index_lookup_counts) { throw "The UseBaseline index-only case has an invalid result-count shape: $casePath" }
            $expectedMiss = $pathName -ceq 'runtime_dynamic_uid' -and $state -ceq 'notresident'
            $expectedHitCount = if ($expectedMiss) { 0 } else { 128 }
            $expectedMissCount = if ($expectedMiss) { 128 } else { 0 }
            if ([int]$case.index_lookup_counts.hit -ne $expectedHitCount -or [int]$case.index_lookup_counts.miss -ne $expectedMissCount) {
                throw "The UseBaseline index-only case lookup counts disagree with its path and state: $casePath"
            }
        }
        $expectedMeasurements = if ($pathName -ceq 'constant_fixed') { @('owner_use') } else { @('owner_use', 'index_only') }
        if ($measurement -notin $expectedMeasurements) { throw "The UseBaseline case has an invalid measurement for path $pathName." }
        $key = @($uidWidth, $capacity, $pathName, $measurement, $state, $cacheMode, $trace) -join '|'
        if (-not $caseKeys.Add($key)) { throw "The UseBaseline 120-case output repeats a matrix case: $key" }

        $cliPath = switch ($pathName) {
            'constant_fixed' { 'constant-fixed' }
            'runtime_fixed_uid' { 'runtime-fixed-uid' }
            'runtime_dynamic_uid' { 'runtime-dynamic-uid' }
        }
        $cliMeasurement = if ($measurement -ceq 'owner_use') { 'owner-use' } else { 'index-only' }
        $expectedArguments = @(
            '--candidate', $candidateName, '--uid-bits', [string]$uidWidth, '--capacity', [string]$capacity,
            '--path', $cliPath, '--measurement', $cliMeasurement, '--state', $state, '--trace', $trace,
            '--mode', 'correctness', '--cache', $cacheMode, '--iterations', '128', '--seed', '1', '--affinity-mask', '1'
        )
        $caseExpectedName = "owned-use-correctness-$caseNumber"
        $expectedOutput = Join-Path $logsRoot ("use-case-{0}.log" -f $caseNumber)
        if ([string]$caseCommand.executable -ine $commandExecutable -or [int]$caseCommand.expectedExitCode -ne 0 -or
            [int]$caseCommand.exitCode -ne 0 -or [string]$caseCommand.output -ine $expectedOutput -or
            (@($caseCommand.arguments | ForEach-Object { [string]$_ }) -join "`0") -cne ($expectedArguments -join "`0")) {
            throw "The UseBaseline 120-case command row does not match its output: $caseExpectedName"
        }
    }

    $expectedCaseKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($uidWidth in @(128, 256)) {
        foreach ($capacity in @(1024, 1048576)) {
            foreach ($pathName in @('constant_fixed', 'runtime_fixed_uid', 'runtime_dynamic_uid')) {
                $measurements = if ($pathName -ceq 'constant_fixed') { @('owner_use') } else { @('owner_use', 'index_only') }
                foreach ($measurement in $measurements) {
                    foreach ($state in @('hit', 'notresident', 'loading')) {
                        foreach ($cacheMode in @('warm', 'cold')) {
                            $trace = if ($state -ceq 'hit') { 'shuffled' } else { 'single' }
                            $null = $expectedCaseKeys.Add(@($uidWidth, $capacity, $pathName, $measurement, $state, $cacheMode, $trace) -join '|')
                        }
                    }
                }
            }
        }
    }
    if ($caseKeys.Count -ne 120 -or $caseKeys.Count -ne $expectedCaseKeys.Count) { throw 'The UseBaseline correctness artifacts do not cover the complete 120-case warm/cold matrix.' }
    foreach ($expectedCaseKey in $expectedCaseKeys) {
        if (-not $caseKeys.Contains($expectedCaseKey)) { throw "The UseBaseline correctness artifacts omit matrix case: $expectedCaseKey" }
    }

    $expectedFinalArguments = @(
        '--candidate', $candidateName, '--uid-bits', '256', '--capacity', '1048576',
        '--path', 'runtime-dynamic-uid', '--measurement', 'index-only',
        '--state', 'loading', '--trace', 'single', '--mode', 'correctness',
        '--cache', 'cold', '--iterations', '128', '--seed', '1', '--affinity-mask', '1'
    )
    if ([string]$Receipt.RuntimeCommand.name -cne 'owned-use-correctness-120' -or
        (@($Receipt.RuntimeCommand.arguments | ForEach-Object { [string]$_ }) -join "`0") -cne ($expectedFinalArguments -join "`0") -or
        [string]$Receipt.RuntimeCommand.output -ine (Join-Path $logsRoot 'use-case-120.log')) {
        throw 'The UseBaseline runtime receipt is not bound to the final 120-case cold correctness invocation.'
    }
}

function Get-LedgerStableInputRow {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )
    $expectedPath = [System.IO.Path]::GetFullPath($Path)
    $beforeRows = @($Manifest.InputsBefore | Where-Object {
        [System.IO.Path]::GetFullPath([string]$_.path) -ieq $expectedPath
    })
    $afterRows = @($Manifest.InputsAfter | Where-Object {
        [System.IO.Path]::GetFullPath([string]$_.path) -ieq $expectedPath
    })
    if ($beforeRows.Count -lt 1 -or $beforeRows.Count -ne $afterRows.Count -or
        @($beforeRows | Where-Object {
            $_.exists -ne $true -or [long]$_.length -ne [long]$beforeRows[0].length -or
            [string]$_.sha256 -ine [string]$beforeRows[0].sha256 -or
            [string]$_.relativePath -cne [string]$beforeRows[0].relativePath
        }).Count -gt 0 -or
        @($afterRows | Where-Object {
            $_.exists -ne $true -or [long]$_.length -ne [long]$beforeRows[0].length -or
            [string]$_.sha256 -ine [string]$beforeRows[0].sha256 -or
            [string]$_.relativePath -cne [string]$beforeRows[0].relativePath
        }).Count -gt 0) {
        throw "$Description is not present exactly once with a stable, existing input row."
    }
    return $beforeRows[0]
}

function ConvertTo-LedgerCompilerArgumentKey {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $normalized = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $argument = [string]$Arguments[$i]
        if ($argument -in @('--zig-lib-dir', '--cache-dir', '--global-cache-dir')) {
            if ($i + 1 -ge $Arguments.Count) { throw 'The Native compiler command has an incomplete path argument.' }
            $normalized.Add($argument)
            $i++
            $normalized.Add([System.IO.Path]::GetFullPath([string]$Arguments[$i]).ToUpperInvariant())
        }
        elseif ($argument.StartsWith('-femit-bin=', [System.StringComparison]::Ordinal)) {
            $normalized.Add('-femit-bin=' + [System.IO.Path]::GetFullPath($argument.Substring('-femit-bin='.Length)).ToUpperInvariant())
        }
        elseif ($argument.StartsWith('-M', [System.StringComparison]::Ordinal) -and $argument.Contains('=')) {
            $separator = $argument.IndexOf('=')
            $normalized.Add($argument.Substring(0, $separator + 1) + [System.IO.Path]::GetFullPath($argument.Substring($separator + 1)).ToUpperInvariant())
        }
        else { $normalized.Add($argument) }
    }
    return ,$normalized.ToArray()
}

function Assert-LedgerNativeProbeArtifacts {
    param(
        [Parameter(Mandatory)][string]$Probe,
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][object]$StageManifest,
        [Parameter(Mandatory)][object]$Receipt,
        [Parameter(Mandatory)][string[]]$CommandArguments,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )
    if ($Probe -cnotin @('LookupGate', 'PublicationGate', 'PushTargetGate')) {
        throw 'The native-probe-correctness profile accepts only LookupGate, PublicationGate, or PushTargetGate.'
    }
    $definitions = @{
        LookupGate = [pscustomobject]@{
            ValidationKind = 'lookup-to-lease-gate'
            Root = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\lookup-gate-tests.zig'
            TestSource = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\lookup-gate-tests.zig'
            TestHashes = @('78152AEC30FEEF2F1ACD99E6DD0C329E63C2A7E570DCB28A4ED09F744BB6DD21')
            ProbeSourceHashes = @()
            ModuleName = 'ledger_test_modules'
            ModulePath = Join-Path $RepositoryRoot 'sdk\zig\src\local_resource_manager\owned_lookup_test_modules.zig'
            ProbeModuleName = $null
            ProbeModulePath = $null
            PrepReceiptName = $null
        }
        PublicationGate = [pscustomobject]@{
            ValidationKind = 'observed-publication-branch'
            Root = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\publication-gate-tests.zig'
            TestSource = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\publication-gate-tests.zig'
            TestHashes = @('C82C147F676B18C655203564521307F018ACC34E83079E25D8143F58CD2906AF', '30BCD2E5ABD300427DB95ABAC8FB9E693CCFB4FB0CB3103A4CBE7FAFC438EFE2')
            ProbeSourceHashes = @('03BCD8D3EE28C72C0BDDCB47DB708BF287360980A932FBB6D6B38075054E446E', 'B77D737071DF9D9B868C7E38F7A4F8DA46C32C238F327F55400680E1D641F536')
            ModuleName = 'ledger_test_modules'
            ModulePath = Join-Path $StageRoot 'source\local_resource_manager\owned_lookup_test_modules.zig'
            ProbeModuleName = 'publication_probe'
            ProbeModulePath = Join-Path $StageRoot 'source\local_resource_manager\publication_probe.zig'
            PrepReceiptName = 'publication-probe-receipt.json'
        }
        PushTargetGate = [pscustomobject]@{
            ValidationKind = 'observed-owner-push-target-revalidation'
            Root = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\push-target-gate-tests.zig'
            TestSource = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\push-target-gate-tests.zig'
            TestHashes = @('A5F5AC225B80472D1F2F3E95BEF78FD825BCB1A4FA0C353550524294804F8C0E')
            ProbeSourceHashes = @('B81C2089D3C106F6E6D44ED4E82A076BDA20EF393F8A62F61F6E3DC938700533')
            ModuleName = 'ledger_test_modules'
            ModulePath = Join-Path $StageRoot 'source\local_resource_manager\owned_lookup_test_modules.zig'
            ProbeModuleName = 'push_target_probe'
            ProbeModulePath = Join-Path $StageRoot 'source\local_resource_manager\push_target_probe.zig'
            PrepReceiptName = 'push-target-probe-receipt.json'
        }
    }
    $definition = $definitions[$Probe]
    if ($StageManifest.ValidationKind -cne $definition.ValidationKind) {
        throw 'The Native probe does not match the exact validation kind for its profile.'
    }
    $stageRoot = [System.IO.Path]::GetFullPath($StageRoot)
    $sourceRoot = Join-Path $RepositoryRoot 'sdk\zig\src\local_resource_manager'
    $valuesRoot = [System.IO.Path]::GetFullPath([string]$StageManifest.ValuesRoot)
    if ([System.IO.Path]::GetFileName($valuesRoot) -cnotin @('default128-two-table', 'override256-two-table-cross-method-order')) {
        throw 'The Native probe stage is not bound to an approved ledger_values configuration directory.'
    }
    $valuesPath = Join-Path $valuesRoot 'ledger_values.zig'
    $structurePath = Join-Path $sourceRoot 'generated\ledger_structure.zig'
    $zigLibRoot = Join-Path ([System.IO.Path]::GetDirectoryName([string]$Receipt.Command.executable)) 'lib'

    $expectedArguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @('test', '--test-no-exec', '-OReleaseSafe', '-j1', '-fno-llvm', '-fno-lld', '--zig-lib-dir', $zigLibRoot)) {
        $expectedArguments.Add([string]$argument)
    }
    if ($Probe -ceq 'LookupGate') {
        foreach ($argument in @(
            '--dep', 'ledger_values', '--dep', 'ledger_test_modules',
            ('-Mroot=' + $definition.Root), '--dep', 'ledger_values',
            ('-Mledger_test_modules=' + $definition.ModulePath), '--dep', 'ledger_structure',
            ('-Mledger_values=' + $valuesPath), ('-Mledger_structure=' + $structurePath),
            '--cache-dir', (Join-Path $stageRoot 'cache\owned-validation'),
            '--global-cache-dir', (Join-Path $stageRoot 'global-cache'),
            ('-femit-bin=' + (Join-Path $stageRoot 'bin\owned-validation.exe'))
        )) { $expectedArguments.Add([string]$argument) }
    }
    else {
        foreach ($argument in @(
            '--dep', 'ledger_values', '--dep', 'ledger_test_modules', '--dep', [string]$definition.ProbeModuleName,
            ('-Mroot=' + $definition.Root), '--dep', 'ledger_values', '--dep', [string]$definition.ProbeModuleName,
            ('-Mledger_test_modules=' + $definition.ModulePath), '--dep', 'ledger_structure',
            ('-Mledger_values=' + $valuesPath), ('-Mledger_structure=' + $structurePath),
            '--cache-dir', (Join-Path $stageRoot 'cache\owned-validation'),
            '--global-cache-dir', (Join-Path $stageRoot 'global-cache'),
            ('-femit-bin=' + (Join-Path $stageRoot 'bin\owned-validation.exe')),
            ('-M' + [string]$definition.ProbeModuleName + '=' + [string]$definition.ProbeModulePath)
        )) { $expectedArguments.Add([string]$argument) }
    }
    $actualArgumentKey = ConvertTo-LedgerCompilerArgumentKey -Arguments $CommandArguments
    $expectedArgumentKey = ConvertTo-LedgerCompilerArgumentKey -Arguments $expectedArguments.ToArray()
    if (($actualArgumentKey -join "`0") -cne ($expectedArgumentKey -join "`0")) {
        throw 'The Native probe compiler arguments do not match the pinned test root, ordered module graph, cache roots, and output path.'
    }

    $testRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $definition.TestSource -Description 'Native probe test root'
    if ($testRow.sha256 -notin $definition.TestHashes) { throw 'The Native probe test root hash is outside the reviewed test-source set.' }
    if ($Probe -ceq 'LookupGate') {
        if ($null -ne $Evidence.PSObject.Properties['probePreparationReceiptPath'] -or
            $null -ne $Evidence.PSObject.Properties['probePreparationReceiptSha256']) {
            throw 'LookupGate evidence does not use a source-copy preparation receipt.'
        }
        $proposalPath = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\notifications\lookup-gate-proposal.zig'
        $proposalRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $proposalPath -Description 'LookupGate proposal source'
        if ([string]$proposalRow.sha256 -ine '08D21166176D4E82444EB1DAA57DA8C8EA647FBE6660810688543302ACCA25A7') {
            throw 'The LookupGate proposal source hash is not the reviewed pinned source.'
        }
        return
    }

    $prepPath = Join-Path $stageRoot ([string]$definition.PrepReceiptName)
    $pinnedPrepPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.probePreparationReceiptPath) `
        -ExpectedSha256 ([string]$Evidence.probePreparationReceiptSha256) -ValidationRoot $StageRoot `
        -Description 'Native probe preparation receipt'
    $null = Assert-LedgerExactPath -Actual $pinnedPrepPath -Expected $prepPath -Description 'Native probe preparation receipt path'
    $prepItem = Get-Item -LiteralPath $prepPath -ErrorAction Stop
    if (($prepItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The Native probe preparation receipt is a reparse point.' }
    Assert-NoReparseComponents -Path $prepPath -Root $stageRoot
    $prepHash = (Get-FileHash -LiteralPath $prepPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($prepHash -ine [string]$Evidence.probePreparationReceiptSha256) { throw 'The Native probe preparation receipt hash differs from its explicit evidence pin.' }
    $prepRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $prepPath -Description 'Native probe preparation receipt'
    if ([string]$prepRow.sha256 -ine $prepHash) { throw 'The Native probe preparation receipt differs from the stage input snapshot.' }
    $prepReceipt = Get-Content -LiteralPath $prepPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $prepReceipt -PropertyNames @(
        'schemaVersion', 'outputRoot', 'sourceRoot', 'copiedSourceRoot', 'moduleRoot', 'probeModuleRoot',
        'copiedFileCount', 'sourceMap', 'testObserver'
    ) -Description 'Native probe preparation receipt'
    $copiedSourceRoot = Join-Path $stageRoot 'source\local_resource_manager'
    $expectedProbePath = [System.IO.Path]::GetFullPath([string]$definition.ProbeModulePath)
    $null = Assert-LedgerExactPath -Actual ([string]$prepReceipt.outputRoot) -Expected $stageRoot -Description 'Native probe preparation output root'
    $null = Assert-LedgerExactPath -Actual ([string]$prepReceipt.sourceRoot) -Expected $sourceRoot -Description 'Native probe original source root'
    $null = Assert-LedgerExactPath -Actual ([string]$prepReceipt.copiedSourceRoot) -Expected $copiedSourceRoot -Description 'Native probe copied source root'
    $null = Assert-LedgerExactPath -Actual ([string]$prepReceipt.moduleRoot) -Expected $definition.ModulePath -Description 'Native probe copied test module root'
    $null = Assert-LedgerExactPath -Actual ([string]$prepReceipt.probeModuleRoot) -Expected $expectedProbePath -Description 'Native probe copied observer module'

    $observer = $prepReceipt.testObserver
    $mutatedHashes = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ($Probe -ceq 'PublicationGate') {
        if ($null -ne $observer.PSObject.Properties['c2MutationCount']) {
            if ([string]$observer.c2RelativePath -cne 'owned\index.zig' -or
                [string]$observer.c2TargetContext -cne 'Candidate.find odd home version branch before spin/retry' -or
                [int]$observer.c2MutationCount -ne 1 -or
                [string]$observer.b2RelativePath -cne 'owned\index_b2.zig' -or
                [string]$observer.b2TargetContext -cne 'Candidate.pinBucket odd bucket version branch before failed pin' -or
                [int]$observer.b2MutationCount -ne 1) {
                throw 'The PublicationGate preparation receipt does not contain the reviewed C2/B2 odd-version observer mutations.'
            }
        }
        elseif ($null -ne $observer.PSObject.Properties['c2OddVersionMutationCount']) {
            if ([string]$observer.c2OddVersionContext -cne 'Candidate.find odd home version branch before spin/retry' -or
                [int]$observer.c2OddVersionMutationCount -ne 1 -or
                [string]$observer.c2WriterWaitContext -cne 'failed std.atomic.Mutex.tryLock branch in candidate lock helper' -or
                [int]$observer.c2WriterWaitMutationCount -ne 1 -or
                [string]$observer.b2OddVersionContext -cne 'Candidate.pinBucket odd bucket version branch before failed pin' -or
                [int]$observer.b2OddVersionMutationCount -ne 1 -or
                [string]$observer.b2WriterWaitContext -cne 'failed std.atomic.Mutex.tryLock branch in candidate lock helper' -or
                [int]$observer.b2WriterWaitMutationCount -ne 1) {
                throw 'The PublicationGate preparation receipt does not contain the reviewed odd-version and actual writer-wait mutations.'
            }
        }
        else { throw 'The PublicationGate preparation receipt has an unsupported observer schema.' }
        if ([int]$observer.ownerUidGuardCount -ne 2 -or
            [string]$observer.ownerSourceSha256 -ine [string]$observer.ownerCopiedSha256) {
            throw 'The PublicationGate preparation receipt does not preserve both UID guards and the unchanged copied Owner source.'
        }
        $mutatedHashes.Add('owned\index.zig', [string]$observer.c2MutatedSha256)
        $mutatedHashes.Add('owned\index_b2.zig', [string]$observer.b2MutatedSha256)
    }
    else {
        if ([string]$observer.ownerRelativePath -cne 'owned\owner.zig' -or
            [string]$observer.initialMissContext -cne 'Owner.push .not_in_memory arm immediately after the initial use result' -or
            [int]$observer.initialMissMutationCount -ne 1 -or
            [string]$observer.knownContext -cne 'Owner.push immediately after the pre-lock index.find result is assigned' -or
            [int]$observer.knownMutationCount -ne 1 -or
            [string]$observer.workerFilter -cne 'std.Thread.getCurrentId equals the explicitly armed worker Thread.Id' -or
            [int]$observer.sourceRetryCount -ne 1 -or $observer.removeRetryRequested -ne $false -or
            [int]$observer.copiedRetryCount -ne 1 -or
            [string]$observer.copiedRetryContext -cne 'exact if (known != null) continue; immediately after locked UID recheck branch') {
            throw 'The PushTargetGate preparation receipt does not preserve the reviewed positive revalidation mutations and retry.'
        }
        if ([string]$observer.ownerSourceSha256 -ine [string]$observer.ownerCopiedBeforeInjectionSha256) {
            throw 'The PushTargetGate source and pre-injection copied Owner hashes differ.'
        }
        $mutatedHashes.Add('owned\owner.zig', [string]$observer.ownerCopiedAfterInjectionSha256)
    }

    $probeSource = Join-Path $RepositoryRoot ('.local\ledger-owner-implementation\notifications\' + (Split-Path -Leaf $expectedProbePath))
    $probeRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $probeSource -Description 'Native probe observer source'
    $probeCopyRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $expectedProbePath -Description 'Native probe copied observer module'
    if ([string]$observer.probeSourcePath -ine $probeSource -or
        [string]$observer.probeSourceSha256 -ine [string]$probeRow.sha256 -or
        [string]$observer.probeCopiedSha256 -ine [string]$probeCopyRow.sha256 -or
        [string]$probeRow.sha256 -ine [string]$probeCopyRow.sha256 -or
        [string]$probeRow.sha256 -notin $definition.ProbeSourceHashes) {
        throw 'The Native probe observer source, preparation receipt, copied module, and stable input snapshot disagree.'
    }

    $sourceMap = @($prepReceipt.sourceMap)
    $expectedSourceCount = if ($Probe -ceq 'PublicationGate') { 56 } else { 58 }
    if ($sourceMap.Count -ne $expectedSourceCount -or [int]$prepReceipt.copiedFileCount -ne $expectedSourceCount) {
        throw 'The Native probe source-copy receipt has an unexpected number of copied Zig files.'
    }
    $seenRelativePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $mappedRelativePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($sourceEntry in $sourceMap) {
        Assert-LedgerObjectProperties -Object $sourceEntry -PropertyNames @('relativePath', 'sourcePath', 'copiedPath', 'sourceSha256', 'copiedBeforeInjectionSha256') -Description 'Native probe source-map entry'
        $relativePath = ([string]$sourceEntry.relativePath).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [System.IO.Path]::IsPathRooted($relativePath) -or
            @($relativePath.Split('\') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
            -not $seenRelativePaths.Add($relativePath)) {
            throw 'The Native probe source-map has an unsafe or repeated relative path.'
        }
        $sourcePath = Join-Path $sourceRoot $relativePath
        $copiedPath = Join-Path $copiedSourceRoot $relativePath
        $null = Assert-LedgerExactPath -Actual ([string]$sourceEntry.sourcePath) -Expected $sourcePath -Description 'Native probe mapped source path'
        $null = Assert-LedgerExactPath -Actual ([string]$sourceEntry.copiedPath) -Expected $copiedPath -Description 'Native probe mapped copy path'
        if ([string]$sourceEntry.sourceSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            [string]$sourceEntry.copiedBeforeInjectionSha256 -ine [string]$sourceEntry.sourceSha256) {
            throw 'The Native probe source map does not prove a byte-identical initial source copy.'
        }
        $sourceRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $sourcePath -Description 'Native probe source file'
        $copyRow = Get-LedgerStableInputRow -Manifest $StageManifest -Path $copiedPath -Description 'Native probe copied source file'
        if ([string]$sourceRow.sha256 -ine [string]$sourceEntry.sourceSha256) {
            throw 'The Native probe source-map hash differs from the stage input snapshot.'
        }
        $copyRelativePath = $relativePath
        if ($mutatedHashes.ContainsKey($copyRelativePath)) {
            $expectedFinalHash = $mutatedHashes[$copyRelativePath]
            if ($expectedFinalHash -notmatch '^[A-Fa-f0-9]{64}$' -or [string]$copyRow.sha256 -ine $expectedFinalHash) {
                throw 'The Native probe injected source hash differs from its pinned preparation receipt.'
            }
        }
        elseif ([string]$copyRow.sha256 -ine [string]$sourceEntry.copiedBeforeInjectionSha256) {
            throw 'A Native probe source-copy file changed beyond the declared observer injection.'
        }
        $null = $mappedRelativePaths.Add($relativePath)
    }
    foreach ($expectedMutation in $mutatedHashes.Keys) {
        if (-not $mappedRelativePaths.Contains($expectedMutation)) { throw 'The Native probe mutation does not name a file in the copied source map.' }
    }
}

function Assert-LedgerStringSequence {
    param(
        [AllowEmptyCollection()][object[]]$Actual = @(),
        [AllowEmptyCollection()][string[]]$Expected = @(),
        [Parameter(Mandatory)][string]$Description
    )
    if ($Actual.Count -ne $Expected.Count) {
        throw "$Description has $($Actual.Count) entries; expected $($Expected.Count)."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ([string]$Actual[$index] -cne [string]$Expected[$index]) {
            throw "$Description differs at argument $index."
        }
    }
}

function Get-NativeOperationProfileExpectation {
    param([Parameter(Mandatory)][string]$Profile)
    if ($Profile -ceq 'original-maximum') {
        return [pscustomobject]@{
            Tables = 64
            Resources = 524288
            Partitions = 65536
            IndexBuckets = 1048576
            IndexNodes = 589824
            FixedRegistrations = 128
            TableCapacities = @((1..64 | ForEach-Object { 8192 }))
            TargetTable = 0
            CloseRows = 8191
            CloseShells = 1024
        }
    }
    if ($Profile -ceq 'product-327699') {
        return [pscustomobject]@{
            Tables = 6
            Resources = 327699
            Partitions = 5
            IndexBuckets = 1048576
            IndexNodes = 327704
            FixedRegistrations = 11
            TableCapacities = @(20480, 4096, 36864, 1, 18, 266240)
            TargetTable = 5
            CloseRows = 266239
            CloseShells = 1
        }
    }
    throw "Unsupported NativeOperations profile: $Profile"
}

function Assert-NativeOperationResult {
    param(
        [Parameter(Mandatory)][object]$Record,
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][int]$UidBits,
        [Parameter(Mandatory)][string]$Profile,
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string]$OperationMode
    )
    $expectedProfile = Get-NativeOperationProfileExpectation -Profile $Profile
    $instrumented = $OperationMode -ceq 'work'
    $expectedRunMode = if ($instrumented) { 'work' } else { 'correctness' }
    Assert-LedgerObjectProperties -Object $Record -PropertyNames @(
        'schema', 'passed', 'candidate', 'uid_bits', 'profile', 'operation', 'run_mode', 'build_mode',
        'instrumented_build', 'iterations', 'sample_count', 'tables', 'resources', 'partitions',
        'index_buckets', 'index_nodes', 'fixed_registrations', 'table_capacities', 'policy_source',
        'operation_facts', 'samples_elapsed_ns', 'setup_timed', 'affinity', 'work_counters', 'memory'
    ) -Description 'NativeOperations case result'
    if (
        [string]$Record.schema -cne 'resource-manager-owner-operations-v1' -or $Record.passed -ne $true -or
        [string]$Record.candidate -cne $Candidate -or [int]$Record.uid_bits -ne $UidBits -or
        [string]$Record.profile -cne $Profile -or [string]$Record.operation -cne $Operation -or
        [string]$Record.run_mode -cne $expectedRunMode -or [string]$Record.build_mode -cne 'ReleaseFast' -or
        [bool]$Record.instrumented_build -ne $instrumented -or [int]$Record.iterations -ne 1 -or
        [long]$Record.sample_count -ne 0 -or @($Record.samples_elapsed_ns).Count -ne 0 -or
        $Record.setup_timed -ne $false -or [string]$Record.policy_source -cne 'ledger_values.Bound.table_0'
    ) { throw "NativeOperations result identity or zero-timing contract failed for $Profile/$UidBits/$Operation." }
    foreach ($field in @(
        @{ Name = 'tables'; Expected = $expectedProfile.Tables },
        @{ Name = 'resources'; Expected = $expectedProfile.Resources },
        @{ Name = 'partitions'; Expected = $expectedProfile.Partitions },
        @{ Name = 'index_buckets'; Expected = $expectedProfile.IndexBuckets },
        @{ Name = 'index_nodes'; Expected = $expectedProfile.IndexNodes },
        @{ Name = 'fixed_registrations'; Expected = $expectedProfile.FixedRegistrations }
    )) {
        if ([long]$Record.($field.Name) -ne [long]$field.Expected) {
            throw "NativeOperations result field $($field.Name) differs from the $Profile profile."
        }
    }
    if ([string]::Join(',', @($Record.table_capacities)) -cne [string]::Join(',', @($expectedProfile.TableCapacities))) {
        throw "NativeOperations table capacities differ from the $Profile profile."
    }
    if (
        [long]$Record.affinity.requested_mask -ne 1 -or [long]$Record.affinity.after_process_mask -ne 1 -or
        (([long]$Record.affinity.system_mask -band 1) -ne 1) -or
        [int]$Record.affinity.executing_group -ne 0 -or [int]$Record.affinity.executing_processor -ne 0
    ) { throw 'NativeOperations result did not prove the selected processor affinity.' }
    if ($instrumented) {
        if ($null -eq $Record.work_counters) { throw 'InstrumentWork result is missing its work counters.' }
    } elseif ($null -ne $Record.work_counters) {
        throw 'Clean correctness result unexpectedly contains work counters.'
    }

    Assert-LedgerObjectProperties -Object $Record.operation_facts -PropertyNames @(
        'target_table', 'target_rows', 'target_shells', 'index_removals', 'index_puts',
        'fixed_bindings_retained', 'fixed_bindings_cleared', 'notifications'
    ) -Description 'NativeOperations operation facts'
    $facts = [ordered]@{
        target_table = $null
        target_rows = $null
        target_shells = $null
        index_removals = $null
        index_puts = $null
        fixed_bindings_retained = $null
        fixed_bindings_cleared = $null
        notifications = [long]0
    }
    switch ($Operation) {
        'prepare-repeat' { $facts.index_removals = [long]$expectedProfile.IndexNodes }
        'single-remove' {
            $facts.target_table = [long]0
            $facts.index_removals = [long]1
            $facts.notifications = [long]1
        }
        'max-table-close' {
            $facts.target_table = [long]$expectedProfile.TargetTable
            $facts.target_rows = [long]$expectedProfile.CloseRows
            $facts.target_shells = [long]$expectedProfile.CloseShells
            $facts.index_removals = [long]($expectedProfile.CloseRows + $expectedProfile.CloseShells)
            $facts.fixed_bindings_retained = [long]1
            $facts.fixed_bindings_cleared = [long]1
            $facts.notifications = [long]($expectedProfile.CloseRows - 1 + $expectedProfile.CloseShells)
        }
        'full-destroy' {
            $facts.index_removals = [long]$expectedProfile.IndexNodes
            $facts.notifications = [long]($expectedProfile.Resources - $expectedProfile.FixedRegistrations + $expectedProfile.Partitions)
        }
        'small-churn' {
            $facts.index_removals = [long]1
            $facts.index_puts = [long]1
            $facts.notifications = [long]1
        }
        'audit' { }
        default { throw "Unsupported NativeOperations case name: $Operation" }
    }
    foreach ($name in $facts.Keys) {
        if ($null -eq $facts[$name]) {
            if ($null -ne $Record.operation_facts.$name) { throw "NativeOperations fact $name must be null for $Operation." }
        }
        elseif ([long]$Record.operation_facts.$name -ne [long]$facts[$name]) {
            throw "NativeOperations fact $name differs for $Profile/$Operation."
        }
    }
}

function Get-NativeOperationRefusalDefinitions {
    param([Parameter(Mandatory)][string]$Candidate, [Parameter(Mandatory)][string]$OperationMode)
    $base = @('--candidate', $Candidate, '--uid-bits', '128', '--profile', 'original-maximum',
        '--operation', 'audit', '--mode', 'correctness', '--iterations', '1', '--affinity-mask', '1')
    $tail = @($base | Select-Object -Skip 2)
    if ($OperationMode -ceq 'work') {
        return @(
            [pscustomobject]@{ Name = 'refusal-01-multi-bit-affinity'; Error = 'AffinityRequiresOneProcessor'; Arguments = [string[]](@('--candidate', $Candidate) + $tail + @('--affinity-mask', '3')) },
            [pscustomobject]@{ Name = 'refusal-02-zero-affinity'; Error = 'AffinityRequiresOneProcessor'; Arguments = [string[]](@('--candidate', $Candidate) + $tail + @('--affinity-mask', '0')) },
            [pscustomobject]@{ Name = 'refusal-03-both-candidates'; Error = 'CandidateBuildMismatch'; Arguments = [string[]](@('--candidate', 'both') + $tail) },
            [pscustomobject]@{ Name = 'refusal-04-invalid-uid-width'; Error = 'InvalidUidWidth'; Arguments = [string[]](@('--candidate', $Candidate, '--uid-bits', '192') + @($base | Select-Object -Skip 4)) },
            [pscustomobject]@{ Name = 'refusal-05-timing-in-instrumented-build'; Error = 'TimingRequiresCleanBuild'; Arguments = [string[]](@('--candidate', $Candidate) + $tail + @('--mode', 'timing', '--samples', '1')) }
        )
    }
    return @(
        [pscustomobject]@{ Name = 'refusal-01-multi-bit-affinity'; Error = 'AffinityRequiresOneProcessor'; Arguments = [string[]](@('--candidate', $Candidate) + $tail + @('--affinity-mask', '3')) },
        [pscustomobject]@{ Name = 'refusal-02-zero-affinity'; Error = 'AffinityRequiresOneProcessor'; Arguments = [string[]](@('--candidate', $Candidate) + $tail + @('--affinity-mask', '0')) },
        [pscustomobject]@{ Name = 'refusal-03-both-candidates'; Error = 'CandidateBuildMismatch'; Arguments = [string[]](@('--candidate', 'both') + $tail) },
        [pscustomobject]@{ Name = 'refusal-04-work-in-clean-build'; Error = 'WorkModeRequiresInstrumentedBuild'; Arguments = [string[]](@('--candidate', $Candidate) + $tail + @('--mode', 'work')) },
        [pscustomobject]@{ Name = 'refusal-05-timing-with-both-candidates'; Error = 'TimingRequiresSingleCandidateBuild'; Arguments = [string[]](@('--candidate', 'both') + $tail + @('--mode', 'timing', '--samples', '1')) }
    )
}

function Get-IndependentSourceEvidence {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$BatchId,
        [Parameter(Mandatory)][string]$ValidationRoot
    )
    if ($BatchId -cne 'ledger-owner-a1') { throw 'IndependentSource evidence is scoped to ledger-owner-a1.' }
    foreach ($propertyName in @(
        'runName', 'stageReceiptPath', 'stageReceiptSha256',
        'stageSourceInputManifestPath', 'stageSourceInputManifestSha256',
        'artifactManifestPath', 'artifactManifestSha256', 'caseResultsPath', 'caseResultsSha256',
        'commandLogPath', 'commandLogSha256', 'executionJobReceiptPath', 'executionJobReceiptSha256',
        'executionJobInputManifestPath', 'executionJobInputManifestSha256',
        'executionJobManifestPath', 'executionJobManifestSha256',
        'executionJobReceiptsPath', 'executionJobReceiptsSha256', 'controllerLogPath', 'controllerLogSha256'
    )) {
        $property = $Evidence.PSObject.Properties[$propertyName]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "IndependentSource evidence is missing '$propertyName'."
        }
    }
    $runName = [string]$Evidence.runName
    if ($runName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw 'IndependentSource runName is invalid.' }

    $validationRootFull = [System.IO.Path]::GetFullPath($ValidationRoot).TrimEnd('\')
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $batchRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $validationRootFull 'batches') $BatchId)).TrimEnd('\')
    $scratchRoot = Join-Path $batchRoot 'scratch'
    $stageName = 'IndependentSource-' + $runName
    $stageRoot = [System.IO.Path]::GetFullPath((Join-Path $scratchRoot $stageName)).TrimEnd('\')
    $stageReceiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.stageReceiptPath) `
        -ExpectedSha256 ([string]$Evidence.stageReceiptSha256) -ValidationRoot $ValidationRoot -Description 'IndependentSource stage receipt'
    $stageReceiptPath = Assert-LedgerExactPath -Actual $stageReceiptPath -Expected (Join-Path $stageRoot 'receipt.json') -Description 'IndependentSource stage receipt path'
    Assert-NoReparseComponents -Path $stageRoot -Root $ValidationRoot
    $stageReceipt = Get-Content -LiteralPath $stageReceiptPath -Raw | ConvertFrom-Json

    $stageFiles = @{}
    foreach ($spec in @(
        @{ PathProperty = 'stageSourceInputManifestPath'; HashProperty = 'stageSourceInputManifestSha256'; Name = 'source-input-manifest.json' },
        @{ PathProperty = 'artifactManifestPath'; HashProperty = 'artifactManifestSha256'; Name = 'artifact-manifest.json' },
        @{ PathProperty = 'caseResultsPath'; HashProperty = 'caseResultsSha256'; Name = 'case-results.json' },
        @{ PathProperty = 'commandLogPath'; HashProperty = 'commandLogSha256'; Name = 'commands.jsonl' }
    )) {
        $path = Assert-LedgerEvidenceFile -Path ([string]$Evidence.($spec.PathProperty)) `
            -ExpectedSha256 ([string]$Evidence.($spec.HashProperty)) -ValidationRoot $ValidationRoot `
            -Description "IndependentSource $($spec.Name)"
        $stageFiles[$spec.Name] = Assert-LedgerExactPath -Actual $path -Expected (Join-Path $stageRoot $spec.Name) `
            -Description "IndependentSource $($spec.Name) path"
    }

    $outerReceiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.executionJobReceiptPath) `
        -ExpectedSha256 ([string]$Evidence.executionJobReceiptSha256) -ValidationRoot $ValidationRoot -Description 'IndependentSource ExecutionJob receipt'
    $outerReceipt = Get-Content -LiteralPath $outerReceiptPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $outerReceipt -PropertyNames @(
        'passed', 'stage', 'runName', 'batchRoot', 'batchState', 'outputRoot', 'metadataRoot',
        'executionJobExitCode', 'normalDrain', 'emptyJob', 'jobMemoryLimitBytes', 'activeProcessLimit', 'cpuRate',
        'physicalReserveBytes', 'commitReserveBytes', 'peakJobMemoryBytes', 'peakProcessMemoryBytes',
        'inputManifestPath', 'inputManifestSha256', 'stageReceiptPath', 'stageReceiptSha256',
        'executionJobManifestPath', 'executionJobManifestSha256', 'executionJobReceiptsPath',
        'executionJobReceiptsSha256', 'controllerLogPath', 'controllerLogSha256', 'stageArtifacts',
        'changedInputCount', 'failure'
    ) -Description 'IndependentSource outer ExecutionJob receipt'
    $metadataRoot = [System.IO.Path]::GetDirectoryName($outerReceiptPath)
    $metadataName = [System.IO.Path]::GetFileName($metadataRoot)
    if ($metadataRoot -ine [System.IO.Path]::GetFullPath([string]$outerReceipt.metadataRoot) -or
        [System.IO.Path]::GetDirectoryName($metadataRoot) -ine $scratchRoot -or
        $metadataName -cnotmatch ('^\.execution-IndependentSource-' + [regex]::Escape($runName) + '-[A-Fa-f0-9]{32}$')) {
        throw 'IndependentSource ExecutionJob metadata is not under the exact run-name metadata root.'
    }
    Assert-NoReparseComponents -Path $metadataRoot -Root $ValidationRoot
    $outerReceiptPath = Assert-LedgerExactPath -Actual $outerReceiptPath -Expected (Join-Path $metadataRoot 'receipt.json') -Description 'IndependentSource outer receipt path'
    $executionJobManifestPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.executionJobManifestPath) `
        -ExpectedSha256 ([string]$Evidence.executionJobManifestSha256) -ValidationRoot $ValidationRoot -Description 'IndependentSource ExecutionJob manifest'
    $executionJobManifestPath = Assert-LedgerExactPath -Actual $executionJobManifestPath `
        -Expected (Join-Path $metadataRoot 'execution-job.json') -Description 'IndependentSource ExecutionJob manifest path'
    $executionJobInputManifestPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.executionJobInputManifestPath) `
        -ExpectedSha256 ([string]$Evidence.executionJobInputManifestSha256) -ValidationRoot $ValidationRoot -Description 'IndependentSource ExecutionJob input manifest'
    $executionJobInputManifestPath = Assert-LedgerExactPath -Actual $executionJobInputManifestPath `
        -Expected (Join-Path $metadataRoot 'source-input-manifest.json') -Description 'IndependentSource ExecutionJob input manifest path'
    $controllerLogPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.controllerLogPath) `
        -ExpectedSha256 ([string]$Evidence.controllerLogSha256) -ValidationRoot $ValidationRoot -Description 'IndependentSource ExecutionJob controller log'
    $controllerLogPath = Assert-LedgerExactPath -Actual $controllerLogPath `
        -Expected (Join-Path $metadataRoot 'execution-job.controller.log') -Description 'IndependentSource controller log path'

    $jobManifest = Get-Content -LiteralPath $executionJobManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $jobManifest -PropertyNames @(
        'executionHoldPath', 'faultReviewPath', 'headroomPath', 'allowedReceiptRoot', 'runDirectory',
        'executable', 'arguments', 'workingDirectory', 'jobMemoryLimitBytes', 'activeProcessLimit',
        'cpuRate', 'commitReserveBytes', 'physicalReserveBytes', 'timeoutSeconds'
    ) -Description 'IndependentSource ExecutionJob manifest'
    $runDirectory = [System.IO.Path]::GetFullPath([string]$jobManifest.runDirectory).TrimEnd('\')
    $allowedReceiptRoot = [System.IO.Path]::GetFullPath([string]$jobManifest.allowedReceiptRoot).TrimEnd('\')
    $expectedAllowedReceiptRoot = Join-Path $metadataRoot 'job-receipts'
    if ($allowedReceiptRoot -ine $expectedAllowedReceiptRoot -or
        [System.IO.Path]::GetDirectoryName($runDirectory) -ine $allowedReceiptRoot -or
        [System.IO.Path]::GetFileName($runDirectory) -cnotmatch ('^independent-source-' + [regex]::Escape($runName) + '-[A-Fa-f0-9]{32}$')) {
        throw 'IndependentSource ExecutionJob run directory is not uniquely bound below its metadata receipt root.'
    }
    Assert-NoReparseComponents -Path $runDirectory -Root $ValidationRoot
    if (-not (Test-Path -LiteralPath $runDirectory -PathType Container)) { throw 'IndependentSource ExecutionJob run directory is missing.' }
    $executionJobReceiptsPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.executionJobReceiptsPath) `
        -ExpectedSha256 ([string]$Evidence.executionJobReceiptsSha256) -ValidationRoot $ValidationRoot -Description 'IndependentSource ExecutionJob lifecycle events'
    $executionJobReceiptsPath = Assert-LedgerExactPath -Actual $executionJobReceiptsPath `
        -Expected (Join-Path $runDirectory 'receipts.jsonl') -Description 'IndependentSource ExecutionJob lifecycle path'

    $expectedValues128 = Join-Path (Join-Path ([string]$stageReceipt.configurationRoot) 'values') 'default128-two-table'
    $expectedValues256 = Join-Path (Join-Path ([string]$stageReceipt.configurationRoot) 'values') 'override256-two-table-cross-method-order'
    $expectedRunner = Join-Path $repositoryRoot '.local\ledger-owner-implementation\validation\Run-IndependentSourceProbe.ps1'
    $expectedJobArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $expectedRunner,
        '-OutputRoot', $stageRoot, '-Values128Root', $expectedValues128, '-Values256Root', $expectedValues256
    )
    Assert-LedgerStringSequence -Actual @($jobManifest.arguments) -Expected $expectedJobArguments -Description 'IndependentSource ExecutionJob arguments'
    if ([string]$jobManifest.executable -notmatch '[\\/]pwsh\.exe$' -or
        [System.IO.Path]::GetFullPath([string]$jobManifest.workingDirectory) -ine $repositoryRoot -or
        [long]$jobManifest.jobMemoryLimitBytes -ne 402653184 -or [int]$jobManifest.activeProcessLimit -ne 8 -or
        [int]$jobManifest.cpuRate -ne 2500 -or [long]$jobManifest.commitReserveBytes -ne 4294967296 -or
        [long]$jobManifest.physicalReserveBytes -ne 2147483648 -or [int]$jobManifest.timeoutSeconds -ne 900) {
        throw 'IndependentSource ExecutionJob manifest limits or payload differ from the bounded 384 MiB route.'
    }
    Assert-LedgerExecutionLifecycle -JobReceiptPath $executionJobReceiptsPath -JobManifest $jobManifest `
        -RunDirectory $runDirectory -AllowedReceiptRoot $allowedReceiptRoot

    Assert-LedgerObjectProperties -Object $stageReceipt -PropertyNames @(
        'schemaVersion', 'stage', 'passed', 'failure', 'primaryFailure', 'finalizationFailures',
        'outputRoot', 'validationRunName', 'executionRoute', 'configurationRoot', 'configurationSourceManifestSha256',
        'generatedProfileCount', 'invalidInputCount'
    ) -Description 'IndependentSource stage receipt'
    if ([int]$stageReceipt.schemaVersion -ne 1 -or [string]$stageReceipt.stage -cne 'IndependentSourceProbe' -or
        $stageReceipt.passed -ne $true -or [string]$stageReceipt.validationRunName -cne $runName -or
        [string]$stageReceipt.executionRoute -cne 'ledger-owner-bounded' -or
        [System.IO.Path]::GetFullPath([string]$stageReceipt.outputRoot) -ine $stageRoot -or
        [int]$stageReceipt.generatedProfileCount -ne 2 -or [int]$stageReceipt.invalidInputCount -ne 14 -or
        @($stageReceipt.finalizationFailures).Count -ne 0 -or
        ($null -ne $stageReceipt.failure -and -not [string]::IsNullOrWhiteSpace([string]$stageReceipt.failure)) -or
        ($null -ne $stageReceipt.primaryFailure -and -not [string]::IsNullOrWhiteSpace([string]$stageReceipt.primaryFailure))) {
        throw 'IndependentSource stage receipt is not a complete successful two-profile/14-rejection run.'
    }
    if ($outerReceipt.passed -ne $true -or [string]$outerReceipt.stage -cne 'IndependentSourceJob' -or
        [string]$outerReceipt.runName -cne $runName -or [string]$outerReceipt.batchState -cne 'running' -or
        [System.IO.Path]::GetFullPath([string]$outerReceipt.batchRoot) -ine $batchRoot -or
        [System.IO.Path]::GetFullPath([string]$outerReceipt.outputRoot) -ine $stageRoot -or
        [int]$outerReceipt.executionJobExitCode -ne 0 -or $outerReceipt.normalDrain -ne $true -or
        $outerReceipt.emptyJob -ne $true -or [long]$outerReceipt.jobMemoryLimitBytes -ne 402653184 -or
        [int]$outerReceipt.activeProcessLimit -ne 8 -or [int]$outerReceipt.cpuRate -ne 2500 -or
        [long]$outerReceipt.physicalReserveBytes -ne 2147483648 -or [long]$outerReceipt.commitReserveBytes -ne 4294967296 -or
        [long]$outerReceipt.peakJobMemoryBytes -le 0 -or [long]$outerReceipt.peakJobMemoryBytes -gt 402653184 -or
        [int]$outerReceipt.changedInputCount -ne 0 -or
        ($null -ne $outerReceipt.failure -and -not [string]::IsNullOrWhiteSpace([string]$outerReceipt.failure))) {
        throw 'IndependentSource outer receipt does not prove the successful drained bounded job.'
    }
    $null = Assert-LedgerExactPath -Actual ([string]$outerReceipt.stageReceiptPath) -Expected $stageReceiptPath -Description 'IndependentSource outer stage receipt path'
    if ([string]$outerReceipt.stageReceiptSha256 -ine [string]$Evidence.stageReceiptSha256 -or
        [System.IO.Path]::GetFullPath([string]$outerReceipt.inputManifestPath) -ine $executionJobInputManifestPath -or
        [string]$outerReceipt.inputManifestSha256 -ine [string]$Evidence.executionJobInputManifestSha256 -or
        [System.IO.Path]::GetFullPath([string]$outerReceipt.executionJobManifestPath) -ine $executionJobManifestPath -or
        [string]$outerReceipt.executionJobManifestSha256 -ine [string]$Evidence.executionJobManifestSha256 -or
        [System.IO.Path]::GetFullPath([string]$outerReceipt.executionJobReceiptsPath) -ine $executionJobReceiptsPath -or
        [string]$outerReceipt.executionJobReceiptsSha256 -ine [string]$Evidence.executionJobReceiptsSha256 -or
        [System.IO.Path]::GetFullPath([string]$outerReceipt.controllerLogPath) -ine $controllerLogPath -or
        [string]$outerReceipt.controllerLogSha256 -ine [string]$Evidence.controllerLogSha256) {
        throw 'IndependentSource outer receipt does not bind its hash-pinned ExecutionJob evidence.'
    }

    $inputRoots = @(
        $repositoryRoot, $validationRootFull,
        'D:\Code\CommonAssets\Tools\Zig\zig-0.16.0',
        'D:\Code\FreedomPoint\artifacts\sdk',
        'C:\Program Files\dotnet',
        [System.IO.Path]::GetDirectoryName([string]$jobManifest.executable)
    ) | ForEach-Object { [System.IO.Path]::GetFullPath([string]$_).TrimEnd('\') } | Sort-Object Length -Descending
    $verifyCurrentInputs = {
        param([object[]]$Rows, [string]$Description)
        foreach ($row in $Rows) {
            Assert-LedgerObjectProperties -Object $row -PropertyNames @('path', 'length', 'sha256') -Description "$Description row"
            $path = [System.IO.Path]::GetFullPath([string]$row.path)
            if ([long]$row.length -lt 0 -or [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw "$Description has an invalid pinned path length or SHA-256."
            }
            $matchedRoot = $null
            foreach ($candidateRoot in $inputRoots) {
                if ($path.StartsWith($candidateRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) { $matchedRoot = $candidateRoot; break }
            }
            if ($null -eq $matchedRoot) { throw "$Description path is outside the expected repository, tool, package, or validation roots: $path" }
            Assert-NoReparseComponents -Path $path -Root $matchedRoot
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Description input is missing: $path" }
            $item = Get-Item -LiteralPath $path
            if ([long]$item.Length -ne [long]$row.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine [string]$row.sha256) {
                throw "$Description input no longer matches its captured length and SHA-256: $path"
            }
        }
    }

    $stageSourceManifestPath = $stageFiles['source-input-manifest.json']
    $stageSourceManifest = Get-Content -LiteralPath $stageSourceManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $stageSourceManifest -PropertyNames @(
        'schemaVersion', 'stage', 'configurationRoot', 'inputsBefore', 'inputsAfter', 'stable'
    ) -Description 'IndependentSource stage source-input manifest'
    $assertIndependentSnapshotStable = {
        param([Parameter(Mandatory)][object]$Manifest, [Parameter(Mandatory)][string]$Description)
        if ($Manifest.stable -ne $true) { throw "$Description does not report stable inputs." }
        $beforeRows = @($Manifest.inputsBefore)
        $afterRows = @($Manifest.inputsAfter)
        if ($beforeRows.Count -eq 0 -or $beforeRows.Count -ne $afterRows.Count) {
            throw "$Description has incomplete before/after input snapshots."
        }
        $signature = {
            param([object[]]$Rows, [string]$Side)
            $map = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($row in $Rows) {
                Assert-LedgerObjectProperties -Object $row -PropertyNames @('path', 'length', 'sha256') -Description "$Description $Side row"
                $path = [System.IO.Path]::GetFullPath([string]$row.path)
                if (-not [System.IO.Path]::IsPathRooted([string]$row.path) -or [long]$row.length -lt 0 -or
                    [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $map.ContainsKey($path)) {
                    throw "$Description has an invalid or duplicate $Side input row."
                }
                $map.Add($path, ([string]$row.length + "`0" + ([string]$row.sha256).ToLowerInvariant()))
            }
            return ,$map
        }
        $beforeMap = & $signature $beforeRows 'before'
        $afterMap = & $signature $afterRows 'after'
        if ($beforeMap.Count -ne $afterMap.Count) { throw "$Description input row counts changed." }
        foreach ($path in $beforeMap.Keys) {
            if (-not $afterMap.ContainsKey($path) -or $beforeMap[$path] -cne $afterMap[$path]) {
                throw "$Description before/after input rows differ: $path"
            }
        }
    }
    & $assertIndependentSnapshotStable $stageSourceManifest 'IndependentSource stage source-input manifest'
    if ([int]$stageSourceManifest.schemaVersion -ne 1 -or [string]$stageSourceManifest.stage -cne 'IndependentSourceProbe' -or
        $stageSourceManifest.stable -ne $true -or @($stageSourceManifest.inputsBefore).Count -ne 38 -or
        [System.IO.Path]::GetFullPath([string]$stageSourceManifest.configurationRoot) -ine [System.IO.Path]::GetFullPath([string]$stageReceipt.configurationRoot) -or
        [System.IO.Path]::GetFullPath([string]$stageReceipt.configurationRoot).StartsWith($scratchRoot + '\', [System.StringComparison]::OrdinalIgnoreCase) -ne $true) {
        throw 'IndependentSource stage source inputs are not a stable 38-file snapshot under the same batch.'
    }
    & $verifyCurrentInputs @($stageSourceManifest.inputsBefore) 'IndependentSource stage source'
    $configurationManifest = Join-Path ([string]$stageReceipt.configurationRoot) 'sourcehash-manifest.json'
    $configurationRows = @($stageSourceManifest.inputsBefore | Where-Object { [System.IO.Path]::GetFullPath([string]$_.path) -ieq [System.IO.Path]::GetFullPath($configurationManifest) })
    if ($configurationRows.Count -ne 1 -or [string]$configurationRows[0].sha256 -ine [string]$stageReceipt.configurationSourceManifestSha256) {
        throw 'IndependentSource receipt does not bind the captured configuration source manifest.'
    }

    $outerInputManifestPath = $executionJobInputManifestPath
    $outerInputManifest = Get-Content -LiteralPath $outerInputManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $outerInputManifest -PropertyNames @(
        'schemaVersion', 'stage', 'runName', 'batchRoot', 'stageRoot', 'inputsBefore', 'inputsAfter', 'changedInputs', 'stable'
    ) -Description 'IndependentSource ExecutionJob input manifest'
    Assert-LedgerObjectProperties -Object $outerInputManifest -PropertyNames @('changedInputs') -Description 'IndependentSource ExecutionJob changed inputs'
    if (@($outerInputManifest.changedInputs).Count -ne 0) { throw 'IndependentSource ExecutionJob manifest reports changed inputs.' }
    & $assertIndependentSnapshotStable $outerInputManifest 'IndependentSource ExecutionJob input manifest'
    if ([int]$outerInputManifest.schemaVersion -ne 1 -or [string]$outerInputManifest.stage -cne 'IndependentSourceJob' -or
        [string]$outerInputManifest.runName -cne $runName -or $outerInputManifest.stable -ne $true -or
        [System.IO.Path]::GetFullPath([string]$outerInputManifest.batchRoot) -ine $batchRoot -or
        [System.IO.Path]::GetFullPath([string]$outerInputManifest.stageRoot) -ine $stageRoot -or
        @($outerInputManifest.inputsBefore).Count -ne 45) {
        throw 'IndependentSource ExecutionJob inputs do not match its stable 45-file batch snapshot.'
    }
    & $verifyCurrentInputs @($outerInputManifest.inputsBefore) 'IndependentSource ExecutionJob'

    $artifactPath = $stageFiles['artifact-manifest.json']
    $artifact = Get-Content -LiteralPath $artifactPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $artifact -PropertyNames @('schemaVersion', 'stage', 'complete', 'artifacts', 'errors', 'commandsPath', 'commandsSha256') -Description 'IndependentSource artifact manifest'
    if ([int]$artifact.schemaVersion -ne 1 -or [string]$artifact.stage -cne 'IndependentSourceProbe' -or
        $artifact.complete -ne $true -or @($artifact.errors).Count -ne 0 -or @($artifact.artifacts).Count -ne 108) {
        throw 'IndependentSource artifact manifest is not the complete 108-entry successful output set.'
    }
    $null = Assert-LedgerExactPath -Actual ([string]$artifact.commandsPath) -Expected $stageFiles['commands.jsonl'] -Description 'IndependentSource artifact command log path'
    if ([string]$artifact.commandsSha256 -ine [string]$Evidence.commandLogSha256) { throw 'IndependentSource artifact manifest command-log SHA-256 differs from its evidence pin.' }
    $artifactRows = @{}
    foreach ($row in @($artifact.artifacts)) {
        Assert-LedgerObjectProperties -Object $row -PropertyNames @('path', 'kind', 'length', 'sha256') -Description 'IndependentSource artifact row'
        $artifactFile = [System.IO.Path]::GetFullPath([string]$row.path)
        $stagePrefix = $stageRoot + '\'
        if (-not $artifactFile.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            [long]$row.length -lt 0 -or [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw 'IndependentSource artifact path or hash row is invalid or outside its output stage.'
        }
        if ($artifactRows.ContainsKey($artifactFile)) { throw "IndependentSource artifact manifest repeats a path: $artifactFile" }
        Assert-NoReparseComponents -Path $artifactFile -Root $stageRoot
        if (-not (Test-Path -LiteralPath $artifactFile -PathType Leaf)) { throw "IndependentSource preserved artifact is missing: $artifactFile" }
        $artifactItem = Get-Item -LiteralPath $artifactFile
        if ([long]$artifactItem.Length -ne [long]$row.length -or
            (Get-FileHash -LiteralPath $artifactFile -Algorithm SHA256).Hash -ine [string]$row.sha256) {
            throw "IndependentSource preserved artifact differs from its manifest: $artifactFile"
        }
        $artifactRows[$artifactFile] = $row
    }
    $artifactKinds = @{}; foreach ($group in @($artifact.artifacts | Group-Object kind)) { $artifactKinds[[string]$group.Name] = [int]$group.Count }
    if ([int]$artifactKinds['zig-sdk-source-copy'] -ne 14 -or [int]$artifactKinds['zig-consumer-executable'] -ne 2 -or
        [int]$artifactKinds['command-log'] -ne 60 -or [int]$artifactKinds['negative-generator-input'] -ne 12) {
        throw 'IndependentSource artifact kind counts do not match the two-profile fixture.'
    }

    $caseResultsPath = $stageFiles['case-results.json']
    $caseResults = Get-Content -LiteralPath $caseResultsPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $caseResults -PropertyNames @('schemaVersion', 'profiles', 'invalidInputs', 'expectedInvalidCaseCount') -Description 'IndependentSource case results'
    if ([int]$caseResults.schemaVersion -ne 1 -or @($caseResults.profiles).Count -ne 2 -or
        @($caseResults.invalidInputs).Count -ne 14 -or [int]$caseResults.expectedInvalidCaseCount -ne 14) {
        throw 'IndependentSource case result counts do not match two profiles and fourteen rejections.'
    }
    $profileShapes = @(
        [pscustomobject]@{ Name = 'uid128-default'; Bits = 128; ValuesPath = $expectedValues128; Label = '128' },
        [pscustomobject]@{ Name = 'uid256-override'; Bits = 256; ValuesPath = $expectedValues256; Label = '256' }
    )
    $cacheRoots = @{}; $globalCacheRoots = @{}
    foreach ($shape in $profileShapes) {
        $profiles = @($caseResults.profiles | Where-Object { [string]$_.profile -ceq $shape.Name })
        if ($profiles.Count -ne 1) { throw "IndependentSource case results must include exactly one $($shape.Name) profile." }
        $profile = $profiles[0]
        if ([int]$profile.uidWidthBits -ne $shape.Bits -or
            [System.IO.Path]::GetFullPath([string]$profile.valuesRoot) -ine [System.IO.Path]::GetFullPath($shape.ValuesPath) -or
            [System.IO.Path]::GetFullPath([string]$profile.sdkSourceRoot) -ine (Join-Path (Join-Path $stageRoot ('profiles\' + $shape.Label)) 'sdk-source') -or
            $profile.csharpRunPassed -ne $true -or $profile.zigRunPassed -ne $true -or
            [string]$profile.zigExecutableSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            [string]$profile.csharpRunPassed -cne 'True') {
            throw "IndependentSource profile result did not pass and bind its UID/configuration data: $($shape.Name)"
        }
        $zigExe = Join-Path (Join-Path $stageRoot ('profiles\' + $shape.Label)) ('independent-ledger-probe-' + $shape.Label + '.exe')
        $csharpAssembly = Join-Path (Join-Path (Join-Path $stageRoot ('bin\IndependentLedgerProbe' + $shape.Label)) 'Release\net10.0') ('IndependentLedgerProbe' + $shape.Label + '.dll')
        $null = Assert-LedgerExactPath -Actual ([string]$profile.zigExecutable) -Expected $zigExe -Description "IndependentSource $($shape.Label) Zig executable"
        $null = Assert-LedgerExactPath -Actual ([string]$profile.consumerAssembly) -Expected $csharpAssembly -Description "IndependentSource $($shape.Label) C# assembly"
        foreach ($spec in @(
            @{ Path = $zigExe; Hash = [string]$profile.zigExecutableSha256; Name = 'Zig executable' },
            @{ Path = $csharpAssembly; Hash = [string]$profile.consumerAssemblySha256; Name = 'C# consumer assembly' },
            @{ Path = [string]$profile.generatedZig; Hash = [string]$profile.generatedZigSha256; Name = 'generated Zig shape' },
            @{ Path = [string]$profile.generatedCSharp; Hash = [string]$profile.generatedCSharpSha256; Name = 'generated C# shape' }
        )) {
            $fullArtifactPath = [System.IO.Path]::GetFullPath($spec.Path)
            if (-not $artifactRows.ContainsKey($fullArtifactPath) -or [string]$artifactRows[$fullArtifactPath].sha256 -ine $spec.Hash) {
                throw "IndependentSource $($shape.Label) $($spec.Name) is not hash-bound by the artifact manifest."
            }
        }
        $cacheRoot = Join-Path (Join-Path $stageRoot 'compiler-cache') $shape.Label
        $globalCacheRoot = Join-Path (Join-Path $stageRoot 'global-cache') $shape.Label
        $cacheRoots[$shape.Label] = $cacheRoot
        $globalCacheRoots[$shape.Label] = $globalCacheRoot
    }
    $expectedInvalidNames = @('unknown-field', 'duplicate-field', 'missing-field', 'negative-unsigned', 'invalid-uid-length', 'invalid-enum', 'unsupported-cli-uid-width')
    $invalidIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($invalid in @($caseResults.invalidInputs)) {
        Assert-LedgerObjectProperties -Object $invalid -PropertyNames @('profile', 'case', 'expectedExit', 'actualExit', 'outputCreated', 'passed') -Description 'IndependentSource invalid input result'
        $expectedProfile = @($profileShapes | Where-Object { [string]$_.Name -ceq [string]$invalid.profile })
        if ($expectedProfile.Count -ne 1 -or [string]$invalid.case -cnotin $expectedInvalidNames -or
            [int]$invalid.expectedExit -ne 2 -or [int]$invalid.actualExit -ne 2 -or
            $invalid.outputCreated -ne $false -or $invalid.passed -ne $true -or
            -not $invalidIds.Add(([string]$invalid.profile + ':' + [string]$invalid.case))) {
            throw 'IndependentSource includes an invalid or duplicate negative-input result.'
        }
    }
    foreach ($shape in $profileShapes) { foreach ($caseName in $expectedInvalidNames) {
        if (-not $invalidIds.Contains(([string]$shape.Name + ':' + $caseName))) { throw 'IndependentSource negative-input results omit a required profile/case pair.' }
    } }

    $commandLogPath = $stageFiles['commands.jsonl']
    $commands = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $commandLogPath) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $commands.Add(($line | ConvertFrom-Json)) }
    }
    if ($commands.Count -ne 30) { throw 'IndependentSource commands.jsonl must contain exactly thirty logged processes.' }
    $commandNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $zigPath = 'D:\Code\CommonAssets\Tools\Zig\zig-0.16.0\zig.exe'
    $zigLib = Join-Path (Split-Path $zigPath -Parent) 'lib'
    $rootModule = Join-Path $repositoryRoot '.local\ledger-owner-implementation\independent-source-probe\ProbeMain.zig'
    $structureModule = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'
    foreach ($command in $commands) {
        Assert-LedgerObjectProperties -Object $command -PropertyNames @('name', 'executable', 'arguments', 'exitCode', 'stdoutPath', 'stdoutSha256', 'stderrPath', 'stderrSha256') -Description 'IndependentSource command record'
        $name = [string]$command.name
        if ([string]::IsNullOrWhiteSpace($name) -or -not $commandNames.Add($name)) { throw 'IndependentSource command log has a missing or duplicate process name.' }
        $expectedExit = if ($name.StartsWith('reject-', [System.StringComparison]::Ordinal)) { 2 } else { 0 }
        if ([int]$command.exitCode -ne $expectedExit) { throw "IndependentSource command failed or has an unexpected exit code: $name" }
        $expectedStdout = Join-Path (Join-Path $stageRoot 'logs') ($name + '.stdout.txt')
        $expectedStderr = Join-Path (Join-Path $stageRoot 'logs') ($name + '.stderr.txt')
        $null = Assert-LedgerExactPath -Actual ([string]$command.stdoutPath) -Expected $expectedStdout -Description 'IndependentSource command stdout path'
        $null = Assert-LedgerExactPath -Actual ([string]$command.stderrPath) -Expected $expectedStderr -Description 'IndependentSource command stderr path'
        foreach ($stream in @(@($expectedStdout, [string]$command.stdoutSha256), @($expectedStderr, [string]$command.stderrSha256))) {
            $streamPath = [System.IO.Path]::GetFullPath([string]$stream[0])
            if ([string]$stream[1] -notmatch '^[A-Fa-f0-9]{64}$' -or -not $artifactRows.ContainsKey($streamPath) -or
                [string]$artifactRows[$streamPath].sha256 -ine [string]$stream[1]) {
                throw "IndependentSource command output is not hash-bound by the artifact manifest: $name"
            }
        }
    }
    $expectedCommandNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($name in @('dotnet-version', 'zig-version', 'restore-generator', 'build-generator')) { $null = $expectedCommandNames.Add($name) }
    foreach ($shape in $profileShapes) {
        $bits = [string]$shape.Label
        foreach ($commandPrefix in @('generate-shape', 'restore-consumer', 'build-consumer', 'run-consumer', 'compile-zig-consumer', 'run-zig-consumer')) {
            $null = $expectedCommandNames.Add($commandPrefix + '-' + $bits)
        }
        foreach ($caseName in @('unknown-field', 'duplicate-field', 'missing-field', 'negative-unsigned', 'invalid-uid-length', 'invalid-enum', 'unsupported-width')) {
            $null = $expectedCommandNames.Add('reject-' + $bits + '-' + $caseName)
        }
    }
    if ($commandNames.Count -ne $expectedCommandNames.Count) { throw 'IndependentSource command-name set differs from the complete two-profile process list.' }
    foreach ($name in $expectedCommandNames) {
        if (-not $commandNames.Contains($name)) { throw "IndependentSource command log omits required process '$name'." }
    }
    foreach ($shape in $profileShapes) {
        $bits = [string]$shape.Label
        $compileName = 'compile-zig-consumer-' + $bits
        $compile = @($commands | Where-Object { [string]$_.name -ceq $compileName })
        if ($compile.Count -ne 1 -or [string]$compile[0].executable -ine $zigPath) { throw "IndependentSource is missing its $bits Zig compile process." }
        $sdkSource = Join-Path (Join-Path $stageRoot ('profiles\' + $bits)) 'sdk-source'
        $generatedRoot = Join-Path (Join-Path $stageRoot ('profiles\' + $bits)) 'generated-shape'
        $shim = Join-Path $sdkSource 'ledger_sdk_shim.zig'
        $valuesPath = Join-Path $shape.ValuesPath 'ledger_values.zig'
        $zigExecutable = Join-Path (Join-Path $stageRoot ('profiles\' + $bits)) ('independent-ledger-probe-' + $bits + '.exe')
        $expectedCompileArguments = @(
            'build-exe', '-j1', '-fno-llvm', '-fno-lld', '-OReleaseSafe', '--zig-lib-dir', $zigLib,
            '--dep', 'generated_shape', '--dep', 'probe_expected', ('-Mroot=' + $rootModule),
            '--dep', 'ledger_values', ('-Mledger_sdk=' + $shim), '--dep', 'ledger_structure',
            ('-Mledger_values=' + $valuesPath), ('-Mledger_structure=' + $structureModule),
            '--dep', 'ledger_sdk', '--dep', 'ledger_values',
            ('-Mgenerated_shape=' + (Join-Path $generatedRoot 'LedgerOwnerShape.zig')),
            ('-Mprobe_expected=' + (Join-Path (Join-Path $stageRoot ('profiles\' + $bits)) 'probe_expected.zig')),
            '--cache-dir', $cacheRoots[$bits], '--global-cache-dir', $globalCacheRoots[$bits],
            ('-femit-bin=' + $zigExecutable)
        )
        Assert-LedgerStringSequence -Actual @($compile[0].arguments) -Expected $expectedCompileArguments -Description "IndependentSource $bits Zig compile arguments"
        $runtimeCommandName = 'run-zig-consumer-' + $bits
        $run = @($commands | Where-Object { [string]$_.name -ceq $runtimeCommandName })
        if ($run.Count -ne 1) { throw "IndependentSource is missing its $bits Zig runtime process." }
        $null = Assert-LedgerExactPath -Actual ([string]$run[0].executable) -Expected $zigExecutable -Description "IndependentSource $bits Zig runtime executable"
        $runtimeHashProperty = $run[0].PSObject.Properties['executableSha256']
        if ($null -ne $runtimeHashProperty -and [string]$runtimeHashProperty.Value -and
            [string]$runtimeHashProperty.Value -ine [string]$artifactRows[[System.IO.Path]::GetFullPath($zigExecutable)].sha256) {
            throw "IndependentSource $bits runtime executable hash differs from its compiled artifact."
        }
    }

    $expectedOuterArtifacts = @('receipt.json', 'source-input-manifest.json', 'case-results.json', 'artifact-manifest.json', 'commands.jsonl')
    $outerArtifacts = @($outerReceipt.stageArtifacts)
    if ($outerArtifacts.Count -ne $expectedOuterArtifacts.Count) { throw 'IndependentSource outer receipt does not enumerate exactly five stage artifacts.' }
    foreach ($name in $expectedOuterArtifacts) {
        $rows = @($outerArtifacts | Where-Object { [string]$_.name -ceq $name })
        if ($rows.Count -ne 1) { throw "IndependentSource outer receipt is missing the $name stage-artifact pin." }
        $path = Join-Path $stageRoot $name
        $sha = switch ($name) {
            'receipt.json' { [string]$Evidence.stageReceiptSha256 }
            'source-input-manifest.json' { [string]$Evidence.stageSourceInputManifestSha256 }
            'case-results.json' { [string]$Evidence.caseResultsSha256 }
            'artifact-manifest.json' { [string]$Evidence.artifactManifestSha256 }
            'commands.jsonl' { [string]$Evidence.commandLogSha256 }
        }
        $null = Assert-LedgerExactPath -Actual ([string]$rows[0].path) -Expected $path -Description 'IndependentSource outer artifact path'
        if ([long]$rows[0].length -ne [long](Get-Item -LiteralPath $path).Length -or [string]$rows[0].sha256 -ine $sha) {
            throw "IndependentSource outer receipt stage-artifact hash or length differs: $name"
        }
    }

    [pscustomobject]@{
        Kind = 'ledger-checks'
        ProofProfile = 'independent-source-correctness'
        RunName = $runName
        StageName = $stageName
        StageRoot = $stageRoot
        CacheOwnerStageName = $stageName
        ReceiptPath = $stageReceiptPath
        ReceiptSha256 = [string]$Evidence.stageReceiptSha256
        BoundedExecutionReceiptPath = $outerReceiptPath
        BoundedExecutionReceiptSha256 = [string]$Evidence.executionJobReceiptSha256
        StageManifestPath = $stageFiles['source-input-manifest.json']
        StageManifestSha256 = [string]$Evidence.stageSourceInputManifestSha256
        ExecutionJobInputManifestPath = $executionJobInputManifestPath
        ExecutionJobInputManifestSha256 = [string]$Evidence.executionJobInputManifestSha256
        ExecutionJobManifestPath = $executionJobManifestPath
        ExecutionJobManifestSha256 = [string]$Evidence.executionJobManifestSha256
        JobReceiptPath = $executionJobReceiptsPath
        JobReceiptSha256 = [string]$Evidence.executionJobReceiptsSha256
        CacheRoots = $cacheRoots
        GlobalCacheRoots = $globalCacheRoots
        InputCount = [int](@($stageSourceManifest.inputsBefore).Count + @($outerInputManifest.inputsBefore).Count)
    }
}

function Get-NativeOperationsEvidence {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$BatchId,
        [Parameter(Mandatory)][string]$ValidationRoot
    )
    if ($BatchId -cne 'ledger-owner-a1') { throw 'native-operations-correctness is scoped to ledger-owner-a1.' }
    Assert-LedgerObjectProperties -Object $Evidence -PropertyNames @(
        'runName', 'operationMode', 'receiptPath', 'receiptSha256',
        'boundedExecutionReceiptPath', 'boundedExecutionReceiptSha256',
        'inputManifestBeforePath', 'inputManifestBeforeSha256',
        'inputManifestAfterPath', 'inputManifestAfterSha256',
        'sourcehashManifestPath', 'sourcehashManifestSha256',
        'artifactManifestPath', 'artifactManifestSha256',
        'commandLogPath', 'commandLogSha256', 'caseResultsPath', 'caseResultsSha256',
        'executionJobInputManifestPath', 'executionJobInputManifestSha256',
        'executionJobManifestPath', 'executionJobManifestSha256', 'jobReceiptPath', 'jobReceiptSha256'
    ) -Description 'NativeOperations prune evidence'
    $runName = [string]$Evidence.runName
    $operationMode = [string]$Evidence.operationMode
    if ($runName -cnotmatch '^operation-(c2|b2)-(clean|work)-[A-Za-z0-9][A-Za-z0-9._-]{0,47}$' -or
        $operationMode -cnotin @('clean', 'work')) {
        throw 'NativeOperations evidence requires an explicit operation run name and operationMode clean or work.'
    }
    $candidateFromName = [regex]::Match($runName, '^operation-(c2|b2)-').Groups[1].Value
    $modeFromName = [regex]::Match($runName, '^operation-(c2|b2)-(clean|work)-').Groups[2].Value
    if ($modeFromName -cne $operationMode) { throw 'NativeOperations operationMode does not match its explicit runName.' }

    $receiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.receiptPath) `
        -ExpectedSha256 ([string]$Evidence.receiptSha256) -ValidationRoot $ValidationRoot -Description 'NativeOperations receipt'
    $stageRoot = [System.IO.Path]::GetDirectoryName($receiptPath)
    $stageName = [System.IO.Path]::GetFileName($stageRoot)
    $expectedStageName = 'NativeOperations-' + $runName
    if ($stageName -cne $expectedStageName) { throw 'NativeOperations receipt is not under its exact run-name stage directory.' }
    $batchRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $ValidationRoot 'batches') $BatchId)).TrimEnd('\')
    $scratchRoot = Join-Path $batchRoot 'scratch'
    $stageRoot = Assert-LedgerExactPath -Actual $stageRoot -Expected (Join-Path $scratchRoot $stageName) -Description 'NativeOperations stage root'
    Assert-NoReparseComponents -Path $stageRoot -Root $ValidationRoot
    $stageItem = Get-Item -LiteralPath $stageRoot
    if (-not $stageItem.PSIsContainer -or (($stageItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The NativeOperations stage root is missing or is a reparse point.'
    }

    $boundPaths = @{}
    foreach ($spec in @(
        @{ Property = 'boundedExecutionReceiptPath'; Hash = 'boundedExecutionReceiptSha256'; Expected = (Join-Path $stageRoot 'bounded-execution-receipt.json'); Description = 'NativeOperations bounded ExecutionJob receipt' },
        @{ Property = 'inputManifestBeforePath'; Hash = 'inputManifestBeforeSha256'; Expected = (Join-Path $stageRoot 'input-manifest-before.json'); Description = 'NativeOperations before-input manifest' },
        @{ Property = 'inputManifestAfterPath'; Hash = 'inputManifestAfterSha256'; Expected = (Join-Path $stageRoot 'input-manifest-after.json'); Description = 'NativeOperations after-input manifest' },
        @{ Property = 'sourcehashManifestPath'; Hash = 'sourcehashManifestSha256'; Expected = (Join-Path $stageRoot 'sourcehash-manifest.json'); Description = 'NativeOperations sourcehash manifest' },
        @{ Property = 'artifactManifestPath'; Hash = 'artifactManifestSha256'; Expected = (Join-Path $stageRoot 'artifact-manifest.json'); Description = 'NativeOperations artifact manifest' },
        @{ Property = 'commandLogPath'; Hash = 'commandLogSha256'; Expected = (Join-Path $stageRoot 'commands.jsonl'); Description = 'NativeOperations command log' },
        @{ Property = 'caseResultsPath'; Hash = 'caseResultsSha256'; Expected = (Join-Path $stageRoot 'case-results.json'); Description = 'NativeOperations case results' }
    )) {
        $fullPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.($spec.Property)) `
            -ExpectedSha256 ([string]$Evidence.($spec.Hash)) -ValidationRoot $ValidationRoot -Description $spec.Description
        $boundPaths[$spec.Property] = Assert-LedgerExactPath -Actual $fullPath -Expected $spec.Expected -Description $spec.Description
    }
    foreach ($spec in @(
        @{ Actual = [string]$Evidence.executionJobManifestPath; Hash = [string]$Evidence.executionJobManifestSha256; Name = 'ExecutionJob manifest' },
        @{ Actual = [string]$Evidence.executionJobInputManifestPath; Hash = [string]$Evidence.executionJobInputManifestSha256; Name = 'ExecutionJob input manifest' },
        @{ Actual = [string]$Evidence.jobReceiptPath; Hash = [string]$Evidence.jobReceiptSha256; Name = 'ExecutionJob event receipt' }
    )) {
        $boundPaths[$spec.Name] = Assert-LedgerEvidenceFile -Path $spec.Actual -ExpectedSha256 $spec.Hash `
            -ValidationRoot $ValidationRoot -Description $spec.Name
    }
    $expectedReceiptPath = Assert-LedgerExactPath -Actual $receiptPath -Expected (Join-Path $stageRoot 'receipt.json') -Description 'NativeOperations receipt path'
    $boundedPath = $boundPaths.boundedExecutionReceiptPath
    $beforePath = $boundPaths.inputManifestBeforePath
    $afterPath = $boundPaths.inputManifestAfterPath
    $sourcehashPath = $boundPaths.sourcehashManifestPath
    $artifactPath = $boundPaths.artifactManifestPath
    $commandLogPath = $boundPaths.commandLogPath
    $caseResultsPath = $boundPaths.caseResultsPath

    $receipt = Get-Content -LiteralPath $expectedReceiptPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $receipt -PropertyNames @(
        'passed', 'stage', 'candidate', 'instrumented', 'mode', 'valuesRoot', 'outputRoot',
        'compileExitCode', 'plannedCaseCount', 'completedCaseCount', 'passedCaseCount',
        'plannedRefusalCount', 'completedRefusalCount', 'passedRefusalCount', 'sourcehashManifest',
        'inputManifestBefore', 'inputManifestAfter', 'artifactManifest', 'commands', 'failures'
    ) -Description 'NativeOperations stage receipt'
    $expectedInstrumented = $operationMode -ceq 'work'
    $expectedStageMode = if ($expectedInstrumented) { 'work' } else { 'correctness' }
    if (
        $receipt.passed -ne $true -or [string]$receipt.stage -cne 'NativeOperations' -or
        [string]$receipt.candidate -cne $candidateFromName -or [bool]$receipt.instrumented -ne $expectedInstrumented -or
        [string]$receipt.mode -cne $expectedStageMode -or [int]$receipt.compileExitCode -ne 0 -or
        [int]$receipt.plannedCaseCount -ne 24 -or [int]$receipt.completedCaseCount -ne 24 -or [int]$receipt.passedCaseCount -ne 24 -or
        [int]$receipt.plannedRefusalCount -ne 5 -or [int]$receipt.completedRefusalCount -ne 5 -or [int]$receipt.passedRefusalCount -ne 5 -or
        @($receipt.failures).Count -ne 0
    ) { throw 'NativeOperations stage receipt is not a complete 24-case plus 5-refusal success.' }
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.outputRoot) -Expected $stageRoot -Description 'NativeOperations output root'
    foreach ($propertyPath in @(
        @{ Actual = [string]$receipt.inputManifestBefore; Expected = $beforePath; Name = 'NativeOperations before-input receipt path' },
        @{ Actual = [string]$receipt.inputManifestAfter; Expected = $afterPath; Name = 'NativeOperations after-input receipt path' },
        @{ Actual = [string]$receipt.sourcehashManifest; Expected = $sourcehashPath; Name = 'NativeOperations sourcehash receipt path' },
        @{ Actual = [string]$receipt.artifactManifest; Expected = $artifactPath; Name = 'NativeOperations artifact receipt path' },
        @{ Actual = [string]$receipt.commands; Expected = $commandLogPath; Name = 'NativeOperations command-log receipt path' }
    )) { $null = Assert-LedgerExactPath -Actual $propertyPath.Actual -Expected $propertyPath.Expected -Description $propertyPath.Name }

    $before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath $afterPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $before -PropertyNames @('schemaVersion', 'stage', 'candidate', 'instrumented', 'valuesRoot', 'sourceInputs', 'executionInputs') -Description 'NativeOperations before-input manifest'
    Assert-LedgerObjectProperties -Object $after -PropertyNames @('schemaVersion', 'stage', 'sourceInputs', 'executionInputs', 'changed') -Description 'NativeOperations after-input manifest'
    $cacheSourceRootProperty = $before.PSObject.Properties['compilerCacheSourceRoot']
    $cacheSourceEvidenceProperty = $before.PSObject.Properties['compilerCacheSourceEvidence']
    if (
        [int]$before.schemaVersion -ne 1 -or [string]$before.stage -cne 'NativeOperations' -or
        [string]$before.candidate -cne $candidateFromName -or [bool]$before.instrumented -ne $expectedInstrumented -or
        [string]$after.stage -cne 'NativeOperations' -or $after.changed -ne $false -or
        ($null -ne $cacheSourceRootProperty -and -not [string]::IsNullOrWhiteSpace([string]$cacheSourceRootProperty.Value)) -or
        ($null -ne $cacheSourceEvidenceProperty -and @($cacheSourceEvidenceProperty.Value).Count -ne 0)
    ) { throw 'NativeOperations stage inputs do not prove a stable self-owned cache producer.' }
    $valuesRoot = [System.IO.Path]::GetFullPath([string]$before.valuesRoot).TrimEnd('\')
    $relativeValuesRoot = [System.IO.Path]::GetRelativePath($scratchRoot, $valuesRoot).Replace('/', '\')
    $valuesSegments = $relativeValuesRoot.Split('\')
    if ($valuesSegments.Length -ne 3 -or $valuesSegments[0] -notmatch '^Configuration-[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or
        $valuesSegments[1] -cne 'values' -or $valuesSegments[2] -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw 'NativeOperations ValuesRoot is not a configuration result under the same batch scratch directory.'
    }
    if ([string]$receipt.valuesRoot -ine $valuesRoot) { throw 'NativeOperations receipt ValuesRoot differs from its before-input manifest.' }
    foreach ($inputSet in @(
        [pscustomobject]@{ Before = @($before.sourceInputs); After = @($after.sourceInputs); Name = 'NativeOperations source inputs' },
        [pscustomobject]@{ Before = @($before.executionInputs); After = @($after.executionInputs); Name = 'NativeOperations execution inputs' }
    )) {
        $snapshot = [pscustomobject]@{
            InputsBefore = $inputSet.Before
            InputsAfter = $inputSet.After
            ChangedInputCount = 0
            ChangedInputs = @()
        }
        try { Assert-LedgerInputSnapshotStable -StageManifest $snapshot } catch { throw "$($inputSet.Name) are not stable: $($_.Exception.Message)" }
    }
    $sourcehash = Get-Content -LiteralPath $sourcehashPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $sourcehash -PropertyNames @('schemaVersion', 'stage', 'sourceSet', 'inputsBefore', 'inputsAfter', 'changed') -Description 'NativeOperations sourcehash manifest'
    if ([int]$sourcehash.schemaVersion -ne 1 -or [string]$sourcehash.stage -cne 'NativeOperations' -or
        $sourcehash.changed -ne $false -or @($sourcehash.inputsBefore).Count -ne @($before.sourceInputs).Count -or
        @($sourcehash.inputsAfter).Count -ne @($after.sourceInputs).Count) {
        throw 'NativeOperations sourcehash manifest is incomplete or reports changed inputs.'
    }
    $sourceSnapshot = [pscustomobject]@{
        InputsBefore = @($sourcehash.inputsBefore)
        InputsAfter = @($sourcehash.inputsAfter)
        ChangedInputCount = 0
        ChangedInputs = @()
    }
    Assert-LedgerInputSnapshotStable -StageManifest $sourceSnapshot
    $expectedSourceJson = ConvertTo-Json -InputObject @($before.sourceInputs) -Depth 8 -Compress
    $actualSourceJson = ConvertTo-Json -InputObject @($sourcehash.inputsBefore) -Depth 8 -Compress
    if ($actualSourceJson -cne $expectedSourceJson) { throw 'NativeOperations sourcehash pins differ from the stage source input snapshot.' }

    $artifact = Get-Content -LiteralPath $artifactPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $artifact -PropertyNames @(
        'Candidate', 'Binary', 'BinarySha256', 'Zig', 'ZigSha256', 'Options', 'OptionsSha256',
        'ValuesRoot', 'CompilerCommand', 'Instrumented', 'Mode', 'Passed'
    ) -Description 'NativeOperations artifact manifest'
    Assert-LedgerObjectProperties -Object $artifact.CompilerCommand -PropertyNames @(
        'name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'processId', 'launchFailure',
        'stdoutPath', 'stderrPath', 'stdoutSha256', 'stderrSha256'
    ) -Description 'NativeOperations compiler command record'
    if (
        $artifact.Passed -ne $true -or [string]$artifact.Candidate -cne $candidateFromName -or
        [bool]$artifact.Instrumented -ne $expectedInstrumented -or [string]$artifact.Mode -cne $expectedStageMode -or
        [string]$artifact.ValuesRoot -ine $valuesRoot
    ) { throw 'NativeOperations artifact manifest does not match the clean/work stage identity.' }
    $expectedBinary = Join-Path $stageRoot ('bin\owned-operation-{0}-{1}.exe' -f $candidateFromName, $expectedStageMode)
    $expectedOptions = Join-Path $stageRoot 'benchmark_options.zig'
    $binaryPath = Assert-LedgerExactPath -Actual ([string]$artifact.Binary) -Expected $expectedBinary -Description 'NativeOperations output binary'
    $optionsPath = Assert-LedgerExactPath -Actual ([string]$artifact.Options) -Expected $expectedOptions -Description 'NativeOperations options module'
    $null = Assert-LedgerExactPath -Actual ([string]$artifact.CompilerCommand.executable) -Expected ([string]$artifact.Zig) -Description 'NativeOperations compiler executable'
    if ([System.IO.Path]::GetFileName([string]$artifact.Zig) -cne 'zig.exe') { throw 'NativeOperations compiler is not a Zig executable.' }
    if ((Get-FileHash -LiteralPath $binaryPath -Algorithm SHA256).Hash -ine [string]$artifact.BinarySha256 -or
        (Get-FileHash -LiteralPath $optionsPath -Algorithm SHA256).Hash -ine [string]$artifact.OptionsSha256 -or
        (Get-FileHash -LiteralPath ([string]$artifact.Zig) -Algorithm SHA256).Hash -ine [string]$artifact.ZigSha256) {
        throw 'NativeOperations binary, options, or Zig compiler hash differs from its artifact manifest.'
    }
    $instrumentText = if ($expectedInstrumented) { 'true' } else { 'false' }
    $expectedOptionsText = "pub const candidate: enum { both, c2, b2 } = .$candidateFromName;`npub const instrument_work: bool = $instrumentText;`n"
    if ([IO.File]::ReadAllText($optionsPath) -cne $expectedOptionsText) { throw 'NativeOperations options module differs from the candidate and work mode.' }
    $expectedValuesModule = Join-Path $valuesRoot 'ledger_values.zig'
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $operationDriver = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\owned_operation_driver.zig'
    $structureModule = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'
    $expectedArguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        'build-exe', '-j1', '-fno-llvm', '-fno-lld', '-OReleaseFast', '--zig-lib-dir',
        (Join-Path ([System.IO.Path]::GetDirectoryName([string]$artifact.Zig)) 'lib'),
        '--dep', 'ledger_values', '--dep', 'benchmark_options', ('-Mroot=' + $operationDriver), '--dep', 'ledger_structure',
        ('-Mledger_values=' + $expectedValuesModule), ('-Mledger_structure=' + $structureModule),
        ('-Mbenchmark_options=' + $optionsPath), '--cache-dir', (Join-Path $stageRoot 'cache'),
        '--global-cache-dir', (Join-Path $stageRoot 'global-cache'), ('-femit-bin=' + $binaryPath)
    )) { $expectedArguments.Add([string]$argument) }
    Assert-LedgerStringSequence -Actual @($artifact.CompilerCommand.arguments) -Expected $expectedArguments.ToArray() -Description 'NativeOperations compile arguments'
    if (
        [string]$artifact.CompilerCommand.name -cne 'compile-operation-probe' -or
        [int]$artifact.CompilerCommand.expectedExitCode -ne 0 -or [int]$artifact.CompilerCommand.exitCode -ne 0 -or
        [long]$artifact.CompilerCommand.processId -le 0 -or $null -ne $artifact.CompilerCommand.launchFailure
    ) { throw 'NativeOperations compiler command is not a successful build-exe invocation.' }

    $bounded = Get-Content -LiteralPath $boundedPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $bounded -PropertyNames @(
        'Passed', 'Stage', 'RunName', 'ExitCode', 'NormalDrain', 'EmptyJob', 'JobMemoryLimitBytes',
        'UnexpectedChangedInputCount', 'RunDirectory', 'Manifest', 'InputManifest', 'OutputRoot', 'ValuesRoot', 'Failure'
    ) -Description 'NativeOperations bounded ExecutionJob receipt'
    if (
        $bounded.Passed -ne $true -or [string]$bounded.Stage -cne 'NativeOperations' -or [string]$bounded.RunName -cne $runName -or
        [int]$bounded.ExitCode -ne 0 -or $bounded.NormalDrain -ne $true -or $bounded.EmptyJob -ne $true -or
        [long]$bounded.JobMemoryLimitBytes -ne 536870912 -or [int]$bounded.UnexpectedChangedInputCount -ne 0 -or
        ($null -ne $bounded.Failure -and -not [string]::IsNullOrWhiteSpace([string]$bounded.Failure))
    ) { throw 'NativeOperations bounded ExecutionJob receipt does not prove a normal empty 512 MiB success.' }
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.OutputRoot) -Expected $stageRoot -Description 'NativeOperations bounded output root'
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.ValuesRoot) -Expected $valuesRoot -Description 'NativeOperations bounded ValuesRoot'
    $jobManifestPath = Assert-LedgerExactPath -Actual $boundPaths['ExecutionJob manifest'] -Expected ([string]$bounded.Manifest) -Description 'ExecutionJob manifest path'
    $jobInputPath = Assert-LedgerExactPath -Actual $boundPaths['ExecutionJob input manifest'] -Expected ([string]$bounded.InputManifest) -Description 'ExecutionJob input manifest path'
    $jobMetadataRoot = [System.IO.Path]::GetDirectoryName($jobManifestPath)
    if ([System.IO.Path]::GetFileName($jobMetadataRoot) -cnotmatch ('^\.execution-NativeOperations-' + [regex]::Escape($runName) + '-[A-Fa-f0-9]{32}$')) {
        throw 'ExecutionJob metadata root is not the exact NativeOperations run metadata directory.'
    }
    $expectedJobReceiptPath = Join-Path ([string]$bounded.RunDirectory) 'receipts.jsonl'
    $jobReceiptPath = Assert-LedgerExactPath -Actual $boundPaths['ExecutionJob event receipt'] -Expected $expectedJobReceiptPath -Description 'ExecutionJob event receipt path'
    $jobManifest = Get-Content -LiteralPath $jobManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $jobManifest -PropertyNames @(
        'arguments', 'runDirectory', 'allowedReceiptRoot', 'jobMemoryLimitBytes', 'activeProcessLimit', 'cpuRate', 'timeoutSeconds'
    ) -Description 'NativeOperations ExecutionJob manifest'
    $expectedAllowedReceiptRoot = Join-Path $jobMetadataRoot 'job-receipts'
    $null = Assert-LedgerExactPath -Actual ([string]$jobManifest.allowedReceiptRoot) -Expected $expectedAllowedReceiptRoot -Description 'ExecutionJob allowed receipt root'
    $null = Assert-LedgerExactPath -Actual ([string]$jobManifest.runDirectory) -Expected ([string]$bounded.RunDirectory) -Description 'ExecutionJob run directory'
    if ([long]$jobManifest.jobMemoryLimitBytes -ne 536870912 -or [int]$jobManifest.activeProcessLimit -ne 8 -or
        [int]$jobManifest.cpuRate -ne 2500 -or [int]$jobManifest.timeoutSeconds -ne 900) {
        throw 'NativeOperations ExecutionJob limits differ from the bounded validation route.'
    }
    $expectedJobArguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $repositoryRoot '.local\ledger-owner-implementation\validation\Run-Bounded.ps1'),
        '-Stage', 'NativeOperations', '-BatchRoot', $batchRoot, '-RunName', $runName,
        '-ExecutionPayload', '-PayloadOutputRoot', $stageRoot, '-PayloadMetadataRoot', $jobMetadataRoot,
        '-ValuesRoot', $valuesRoot, '-FacadeCandidate', $candidateFromName
    )) { $expectedJobArguments.Add([string]$argument) }
    if ($expectedInstrumented) { $expectedJobArguments.Add('-InstrumentWork') }
    Assert-LedgerStringSequence -Actual @($jobManifest.arguments) -Expected $expectedJobArguments.ToArray() -Description 'NativeOperations ExecutionJob wrapper arguments'

    $jobInput = Get-Content -LiteralPath $jobInputPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $jobInput -PropertyNames @(
        'SchemaVersion', 'Stage', 'RunName', 'BatchRoot', 'OutputRoot', 'ValuesRoot',
        'InputsBefore', 'InputsAfter', 'ChangedInputs', 'UnexpectedChangedInputs'
    ) -Description 'NativeOperations ExecutionJob input manifest'
    if ([int]$jobInput.SchemaVersion -ne 1 -or [string]$jobInput.Stage -cne 'NativeOperations' -or
        [string]$jobInput.RunName -cne $runName -or [string]$jobInput.BatchRoot -ine $batchRoot -or
        [string]$jobInput.OutputRoot -ine $stageRoot -or [string]$jobInput.ValuesRoot -ine $valuesRoot -or
        @($jobInput.ChangedInputs).Count -ne 0 -or @($jobInput.UnexpectedChangedInputs).Count -ne 0) {
        throw 'NativeOperations ExecutionJob input manifest is not bound to the stage or reports changes.'
    }
    $jobInputSnapshot = [pscustomobject]@{
        InputsBefore = @($jobInput.InputsBefore)
        InputsAfter = @($jobInput.InputsAfter)
        ChangedInputs = @($jobInput.ChangedInputs)
        UnexpectedChangedInputs = @($jobInput.UnexpectedChangedInputs)
    }
    Assert-LedgerInputSnapshotStable -StageManifest $jobInputSnapshot -CountProperty '' -ChangedArrayProperties @('ChangedInputs', 'UnexpectedChangedInputs')
    Assert-LedgerExecutionLifecycle -JobReceiptPath $jobReceiptPath -JobManifest $jobManifest `
        -RunDirectory ([string]$bounded.RunDirectory) -AllowedReceiptRoot $expectedAllowedReceiptRoot

    $commands = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $commandLogPath) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $commands.Add(($line | ConvertFrom-Json)) }
    }
    $cases = @(Get-Content -LiteralPath $caseResultsPath -Raw | ConvertFrom-Json)
    if ($commands.Count -ne 30 -or $cases.Count -ne 24) { throw 'NativeOperations command log or case result count differs from the required 30/24 shape.' }
    $compileRow = $commands[0]
    Assert-LedgerObjectProperties -Object $compileRow -PropertyNames @(
        'name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'processId', 'launchFailure',
        'stdoutPath', 'stderrPath', 'stdoutSha256', 'stderrSha256'
    ) -Description 'NativeOperations compile command log row'
    if ([string]$compileRow.name -cne 'compile-operation-probe' -or
        [string]$compileRow.executable -ine [string]$artifact.Zig -or
        [int]$compileRow.expectedExitCode -ne 0 -or [int]$compileRow.exitCode -ne 0 -or
        [long]$compileRow.processId -le 0 -or $null -ne $compileRow.launchFailure) {
        throw 'NativeOperations command log does not begin with its successful compiler invocation.'
    }
    Assert-LedgerStringSequence -Actual @($compileRow.arguments) -Expected $expectedArguments.ToArray() -Description 'NativeOperations logged compiler arguments'
    $null = Assert-LedgerExactPath -Actual ([string]$compileRow.stdoutPath) -Expected (Join-Path $stageRoot 'logs\compile.stdout') -Description 'NativeOperations compiler stdout path'
    $null = Assert-LedgerExactPath -Actual ([string]$compileRow.stderrPath) -Expected (Join-Path $stageRoot 'logs\compile.stderr') -Description 'NativeOperations compiler stderr path'
    foreach ($field in @('executable', 'name', 'expectedExitCode', 'exitCode', 'stdoutPath', 'stderrPath', 'stdoutSha256', 'stderrSha256')) {
        if ([string]$compileRow.$field -cne [string]$artifact.CompilerCommand.$field) { throw "Artifact compiler command and command log differ at '$field'." }
    }
    foreach ($spec in @(
        @{ Path = [string]$compileRow.stdoutPath; Expected = (Join-Path $stageRoot 'logs\compile.stdout'); Hash = [string]$compileRow.stdoutSha256; Name = 'NativeOperations compiler stdout' },
        @{ Path = [string]$compileRow.stderrPath; Expected = (Join-Path $stageRoot 'logs\compile.stderr'); Hash = [string]$compileRow.stderrSha256; Name = 'NativeOperations compiler stderr' }
    )) {
        $fullPath = Assert-LedgerEvidenceFile -Path $spec.Path -ExpectedSha256 $spec.Hash -ValidationRoot $ValidationRoot -Description $spec.Name
        $null = Assert-LedgerExactPath -Actual $fullPath -Expected $spec.Expected -Description $spec.Name
    }

    $refusals = @(Get-NativeOperationRefusalDefinitions -Candidate $candidateFromName -OperationMode $operationMode)
    for ($refusalIndex = 0; $refusalIndex -lt $refusals.Count; $refusalIndex++) {
        $row = $commands[$refusalIndex + 1]
        $definition = $refusals[$refusalIndex]
        Assert-LedgerObjectProperties -Object $row -PropertyNames @(
            'name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'processId', 'launchFailure',
            'stdoutPath', 'stderrPath', 'stdoutSha256', 'stderrSha256'
        ) -Description 'NativeOperations refusal command row'
        if ([string]$row.name -cne $definition.Name -or [string]$row.executable -ine $binaryPath -or
            [int]$row.expectedExitCode -ne 1 -or [int]$row.exitCode -ne 1 -or [long]$row.processId -le 0 -or
            $null -ne $row.launchFailure) { throw "NativeOperations refusal command '$($definition.Name)' did not fail as expected." }
        Assert-LedgerStringSequence -Actual @($row.arguments) -Expected @($definition.Arguments) -Description "NativeOperations refusal arguments '$($definition.Name)'"
        $expectedStdout = Join-Path $stageRoot ("raw\{0}.stdout" -f $definition.Name)
        $expectedStderr = Join-Path $stageRoot ("raw\{0}.stderr" -f $definition.Name)
        $stdoutPath = Assert-LedgerExactPath -Actual ([string]$row.stdoutPath) -Expected $expectedStdout -Description 'NativeOperations refusal stdout path'
        $stderrPath = Assert-LedgerExactPath -Actual ([string]$row.stderrPath) -Expected $expectedStderr -Description 'NativeOperations refusal stderr path'
        $stdoutPath = Assert-LedgerEvidenceFile -Path $stdoutPath -ExpectedSha256 ([string]$row.stdoutSha256) -ValidationRoot $ValidationRoot -Description 'NativeOperations refusal stdout'
        $stderrPath = Assert-LedgerEvidenceFile -Path $stderrPath -ExpectedSha256 ([string]$row.stderrSha256) -ValidationRoot $ValidationRoot -Description 'NativeOperations refusal stderr'
        if (([IO.File]::ReadAllText($stdoutPath)).Length -ne 0 -or
            ([IO.File]::ReadAllText($stderrPath)).Trim() -cne ('error: ' + [string]$definition.Error)) {
            throw "NativeOperations refusal '$($definition.Name)' lacks its exact diagnostic output."
        }
    }

    $operationNames = @('audit', 'prepare-repeat', 'single-remove', 'max-table-close', 'full-destroy', 'small-churn')
    $widths = @(128, 256)
    $profiles = @('original-maximum', 'product-327699')
    $caseIndex = 0
    foreach ($uidBits in $widths) {
        foreach ($profile in $profiles) {
            foreach ($operation in $operationNames) {
                $case = $cases[$caseIndex]
                $caseId = '{0}-{1}-{2}-{3}' -f $candidateFromName, $uidBits, $profile, $operation
                $caseName = 'case-' + $caseId
                $expectedCaseArguments = @('--candidate', $candidateFromName, '--uid-bits', [string]$uidBits,
                    '--profile', $profile, '--operation', $operation, '--mode', $expectedStageMode,
                    '--iterations', '1', '--affinity-mask', '1')
                Assert-LedgerObjectProperties -Object $case -PropertyNames @(
                    'caseId', 'candidate', 'uidBits', 'profile', 'operation', 'mode', 'arguments', 'exitCode',
                    'passed', 'validationFailure', 'stdoutPath', 'stderrPath', 'result'
                ) -Description 'NativeOperations case result row'
                if ([string]$case.caseId -cne $caseId -or [string]$case.candidate -cne $candidateFromName -or
                    [int]$case.uidBits -ne $uidBits -or [string]$case.profile -cne $profile -or
                    [string]$case.operation -cne $operation -or [string]$case.mode -cne $expectedStageMode -or
                    [int]$case.exitCode -ne 0 -or $case.passed -ne $true -or
                    ($null -ne $case.validationFailure -and -not [string]::IsNullOrWhiteSpace([string]$case.validationFailure)) -or
                    $null -eq $case.result) {
                    throw "NativeOperations result row did not pass for $caseId."
                }
                Assert-LedgerStringSequence -Actual @($case.arguments) -Expected $expectedCaseArguments -Description "NativeOperations case arguments '$caseId'"
                $commandRow = $commands[$caseIndex + 6]
                if ([string]$commandRow.name -cne $caseName -or [string]$commandRow.executable -ine $binaryPath -or
                    [int]$commandRow.expectedExitCode -ne 0 -or [int]$commandRow.exitCode -ne 0 -or
                    [long]$commandRow.processId -le 0 -or $null -ne $commandRow.launchFailure) {
                    throw "NativeOperations command row did not pass for $caseId."
                }
                Assert-LedgerStringSequence -Actual @($commandRow.arguments) -Expected $expectedCaseArguments -Description "NativeOperations logged case arguments '$caseId'"
                $expectedStdout = Join-Path $stageRoot ("raw\{0}.stdout" -f $caseName)
                $expectedStderr = Join-Path $stageRoot ("raw\{0}.stderr" -f $caseName)
                $stdoutPath = Assert-LedgerExactPath -Actual ([string]$case.stdoutPath) -Expected $expectedStdout -Description 'NativeOperations case stdout path'
                $stderrPath = Assert-LedgerExactPath -Actual ([string]$case.stderrPath) -Expected $expectedStderr -Description 'NativeOperations case stderr path'
                $null = Assert-LedgerExactPath -Actual ([string]$commandRow.stdoutPath) -Expected $stdoutPath -Description 'NativeOperations logged case stdout path'
                $null = Assert-LedgerExactPath -Actual ([string]$commandRow.stderrPath) -Expected $stderrPath -Description 'NativeOperations logged case stderr path'
                $stdoutPath = Assert-LedgerEvidenceFile -Path $stdoutPath -ExpectedSha256 ([string]$commandRow.stdoutSha256) -ValidationRoot $ValidationRoot -Description 'NativeOperations case stdout'
                $stderrPath = Assert-LedgerEvidenceFile -Path $stderrPath -ExpectedSha256 ([string]$commandRow.stderrSha256) -ValidationRoot $ValidationRoot -Description 'NativeOperations case stderr'
                $outputLines = @(([IO.File]::ReadAllText($stdoutPath) -split '\r?\n') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                if ($outputLines.Count -ne 1) { throw "NativeOperations case '$caseId' did not emit exactly one JSON record." }
                $rawRecord = $outputLines[0] | ConvertFrom-Json
                $rawJson = ConvertTo-Json -InputObject $rawRecord -Depth 30 -Compress
                $caseJson = ConvertTo-Json -InputObject $case.result -Depth 30 -Compress
                if ($rawJson -cne $caseJson) { throw "NativeOperations raw result differs from case-results.json for '$caseId'." }
                Assert-NativeOperationResult -Record $rawRecord -Candidate $candidateFromName -UidBits $uidBits `
                    -Profile $profile -Operation $operation -OperationMode $operationMode
                $caseIndex++
            }
        }
    }

    [pscustomobject]@{
        ProofProfile = 'native-operations-correctness'
        NativeProbe = $null
        OperationMode = $operationMode
        Candidate = $candidateFromName
        StageName = $stageName
        RunName = $runName
        CacheOwnerStageName = $stageName
        ReceiptPath = $receiptPath
        ReceiptSha256 = [string]$Evidence.receiptSha256
        BoundedExecutionReceiptPath = $boundedPath
        BoundedExecutionReceiptSha256 = [string]$Evidence.boundedExecutionReceiptSha256
        StageManifestPath = $beforePath
        StageManifestSha256 = [string]$Evidence.inputManifestBeforeSha256
        ExecutionJobInputManifestPath = $jobInputPath
        ExecutionJobInputManifestSha256 = [string]$Evidence.executionJobInputManifestSha256
        ExecutionJobManifestPath = $jobManifestPath
        ExecutionJobManifestSha256 = [string]$Evidence.executionJobManifestSha256
        JobReceiptPath = $jobReceiptPath
        JobReceiptSha256 = [string]$Evidence.jobReceiptSha256
        ArtifactManifestPath = $artifactPath
        ArtifactManifestSha256 = [string]$Evidence.artifactManifestSha256
        CommandLogPath = $commandLogPath
        CommandLogSha256 = [string]$Evidence.commandLogSha256
        CorrectnessResultsPath = $caseResultsPath
        CorrectnessResultsSha256 = [string]$Evidence.caseResultsSha256
        CacheRoot = Join-Path $stageRoot 'cache'
        GlobalCacheRoot = Join-Path $stageRoot 'global-cache'
        InputCount = [int](@($before.sourceInputs).Count + @($before.executionInputs).Count)
    }
}

function Get-OwnerConcurrentEvidence {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$BatchId,
        [Parameter(Mandatory)][string]$ValidationRoot
    )
    if ($BatchId -cne 'ledger-owner-a1') { throw 'owner-concurrent-correctness is scoped to ledger-owner-a1.' }
    Assert-LedgerObjectProperties -Object $Evidence -PropertyNames @(
        'runName', 'receiptPath', 'receiptSha256',
        'boundedExecutionReceiptPath', 'boundedExecutionReceiptSha256',
        'inputManifestBeforePath', 'inputManifestBeforeSha256',
        'inputManifestAfterPath', 'inputManifestAfterSha256',
        'sourcehashManifestPath', 'sourcehashManifestSha256',
        'artifactManifestPath', 'artifactManifestSha256',
        'artifactInputManifestPath', 'artifactInputManifestSha256',
        'commandLogPath', 'commandLogSha256', 'caseResultsPath', 'caseResultsSha256',
        'executionJobInputManifestPath', 'executionJobInputManifestSha256',
        'executionJobManifestPath', 'executionJobManifestSha256', 'jobReceiptPath', 'jobReceiptSha256'
    ) -Description 'OwnerConcurrent prune evidence'
    $runName = [string]$Evidence.runName
    if ($runName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw 'owner-concurrent-correctness requires an explicit valid runName.'
    }

    $receiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.receiptPath) `
        -ExpectedSha256 ([string]$Evidence.receiptSha256) -ValidationRoot $ValidationRoot -Description 'OwnerConcurrent receipt'
    $stageRoot = [System.IO.Path]::GetDirectoryName($receiptPath)
    $stageName = [System.IO.Path]::GetFileName($stageRoot)
    if ($stageName -cne ('OwnerConcurrent-' + $runName)) { throw 'OwnerConcurrent receipt is not under its exact run-name stage directory.' }
    $batchRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $ValidationRoot 'batches') $BatchId)).TrimEnd('\')
    $scratchRoot = Join-Path $batchRoot 'scratch'
    $stageRoot = Assert-LedgerExactPath -Actual $stageRoot -Expected (Join-Path $scratchRoot $stageName) -Description 'OwnerConcurrent stage root'
    Assert-NoReparseComponents -Path $stageRoot -Root $ValidationRoot
    $stageItem = Get-Item -LiteralPath $stageRoot
    if (-not $stageItem.PSIsContainer -or (($stageItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The OwnerConcurrent stage root is missing or is a reparse point.'
    }

    $boundPaths = @{}
    foreach ($spec in @(
        @{ Property = 'boundedExecutionReceiptPath'; Hash = 'boundedExecutionReceiptSha256'; Expected = (Join-Path $stageRoot 'bounded-execution-receipt.json'); Description = 'OwnerConcurrent bounded ExecutionJob receipt' },
        @{ Property = 'inputManifestBeforePath'; Hash = 'inputManifestBeforeSha256'; Expected = (Join-Path $stageRoot 'input-manifest-before.json'); Description = 'OwnerConcurrent before-input manifest' },
        @{ Property = 'inputManifestAfterPath'; Hash = 'inputManifestAfterSha256'; Expected = (Join-Path $stageRoot 'input-manifest-after.json'); Description = 'OwnerConcurrent after-input manifest' },
        @{ Property = 'sourcehashManifestPath'; Hash = 'sourcehashManifestSha256'; Expected = (Join-Path $stageRoot 'sourcehash-manifest.json'); Description = 'OwnerConcurrent sourcehash manifest' },
        @{ Property = 'artifactManifestPath'; Hash = 'artifactManifestSha256'; Expected = (Join-Path $stageRoot 'artifact-manifest.json'); Description = 'OwnerConcurrent artifact manifest' },
        @{ Property = 'artifactInputManifestPath'; Hash = 'artifactInputManifestSha256'; Expected = (Join-Path $stageRoot 'artifact-input-manifest.json'); Description = 'OwnerConcurrent artifact-input manifest' },
        @{ Property = 'commandLogPath'; Hash = 'commandLogSha256'; Expected = (Join-Path $stageRoot 'commands.jsonl'); Description = 'OwnerConcurrent command log' },
        @{ Property = 'caseResultsPath'; Hash = 'caseResultsSha256'; Expected = (Join-Path $stageRoot 'case-results.json'); Description = 'OwnerConcurrent case results' }
    )) {
        $path = Assert-LedgerEvidenceFile -Path ([string]$Evidence.($spec.Property)) `
            -ExpectedSha256 ([string]$Evidence.($spec.Hash)) -ValidationRoot $ValidationRoot -Description $spec.Description
        $boundPaths[$spec.Property] = Assert-LedgerExactPath -Actual $path -Expected $spec.Expected -Description $spec.Description
    }
    foreach ($spec in @(
        @{ Property = 'executionJobManifestPath'; Hash = 'executionJobManifestSha256'; Name = 'OwnerConcurrent ExecutionJob manifest' },
        @{ Property = 'executionJobInputManifestPath'; Hash = 'executionJobInputManifestSha256'; Name = 'OwnerConcurrent ExecutionJob input manifest' },
        @{ Property = 'jobReceiptPath'; Hash = 'jobReceiptSha256'; Name = 'OwnerConcurrent ExecutionJob event receipt' }
    )) {
        $boundPaths[$spec.Name] = Assert-LedgerEvidenceFile -Path ([string]$Evidence.($spec.Property)) `
            -ExpectedSha256 ([string]$Evidence.($spec.Hash)) -ValidationRoot $ValidationRoot -Description $spec.Name
    }

    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $receipt -PropertyNames @(
        'passed', 'stage', 'phase', 'workloadRevision', 'outputRoot', 'valuesRoot',
        'planPath', 'planSha256', 'registrationPath', 'registrationSha256',
        'topologyEvidencePath', 'topologyEvidenceSha256', 'plannedCaseCount', 'completedCaseCount',
        'passedCaseCount', 'plannedArmCount', 'completedArmCount', 'plannedRawSampleCount',
        'recordedRawSampleCount', 'correctnessBuildCount', 'inputsStable', 'inputManifestBefore',
        'inputManifestAfter', 'sourcehashManifest', 'artifactManifest', 'failure'
    ) -Description 'OwnerConcurrent stage receipt'
    if (
        $receipt.passed -ne $true -or [string]$receipt.stage -cne 'OwnerConcurrent' -or
        [string]$receipt.phase -cne 'Correctness' -or [int]$receipt.workloadRevision -lt 2 -or
        [int]$receipt.plannedCaseCount -ne 48 -or [int]$receipt.completedCaseCount -ne 48 -or [int]$receipt.passedCaseCount -ne 48 -or
        [int]$receipt.correctnessBuildCount -ne 2 -or [int]$receipt.plannedArmCount -ne 0 -or
        [int]$receipt.completedArmCount -ne 0 -or [int]$receipt.plannedRawSampleCount -ne 0 -or
        [int]$receipt.recordedRawSampleCount -ne 0 -or $receipt.inputsStable -ne $true -or
        ($null -ne $receipt.failure -and -not [string]::IsNullOrWhiteSpace([string]$receipt.failure))
    ) { throw 'OwnerConcurrent receipt is not a successful 48-case correctness-only stage.' }
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.outputRoot) -Expected $stageRoot -Description 'OwnerConcurrent output root'
    foreach ($spec in @(
        @{ Actual = [string]$receipt.inputManifestBefore; Expected = $boundPaths.inputManifestBeforePath; Name = 'OwnerConcurrent receipt before-input path' },
        @{ Actual = [string]$receipt.inputManifestAfter; Expected = $boundPaths.inputManifestAfterPath; Name = 'OwnerConcurrent receipt after-input path' },
        @{ Actual = [string]$receipt.sourcehashManifest; Expected = $boundPaths.sourcehashManifestPath; Name = 'OwnerConcurrent receipt sourcehash path' },
        @{ Actual = [string]$receipt.artifactManifest; Expected = $boundPaths.artifactManifestPath; Name = 'OwnerConcurrent receipt artifact path' }
    )) { $null = Assert-LedgerExactPath -Actual $spec.Actual -Expected $spec.Expected -Description $spec.Name }

    $beforePath = $boundPaths.inputManifestBeforePath
    $afterPath = $boundPaths.inputManifestAfterPath
    $sourcehashPath = $boundPaths.sourcehashManifestPath
    $artifactPath = $boundPaths.artifactManifestPath
    $artifactInputPath = $boundPaths.artifactInputManifestPath
    $commandLogPath = $boundPaths.commandLogPath
    $caseResultsPath = $boundPaths.caseResultsPath
    $before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath $afterPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $before -PropertyNames @(
        'schemaVersion', 'stage', 'phase', 'outputRoot', 'valuesRoot', 'planPath', 'planSha256',
        'registrationSha256', 'topologyEvidenceSha256', 'compilerCacheSourceRoot',
        'compilerCacheSourceEvidence', 'timingSupport', 'concurrentSupport', 'sourceInputs', 'executionInputs'
    ) -Description 'OwnerConcurrent before-input manifest'
    Assert-LedgerObjectProperties -Object $after -PropertyNames @('schemaVersion', 'stage', 'phase', 'sourceInputs', 'executionInputs', 'changed') -Description 'OwnerConcurrent after-input manifest'
    $cacheSourceRootProperty = $before.PSObject.Properties['compilerCacheSourceRoot']
    $cacheSourceEvidenceProperty = $before.PSObject.Properties['compilerCacheSourceEvidence']
    $cacheSourceEvidenceValue = if ($null -ne $cacheSourceEvidenceProperty) { $cacheSourceEvidenceProperty.Value } else { $null }
    if (
        [int]$before.schemaVersion -ne 1 -or [string]$before.stage -cne 'OwnerConcurrent' -or
        [string]$before.phase -cne 'Correctness' -or [string]$after.stage -cne 'OwnerConcurrent' -or
        [string]$after.phase -cne 'Correctness' -or $after.changed -ne $false -or
        ($null -ne $cacheSourceRootProperty -and -not [string]::IsNullOrWhiteSpace([string]$cacheSourceRootProperty.Value)) -or
        ($null -ne $cacheSourceEvidenceValue -and @($cacheSourceEvidenceValue).Count -ne 0)
    ) { throw 'OwnerConcurrent stage inputs do not prove stable, self-owned compiler caches.' }
    $null = Assert-LedgerExactPath -Actual ([string]$before.outputRoot) -Expected $stageRoot -Description 'OwnerConcurrent manifest output root'
    $valuesRoot = [System.IO.Path]::GetFullPath([string]$before.valuesRoot).TrimEnd('\')
    $relativeValuesRoot = [System.IO.Path]::GetRelativePath($scratchRoot, $valuesRoot).Replace('/', '\')
    $valuesSegments = $relativeValuesRoot.Split('\')
    if ($valuesSegments.Length -ne 3 -or $valuesSegments[0] -notmatch '^Configuration-[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or
        $valuesSegments[1] -cne 'values' -or $valuesSegments[2] -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw 'OwnerConcurrent ValuesRoot is not a configuration result under the same batch scratch directory.'
    }
    if ([string]$receipt.valuesRoot -ine $valuesRoot) { throw 'OwnerConcurrent receipt ValuesRoot differs from its before-input manifest.' }
    foreach ($inputSet in @(
        [pscustomobject]@{ Before = @($before.sourceInputs); After = @($after.sourceInputs); Name = 'OwnerConcurrent source inputs' },
        [pscustomobject]@{ Before = @($before.executionInputs); After = @($after.executionInputs); Name = 'OwnerConcurrent execution inputs' }
    )) {
        $snapshot = [pscustomobject]@{ InputsBefore = $inputSet.Before; InputsAfter = $inputSet.After; ChangedInputCount = 0; ChangedInputs = @() }
        try { Assert-LedgerInputSnapshotStable -StageManifest $snapshot } catch { throw "$($inputSet.Name) are not stable: $($_.Exception.Message)" }
    }
    if ([string]$receipt.planSha256 -ine [string]$before.planSha256 -or
        [string]$receipt.registrationSha256 -ine [string]$before.registrationSha256 -or
        [string]$receipt.topologyEvidenceSha256 -ine [string]$before.topologyEvidenceSha256) {
        throw 'OwnerConcurrent receipt and input manifest disagree on preregistration or topology hashes.'
    }
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $expectedPlanPath = Join-Path $repositoryRoot '.local\ledger-owner-implementation\foundation\concurrent-timing-plan.json'
    $expectedPlanArchive = Join-Path $repositoryRoot '.local\ledger-owner-implementation\foundation\concurrent-timing-plan-v2-before-managed-collection.json'
    $expectedRegistrationPath = Join-Path $repositoryRoot '.local\ledger-owner-implementation\validation\concurrent-probe-registration.json'
    $expectedTopologyPath = Join-Path $repositoryRoot '.local\ledger-owner-implementation\validation\topology-route-report.md'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.planPath) -Expected $expectedPlanPath -Description 'OwnerConcurrent preregistration plan path'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.registrationPath) -Expected $expectedRegistrationPath -Description 'OwnerConcurrent registration path'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.topologyEvidencePath) -Expected $expectedTopologyPath -Description 'OwnerConcurrent topology evidence path'
    $null = Assert-LedgerExactPath -Actual ([string]$before.planPath) -Expected $expectedPlanPath -Description 'OwnerConcurrent manifest preregistration plan path'
    if ([string]$before.planSha256 -ine [string]$receipt.planSha256) { throw 'OwnerConcurrent plan hash differs between the receipt and input manifest.' }
    $planArchivePathProperty = $Evidence.PSObject.Properties['planSourceArchivePath']
    $planArchiveHashProperty = $Evidence.PSObject.Properties['planSourceArchiveSha256']
    if (($null -eq $planArchivePathProperty) -ne ($null -eq $planArchiveHashProperty)) {
        throw 'The OwnerConcurrent plan source archive path and SHA-256 must be supplied together.'
    }
    foreach ($rowSpec in @(
        @{ Path = $expectedPlanPath; Hash = [string]$receipt.planSha256; Description = 'OwnerConcurrent plan' },
        @{ Path = $expectedRegistrationPath; Hash = [string]$receipt.registrationSha256; Description = 'OwnerConcurrent registration' },
        @{ Path = $expectedTopologyPath; Hash = [string]$receipt.topologyEvidenceSha256; Description = 'OwnerConcurrent topology evidence' }
    )) {
        $inputRows = @($before.sourceInputs | Where-Object { [System.IO.Path]::GetFullPath([string]$_.path) -ieq [System.IO.Path]::GetFullPath($rowSpec.Path) })
        if ($inputRows.Count -ne 1 -or $inputRows[0].exists -ne $true -or [string]$inputRows[0].sha256 -ine $rowSpec.Hash) {
            throw "$($rowSpec.Description) is not bound by exactly one stable source-input row."
        }
        if ($rowSpec.Description -ceq 'OwnerConcurrent plan') {
            $planRow = $inputRows[0]
            $livePlanMatches = $false
            Assert-NoReparseComponents -Path $rowSpec.Path -Root $repositoryRoot
            if (Test-Path -LiteralPath $rowSpec.Path -PathType Leaf) {
                $livePlan = Get-Item -LiteralPath $rowSpec.Path
                $livePlanHash = (Get-FileHash -LiteralPath $rowSpec.Path -Algorithm SHA256).Hash
                $livePlanMatches = ([long]$livePlan.Length -eq [long]$planRow.length -and $livePlanHash -ieq $rowSpec.Hash)
            }
            if (-not $livePlanMatches -or $null -ne $planArchivePathProperty) {
                if ($null -eq $planArchivePathProperty) {
                    throw 'The historical OwnerConcurrent plan input changed; its exact archived source must be pinned.'
                }
                $planArchivePath = Assert-LedgerExactPath -Actual ([string]$planArchivePathProperty.Value) `
                    -Expected $expectedPlanArchive -Description 'OwnerConcurrent plan source archive path'
                Assert-NoReparseComponents -Path $planArchivePath -Root $repositoryRoot
                if (-not (Test-Path -LiteralPath $planArchivePath -PathType Leaf)) {
                    throw 'The pinned OwnerConcurrent plan archive is missing.'
                }
                $planArchive = Get-Item -LiteralPath $planArchivePath
                if ([long]$planArchive.Length -ne [long]$planRow.length -or
                    (Get-FileHash -LiteralPath $planArchivePath -Algorithm SHA256).Hash -ine $rowSpec.Hash -or
                    [string]$planArchiveHashProperty.Value -ine $rowSpec.Hash) {
                    throw 'The OwnerConcurrent plan archive does not reproduce the stage-pinned source hash and length.'
                }
            }
        }
    }

    $benchmarkPath = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\owned\owner_concurrent_benchmark.zig'
    $benchmarkRows = @($before.sourceInputs | Where-Object { [System.IO.Path]::GetFullPath([string]$_.path) -ieq [System.IO.Path]::GetFullPath($benchmarkPath) })
    if ($benchmarkRows.Count -ne 1 -or $benchmarkRows[0].exists -ne $true -or
        [string]$benchmarkRows[0].sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [long]$benchmarkRows[0].length -lt 0) {
        throw 'OwnerConcurrent benchmark source is not bound by exactly one existing source-input row.'
    }
    $expectedBenchmarkArchive = Join-Path $repositoryRoot '.local\ledger-owner-implementation\notifications\owner_concurrent_benchmark.before-release-reset-fix.zig'
    $archivePathProperty = $Evidence.PSObject.Properties['benchmarkSourceArchivePath']
    $archiveHashProperty = $Evidence.PSObject.Properties['benchmarkSourceArchiveSha256']
    if (($null -eq $archivePathProperty) -ne ($null -eq $archiveHashProperty)) {
        throw 'The OwnerConcurrent benchmark source archive path and SHA-256 must be supplied together.'
    }
    $liveBenchmarkMatches = $false
    Assert-NoReparseComponents -Path $benchmarkPath -Root $repositoryRoot
    if (Test-Path -LiteralPath $benchmarkPath -PathType Leaf) {
        $liveItem = Get-Item -LiteralPath $benchmarkPath
        $liveHash = (Get-FileHash -LiteralPath $benchmarkPath -Algorithm SHA256).Hash
        $liveBenchmarkMatches = ([long]$liveItem.Length -eq [long]$benchmarkRows[0].length -and $liveHash -ieq [string]$benchmarkRows[0].sha256)
    }
    if (-not $liveBenchmarkMatches -or $null -ne $archivePathProperty) {
        if ($null -eq $archivePathProperty) { throw 'The historical OwnerConcurrent benchmark input changed; its exact archived source must be pinned.' }
        $archivePath = Assert-LedgerExactPath -Actual ([string]$archivePathProperty.Value) -Expected $expectedBenchmarkArchive -Description 'OwnerConcurrent benchmark source archive path'
        Assert-NoReparseComponents -Path $archivePath -Root $repositoryRoot
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'The pinned OwnerConcurrent benchmark archive is missing.' }
        $archiveItem = Get-Item -LiteralPath $archivePath
        if ([long]$archiveItem.Length -ne [long]$benchmarkRows[0].length -or
            (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ine [string]$benchmarkRows[0].sha256 -or
            [string]$archiveHashProperty.Value -ine [string]$benchmarkRows[0].sha256) {
            throw 'The OwnerConcurrent benchmark archive does not reproduce the stage-pinned source hash and length.'
        }
    }

    $sourcehash = Get-Content -LiteralPath $sourcehashPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $sourcehash -PropertyNames @('schemaVersion', 'stage', 'phase', 'inputsBefore', 'inputsAfter', 'changed') -Description 'OwnerConcurrent sourcehash manifest'
    if ([int]$sourcehash.schemaVersion -ne 1 -or [string]$sourcehash.stage -cne 'OwnerConcurrent' -or
        [string]$sourcehash.phase -cne 'Correctness' -or $sourcehash.changed -ne $false -or
        @($sourcehash.inputsBefore).Count -ne @($before.sourceInputs).Count -or
        @($sourcehash.inputsAfter).Count -ne @($after.sourceInputs).Count) {
        throw 'OwnerConcurrent sourcehash manifest is incomplete or reports changed inputs.'
    }
    $sourceSnapshot = [pscustomobject]@{ InputsBefore = @($sourcehash.inputsBefore); InputsAfter = @($sourcehash.inputsAfter); ChangedInputCount = 0; ChangedInputs = @() }
    Assert-LedgerInputSnapshotStable -StageManifest $sourceSnapshot
    if ((ConvertTo-Json -InputObject @($sourcehash.inputsBefore) -Depth 8 -Compress) -cne
        (ConvertTo-Json -InputObject @($before.sourceInputs) -Depth 8 -Compress)) {
        throw 'OwnerConcurrent sourcehash pins differ from the stage source input snapshot.'
    }

    $artifact = Get-Content -LiteralPath $artifactPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $artifact -PropertyNames @(
        'schema', 'phase', 'passed', 'valuesRoot', 'planSha256', 'registrationSha256', 'compilerCacheSourceRoot', 'artifacts'
    ) -Description 'OwnerConcurrent artifact manifest'
    if ($artifact.schema -cne 'resource-manager/owner-concurrent-artifacts/v1' -or
        [string]$artifact.phase -cne 'Correctness' -or $artifact.passed -ne $true -or
        [string]$artifact.valuesRoot -ine $valuesRoot -or [string]$artifact.planSha256 -ine [string]$receipt.planSha256 -or
        [string]$artifact.registrationSha256 -ine [string]$receipt.registrationSha256 -or
        ($null -ne $artifact.compilerCacheSourceRoot -and -not [string]::IsNullOrWhiteSpace([string]$artifact.compilerCacheSourceRoot))) {
        throw 'OwnerConcurrent artifact manifest is not a stable successful self-owned correctness build.'
    }
    $artifacts = @($artifact.artifacts)
    if ($artifacts.Count -ne 2) { throw 'OwnerConcurrent artifact manifest must contain exactly the c2 and b2 correctness builds.' }
    $valuesModule = Join-Path $valuesRoot 'ledger_values.zig'
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $rootModule = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\owned_concurrent_driver.zig'
    $structureModule = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'
    foreach ($sourcePath in @($valuesModule, $rootModule, $structureModule, $benchmarkPath)) {
        $sourceRows = @($before.sourceInputs | Where-Object { [System.IO.Path]::GetFullPath([string]$_.path) -ieq [System.IO.Path]::GetFullPath($sourcePath) })
        if ($sourceRows.Count -ne 1 -or $sourceRows[0].exists -ne $true -or
            [long]$sourceRows[0].length -lt 0 -or [string]$sourceRows[0].sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "OwnerConcurrent compile input is not pinned by exactly one stable source-input row: $sourcePath"
        }
    }
    $cacheRoots = @{}
    $globalCacheRoots = @{}
    foreach ($candidate in @('c2', 'b2')) {
        $matching = @($artifacts | Where-Object { [string]$_.Candidate -ceq $candidate })
        if ($matching.Count -ne 1) { throw "OwnerConcurrent artifact manifest must contain exactly one $candidate build." }
        $build = $matching[0]
        Assert-LedgerObjectProperties -Object $build -PropertyNames @(
            'Candidate', 'Binary', 'BinarySha256', 'Zig', 'ZigSha256', 'Options', 'OptionsSha256',
            'ValuesRoot', 'CompilerCommand', 'Mode', 'Instrumented', 'Passed', 'CorrectnessCaseCount'
        ) -Description "OwnerConcurrent $candidate artifact"
        Assert-LedgerObjectProperties -Object $build.CompilerCommand -PropertyNames @(
            'name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'processId', 'launchFailure', 'stdoutPath', 'stderrPath', 'stdoutSha256', 'stderrSha256'
        ) -Description "OwnerConcurrent $candidate compiler command"
        if ($build.Passed -ne $true -or [string]$build.Mode -cne 'ReleaseFast' -or $build.Instrumented -ne $false -or
            [int]$build.CorrectnessCaseCount -ne 24 -or [string]$build.ValuesRoot -ine $valuesRoot) {
            throw "OwnerConcurrent $candidate artifact does not describe a successful uninstrumented 24-case correctness build."
        }
        $binaryPath = Assert-LedgerExactPath -Actual ([string]$build.Binary) -Expected (Join-Path $stageRoot ("bin\owned-concurrent-$candidate.exe")) -Description "OwnerConcurrent $candidate binary"
        $optionsPath = Assert-LedgerExactPath -Actual ([string]$build.Options) -Expected (Join-Path $stageRoot ("options\benchmark_options-$candidate.zig")) -Description "OwnerConcurrent $candidate options"
        $zigPath = [System.IO.Path]::GetFullPath([string]$build.Zig)
        if ([System.IO.Path]::GetFileName($zigPath) -cne 'zig.exe') { throw "OwnerConcurrent $candidate artifact compiler is not a Zig executable." }
        foreach ($fileSpec in @(
            @{ Path = $binaryPath; Length = $null; Hash = [string]$build.BinarySha256; Name = "OwnerConcurrent $candidate binary" },
            @{ Path = $optionsPath; Length = $null; Hash = [string]$build.OptionsSha256; Name = "OwnerConcurrent $candidate options" },
            @{ Path = $zigPath; Length = $null; Hash = [string]$build.ZigSha256; Name = "OwnerConcurrent $candidate Zig compiler" }
        )) {
            if (-not (Test-Path -LiteralPath $fileSpec.Path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $fileSpec.Path -Algorithm SHA256).Hash -ine $fileSpec.Hash) {
                throw "$($fileSpec.Name) is missing or has a hash that differs from the artifact manifest."
            }
        }
        $expectedOptionsText = "pub const candidate: enum { both, c2, b2 } = .$candidate;"
        $optionText = [IO.File]::ReadAllText($optionsPath).TrimEnd("`r", "`n")
        if ($optionText -cne $expectedOptionsText) { throw "OwnerConcurrent $candidate options module selects a different candidate." }

        $cacheRoot = Join-Path (Join-Path $stageRoot 'cache') $candidate
        $globalCacheRoot = Join-Path (Join-Path $stageRoot 'global-cache') $candidate
        $cacheRoots[$candidate] = $cacheRoot
        $globalCacheRoots[$candidate] = $globalCacheRoot
        $zigLibRoot = Join-Path ([System.IO.Path]::GetDirectoryName($zigPath)) 'lib'
        $expectedArguments = @(
            'build-exe', '-j1', '-fno-llvm', '-fno-lld', '-OReleaseFast', '--zig-lib-dir', $zigLibRoot,
            '--dep', 'ledger_values', '--dep', 'benchmark_options', ('-Mroot=' + $rootModule),
            '--dep', 'ledger_structure', ('-Mledger_values=' + $valuesModule),
            ('-Mledger_structure=' + $structureModule), ('-Mbenchmark_options=' + $optionsPath),
            '--cache-dir', $cacheRoot, '--global-cache-dir', $globalCacheRoot,
            ('-femit-bin=' + $binaryPath)
        )
        Assert-LedgerStringSequence -Actual @($build.CompilerCommand.arguments) -Expected $expectedArguments -Description "OwnerConcurrent $candidate compiler arguments"
        if ([string]$build.CompilerCommand.name -cne "compile-concurrent-$candidate" -or
            [string]$build.CompilerCommand.executable -ine $zigPath -or [int]$build.CompilerCommand.expectedExitCode -ne 0 -or
            [int]$build.CompilerCommand.exitCode -ne 0 -or [long]$build.CompilerCommand.processId -le 0 -or
            $null -ne $build.CompilerCommand.launchFailure) {
            throw "OwnerConcurrent $candidate compiler command did not complete successfully."
        }
        foreach ($spec in @(
            @{ Path = [string]$build.CompilerCommand.stdoutPath; Expected = (Join-Path $stageRoot "logs\compile-concurrent-$candidate.stdout"); Hash = [string]$build.CompilerCommand.stdoutSha256; Name = "OwnerConcurrent $candidate compiler stdout" },
            @{ Path = [string]$build.CompilerCommand.stderrPath; Expected = (Join-Path $stageRoot "logs\compile-concurrent-$candidate.stderr"); Hash = [string]$build.CompilerCommand.stderrSha256; Name = "OwnerConcurrent $candidate compiler stderr" }
        )) {
            $full = Assert-LedgerEvidenceFile -Path $spec.Path -ExpectedSha256 $spec.Hash -ValidationRoot $ValidationRoot -Description $spec.Name
            $null = Assert-LedgerExactPath -Actual $full -Expected $spec.Expected -Description $spec.Name
        }
    }
    if ([string]$artifact.CompilerCacheSourceRoot -and -not [string]::IsNullOrWhiteSpace([string]$artifact.CompilerCacheSourceRoot)) {
        throw 'OwnerConcurrent artifact manifest aliases another stage cache and cannot own a cleanup target.'
    }

    $artifactInputs = Get-Content -LiteralPath $artifactInputPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $artifactInputs -PropertyNames @('schemaVersion', 'stage', 'phase', 'artifactsBeforeExecution', 'artifactsAfterExecution', 'changed') -Description 'OwnerConcurrent artifact-input manifest'
    if ([int]$artifactInputs.schemaVersion -ne 1 -or [string]$artifactInputs.stage -cne 'OwnerConcurrent' -or
        [string]$artifactInputs.phase -cne 'Correctness' -or $artifactInputs.changed -ne $false) {
        throw 'OwnerConcurrent artifact-input manifest is not a stable successful build snapshot.'
    }
    $artifactSnapshot = [pscustomobject]@{
        InputsBefore = @($artifactInputs.artifactsBeforeExecution)
        InputsAfter = @($artifactInputs.artifactsAfterExecution)
        ChangedInputCount = 0
        ChangedInputs = @()
    }
    Assert-LedgerInputSnapshotStable -StageManifest $artifactSnapshot
    $expectedArtifactSnapshotPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in @('c2', 'b2')) {
        $null = $expectedArtifactSnapshotPaths.Add((Join-Path $stageRoot ("bin\owned-concurrent-$candidate.exe")))
        $null = $expectedArtifactSnapshotPaths.Add((Join-Path $stageRoot ("options\benchmark_options-$candidate.zig")))
    }
    $zigRows = @($artifacts | Select-Object -ExpandProperty Zig -Unique)
    if ($zigRows.Count -ne 1) { throw 'OwnerConcurrent artifacts do not use one compiler executable.' }
    $null = $expectedArtifactSnapshotPaths.Add([string]$zigRows[0])
    if (@($artifactInputs.artifactsBeforeExecution).Count -ne $expectedArtifactSnapshotPaths.Count) {
        throw 'OwnerConcurrent artifact-input manifest has an unexpected file set.'
    }
    $seenArtifactSnapshotPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in @($artifactInputs.artifactsBeforeExecution)) {
        $artifactSnapshotPath = [System.IO.Path]::GetFullPath([string]$row.path)
        if ($row.exists -ne $true -or -not $expectedArtifactSnapshotPaths.Contains([System.IO.Path]::GetFullPath([string]$row.path)) -or
            -not $seenArtifactSnapshotPaths.Add($artifactSnapshotPath) -or
            [long]$row.length -lt 0 -or [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw 'OwnerConcurrent artifact-input manifest includes an unexpected or incomplete artifact.'
        }
        if (-not (Test-Path -LiteralPath ([string]$row.path) -PathType Leaf) -or
            (Get-Item -LiteralPath ([string]$row.path)).Length -ne [long]$row.length -or
            (Get-FileHash -LiteralPath ([string]$row.path) -Algorithm SHA256).Hash -ine [string]$row.sha256) {
            throw 'OwnerConcurrent build artifact differs from its before/after input snapshot.'
        }
    }

    $boundedPath = $boundPaths.boundedExecutionReceiptPath
    $bounded = Get-Content -LiteralPath $boundedPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $bounded -PropertyNames @(
        'Passed', 'Stage', 'RunName', 'ExitCode', 'NormalDrain', 'EmptyJob', 'JobMemoryLimitBytes',
        'UnexpectedChangedInputCount', 'Failure', 'RunDirectory', 'Manifest', 'InputManifest', 'OutputRoot', 'ValuesRoot'
    ) -Description 'OwnerConcurrent bounded ExecutionJob receipt'
    if ($bounded.Passed -ne $true -or [string]$bounded.Stage -cne 'OwnerConcurrent' -or
        [string]$bounded.RunName -cne $runName -or [int]$bounded.ExitCode -ne 0 -or
        $bounded.NormalDrain -ne $true -or $bounded.EmptyJob -ne $true -or
        [long]$bounded.JobMemoryLimitBytes -ne 536870912 -or [int]$bounded.UnexpectedChangedInputCount -ne 0 -or
        ($null -ne $bounded.Failure -and -not [string]::IsNullOrWhiteSpace([string]$bounded.Failure))) {
        throw 'OwnerConcurrent bounded receipt does not prove a successful normal empty 512 MiB Job.'
    }
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.OutputRoot) -Expected $stageRoot -Description 'OwnerConcurrent bounded output root'
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.ValuesRoot) -Expected $valuesRoot -Description 'OwnerConcurrent bounded ValuesRoot'
    $jobManifestPath = $boundPaths['OwnerConcurrent ExecutionJob manifest']
    $jobInputPath = $boundPaths['OwnerConcurrent ExecutionJob input manifest']
    $jobMetadataRoot = [System.IO.Path]::GetDirectoryName($jobManifestPath)
    if ([System.IO.Path]::GetFileName($jobMetadataRoot) -cnotmatch ('^\.execution-OwnerConcurrent-' + [regex]::Escape($runName) + '-[A-Fa-f0-9]{32}$')) {
        throw 'OwnerConcurrent ExecutionJob metadata root does not match the exact run name.'
    }
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.Manifest) -Expected $jobManifestPath -Description 'OwnerConcurrent bounded manifest path'
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.InputManifest) -Expected $jobInputPath -Description 'OwnerConcurrent bounded input-manifest path'
    $jobReceiptPath = Assert-LedgerExactPath -Actual $boundPaths['OwnerConcurrent ExecutionJob event receipt'] `
        -Expected (Join-Path ([string]$bounded.RunDirectory) 'receipts.jsonl') -Description 'OwnerConcurrent ExecutionJob event receipt path'
    $runDirectory = [System.IO.Path]::GetFullPath([string]$bounded.RunDirectory).TrimEnd('\')
    Assert-NoReparseComponents -Path $runDirectory -Root $ValidationRoot
    $runInfo = [System.IO.DirectoryInfo]::new($runDirectory)
    $allowedReceiptRootInfo = $runInfo.Parent
    if ($null -eq $allowedReceiptRootInfo -or $allowedReceiptRootInfo.Name -cne 'job-receipts') {
        throw 'OwnerConcurrent ExecutionJob run directory is not below its job-receipts root.'
    }
    $jobManifest = Get-Content -LiteralPath $jobManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $jobManifest -PropertyNames @(
        'arguments', 'runDirectory', 'allowedReceiptRoot', 'jobMemoryLimitBytes', 'activeProcessLimit', 'cpuRate', 'timeoutSeconds'
    ) -Description 'OwnerConcurrent ExecutionJob manifest'
    $allowedReceiptRoot = [System.IO.Path]::GetFullPath($allowedReceiptRootInfo.FullName).TrimEnd('\')
    $jobMetadataInfo = $allowedReceiptRootInfo.Parent
    if ($null -eq $jobMetadataInfo -or $jobMetadataInfo.FullName -ine $jobMetadataRoot -or
        $jobMetadataInfo.Parent.FullName -ine $scratchRoot) {
        throw 'OwnerConcurrent ExecutionJob metadata is outside its exact scratch-root lifecycle directory.'
    }
    $null = Assert-LedgerExactPath -Actual ([string]$jobManifest.runDirectory) -Expected $runDirectory -Description 'OwnerConcurrent ExecutionJob run directory'
    $null = Assert-LedgerExactPath -Actual ([string]$jobManifest.allowedReceiptRoot) -Expected (Join-Path $jobMetadataRoot 'job-receipts') -Description 'OwnerConcurrent allowed receipt root'
    if ([long]$jobManifest.jobMemoryLimitBytes -ne 536870912 -or [int]$jobManifest.activeProcessLimit -ne 8 -or
        [int]$jobManifest.cpuRate -ne 2500 -or [int]$jobManifest.timeoutSeconds -ne 900) {
        throw 'OwnerConcurrent ExecutionJob limits differ from the bounded validation route.'
    }
    $expectedJobArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        (Join-Path $repositoryRoot '.local\ledger-owner-implementation\validation\Run-Bounded.ps1'),
        '-Stage', 'OwnerConcurrent', '-BatchRoot', $batchRoot, '-RunName', $runName,
        '-ExecutionPayload', '-PayloadOutputRoot', $stageRoot, '-PayloadMetadataRoot', $jobMetadataRoot,
        '-ValuesRoot', $valuesRoot, '-ConcurrentPhase', 'Correctness', '-ConcurrentCaseIndex', '0',
        '-ConcurrentSupportRoot', [string]$before.concurrentSupport.root,
        '-TimingSupportRoot', [string]$before.timingSupport.root
    )
    Assert-LedgerStringSequence -Actual @($jobManifest.arguments) -Expected $expectedJobArguments -Description 'OwnerConcurrent ExecutionJob wrapper arguments'
    $jobInput = Get-Content -LiteralPath $jobInputPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $jobInput -PropertyNames @(
        'SchemaVersion', 'Stage', 'RunName', 'BatchRoot', 'OutputRoot', 'ValuesRoot',
        'InputsBefore', 'InputsAfter', 'ChangedInputs', 'UnexpectedChangedInputs'
    ) -Description 'OwnerConcurrent ExecutionJob input manifest'
    if ([int]$jobInput.SchemaVersion -ne 1 -or [string]$jobInput.Stage -cne 'OwnerConcurrent' -or
        [string]$jobInput.RunName -cne $runName -or [string]$jobInput.BatchRoot -ine $batchRoot -or
        [string]$jobInput.OutputRoot -ine $stageRoot -or [string]$jobInput.ValuesRoot -ine $valuesRoot) {
        throw 'OwnerConcurrent ExecutionJob input manifest does not match its stage, batch, or values root.'
    }
    $jobInputSnapshot = [pscustomobject]@{
        InputsBefore = @($jobInput.InputsBefore)
        InputsAfter = @($jobInput.InputsAfter)
        ChangedInputs = @($jobInput.ChangedInputs)
        UnexpectedChangedInputs = @($jobInput.UnexpectedChangedInputs)
    }
    Assert-LedgerInputSnapshotStable -StageManifest $jobInputSnapshot -CountProperty '' `
        -ChangedArrayProperties @('ChangedInputs', 'UnexpectedChangedInputs')
    Assert-LedgerExecutionLifecycle -JobReceiptPath $jobReceiptPath -JobManifest $jobManifest `
        -RunDirectory $runDirectory -AllowedReceiptRoot $allowedReceiptRoot

    $commands = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content -LiteralPath $commandLogPath) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $commands.Add(($line | ConvertFrom-Json)) }
    }
    $cases = @(Get-Content -LiteralPath $caseResultsPath -Raw | ConvertFrom-Json)
    if ($commands.Count -ne 50 -or $cases.Count -ne 48) { throw 'OwnerConcurrent command log or case results differ from the required 50/48 shape.' }
    $commandIndex = 0
    foreach ($candidate in @('c2', 'b2')) {
        $build = @($artifacts | Where-Object { [string]$_.Candidate -ceq $candidate })[0]
        $compileRow = $commands[$commandIndex]
        Assert-LedgerStringSequence -Actual @($compileRow.arguments) -Expected @($build.CompilerCommand.arguments) -Description "OwnerConcurrent $candidate logged compile arguments"
        if ([string]$compileRow.name -cne "compile-concurrent-$candidate" -or
            [string]$compileRow.executable -ine [string]$build.Zig -or
            [int]$compileRow.expectedExitCode -ne 0 -or [int]$compileRow.exitCode -ne 0 -or
            [long]$compileRow.processId -le 0 -or $null -ne $compileRow.launchFailure -or
            [string]$compileRow.stdoutPath -ine [string]$build.CompilerCommand.stdoutPath -or
            [string]$compileRow.stdoutSha256 -ine [string]$build.CompilerCommand.stdoutSha256 -or
            [string]$compileRow.stderrPath -ine [string]$build.CompilerCommand.stderrPath -or
            [string]$compileRow.stderrSha256 -ine [string]$build.CompilerCommand.stderrSha256) {
            throw "OwnerConcurrent $candidate compile command log differs from its artifact manifest."
        }
        $commandIndex++
    }

    $scenarios = @('table_write_unrelated_use', 'full_audit_unrelated_use', 'capacity_scan_unrelated_use', 'release_unrelated_use', 'notification_unrelated_use', 'same_home_publication')
    $caseOffset = 0
    foreach ($candidate in @('c2', 'b2')) {
        $binaryPath = Join-Path $stageRoot "bin\owned-concurrent-$candidate.exe"
        $candidateCaseIndex = 0
        foreach ($scenario in $scenarios) {
            foreach ($uidBits in @(128, 256)) {
                foreach ($capacity in @(1024, 1048576)) {
                    $case = $cases[$caseOffset]
                    $command = $commands[$caseOffset + 2]
                    $caseName = "correctness-$candidate-u$uidBits-r$capacity-$scenario"
                    $expectedArguments = @('--scenario', $scenario, '--candidate', $candidate,
                        '--uid-bits', [string]$uidBits, '--capacity', [string]$capacity, '--mode', 'correctness')
                    Assert-LedgerObjectProperties -Object $case -PropertyNames @(
                        'candidate', 'caseIndex', 'scenario', 'uidWidthBits', 'capacity', 'exitCode', 'passed',
                        'validationFailure', 'process', 'resultStorage', 'resultPath', 'resultSha256'
                    ) -Description 'OwnerConcurrent case result row'
                    Assert-LedgerObjectProperties -Object $case.process -PropertyNames @(
                        'name', 'executable', 'arguments', 'expectedExitCode', 'exitCode', 'processId', 'launchFailure',
                        'stdoutStorage', 'stdoutPath', 'stdoutSha256', 'stdoutDecodedSha256', 'stdoutDecodedLength', 'stderrPath', 'stderrSha256'
                    ) -Description 'OwnerConcurrent case process record'
                    if ([string]$case.candidate -cne $candidate -or [int]$case.caseIndex -ne ($candidateCaseIndex + 1) -or
                        [string]$case.scenario -cne $scenario -or [int]$case.uidWidthBits -ne $uidBits -or
                        [long]$case.capacity -ne $capacity -or [int]$case.exitCode -ne 0 -or $case.passed -ne $true -or
                        ($null -ne $case.validationFailure -and -not [string]::IsNullOrWhiteSpace([string]$case.validationFailure)) -or
                        [string]$case.resultStorage -cne 'hash-pinned-stdout-json') {
                        throw "OwnerConcurrent case result did not pass for $caseName."
                    }
                    $null = Assert-LedgerExactPath -Actual ([string]$case.process.executable) -Expected $binaryPath -Description 'OwnerConcurrent case executable'
                    Assert-LedgerStringSequence -Actual @($case.process.arguments) -Expected $expectedArguments -Description "OwnerConcurrent case arguments '$caseName'"
                    if ([string]$case.process.name -cne $caseName -or [int]$case.process.expectedExitCode -ne 0 -or
                        [int]$case.process.exitCode -ne 0 -or [long]$case.process.processId -le 0 -or
                        $null -ne $case.process.launchFailure) {
                        throw "OwnerConcurrent runtime process did not complete successfully for $caseName."
                    }
                    if ([string]$command.name -cne $caseName -or [string]$command.executable -ine $binaryPath -or
                        [int]$command.expectedExitCode -ne 0 -or [int]$command.exitCode -ne 0 -or
                        [long]$command.processId -le 0 -or $null -ne $command.launchFailure) {
                        throw "OwnerConcurrent command log did not pass for $caseName."
                    }
                    Assert-LedgerStringSequence -Actual @($command.arguments) -Expected $expectedArguments -Description "OwnerConcurrent logged case arguments '$caseName'"
                    $stdoutExpected = Join-Path $stageRoot "raw\$caseName.stdout.gz"
                    $stderrExpected = Join-Path $stageRoot "raw\$caseName.stderr"
                    $stdoutPath = Assert-LedgerExactPath -Actual ([string]$case.process.stdoutPath) -Expected $stdoutExpected -Description 'OwnerConcurrent raw stdout path'
                    $stderrPath = Assert-LedgerExactPath -Actual ([string]$case.process.stderrPath) -Expected $stderrExpected -Description 'OwnerConcurrent raw stderr path'
                    $null = Assert-LedgerExactPath -Actual ([string]$command.stdoutPath) -Expected $stdoutPath -Description 'OwnerConcurrent command stdout path'
                    $null = Assert-LedgerExactPath -Actual ([string]$command.stderrPath) -Expected $stderrPath -Description 'OwnerConcurrent command stderr path'
                    if ([string]$case.process.stdoutStorage -cne 'gzip-utf8' -or [string]$command.stdoutStorage -cne 'gzip-utf8' -or
                        [string]$case.resultPath -ine $stdoutPath -or [string]$case.resultSha256 -ine [string]$case.process.stdoutSha256 -or
                        [string]$command.stdoutSha256 -ine [string]$case.process.stdoutSha256 -or
                        [string]$command.stderrSha256 -ine [string]$case.process.stderrSha256 -or
                        [string]$case.process.stdoutDecodedSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
                        [long]$case.process.stdoutDecodedLength -le 0) {
                        throw "OwnerConcurrent raw-output hashes or storage metadata do not match for $caseName."
                    }
                    $null = Assert-LedgerEvidenceFile -Path $stdoutPath -ExpectedSha256 ([string]$case.process.stdoutSha256) `
                        -ValidationRoot $ValidationRoot -Description "OwnerConcurrent $caseName stdout"
                    $null = Assert-LedgerEvidenceFile -Path $stderrPath -ExpectedSha256 ([string]$case.process.stderrSha256) `
                        -ValidationRoot $ValidationRoot -Description "OwnerConcurrent $caseName stderr"
                    $candidateCaseIndex++
                    $caseOffset++
                }
            }
        }
    }

    [pscustomobject]@{
        ProofProfile = 'owner-concurrent-correctness'
        NativeProbe = $null
        RunName = $runName
        StageName = $stageName
        StageRoot = $stageRoot
        CacheOwnerStageName = $stageName
        ReceiptPath = $receiptPath
        ReceiptSha256 = [string]$Evidence.receiptSha256
        BoundedExecutionReceiptPath = $boundPaths.boundedExecutionReceiptPath
        BoundedExecutionReceiptSha256 = [string]$Evidence.boundedExecutionReceiptSha256
        StageManifestPath = $beforePath
        StageManifestSha256 = [string]$Evidence.inputManifestBeforeSha256
        ExecutionJobInputManifestPath = $jobInputPath
        ExecutionJobInputManifestSha256 = [string]$Evidence.executionJobInputManifestSha256
        ExecutionJobManifestPath = $jobManifestPath
        ExecutionJobManifestSha256 = [string]$Evidence.executionJobManifestSha256
        JobReceiptPath = $jobReceiptPath
        JobReceiptSha256 = [string]$Evidence.jobReceiptSha256
        CacheRoots = $cacheRoots
        GlobalCacheRoots = $globalCacheRoots
        InputCount = [int](@($before.sourceInputs).Count + @($before.executionInputs).Count)
    }
}

function Get-LedgerFacadeEvidenceFile {
    param(
        [Parameter(Mandatory)][object]$EvidenceSet,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ExpectedPath,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][string]$Description
    )
    $property = $EvidenceSet.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "$Description is missing its evidence record '$Name'." }
    Assert-LedgerObjectProperties -Object $property.Value -PropertyNames @('path', 'sha256') -Description "$Description evidence record '$Name'"
    $path = Assert-LedgerEvidenceFile -Path ([string]$property.Value.path) `
        -ExpectedSha256 ([string]$property.Value.sha256) -ValidationRoot $ValidationRoot -Description $Description
    Assert-LedgerExactPath -Actual $path -Expected $ExpectedPath -Description "$Description path"
}

function Assert-LedgerFacadeInputManifest {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedStage,
        [Parameter(Mandatory)][string]$ExpectedRunName,
        [Parameter(Mandatory)][string]$BatchRoot,
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$ValuesRoot,
        [switch]$AllowEmptyValuesRoot,
        [Parameter(Mandatory)][string]$Description
    )
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $manifest -PropertyNames @(
        'SchemaVersion', 'Stage', 'RunName', 'BatchRoot', 'OutputRoot', 'ValuesRoot',
        'InputsBefore', 'InputsAfter', 'ChangedInputs', 'UnexpectedChangedInputs'
    ) -Description $Description
    $valuesRootMatches = if ($AllowEmptyValuesRoot) {
        [string]$manifest.ValuesRoot -ceq ''
    }
    else {
        [System.IO.Path]::GetFullPath([string]$manifest.ValuesRoot) -ieq $ValuesRoot
    }
    if ([int]$manifest.SchemaVersion -ne 1 -or [string]$manifest.Stage -cne $ExpectedStage -or
        [string]$manifest.RunName -cne $ExpectedRunName -or
        [System.IO.Path]::GetFullPath([string]$manifest.BatchRoot) -ine $BatchRoot -or
        [System.IO.Path]::GetFullPath([string]$manifest.OutputRoot) -ine $OutputRoot -or
        -not $valuesRootMatches) {
        throw "$Description does not match its exact stage, run, batch, output, or values root."
    }
    Assert-LedgerInputSnapshotStable -StageManifest $manifest -CountProperty '' `
        -ChangedArrayProperties @('ChangedInputs', 'UnexpectedChangedInputs')
    $manifest
}

function Assert-LedgerFacadeManifestInput {
    param(
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [string]$ExpectedSha256
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rows = @($Manifest.InputsBefore | Where-Object {
        [System.IO.Path]::GetFullPath([string]$_.path) -ieq $fullPath
    })
    if ($rows.Count -lt 1) {
        throw "$Description is not present as an input in the stable stage manifest."
    }
    $firstRow = $null
    foreach ($row in $rows) {
        if ($row.exists -ne $true -or [long]$row.length -lt 0 -or
            [string]$row.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "$Description is not present as an existing, hashed input in the stable stage manifest."
        }
        if ($null -eq $firstRow) {
            $firstRow = $row
        }
        elseif ([long]$row.length -ne [long]$firstRow.length -or
            [string]$row.sha256 -ine [string]$firstRow.sha256) {
            throw "$Description has duplicate stable-manifest entries with different length or hash values."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and [string]$firstRow.sha256 -ine $ExpectedSha256) {
        throw "$Description input hash differs from the bound artifact."
    }
}

function Assert-LedgerFacadeExecutionJob {
    param(
        [Parameter(Mandatory)][object]$EvidenceSet,
        [Parameter(Mandatory)][string]$BoundedReceiptPath,
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$RunName,
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][string]$BatchRoot,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][string]$ValuesRoot,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][int]$UidBits,
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][long]$ExpectedJobMemoryLimitBytes,
        [switch]$AllowEmptyValuesRoot,
        [string]$NativeFacadeRoot
    )
    $bounded = Get-Content -LiteralPath $BoundedReceiptPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $bounded -PropertyNames @(
        'Passed', 'Stage', 'RunName', 'ExitCode', 'NormalDrain', 'EmptyJob', 'JobMemoryLimitBytes',
        'Failure', 'UnexpectedChangedInputCount', 'RunDirectory', 'Manifest', 'ControllerLog',
        'InputManifest', 'OutputRoot', 'ValuesRoot'
    ) -Description "$Stage bounded ExecutionJob receipt"
    if ($bounded.Passed -ne $true -or [string]$bounded.Stage -cne $Stage -or
        [string]$bounded.RunName -cne $RunName -or [int]$bounded.ExitCode -ne 0 -or
        $bounded.NormalDrain -ne $true -or $bounded.EmptyJob -ne $true -or
        [long]$bounded.JobMemoryLimitBytes -ne $ExpectedJobMemoryLimitBytes -or
        [int]$bounded.UnexpectedChangedInputCount -ne 0 -or
        ($null -ne $bounded.Failure -and -not [string]::IsNullOrWhiteSpace([string]$bounded.Failure))) {
        throw "$Stage bounded ExecutionJob receipt does not prove successful normal drain of an empty Job."
    }
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.OutputRoot) -Expected $StageRoot -Description "$Stage bounded output root"
    if ($AllowEmptyValuesRoot) {
        if ([string]$bounded.ValuesRoot -cne '') { throw "$Stage bounded ValuesRoot must be empty for this recorded legacy consumer profile." }
    }
    else {
        $null = Assert-LedgerExactPath -Actual ([string]$bounded.ValuesRoot) -Expected $ValuesRoot -Description "$Stage bounded ValuesRoot"
    }

    $metadataRoot = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath([string]$bounded.Manifest))
    $metadataName = [System.IO.Path]::GetFileName($metadataRoot)
    if ([System.IO.Path]::GetDirectoryName($metadataRoot) -ine (Join-Path $BatchRoot 'scratch') -or
        $metadataName -cnotmatch ('^\.execution-' + [regex]::Escape($Stage) + '-' + [regex]::Escape($RunName) + '-[A-Fa-f0-9]{32}$')) {
        throw "$Stage ExecutionJob metadata is not below the exact batch scratch root."
    }
    Assert-NoReparseComponents -Path $metadataRoot -Root $ValidationRoot
    $executionJobManifestPath = Join-Path $metadataRoot 'execution-job.json'
    $executionJobInputManifestPath = Join-Path $metadataRoot 'input-manifest.json'
    $controllerLogPath = Join-Path $metadataRoot 'execution-job.controller.log'
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.Manifest) -Expected $executionJobManifestPath -Description "$Stage bounded Job manifest"
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.InputManifest) -Expected $executionJobInputManifestPath -Description "$Stage bounded Job input manifest"
    $null = Assert-LedgerExactPath -Actual ([string]$bounded.ControllerLog) -Expected $controllerLogPath -Description "$Stage bounded Job controller log"
    $executionJobManifestPath = Get-LedgerFacadeEvidenceFile -EvidenceSet $EvidenceSet -Name 'jobManifest' `
        -ExpectedPath $executionJobManifestPath -ValidationRoot $ValidationRoot `
        -Description "$Stage ExecutionJob manifest"
    $executionJobInputManifestPath = Get-LedgerFacadeEvidenceFile -EvidenceSet $EvidenceSet -Name 'jobInputManifest' `
        -ExpectedPath $executionJobInputManifestPath -ValidationRoot $ValidationRoot `
        -Description "$Stage ExecutionJob input manifest"
    $controllerLogPath = Get-LedgerFacadeEvidenceFile -EvidenceSet $EvidenceSet -Name 'controllerLog' `
        -ExpectedPath $controllerLogPath -ValidationRoot $ValidationRoot `
        -Description "$Stage ExecutionJob controller log"

    $runDirectory = [System.IO.Path]::GetFullPath([string]$bounded.RunDirectory).TrimEnd('\')
    $allowedReceiptRoot = Join-Path $metadataRoot 'job-receipts'
    if ([System.IO.Path]::GetDirectoryName($runDirectory) -ine $allowedReceiptRoot -or
        [System.IO.Path]::GetFileName($runDirectory) -cnotmatch ('^ledger-owner-' + $Stage.ToLowerInvariant() + '-' + [regex]::Escape($RunName) + '-[A-Fa-f0-9]{32}$')) {
        throw "$Stage ExecutionJob run directory is not its exact direct child below job-receipts."
    }
    Assert-NoReparseComponents -Path $runDirectory -Root $ValidationRoot
    if (-not (Test-Path -LiteralPath $runDirectory -PathType Container)) { throw "$Stage ExecutionJob run directory is missing." }
    $jobReceiptPath = Get-LedgerFacadeEvidenceFile -EvidenceSet $EvidenceSet -Name 'jobReceipt' `
        -ExpectedPath (Join-Path $runDirectory 'receipts.jsonl') -ValidationRoot $ValidationRoot `
        -Description "$Stage ExecutionJob lifecycle receipt"

    $jobManifest = Get-Content -LiteralPath $executionJobManifestPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $jobManifest -PropertyNames @(
        'allowedReceiptRoot', 'runDirectory', 'executable', 'arguments', 'workingDirectory',
        'jobMemoryLimitBytes', 'activeProcessLimit', 'cpuRate', 'commitReserveBytes',
        'physicalReserveBytes', 'timeoutSeconds'
    ) -Description "$Stage ExecutionJob manifest"
    if ([System.IO.Path]::GetFullPath([string]$jobManifest.allowedReceiptRoot) -ine $allowedReceiptRoot -or
        [System.IO.Path]::GetFullPath([string]$jobManifest.runDirectory) -ine $runDirectory -or
        [string]$jobManifest.executable -notmatch '[\\/]pwsh\.exe$' -or
        [System.IO.Path]::GetFullPath([string]$jobManifest.workingDirectory) -ine $RepositoryRoot -or
        [long]$jobManifest.jobMemoryLimitBytes -ne $ExpectedJobMemoryLimitBytes -or
        [int]$jobManifest.activeProcessLimit -ne 8 -or [int]$jobManifest.cpuRate -ne 2500 -or
        [long]$jobManifest.commitReserveBytes -ne 4294967296 -or
        [long]$jobManifest.physicalReserveBytes -ne 2147483648 -or [int]$jobManifest.timeoutSeconds -ne 900) {
        throw "$Stage ExecutionJob manifest differs from the registered bounded route."
    }
    $runnerPath = Join-Path $RepositoryRoot '.local\ledger-owner-implementation\validation\Run-Bounded.ps1'
    $expectedArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runnerPath,
        '-Stage', $Stage, '-BatchRoot', $BatchRoot, '-RunName', $RunName, '-ExecutionPayload',
        '-PayloadOutputRoot', $StageRoot, '-PayloadMetadataRoot', $metadataRoot
    )
    if ($Stage -ceq 'NativeFacade') {
        $expectedArguments += @('-ValuesRoot', $ValuesRoot, '-FacadeCandidate', $Candidate, '-FacadeUidBits', [string]$UidBits)
    }
    else {
        $expectedArguments += @('-FacadeCandidate', $Candidate, '-FacadeUidBits', [string]$UidBits,
            '-NativeFacadeRoot', $NativeFacadeRoot)
    }
    Assert-LedgerStringSequence -Actual @($jobManifest.arguments) -Expected $expectedArguments -Description "$Stage ExecutionJob payload arguments"

    $jobInputManifest = Assert-LedgerFacadeInputManifest -Path $executionJobInputManifestPath `
        -ExpectedStage $Stage -ExpectedRunName $RunName -BatchRoot $BatchRoot -OutputRoot $StageRoot `
        -ValuesRoot $ValuesRoot -AllowEmptyValuesRoot:$AllowEmptyValuesRoot `
        -Description "$Stage ExecutionJob input manifest"
    Assert-LedgerExecutionLifecycle -JobReceiptPath $jobReceiptPath -JobManifest $jobManifest `
        -RunDirectory $runDirectory -AllowedReceiptRoot $allowedReceiptRoot
    [pscustomobject]@{
        Bounded = $bounded
        MetadataRoot = $metadataRoot
        ManifestPath = $executionJobManifestPath
        InputManifestPath = $executionJobInputManifestPath
        InputManifest = $jobInputManifest
        JobReceiptPath = $jobReceiptPath
        ControllerLogPath = $controllerLogPath
    }
}

function Get-NativeFacadeConsumedEvidence {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$BatchId,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][ValidateSet('native-facade-consumed', 'native-facade-consumed-13')][string]$ProofProfile
    )
    if ($BatchId -cne 'ledger-owner-a1') { throw 'native-facade-consumed evidence is scoped to ledger-owner-a1.' }
    Assert-LedgerObjectProperties -Object $Evidence -PropertyNames @('producerRunName', 'managedRunName', 'producer', 'managed') `
        -Description 'NativeFacade/ManagedProbe prune evidence'
    $producerRunName = [string]$Evidence.producerRunName
    $managedRunName = [string]$Evidence.managedRunName
    $candidate = $null
    $uidBits = 0
    $expectedManagedRunName = $null
    if ($ProofProfile -ceq 'native-facade-consumed-13') {
        switch -CaseSensitive ($producerRunName) {
            'b2-128-a2' { $candidate = 'b2'; $uidBits = 128; $expectedManagedRunName = 'b2-128-a1' }
            'b2-256-a2' { $candidate = 'b2'; $uidBits = 256; $expectedManagedRunName = 'b2-256-a1' }
            'c2-256-a2' { $candidate = 'c2'; $uidBits = 256; $expectedManagedRunName = 'c2-256-a1' }
            default { throw 'The historical 13-case profile accepts only its three explicit NativeFacade/ManagedProbe pairs.' }
        }
    }
    else {
        switch -CaseSensitive ($producerRunName) {
            'b2-128-a3' { $candidate = 'b2'; $uidBits = 128; $expectedManagedRunName = 'b2-128-a2' }
            'b2-256-a3' { $candidate = 'b2'; $uidBits = 256; $expectedManagedRunName = 'b2-256-a2' }
            'c2-256-a3' { $candidate = 'c2'; $uidBits = 256; $expectedManagedRunName = 'c2-256-a2' }
            default { throw 'The current 14-case profile accepts only its three explicit NativeFacade/ManagedProbe pairs.' }
        }
    }
    if ($managedRunName -cne $expectedManagedRunName) { throw 'ManagedProbe run does not match its exact NativeFacade producer pair.' }
    $producerStageName = 'NativeFacade-' + $producerRunName
    $managedStageName = 'ManagedProbe-' + $managedRunName
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $batchRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $ValidationRoot 'batches') $BatchId)).TrimEnd('\')
    $scratchRoot = Join-Path $batchRoot 'scratch'
    $producerRoot = Join-Path $scratchRoot $producerStageName
    $managedRoot = Join-Path $scratchRoot $managedStageName
    foreach ($stageRoot in @($producerRoot, $managedRoot)) {
        Assert-NoReparseComponents -Path $stageRoot -Root $ValidationRoot
        if (-not (Test-Path -LiteralPath $stageRoot -PathType Container)) { throw "Evidence stage root is missing: $stageRoot" }
    }
    $valuesProfile = if ($uidBits -eq 128) { 'default128-two-table' } else { 'override256-two-table-cross-method-order' }
    $legacyCompilerCommandProfile = $ProofProfile -ceq 'native-facade-consumed-13'
    $legacyManagedEmptyValuesRootProfile = $ProofProfile -ceq 'native-facade-consumed-13'
    $valuesRoot = Join-Path (Join-Path $scratchRoot 'Configuration-config-a6\values') $valuesProfile
    Assert-NoReparseComponents -Path $valuesRoot -Root $ValidationRoot
    if (-not (Test-Path -LiteralPath $valuesRoot -PathType Container)) { throw 'The NativeFacade ValuesRoot is missing.' }

    $producerEvidence = $Evidence.producer
    $managedEvidence = $Evidence.managed
    $producerFiles = @{}
    foreach ($spec in @(
        @{ Name = 'receipt'; File = 'receipt.json' },
        @{ Name = 'boundedReceipt'; File = 'bounded-execution-receipt.json' },
        @{ Name = 'stageManifest'; File = 'input-manifest.json' },
        @{ Name = 'artifactManifest'; File = 'artifact-manifest.json' },
        @{ Name = 'commandLog'; File = 'commands.jsonl' },
        @{ Name = 'compilerLog'; File = 'logs\compile-native-facade.log' }
    )) {
        $producerFiles[$spec.Name] = Get-LedgerFacadeEvidenceFile -EvidenceSet $producerEvidence -Name $spec.Name `
            -ExpectedPath (Join-Path $producerRoot $spec.File) -ValidationRoot $ValidationRoot `
            -Description "NativeFacade $producerRunName $($spec.Name)"
    }
    $producerReceipt = Get-Content -LiteralPath $producerFiles.receipt -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $producerReceipt -PropertyNames @('Passed', 'Stage', 'Candidate', 'UidBits', 'Failure', 'OutputRoot', 'Artifact') `
        -Description 'NativeFacade producer receipt'
    if ($producerReceipt.Passed -ne $true -or [string]$producerReceipt.Stage -cne 'NativeFacade' -or
        [string]$producerReceipt.Candidate -cne $candidate -or [int]$producerReceipt.UidBits -ne $uidBits -or
        [System.IO.Path]::GetFullPath([string]$producerReceipt.OutputRoot) -ine $producerRoot -or
        ($null -ne $producerReceipt.Failure -and -not [string]::IsNullOrWhiteSpace([string]$producerReceipt.Failure))) {
        throw 'NativeFacade producer receipt does not prove the selected successful profile.'
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$producerReceipt.NativeFacadeRoot)) {
        throw 'NativeFacade producer receipt must not claim to consume another NativeFacade root.'
    }
    $producerManifest = Get-Content -LiteralPath $producerFiles.artifactManifest -Raw | ConvertFrom-Json
    $producerArtifact = $producerReceipt.Artifact
    $producerArtifactProperties = @(
        'Candidate', 'UidBits', 'Dll', 'DllSha256', 'ValuesRoot', 'Options', 'OptionsSha256', 'Zig', 'ZigSha256'
    )
    $producerManifestProperties = @($producerArtifactProperties)
    if (-not $legacyCompilerCommandProfile) {
        $producerArtifactProperties += 'CompilerCommand'
        $producerManifestProperties += 'CompilerCommand'
    }
    Assert-LedgerObjectProperties -Object $producerArtifact -PropertyNames $producerArtifactProperties `
        -Description 'NativeFacade producer artifact'
    Assert-LedgerObjectProperties -Object $producerManifest -PropertyNames $producerManifestProperties `
        -Description 'NativeFacade artifact manifest'
    if ($legacyCompilerCommandProfile -and (
        $null -ne $producerArtifact.PSObject.Properties['CompilerCommand'] -or
        $null -ne $producerManifest.PSObject.Properties['CompilerCommand'])) {
        throw 'The selected early NativeFacade profile has an unexpected compiler-command artifact field.'
    }
    foreach ($field in @('Candidate', 'Dll', 'DllSha256', 'ValuesRoot', 'Options', 'OptionsSha256', 'Zig', 'ZigSha256')) {
        if ([string]$producerManifest.$field -cne [string]$producerArtifact.$field) {
            throw "NativeFacade artifact manifest differs from its receipt at '$field'."
        }
    }
    if ([int]$producerManifest.UidBits -ne $uidBits -or [int]$producerArtifact.UidBits -ne $uidBits -or
        [string]$producerManifest.Candidate -cne $candidate -or
        [System.IO.Path]::GetFullPath([string]$producerArtifact.ValuesRoot) -ine $valuesRoot) {
        throw 'NativeFacade artifact identity or ValuesRoot differs from its selected profile.'
    }
    $dllPath = Join-Path $producerRoot ("bin\owned-probe-{0}-{1}.dll" -f $candidate, $uidBits)
    $optionsPath = Join-Path $producerRoot 'comparison_options.zig'
    $null = Assert-LedgerExactPath -Actual ([string]$producerArtifact.Dll) -Expected $dllPath -Description 'NativeFacade DLL path'
    $null = Assert-LedgerExactPath -Actual ([string]$producerArtifact.Options) -Expected $optionsPath -Description 'NativeFacade options path'
    if (-not $legacyCompilerCommandProfile) {
        $null = Assert-LedgerExactPath -Actual ([string]$producerArtifact.CompilerCommand.executable) `
            -Expected ([string]$producerArtifact.Zig) -Description 'NativeFacade compiler executable'
    }
    if ((Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash -ine [string]$producerArtifact.DllSha256 -or
        (Get-FileHash -LiteralPath $optionsPath -Algorithm SHA256).Hash -ine [string]$producerArtifact.OptionsSha256 -or
        (Get-FileHash -LiteralPath ([string]$producerArtifact.Zig) -Algorithm SHA256).Hash -ine [string]$producerArtifact.ZigSha256) {
        throw 'NativeFacade DLL, options, or Zig compiler hash differs from the producer artifact.'
    }
    $expectedOptions = "pub const candidate: enum { c2, b2 } = .$candidate;`npub const uid_width_bits: u16 = $uidBits;`n"
    if ([System.IO.File]::ReadAllText($optionsPath) -cne $expectedOptions) { throw 'NativeFacade options file does not match the selected candidate and UID width.' }

    $producerStageManifestPath = $producerFiles.stageManifest
    $producerStageManifest = Assert-LedgerFacadeInputManifest -Path $producerStageManifestPath `
        -ExpectedStage 'NativeFacade' -ExpectedRunName $producerRunName -BatchRoot $batchRoot `
        -OutputRoot $producerRoot -ValuesRoot $valuesRoot -Description 'NativeFacade input manifest'
    $nativeRootModule = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\owned_native_probe.zig'
    $structureModule = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'
    $valuesModule = Join-Path $valuesRoot 'ledger_values.zig'
    Assert-LedgerFacadeManifestInput -Manifest $producerStageManifest -Path $nativeRootModule -Description 'NativeFacade root module'
    Assert-LedgerFacadeManifestInput -Manifest $producerStageManifest -Path $structureModule -Description 'NativeFacade structure module'
    Assert-LedgerFacadeManifestInput -Manifest $producerStageManifest -Path $valuesModule -Description 'NativeFacade values module'
    $producerCacheRoot = Join-Path $producerRoot 'cache'
    $producerGlobalCacheRoot = Join-Path $producerRoot 'global-cache'
    $zigLibraryRoot = Join-Path ([System.IO.Path]::GetDirectoryName([string]$producerArtifact.Zig)) 'lib'
    $expectedCompilerArguments = @(
        'build-lib', '-dynamic', '-j1', '-fllvm', '-flld', '-fcompiler-rt', '-OReleaseSafe',
        '--zig-lib-dir', $zigLibraryRoot,
        '--dep', 'ledger_values', '--dep', 'ledger_structure', '--dep', 'comparison_options',
        ('-Mroot=' + $nativeRootModule), '--dep', 'ledger_structure',
        ('-Mledger_values=' + $valuesModule), ('-Mledger_structure=' + $structureModule),
        ('-Mcomparison_options=' + $optionsPath), '--cache-dir', $producerCacheRoot,
        '--global-cache-dir', $producerGlobalCacheRoot, ('-femit-bin=' + $dllPath)
    )
    $producerCommands = @(Get-Content -LiteralPath $producerFiles.commandLog | ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace($_)) { $_ | ConvertFrom-Json }
    })
    if ($legacyCompilerCommandProfile) {
        if ($producerCommands.Count -ne 1) {
            throw 'The early NativeFacade command log must contain exactly its successful compiler invocation.'
        }
        $compilerCommand = $producerCommands[0]
    }
    else {
        $compilerCommand = $producerArtifact.CompilerCommand
    }
    Assert-LedgerObjectProperties -Object $compilerCommand -PropertyNames @('name', 'executable', 'arguments', 'exitCode', 'output') `
        -Description 'NativeFacade compile command'
    $null = Assert-LedgerExactPath -Actual ([string]$compilerCommand.executable) `
        -Expected ([string]$producerArtifact.Zig) -Description 'NativeFacade compiler executable'
    if ([string]$compilerCommand.name -cne 'compile-native-facade' -or [int]$compilerCommand.exitCode -ne 0 -or
        [System.IO.Path]::GetFullPath([string]$compilerCommand.output) -ine (Join-Path $producerRoot 'logs\compile-native-facade.log')) {
        throw 'NativeFacade compile command is not the recorded successful compiler invocation.'
    }
    Assert-LedgerStringSequence -Actual @($compilerCommand.arguments) -Expected $expectedCompilerArguments -Description 'NativeFacade compile arguments'
    if (-not $legacyCompilerCommandProfile) {
        Assert-LedgerStringSequence -Actual @($producerManifest.CompilerCommand.arguments) -Expected $expectedCompilerArguments `
            -Description 'NativeFacade artifact compile arguments'
    }
    if ($producerCommands.Count -ne 1 -or [string]$producerCommands[0].name -cne 'compile-native-facade' -or
        [string]$producerCommands[0].executable -ine [string]$compilerCommand.executable -or
        [int]$producerCommands[0].exitCode -ne 0 -or
        [System.IO.Path]::GetFullPath([string]$producerCommands[0].output) -ine [System.IO.Path]::GetFullPath([string]$compilerCommand.output)) {
        throw 'NativeFacade command log does not contain exactly its successful compiler invocation.'
    }
    Assert-LedgerStringSequence -Actual @($producerCommands[0].arguments) -Expected $expectedCompilerArguments -Description 'NativeFacade logged compile arguments'

    $producerJob = Assert-LedgerFacadeExecutionJob -EvidenceSet $producerEvidence `
        -BoundedReceiptPath $producerFiles.boundedReceipt -Stage 'NativeFacade' -RunName $producerRunName `
        -StageRoot $producerRoot -BatchRoot $batchRoot -ValidationRoot $ValidationRoot -ValuesRoot $valuesRoot -RepositoryRoot $repositoryRoot `
        -UidBits $uidBits -Candidate $candidate -ExpectedJobMemoryLimitBytes 536870912

    $managedFiles = @{}
    foreach ($spec in @(
        @{ Name = 'receipt'; File = 'receipt.json' },
        @{ Name = 'boundedReceipt'; File = 'bounded-execution-receipt.json' },
        @{ Name = 'stageManifest'; File = 'input-manifest.json' },
        @{ Name = 'artifactManifest'; File = 'artifact-manifest.json' },
        @{ Name = 'commandLog'; File = 'commands.jsonl' },
        @{ Name = 'buildLog'; File = 'logs\build-managed-probe.log' },
        @{ Name = 'runLog'; File = 'logs\run-managed-probe.log' },
        @{ Name = 'consumerResult'; File = 'consumer-result.json' },
        @{ Name = 'verifiedNativeInputs'; File = 'verified-native-inputs.json' }
    )) {
        $managedFiles[$spec.Name] = Get-LedgerFacadeEvidenceFile -EvidenceSet $managedEvidence -Name $spec.Name `
            -ExpectedPath (Join-Path $managedRoot $spec.File) -ValidationRoot $ValidationRoot `
            -Description "ManagedProbe $managedRunName $($spec.Name)"
    }
    $managedReceipt = Get-Content -LiteralPath $managedFiles.receipt -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $managedReceipt -PropertyNames @('Passed', 'Stage', 'Candidate', 'UidBits', 'Failure', 'OutputRoot', 'NativeFacadeRoot', 'Artifact') `
        -Description 'ManagedProbe consumer receipt'
    if ($managedReceipt.Passed -ne $true -or [string]$managedReceipt.Stage -cne 'ManagedProbe' -or
        [string]$managedReceipt.Candidate -cne $candidate -or [int]$managedReceipt.UidBits -ne $uidBits -or
        [System.IO.Path]::GetFullPath([string]$managedReceipt.OutputRoot) -ine $managedRoot -or
        [System.IO.Path]::GetFullPath([string]$managedReceipt.NativeFacadeRoot) -ine $producerRoot -or
        ($null -ne $managedReceipt.Failure -and -not [string]::IsNullOrWhiteSpace([string]$managedReceipt.Failure))) {
        throw 'ManagedProbe receipt does not prove the successful matching consumer profile.'
    }
    $managedArtifact = $managedReceipt.Artifact
    $managedArtifactProperties = @(
        'Candidate', 'UidBits', 'Dll', 'DllSha256', 'ValuesRoot', 'Options', 'OptionsSha256', 'Zig', 'ZigSha256'
    )
    if (-not $legacyCompilerCommandProfile) { $managedArtifactProperties += 'CompilerCommand' }
    Assert-LedgerObjectProperties -Object $managedArtifact -PropertyNames $managedArtifactProperties `
        -Description 'ManagedProbe consumer artifact'
    if ($legacyCompilerCommandProfile -and $null -ne $managedArtifact.PSObject.Properties['CompilerCommand']) {
        throw 'The selected early ManagedProbe profile has an unexpected compiler-command artifact field.'
    }
    if ([string]$managedArtifact.Candidate -cne $candidate -or [int]$managedArtifact.UidBits -ne $uidBits -or
        [System.IO.Path]::GetFullPath([string]$managedArtifact.Dll) -ine $dllPath -or
        [string]$managedArtifact.DllSha256 -ine [string]$producerArtifact.DllSha256 -or
        [System.IO.Path]::GetFullPath([string]$managedArtifact.ValuesRoot) -ine $valuesRoot -or
        [System.IO.Path]::GetFullPath([string]$managedArtifact.Options) -ine $optionsPath -or
        [string]$managedArtifact.OptionsSha256 -ine [string]$producerArtifact.OptionsSha256 -or
        [System.IO.Path]::GetFullPath([string]$managedArtifact.Zig) -ine [System.IO.Path]::GetFullPath([string]$producerArtifact.Zig) -or
        [string]$managedArtifact.ZigSha256 -ine [string]$producerArtifact.ZigSha256) {
        throw 'ManagedProbe artifact does not bind the exact producer DLL and profile.'
    }
    $managedArtifactManifest = Get-Content -LiteralPath $managedFiles.artifactManifest -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $managedArtifactManifest -PropertyNames @('Native', 'NativeRoot', 'Assembly', 'AssemblySha256', 'Dotnet', 'DotnetSha256') `
        -Description 'ManagedProbe artifact manifest'
    if ([System.IO.Path]::GetFullPath([string]$managedArtifactManifest.NativeRoot) -ine $producerRoot -or
        [System.IO.Path]::GetFullPath([string]$managedArtifactManifest.Assembly) -ine (Join-Path $managedRoot 'bin\OwnedLedgerComparisonProbe.dll') -or
        [string]$managedArtifactManifest.AssemblySha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        [string]$managedArtifactManifest.DotnetSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'ManagedProbe artifact manifest does not bind the exact producer and a hashed assembly/toolchain.'
    }
    $managedNativeArtifact = $managedArtifactManifest.Native
    if ([string]$managedNativeArtifact.Candidate -cne $candidate -or [int]$managedNativeArtifact.UidBits -ne $uidBits -or
        [System.IO.Path]::GetFullPath([string]$managedNativeArtifact.Dll) -ine $dllPath -or
        [string]$managedNativeArtifact.DllSha256 -ine [string]$producerArtifact.DllSha256 -or
        [System.IO.Path]::GetFullPath([string]$managedNativeArtifact.ValuesRoot) -ine $valuesRoot -or
        [string]$managedNativeArtifact.OptionsSha256 -ine [string]$producerArtifact.OptionsSha256 -or
        [string]$managedNativeArtifact.ZigSha256 -ine [string]$producerArtifact.ZigSha256) {
        throw 'ManagedProbe artifact manifest embeds a different NativeFacade artifact.'
    }
    $assemblyPath = Join-Path $managedRoot 'bin\OwnedLedgerComparisonProbe.dll'
    if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash -ine [string]$managedArtifactManifest.AssemblySha256 -or
        (Get-FileHash -LiteralPath ([string]$managedArtifactManifest.Dotnet) -Algorithm SHA256).Hash -ine [string]$managedArtifactManifest.DotnetSha256) {
        throw 'ManagedProbe assembly or dotnet executable differs from the recorded artifact hashes.'
    }
    $managedStageManifest = Assert-LedgerFacadeInputManifest -Path $managedFiles.stageManifest `
        -ExpectedStage 'ManagedProbe' -ExpectedRunName $managedRunName -BatchRoot $batchRoot `
        -OutputRoot $managedRoot -ValuesRoot $valuesRoot `
        -AllowEmptyValuesRoot:$legacyManagedEmptyValuesRootProfile -Description 'ManagedProbe input manifest'
    Assert-LedgerFacadeManifestInput -Manifest $managedStageManifest -Path $dllPath `
        -Description 'ManagedProbe NativeFacade DLL' -ExpectedSha256 ([string]$producerArtifact.DllSha256)

    $consumerResult = Get-Content -LiteralPath $managedFiles.consumerResult -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $consumerResult -PropertyNames @('passed', 'candidate', 'uid_width_bits', 'cases', 'native_comparison_only') `
        -Description 'ManagedProbe 14-case consumer result'
    $expectedCases = if ($legacyManagedEmptyValuesRootProfile) {
        @(
            'zero_init_and_empty_lookup', 'opaque_full_width_uid_and_notification',
            'dynamic_and_fixed_discard_with_reload', 'loading_exclusivity_use_counts_and_close',
            'shared_same_page_growth_and_blocked_move', 'sole_holder_growth_tail_and_shrink_failure',
            'uid_only_offline_update', 'late_old_lease_after_replacement',
            'span_and_task_pin_defer_end_use', 'set_mode_and_tick_cleanup',
            'notification_payload_exception_and_unsubscribe', 'partition_tree_close',
            'forced_destroy_with_outstanding_lease'
        )
    }
    else {
        @(
            'zero_init_and_empty_lookup', 'opaque_full_width_uid_and_notification',
            'dynamic_and_fixed_discard_with_reload', 'loading_exclusivity_use_counts_and_close',
            'shared_same_page_growth_and_blocked_move', 'sole_holder_growth_tail_and_shrink_failure',
            'uid_only_offline_update', 'late_old_lease_after_replacement',
            'span_and_task_pin_defer_end_use', 'named_pipe_async_read_pins_native_memory',
            'set_mode_and_tick_cleanup', 'notification_payload_exception_and_unsubscribe',
            'partition_tree_close', 'forced_destroy_with_outstanding_lease'
        )
    }
    if ($consumerResult.passed -ne $true -or [string]$consumerResult.candidate -cne $candidate -or
        [int]$consumerResult.uid_width_bits -ne $uidBits -or $consumerResult.native_comparison_only -ne $true) {
        throw 'ManagedProbe consumer result does not prove the successful comparison-facade profile.'
    }
    Assert-LedgerStringSequence -Actual @($consumerResult.cases) -Expected $expectedCases -Description 'ManagedProbe named consumer cases'

    $managedCommands = @(Get-Content -LiteralPath $managedFiles.commandLog | ForEach-Object {
        if (-not [string]::IsNullOrWhiteSpace($_)) { $_ | ConvertFrom-Json }
    })
    if ($managedCommands.Count -ne 2 -or [string]$managedCommands[0].name -cne 'build-managed-probe' -or
        [string]$managedCommands[1].name -cne 'run-managed-probe') {
        throw 'ManagedProbe command log must contain exactly its build and 14-case consumer invocations.'
    }
    $managedProjectPath = Join-Path $repositoryRoot '.local\ledger-owner-implementation\managed-probe\OwnedLedgerComparisonProbe.csproj'
    $managedBuildArguments = @(
        'build', $managedProjectPath, '--disable-build-servers', '-c', 'Release', '-o', (Join-Path $managedRoot 'bin'),
        ('-p:BaseIntermediateOutputPath=' + (Join-Path $managedRoot 'obj\')),
        ('-p:MSBuildProjectExtensionsPath=' + (Join-Path $managedRoot 'obj\')),
        '-p:UseSharedCompilation=false'
    )
    if ([string]$managedCommands[0].executable -ine [string]$managedArtifactManifest.Dotnet -or
        [int]$managedCommands[0].exitCode -ne 0 -or
        [System.IO.Path]::GetFullPath([string]$managedCommands[0].output) -ine (Join-Path $managedRoot 'logs\build-managed-probe.log')) {
        throw 'ManagedProbe build command differs from the recorded successful build.'
    }
    Assert-LedgerStringSequence -Actual @($managedCommands[0].arguments) -Expected $managedBuildArguments -Description 'ManagedProbe build arguments'
    $managedRunArguments = @($assemblyPath, '--dll', $dllPath, '--candidate', $candidate, '--uid-bits', [string]$uidBits)
    if ([string]$managedCommands[1].executable -ine [string]$managedArtifactManifest.Dotnet -or [int]$managedCommands[1].exitCode -ne 0 -or
        [System.IO.Path]::GetFullPath([string]$managedCommands[1].output) -ine (Join-Path $managedRoot 'logs\run-managed-probe.log')) {
        throw 'ManagedProbe run command differs from the recorded successful consumer invocation.'
    }
    Assert-LedgerStringSequence -Actual @($managedCommands[1].arguments) -Expected $managedRunArguments -Description 'ManagedProbe run arguments'

    $verifiedNativeInputs = Get-Content -LiteralPath $managedFiles.verifiedNativeInputs -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $verifiedNativeInputs -PropertyNames @('CompilerCommand', 'ProducerInputManifest', 'Inputs', 'PreservedBroadInputCount') `
        -Description 'ManagedProbe verified Native inputs'
    if ([System.IO.Path]::GetFullPath([string]$verifiedNativeInputs.CompilerCommand) -ine $producerFiles.commandLog -or
        [System.IO.Path]::GetFullPath([string]$verifiedNativeInputs.ProducerInputManifest) -ine $producerJob.InputManifestPath -or
        @($verifiedNativeInputs.Inputs).Count -lt 1 -or [int]$verifiedNativeInputs.PreservedBroadInputCount -lt 1) {
        throw 'ManagedProbe verified-native-inputs does not bind the exact producer compiler closure.'
    }

    $managedJob = Assert-LedgerFacadeExecutionJob -EvidenceSet $managedEvidence `
        -BoundedReceiptPath $managedFiles.boundedReceipt -Stage 'ManagedProbe' -RunName $managedRunName `
        -StageRoot $managedRoot -BatchRoot $batchRoot -ValidationRoot $ValidationRoot -ValuesRoot $valuesRoot -RepositoryRoot $repositoryRoot `
        -UidBits $uidBits -Candidate $candidate -ExpectedJobMemoryLimitBytes 1073741824 `
        -AllowEmptyValuesRoot:$legacyManagedEmptyValuesRootProfile -NativeFacadeRoot $producerRoot

    [pscustomobject]@{
        Kind = 'ledger-checks'
        ProofProfile = $ProofProfile
        Candidate = $candidate
        UidBits = $uidBits
        RunName = $producerRunName
        StageName = $producerStageName
        StageRoot = $producerRoot
        CacheOwnerStageName = $producerStageName
        ManagedRunName = $managedRunName
        ManagedStageName = $managedStageName
        ManagedReceiptPath = $managedFiles.receipt
        ManagedReceiptSha256 = [string]$Evidence.managed.receipt.sha256
        NativeDllPath = $dllPath
        NativeDllSha256 = [string]$producerArtifact.DllSha256
        ReceiptPath = $producerFiles.receipt
        ReceiptSha256 = [string]$Evidence.producer.receipt.sha256
        BoundedExecutionReceiptPath = $producerFiles.boundedReceipt
        BoundedExecutionReceiptSha256 = [string]$Evidence.producer.boundedReceipt.sha256
        StageManifestPath = $producerFiles.stageManifest
        StageManifestSha256 = [string]$Evidence.producer.stageManifest.sha256
        ExecutionJobInputManifestPath = $producerJob.InputManifestPath
        ExecutionJobInputManifestSha256 = [string]$Evidence.producer.jobInputManifest.sha256
        ExecutionJobManifestPath = $producerJob.ManifestPath
        ExecutionJobManifestSha256 = [string]$Evidence.producer.jobManifest.sha256
        JobReceiptPath = $producerJob.JobReceiptPath
        JobReceiptSha256 = [string]$Evidence.producer.jobReceipt.sha256
        CommandLogPath = $producerFiles.commandLog
        CommandLogSha256 = [string]$Evidence.producer.commandLog.sha256
        ArtifactManifestPath = $producerFiles.artifactManifest
        ArtifactManifestSha256 = [string]$Evidence.producer.artifactManifest.sha256
        CacheRoot = $producerCacheRoot
        GlobalCacheRoot = $producerGlobalCacheRoot
        InputCount = @($producerStageManifest.InputsBefore).Count + @($managedStageManifest.InputsBefore).Count
    }
}

function Get-LedgerCheckEvidence {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$BatchId,
        [Parameter(Mandatory)][string]$ValidationRoot
    )
    if ([string]$Evidence.kind -cne 'ledger-checks') { throw 'Each ledgerChecks entry must use kind ledger-checks.' }
    $proofProfile = 'owner-validation'
    if ($null -ne $Evidence.PSObject.Properties['proofProfile']) {
        $proofProfile = [string]$Evidence.proofProfile
    }
    if ($proofProfile -cnotin @('owner-validation', 'use-baseline-correctness', 'use-baseline-correctness-120-cold', 'native-probe-correctness', 'native-facade-consumed', 'native-facade-consumed-13', 'native-operations-correctness', 'owner-concurrent-correctness', 'independent-source-correctness')) {
        throw 'ledgerChecks proofProfile must be owner-validation, a supported UseBaseline profile, a supported NativeFacade profile, native-probe-correctness, native-operations-correctness, owner-concurrent-correctness, or independent-source-correctness.'
    }
    if ($proofProfile -cin @('native-facade-consumed', 'native-facade-consumed-13')) {
        return Get-NativeFacadeConsumedEvidence -Evidence $Evidence -BatchId $BatchId -ValidationRoot $ValidationRoot -ProofProfile $proofProfile
    }
    if ($proofProfile -ceq 'native-operations-correctness') {
        return Get-NativeOperationsEvidence -Evidence $Evidence -BatchId $BatchId -ValidationRoot $ValidationRoot
    }
    if ($proofProfile -ceq 'owner-concurrent-correctness') {
        return Get-OwnerConcurrentEvidence -Evidence $Evidence -BatchId $BatchId -ValidationRoot $ValidationRoot
    }
    if ($proofProfile -ceq 'independent-source-correctness') {
        return Get-IndependentSourceEvidence -Evidence $Evidence -BatchId $BatchId -ValidationRoot $ValidationRoot
    }
    $requiredProperties = @(
        'runName',
        'receiptPath', 'receiptSha256',
        'boundedExecutionReceiptPath', 'boundedExecutionReceiptSha256',
        'stageManifestPath', 'stageManifestSha256',
        'executionJobInputManifestPath', 'executionJobInputManifestSha256',
        'executionJobManifestPath', 'executionJobManifestSha256',
        'jobReceiptPath', 'jobReceiptSha256'
    )
    foreach ($propertyName in $requiredProperties) {
        $property = $Evidence.PSObject.Properties[$propertyName]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "Ledger prune evidence is missing '$propertyName'."
        }
    }
    if ($proofProfile -ceq 'use-baseline-correctness-120-cold') {
        foreach ($propertyName in @('artifactManifestPath', 'artifactManifestSha256', 'commandLogPath', 'commandLogSha256', 'correctnessResultsPath', 'correctnessResultsSha256')) {
            $property = $Evidence.PSObject.Properties[$propertyName]
            if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                throw "The UseBaseline 120-case ledger evidence is missing '$propertyName'."
            }
        }
    }
    if ($proofProfile -ceq 'native-probe-correctness') {
        $nativeProbeProperty = $Evidence.PSObject.Properties['nativeProbe']
        if ($null -eq $nativeProbeProperty -or [string]$nativeProbeProperty.Value -cnotin @('LookupGate', 'PublicationGate', 'PushTargetGate')) {
            throw 'The native-probe-correctness evidence must explicitly name LookupGate, PublicationGate, or PushTargetGate.'
        }
        if ([string]$nativeProbeProperty.Value -cin @('PublicationGate', 'PushTargetGate')) {
            foreach ($propertyName in @('probePreparationReceiptPath', 'probePreparationReceiptSha256')) {
                $property = $Evidence.PSObject.Properties[$propertyName]
                if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                    throw "The $($nativeProbeProperty.Value) evidence must explicitly pin '$propertyName'."
                }
            }
        }
    }
    if ($BatchId -cne 'ledger-owner-a1') { throw 'ledger-checks evidence is scoped to ledger-owner-a1.' }
    $declaredRunName = [string]$Evidence.runName
    if ($declaredRunName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw 'ledgerChecks must explicitly list a valid Native RunName.'
    }

    $receiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.receiptPath) -ExpectedSha256 ([string]$Evidence.receiptSha256) -ValidationRoot $ValidationRoot -Description 'Native stage receipt'
    $stageRoot = [System.IO.Path]::GetDirectoryName($receiptPath)
    $stageName = [System.IO.Path]::GetFileName($stageRoot)
    if ($stageName -cnotmatch '^Native-[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw 'ledger-checks requires an exact Native-<RunName> stage directory.'
    }
    $batchRoot = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $ValidationRoot 'batches') $BatchId)).TrimEnd('\')
    $scratchRoot = Join-Path $batchRoot 'scratch'
    $expectedStageRoot = Join-Path $scratchRoot $stageName
    $stageRoot = Assert-LedgerExactPath -Actual $stageRoot -Expected $expectedStageRoot -Description 'Native stage root'
    Assert-NoReparseComponents -Path $stageRoot -Root $ValidationRoot
    $stageItem = Get-Item -LiteralPath $stageRoot
    if (-not $stageItem.PSIsContainer -or (($stageItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'The bound Native stage root is missing or is a reparse point.'
    }

    $expectedReceiptPath = Join-Path $stageRoot 'receipt.json'
    $receiptPath = Assert-LedgerExactPath -Actual $receiptPath -Expected $expectedReceiptPath -Description 'Native receipt path'
    $boundedReceiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.boundedExecutionReceiptPath) -ExpectedSha256 ([string]$Evidence.boundedExecutionReceiptSha256) -ValidationRoot $ValidationRoot -Description 'Bounded ExecutionJob receipt'
    $boundedReceiptPath = Assert-LedgerExactPath -Actual $boundedReceiptPath -Expected (Join-Path $stageRoot 'bounded-execution-receipt.json') -Description 'Bounded ExecutionJob receipt path'
    $stageManifestPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.stageManifestPath) -ExpectedSha256 ([string]$Evidence.stageManifestSha256) -ValidationRoot $ValidationRoot -Description 'Native stage input manifest'
    $stageManifestPath = Assert-LedgerExactPath -Actual $stageManifestPath -Expected (Join-Path $stageRoot 'input-manifest.json') -Description 'Native stage input-manifest path'

    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $receipt -PropertyNames @(
        'Passed', 'Stage', 'RunName', 'ExitCode', 'CompiledCandidate', 'ExpectedFaultDetected', 'Failure',
        'Command', 'CommandLog', 'RuntimeCommand', 'RuntimeLog', 'InputManifest', 'ChangedInputCount', 'OutputRoot', 'ExecutionJobRoute'
    ) -Description 'Native stage receipt'
    Assert-LedgerObjectProperties -Object $receipt.Command -PropertyNames @('executable', 'arguments', 'expectedExitCode', 'exitCode') -Description 'Native compiler result'
    Assert-LedgerObjectProperties -Object $receipt.RuntimeCommand -PropertyNames @('executable', 'arguments', 'expectedExitCode', 'exitCode') -Description 'Native runtime result'
    if ($proofProfile -cin @('use-baseline-correctness', 'use-baseline-correctness-120-cold')) {
        Assert-LedgerObjectProperties -Object $receipt.Command -PropertyNames @('output') -Description 'UseBaseline compiler result'
        Assert-LedgerObjectProperties -Object $receipt.RuntimeCommand -PropertyNames @('name', 'output') -Description 'UseBaseline runtime result'
        if ($null -ne $receipt.RuntimeCommand.PSObject.Properties['expectedNonzeroExitCode'] -and $receipt.RuntimeCommand.ExpectedNonzeroExitCode -eq $true) {
            throw 'The UseBaseline runtime result must not expect a nonzero exit.'
        }
    } elseif ($proofProfile -ceq 'native-probe-correctness') {
        Assert-LedgerObjectProperties -Object $receipt.RuntimeCommand -PropertyNames @('name', 'expectedNonzeroExitCode', 'output') -Description 'Native probe runtime result'
    } else {
        Assert-LedgerObjectProperties -Object $receipt.RuntimeCommand -PropertyNames @('expectedNonzeroExitCode') -Description 'Owner runtime result'
    }
    $runName = [string]$receipt.RunName
    if ($runName -cne $declaredRunName -or $stageName -cne ('Native-' + $declaredRunName)) {
        throw 'The Native receipt RunName does not match the exact Native-<RunName> stage directory.'
    }
    if (
        $receipt.Passed -ne $true -or $receipt.Stage -cne 'Native' -or [int]$receipt.ExitCode -ne 0 -or
        $receipt.ExpectedFaultDetected -ne $false -or [int]$receipt.ChangedInputCount -ne 0 -or
        $receipt.ExecutionJobRoute -cne 'ledger-owner-bounded' -or
        [int]$receipt.Command.expectedExitCode -ne 0 -or [int]$receipt.Command.ExitCode -ne 0 -or
        [int]$receipt.RuntimeCommand.expectedExitCode -ne 0 -or [int]$receipt.RuntimeCommand.ExitCode -ne 0 -or
        ($null -ne $receipt.Failure -and -not [string]::IsNullOrWhiteSpace([string]$receipt.Failure))
    ) { throw 'The Native stage is not a clean successful ledger validation run.' }
    if ($proofProfile -ceq 'owner-validation' -and $receipt.CompiledCandidate -cne 'both') {
        throw 'The ordinary Owner validation profile requires CompiledCandidate=both.'
    }
    if ($proofProfile -ceq 'owner-validation' -and $receipt.RuntimeCommand.ExpectedNonzeroExitCode -ne $false) {
        throw 'The ordinary Owner runtime result must not expect a nonzero exit.'
    }
    if ($proofProfile -ceq 'native-probe-correctness' -and
        ([string]$receipt.CompiledCandidate -cne 'both' -or $receipt.RuntimeCommand.ExpectedNonzeroExitCode -ne $false -or
            @($receipt.RuntimeCommand.arguments).Count -ne 0 -or [string]$receipt.RuntimeCommand.name -cne 'sdk38-owned-validation-runtime')) {
        throw 'The native-probe-correctness profile requires both compiled candidates and the ordinary zero-argument runtime result.'
    }
    if ($proofProfile -cin @('use-baseline-correctness', 'use-baseline-correctness-120-cold') -and [string]$receipt.CompiledCandidate -cnotin @('c2', 'b2')) {
        throw 'The UseBaseline correctness profile requires the explicit c2 or b2 candidate.'
    }
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.OutputRoot) -Expected $stageRoot -Description 'Native receipt output root'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.InputManifest) -Expected $stageManifestPath -Description 'Native receipt input-manifest path'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.CommandLog) -Expected (Join-Path $stageRoot 'logs\owned-validation.log') -Description 'Native command log path'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.RuntimeLog) -Expected (Join-Path $stageRoot 'logs\owned-validation-runtime.log') -Description 'Native runtime log path'
    $null = Assert-LedgerExactPath -Actual ([string]$receipt.RuntimeCommand.executable) -Expected (Join-Path $stageRoot 'bin\owned-validation.exe') -Description 'Native executable path'
    if ($proofProfile -cin @('use-baseline-correctness', 'use-baseline-correctness-120-cold')) {
        $null = Assert-LedgerExactPath -Actual ([string]$receipt.Command.output) -Expected (Join-Path $stageRoot 'logs\owned-validation.log') -Description 'UseBaseline compiler log path'
    }
    if ([System.IO.Path]::GetFileName([string]$receipt.Command.executable) -cne 'zig.exe') {
        throw 'The Native receipt compiler is not a Zig executable.'
    }

    $commandArguments = @($receipt.Command.arguments | ForEach-Object { [string]$_ })
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $expectedRootModule = if ($proofProfile -ceq 'owner-validation') {
        Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\owned_validation_tests.zig'
    } elseif ($proofProfile -ceq 'native-probe-correctness') {
        switch ([string]$Evidence.nativeProbe) {
            'LookupGate' { Join-Path $repositoryRoot '.local\ledger-owner-implementation\notifications\lookup-gate-tests.zig' }
            'PublicationGate' { Join-Path $repositoryRoot '.local\ledger-owner-implementation\notifications\publication-gate-tests.zig' }
            'PushTargetGate' { Join-Path $repositoryRoot '.local\ledger-owner-implementation\notifications\push-target-gate-tests.zig' }
            default { throw 'The Native probe root module is not supported.' }
        }
    } else {
        Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\owned_use_driver.zig'
    }
    if ($proofProfile -ceq 'owner-validation') {
        if (
            $commandArguments.Count -lt 2 -or $commandArguments[0] -cne 'test' -or
            -not ($commandArguments -ccontains '--test-no-exec') -or
            -not ($commandArguments -ccontains '-OReleaseSafe')
        ) { throw 'The Native compiler command is not the expected test-only Owner validation shape.' }
    } elseif ($proofProfile -ceq 'native-probe-correctness') {
        foreach ($requiredArgument in @('test', '--test-no-exec', '-OReleaseSafe', '-j1', '-fno-llvm', '-fno-lld')) {
            if (@($commandArguments | Where-Object { $_ -ceq $requiredArgument }).Count -ne 1) {
                throw "The Native probe compiler command must contain exactly one '$requiredArgument'."
            }
        }
        if ($commandArguments -ccontains 'build-exe') { throw 'The Native probe compiler command cannot be a UseBaseline build-exe command.' }
        $zigLibRoot = Get-LedgerArgumentValue -Arguments $commandArguments -Name '--zig-lib-dir' -Context 'The Native probe compiler command'
        $expectedZigLibRoot = Join-Path ([System.IO.Path]::GetDirectoryName([string]$receipt.Command.executable)) 'lib'
        $null = Assert-LedgerExactPath -Actual $zigLibRoot -Expected $expectedZigLibRoot -Description 'Native probe Zig standard-library root'
    } else {
        foreach ($requiredArgument in @('build-exe', '-OReleaseFast', '-j1', '-fno-llvm', '-fno-lld')) {
            if (@($commandArguments | Where-Object { $_ -ceq $requiredArgument }).Count -ne 1) {
                throw "The UseBaseline compiler command must contain exactly one '$requiredArgument'."
            }
        }
        if ($commandArguments[0] -cne 'build-exe' -or $commandArguments -ccontains '--test-no-exec') {
            throw 'The UseBaseline compiler command is not a build-exe correctness-driver build.'
        }
        $zigLibRoot = Get-LedgerArgumentValue -Arguments $commandArguments -Name '--zig-lib-dir' -Context 'The UseBaseline compiler command'
        $expectedZigLibRoot = Join-Path ([System.IO.Path]::GetDirectoryName([string]$receipt.Command.executable)) 'lib'
        $null = Assert-LedgerExactPath -Actual $zigLibRoot -Expected $expectedZigLibRoot -Description 'UseBaseline Zig standard-library root'
        $dependencyNames = [System.Collections.Generic.List[string]]::new()
        for ($argumentIndex = 0; $argumentIndex -lt $commandArguments.Count; $argumentIndex++) {
            if ($commandArguments[$argumentIndex] -ceq '--dep') {
                if ($argumentIndex + 1 -ge $commandArguments.Count) { throw 'The UseBaseline compiler command has an incomplete module dependency.' }
                $dependencyNames.Add([string]$commandArguments[$argumentIndex + 1])
            }
        }
        $expectedDependencyNames = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') {
            @('ledger_values', 'ledger_structure', 'benchmark_options', 'benchmark_cache_cold')
        } else {
            @('ledger_values', 'ledger_structure', 'benchmark_options')
        }
        if ($dependencyNames.Count -ne $expectedDependencyNames.Count -or
            @($dependencyNames | Where-Object { $_ -notin $expectedDependencyNames }).Count -ne 0 -or
            @($dependencyNames | Select-Object -Unique).Count -ne $expectedDependencyNames.Count) {
            throw 'The UseBaseline compiler command does not declare exactly the modules required by its proof profile.'
        }
        $moduleArguments = @($commandArguments | Where-Object { $_.StartsWith('-M', [System.StringComparison]::Ordinal) })
        $expectedModuleArgumentCount = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { 5 } else { 4 }
        if ($moduleArguments.Count -ne $expectedModuleArgumentCount) { throw 'The UseBaseline compiler command has an unexpected module binding count for its proof profile.' }
    }
    $rootModuleArguments = @($commandArguments | Where-Object { $_.StartsWith('-Mroot=', [System.StringComparison]::Ordinal) })
    if ($rootModuleArguments.Count -ne 1) { throw 'The Native compiler command must declare exactly one profile root module.' }
    $null = Assert-LedgerExactPath -Actual $rootModuleArguments[0].Substring('-Mroot='.Length) -Expected $expectedRootModule -Description 'Native profile root module'
    if ($proofProfile -cin @('use-baseline-correctness', 'use-baseline-correctness-120-cold')) {
        $valuesModuleArguments = @($commandArguments | Where-Object { $_.StartsWith('-Mledger_values=', [System.StringComparison]::Ordinal) })
        $structureModuleArguments = @($commandArguments | Where-Object { $_.StartsWith('-Mledger_structure=', [System.StringComparison]::Ordinal) })
        $optionsModuleArguments = @($commandArguments | Where-Object { $_.StartsWith('-Mbenchmark_options=', [System.StringComparison]::Ordinal) })
        if ($valuesModuleArguments.Count -ne 1 -or $structureModuleArguments.Count -ne 1 -or $optionsModuleArguments.Count -ne 1) {
            throw 'The UseBaseline compiler command must bind each explicit values, structure, and options module exactly once.'
        }
        $coldModuleArguments = @($commandArguments | Where-Object { $_.StartsWith('-Mbenchmark_cache_cold=', [System.StringComparison]::Ordinal) })
        if (($proofProfile -ceq 'use-baseline-correctness-120-cold' -and $coldModuleArguments.Count -ne 1) -or
            ($proofProfile -ceq 'use-baseline-correctness' -and $coldModuleArguments.Count -ne 0)) {
            throw 'The UseBaseline compiler command has an unexpected cache-cold module binding for its proof profile.'
        }
        $stageManifestForModules = Get-Content -LiteralPath $stageManifestPath -Raw | ConvertFrom-Json
        $expectedValuesPath = Join-Path ([string]$stageManifestForModules.ValuesRoot) 'ledger_values.zig'
        $expectedStructurePath = Join-Path $repositoryRoot 'sdk\zig\src\local_resource_manager\generated\ledger_structure.zig'
        $null = Assert-LedgerExactPath -Actual $valuesModuleArguments[0].Substring('-Mledger_values='.Length) -Expected $expectedValuesPath -Description 'UseBaseline values module'
        $null = Assert-LedgerExactPath -Actual $structureModuleArguments[0].Substring('-Mledger_structure='.Length) -Expected $expectedStructurePath -Description 'UseBaseline structure module'
        $null = Assert-LedgerExactPath -Actual $optionsModuleArguments[0].Substring('-Mbenchmark_options='.Length) -Expected (Join-Path $stageRoot 'benchmark_options.zig') -Description 'UseBaseline benchmark options module'
        if ($proofProfile -ceq 'use-baseline-correctness-120-cold') {
            $repositoryColdModule = Join-Path $repositoryRoot '.local\ledger-owner-implementation\foundation\cache_cold_proposal.zig'
            $null = Assert-LedgerExactPath -Actual $coldModuleArguments[0].Substring('-Mbenchmark_cache_cold='.Length) -Expected $repositoryColdModule -Description 'UseBaseline cache-cold module'
        }
        $expectedCompileArguments = [System.Collections.Generic.List[string]]::new()
        foreach ($argument in @('build-exe', '-OReleaseFast', '-j1', '-fno-llvm', '-fno-lld', '--zig-lib-dir', $expectedZigLibRoot,
            '--dep', 'ledger_values', '--dep', 'benchmark_options')) { $expectedCompileArguments.Add([string]$argument) }
        if ($proofProfile -ceq 'use-baseline-correctness-120-cold') {
            foreach ($argument in @('--dep', 'benchmark_cache_cold')) { $expectedCompileArguments.Add([string]$argument) }
        }
        foreach ($argument in @(
            ('-Mroot=' + $expectedRootModule), '--dep', 'ledger_structure',
            ('-Mledger_values=' + [System.IO.Path]::GetFullPath($expectedValuesPath)),
            ('-Mledger_structure=' + [System.IO.Path]::GetFullPath($expectedStructurePath)),
            '--cache-dir', (Join-Path (Join-Path $stageRoot 'cache') 'owned-validation'),
            '--global-cache-dir', (Join-Path $stageRoot 'global-cache'),
            ('-femit-bin=' + (Join-Path $stageRoot 'bin\owned-validation.exe')),
            ('-Mbenchmark_options=' + (Join-Path $stageRoot 'benchmark_options.zig')))) { $expectedCompileArguments.Add([string]$argument) }
        if ($proofProfile -ceq 'use-baseline-correctness-120-cold') {
            $expectedCompileArguments.Add('-Mbenchmark_cache_cold=' + $repositoryColdModule)
        }
        if (($commandArguments -join "`0") -cne ($expectedCompileArguments.ToArray() -join "`0")) {
            throw 'The UseBaseline compiler arguments do not match the pinned build-exe, module, cache, and artifact shape.'
        }
        if ($proofProfile -ceq 'use-baseline-correctness') {
            if ([string]$receipt.RuntimeCommand.name -cne 'owned-use-correctness-60') {
                throw 'The UseBaseline runtime command is not the 60-case correctness driver.'
            }
            $expectedRuntimeArguments = @(
                '--candidate', [string]$receipt.CompiledCandidate,
                '--uid-bits', '256', '--capacity', '1048576',
                '--path', 'runtime-dynamic-uid', '--measurement', 'index-only',
                '--state', 'loading', '--trace', 'single', '--mode', 'correctness',
                '--cache', 'warm', '--iterations', '128', '--seed', '1', '--affinity-mask', '1'
            )
            $actualRuntimeArguments = @($receipt.RuntimeCommand.arguments | ForEach-Object { [string]$_ })
            if (($actualRuntimeArguments -join "`0") -cne ($expectedRuntimeArguments -join "`0")) {
                throw 'The UseBaseline runtime arguments do not match the pinned one-case correctness invocation.'
            }
            $expectedRuntimeOutput = Join-Path $stageRoot 'logs\use-case-60.log'
            $null = Assert-LedgerExactPath -Actual ([string]$receipt.RuntimeCommand.output) -Expected $expectedRuntimeOutput -Description 'UseBaseline runtime result path'
        } else {
            $expectedRuntimeOutput = Join-Path $stageRoot 'logs\use-case-120.log'
            $null = Assert-LedgerExactPath -Actual ([string]$receipt.RuntimeCommand.output) -Expected $expectedRuntimeOutput -Description 'UseBaseline 120-case runtime result path'
        }
        $compileLogPath = Join-Path $stageRoot 'logs\owned-validation.log'
        if (-not (Test-Path -LiteralPath $compileLogPath -PathType Leaf)) { throw 'The UseBaseline compiler log artifact is missing.' }
        $compileLogItem = Get-Item -LiteralPath $compileLogPath
        if (($compileLogItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The UseBaseline compiler log artifact is a reparse point.' }
        $optionsPath = Join-Path $stageRoot 'benchmark_options.zig'
        $optionsInputRows = @($stageManifestForModules.InputsBefore | Where-Object {
            [System.IO.Path]::GetFullPath([string]$_.path) -ieq [System.IO.Path]::GetFullPath($optionsPath)
        })
        if ($optionsInputRows.Count -lt 1) { throw 'The UseBaseline input snapshot does not include its generated benchmark options file.' }
        Assert-LedgerInputSnapshotStable -StageManifest $stageManifestForModules
        if ($proofProfile -ceq 'use-baseline-correctness') {
            Assert-LedgerUseBaselineArtifacts -StageRoot $stageRoot -Receipt $receipt -StageManifest $stageManifestForModules `
                -Candidate ([string]$receipt.CompiledCandidate) -RootModulePath $expectedRootModule
        } else {
            $artifactManifestPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.artifactManifestPath) `
                -ExpectedSha256 ([string]$Evidence.artifactManifestSha256) -ValidationRoot $ValidationRoot -Description 'UseBaseline 120-case artifact manifest'
            $artifactManifestPath = Assert-LedgerExactPath -Actual $artifactManifestPath -Expected (Join-Path $stageRoot 'artifact-manifest.json') -Description 'UseBaseline 120-case artifact-manifest path'
            $commandLogPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.commandLogPath) `
                -ExpectedSha256 ([string]$Evidence.commandLogSha256) -ValidationRoot $ValidationRoot -Description 'UseBaseline 120-case command log'
            $commandLogPath = Assert-LedgerExactPath -Actual $commandLogPath -Expected (Join-Path $stageRoot 'commands.jsonl') -Description 'UseBaseline 120-case command-log path'
            $correctnessResultsPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.correctnessResultsPath) `
                -ExpectedSha256 ([string]$Evidence.correctnessResultsSha256) -ValidationRoot $ValidationRoot -Description 'UseBaseline 120-case correctness results'
            $correctnessResultsPath = Assert-LedgerExactPath -Actual $correctnessResultsPath -Expected (Join-Path $stageRoot 'use-correctness-results.json') -Description 'UseBaseline 120-case results path'
            Assert-LedgerUseBaseline120ColdArtifacts -StageRoot $stageRoot -Receipt $receipt -StageManifest $stageManifestForModules `
                -Candidate ([string]$receipt.CompiledCandidate) -RootModulePath $expectedRootModule `
                -CommandsPath $commandLogPath -ResultsPath $correctnessResultsPath -ArtifactManifestPath $artifactManifestPath
        }
    }
    $cacheArgument = Get-LedgerArgumentValue -Arguments $commandArguments -Name '--cache-dir' -Context 'The Native compiler command'
    $globalCacheArgument = Get-LedgerArgumentValue -Arguments $commandArguments -Name '--global-cache-dir' -Context 'The Native compiler command'
    $cacheRoot = Join-Path $stageRoot 'cache'
    $globalCacheRoot = Join-Path $stageRoot 'global-cache'
    $null = Assert-LedgerExactPath -Actual $cacheArgument -Expected (Join-Path $cacheRoot 'owned-validation') -Description 'Native compiler cache argument'
    $null = Assert-LedgerExactPath -Actual $globalCacheArgument -Expected $globalCacheRoot -Description 'Native compiler global-cache argument'
    $emitArguments = @($commandArguments | Where-Object { $_.StartsWith('-femit-bin=', [System.StringComparison]::Ordinal) })
    if ($emitArguments.Count -ne 1) { throw 'The Native compiler command must name exactly one emitted executable.' }
    $null = Assert-LedgerExactPath -Actual $emitArguments[0].Substring('-femit-bin='.Length) -Expected (Join-Path $stageRoot 'bin\owned-validation.exe') -Description 'Native compiler output path'

    if (Test-Path -LiteralPath $cacheRoot -PathType Container) {
        Assert-NoReparseComponents -Path $cacheRoot -Root $ValidationRoot
        $cacheChildren = @(Get-ChildItem -LiteralPath $cacheRoot -Force)
        if ($cacheChildren.Count -gt 1 -or ($cacheChildren.Count -eq 1 -and ($cacheChildren[0].Name -cne 'owned-validation' -or -not $cacheChildren[0].PSIsContainer))) {
            throw 'The Native cache root contains entries outside the command-bound owned-validation cache.'
        }
    }
    if (Test-Path -LiteralPath $globalCacheRoot -PathType Container) {
        Assert-NoReparseComponents -Path $globalCacheRoot -Root $ValidationRoot
        $globalChildren = @(Get-ChildItem -LiteralPath $globalCacheRoot -Force)
        foreach ($child in $globalChildren) {
            if ($child.Name -cnotin @('b', 'h', 'o', 'tmp', 'z') -or -not $child.PSIsContainer) {
                throw 'The Native global-cache root contains an entry outside the observed Zig cache buckets.'
            }
        }
    }

    $stageManifest = Get-Content -LiteralPath $stageManifestPath -Raw | ConvertFrom-Json
    if (
        [int]$stageManifest.SchemaVersion -ne 1 -or $stageManifest.Stage -cne 'Native' -or
        $stageManifest.RunName -cne $runName -or
        [System.IO.Path]::GetFullPath([string]$stageManifest.BatchRoot) -ine $batchRoot
    ) { throw 'The Native input manifest does not match its stage, run, or batch.' }
    if ($proofProfile -cin @('use-baseline-correctness', 'use-baseline-correctness-120-cold') -and $stageManifest.ValidationKind -cne 'complete-owner-use-correctness') {
        throw 'The UseBaseline proof requires the complete-owner-use-correctness validation kind.'
    }
    if ($proofProfile -ceq 'native-probe-correctness') {
        Assert-LedgerNativeProbeArtifacts -Probe ([string]$Evidence.nativeProbe) -Evidence $Evidence -StageRoot $stageRoot `
            -StageManifest $stageManifest -Receipt $receipt -CommandArguments $commandArguments -RepositoryRoot $repositoryRoot
    }
    Assert-LedgerInputSnapshotStable -StageManifest $stageManifest

    $boundedReceipt = Get-Content -LiteralPath $boundedReceiptPath -Raw | ConvertFrom-Json
    Assert-LedgerObjectProperties -Object $boundedReceipt -PropertyNames @(
        'Passed', 'Stage', 'RunName', 'ExitCode', 'NormalDrain', 'EmptyJob', 'UnexpectedChangedInputCount',
        'Failure', 'RunDirectory', 'Manifest', 'InputManifest', 'OutputRoot'
    ) -Description 'Bounded Native receipt'
    if (
        $boundedReceipt.Passed -ne $true -or $boundedReceipt.Stage -cne 'Native' -or
        $boundedReceipt.RunName -cne $runName -or [int]$boundedReceipt.ExitCode -ne 0 -or
        $boundedReceipt.NormalDrain -ne $true -or $boundedReceipt.EmptyJob -ne $true -or
        [int]$boundedReceipt.UnexpectedChangedInputCount -ne 0 -or
        ($null -ne $boundedReceipt.Failure -and -not [string]::IsNullOrWhiteSpace([string]$boundedReceipt.Failure))
    ) { throw 'The bounded Native receipt does not prove a successful normal empty-job drain.' }
    $null = Assert-LedgerExactPath -Actual ([string]$boundedReceipt.OutputRoot) -Expected $stageRoot -Description 'Bounded receipt output root'

    $runDirectory = [System.IO.Path]::GetFullPath([string]$boundedReceipt.RunDirectory).TrimEnd('\')
    Assert-NoReparseComponents -Path $runDirectory -Root $ValidationRoot
    if (-not (Test-Path -LiteralPath $runDirectory -PathType Container)) { throw 'The bounded ExecutionJob run directory is missing.' }
    $runInfo = [System.IO.DirectoryInfo]::new($runDirectory)
    $allowedReceiptRoot = $runInfo.Parent
    if ($null -eq $allowedReceiptRoot -or $allowedReceiptRoot.Name -cne 'job-receipts') {
        throw 'The bounded ExecutionJob run directory is not below its job-receipts root.'
    }
    $metadataRootInfo = $allowedReceiptRoot.Parent
    if ($null -eq $metadataRootInfo -or $metadataRootInfo.Parent.FullName -ine $scratchRoot -or
        $metadataRootInfo.Name -cnotmatch ('^\.execution-Native-' + [regex]::Escape($runName) + '-[A-Fa-f0-9]{32}$')) {
        throw 'The ExecutionJob metadata root is not the matching Native stage root under this batch scratch.'
    }
    $metadataRoot = $metadataRootInfo.FullName
    $executionJobInputManifestPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.executionJobInputManifestPath) -ExpectedSha256 ([string]$Evidence.executionJobInputManifestSha256) -ValidationRoot $ValidationRoot -Description 'ExecutionJob input manifest'
    $executionJobInputManifestPath = Assert-LedgerExactPath -Actual $executionJobInputManifestPath -Expected (Join-Path $metadataRoot 'input-manifest.json') -Description 'ExecutionJob input-manifest path'
    $null = Assert-LedgerExactPath -Actual ([string]$boundedReceipt.InputManifest) -Expected $executionJobInputManifestPath -Description 'Bounded receipt input-manifest path'
    $executionJobInputManifest = Get-Content -LiteralPath $executionJobInputManifestPath -Raw | ConvertFrom-Json
    if (
        [int]$executionJobInputManifest.SchemaVersion -ne 1 -or $executionJobInputManifest.Stage -cne 'Native' -or
        $executionJobInputManifest.RunName -cne $runName -or
        [System.IO.Path]::GetFullPath([string]$executionJobInputManifest.BatchRoot) -ine $batchRoot -or
        [System.IO.Path]::GetFullPath([string]$executionJobInputManifest.OutputRoot) -ine $stageRoot -or
        [System.IO.Path]::GetFullPath([string]$executionJobInputManifest.ValuesRoot) -ine [System.IO.Path]::GetFullPath([string]$stageManifest.ValuesRoot)
    ) { throw 'The ExecutionJob input manifest does not match its Native stage, batch, or values root.' }
    Assert-LedgerInputSnapshotStable -StageManifest $executionJobInputManifest -CountProperty '' `
        -ChangedArrayProperties @('ChangedInputs', 'UnexpectedChangedInputs')
    $executionJobManifestPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.executionJobManifestPath) -ExpectedSha256 ([string]$Evidence.executionJobManifestSha256) -ValidationRoot $ValidationRoot -Description 'ExecutionJob manifest'
    $executionJobManifestPath = Assert-LedgerExactPath -Actual $executionJobManifestPath -Expected (Join-Path $metadataRoot 'execution-job.json') -Description 'ExecutionJob manifest path'
    $null = Assert-LedgerExactPath -Actual ([string]$boundedReceipt.Manifest) -Expected $executionJobManifestPath -Description 'Bounded receipt ExecutionJob manifest path'
    $jobReceiptPath = Assert-LedgerEvidenceFile -Path ([string]$Evidence.jobReceiptPath) -ExpectedSha256 ([string]$Evidence.jobReceiptSha256) -ValidationRoot $ValidationRoot -Description 'ExecutionJob event receipt'
    $jobReceiptPath = Assert-LedgerExactPath -Actual $jobReceiptPath -Expected (Join-Path $runDirectory 'receipts.jsonl') -Description 'ExecutionJob event receipt path'

    $jobManifest = Get-Content -LiteralPath $executionJobManifestPath -Raw | ConvertFrom-Json
    $allowedReceiptRoot = [System.IO.Path]::GetFullPath($allowedReceiptRoot.FullName).TrimEnd('\')
    $jobArguments = @($jobManifest.arguments | ForEach-Object { [string]$_ })
    $boundedScriptArguments = @($jobArguments | Where-Object { $_ -match '(^|[\\/])Run-Bounded\.ps1$' })
    $nativeProbe = Get-LedgerArgumentValue -Arguments $jobArguments -Name '-NativeProbe' -Context 'The ExecutionJob wrapper command'
    $compiledCandidate = Get-LedgerArgumentValue -Arguments $jobArguments -Name '-CompiledCandidate' -Context 'The ExecutionJob wrapper command'
    $useBaselineArguments = @($jobArguments | Where-Object { $_ -ceq '-UseBaseline' })
    $expectedJobCandidate = if ($proofProfile -cin @('owner-validation', 'native-probe-correctness')) { 'both' } else { [string]$receipt.CompiledCandidate }
    $expectedNativeProbe = if ($proofProfile -ceq 'native-probe-correctness') { [string]$Evidence.nativeProbe } else { 'Owner' }
    $useBaselineProfileMismatch = if ($proofProfile -cin @('owner-validation', 'native-probe-correctness')) {
        $useBaselineArguments.Count -ne 0
    } else {
        $useBaselineArguments.Count -ne 1 -or $compiledCandidate -cne [string]$receipt.CompiledCandidate
    }
    $boundedScriptIndex = if ($boundedScriptArguments.Count -eq 1) { [array]::IndexOf($jobArguments, $boundedScriptArguments[0]) } else { -1 }
    if ((Get-LedgerArgumentValue -Arguments $jobArguments -Name '-Stage' -Context 'The ExecutionJob wrapper command') -cne 'Native' -or
        (Get-LedgerArgumentValue -Arguments $jobArguments -Name '-BatchRoot' -Context 'The ExecutionJob wrapper command') -ine $batchRoot -or
        (Get-LedgerArgumentValue -Arguments $jobArguments -Name '-RunName' -Context 'The ExecutionJob wrapper command') -cne $runName -or
        $nativeProbe -cne $expectedNativeProbe -or $compiledCandidate -cne $expectedJobCandidate -or $useBaselineProfileMismatch -or
        (Get-LedgerArgumentValue -Arguments $jobArguments -Name '-PayloadOutputRoot' -Context 'The ExecutionJob wrapper command') -ine $stageRoot -or
        (Get-LedgerArgumentValue -Arguments $jobArguments -Name '-PayloadMetadataRoot' -Context 'The ExecutionJob wrapper command') -ine $metadataRoot -or
        $boundedScriptIndex -lt 1 -or
        $jobArguments[$boundedScriptIndex - 1] -cne '-File') {
        throw 'The ExecutionJob manifest arguments do not bind this Native stage and metadata root.'
    }
    Assert-LedgerExecutionLifecycle -JobReceiptPath $jobReceiptPath -JobManifest $jobManifest -RunDirectory $runDirectory -AllowedReceiptRoot $allowedReceiptRoot

    [pscustomobject]@{
        Kind = 'ledger-checks'
        ProofProfile = $proofProfile
        NativeProbe = if ($proofProfile -ceq 'native-probe-correctness') { [string]$Evidence.nativeProbe } else { $null }
        RunName = $runName
        StageName = $stageName
        StageRoot = $stageRoot
        ReceiptPath = $receiptPath
        ReceiptSha256 = [string]$Evidence.receiptSha256
        BoundedExecutionReceiptPath = $boundedReceiptPath
        BoundedExecutionReceiptSha256 = [string]$Evidence.boundedExecutionReceiptSha256
        StageManifestPath = $stageManifestPath
        StageManifestSha256 = [string]$Evidence.stageManifestSha256
        ExecutionJobInputManifestPath = $executionJobInputManifestPath
        ExecutionJobInputManifestSha256 = [string]$Evidence.executionJobInputManifestSha256
        ExecutionJobManifestPath = $executionJobManifestPath
        ExecutionJobManifestSha256 = [string]$Evidence.executionJobManifestSha256
        JobReceiptPath = $jobReceiptPath
        JobReceiptSha256 = [string]$Evidence.jobReceiptSha256
        ArtifactManifestPath = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { [string]$Evidence.artifactManifestPath } else { $null }
        ArtifactManifestSha256 = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { [string]$Evidence.artifactManifestSha256 } else { $null }
        CommandLogPath = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { [string]$Evidence.commandLogPath } else { $null }
        CommandLogSha256 = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { [string]$Evidence.commandLogSha256 } else { $null }
        CorrectnessResultsPath = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { [string]$Evidence.correctnessResultsPath } else { $null }
        CorrectnessResultsSha256 = if ($proofProfile -ceq 'use-baseline-correctness-120-cold') { [string]$Evidence.correctnessResultsSha256 } else { $null }
        CacheRoot = $cacheRoot
        GlobalCacheRoot = $globalCacheRoot
        InputCount = @($stageManifest.InputsBefore).Count
    }
}

function Get-LedgerCacheBinding {
    param(
        [Parameter(Mandatory)][object]$StageEvidence,
        [Parameter(Mandatory)][object]$Target
    )
    if ($Target.Shape -ceq 'ledger-native-facade-cache') {
        if ($StageEvidence.ProofProfile -cnotin @('native-facade-consumed', 'native-facade-consumed-13') -or
            [string]$Target.OwnerId -cne [string]$StageEvidence.StageName -or
            [string]$Target.Candidate -cne [string]$StageEvidence.Candidate -or
            [int]$StageEvidence.UidBits -notin @(128, 256) -or
            $Target.CacheKind -cnotin @('cache', 'global-cache')) {
            throw 'The NativeFacade cache target lacks its exact paired ManagedProbe proof.'
        }
        if ($Target.CacheKind -ceq 'cache') { return $StageEvidence.CacheRoot }
        return $StageEvidence.GlobalCacheRoot
    }
    if ($Target.Shape -ceq 'ledger-native-concurrent-cache') {
        if ($StageEvidence.ProofProfile -cne 'owner-concurrent-correctness' -or
            [string]$Target.Candidate -cnotin @('c2', 'b2')) {
            throw 'The OwnerConcurrent cache target lacks matching candidate-specific correctness evidence.'
        }
        if ($Target.CacheKind -ceq 'cache') { return $StageEvidence.CacheRoots[[string]$Target.Candidate] }
        return $StageEvidence.GlobalCacheRoots[[string]$Target.Candidate]
    }
    if ($Target.Shape -ceq 'ledger-independent-source-cache') {
        if ($StageEvidence.ProofProfile -cne 'independent-source-correctness' -or
            [string]$Target.Candidate -cnotin @('128', '256')) {
            throw 'The IndependentSource cache target lacks matching UID-profile correctness evidence.'
        }
        if ($Target.CacheKind -ceq 'compiler-cache') { return $StageEvidence.CacheRoots[[string]$Target.Candidate] }
        if ($Target.CacheKind -ceq 'global-cache') { return $StageEvidence.GlobalCacheRoots[[string]$Target.Candidate] }
        throw 'The IndependentSource cache target has an unsupported cache kind.'
    }
    if ($Target.Shape -ceq 'ledger-native-operations-cache' -and
        $StageEvidence.ProofProfile -cne 'native-operations-correctness') {
        throw 'The NativeOperations cache target lacks its matching correctness profile.'
    }
    if ($Target.CacheKind -ceq 'cache') { return $StageEvidence.CacheRoot }
    return $StageEvidence.GlobalCacheRoot
}

function Resolve-FreshCachePath {
    param(
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][string]$ExpectedBatchId,
        [Parameter(Mandatory)][string]$ValidationRoot
    )
    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(':') -or $RelativePath.Contains('*') -or $RelativePath.Contains('?')
    ) {
        throw "Cache allowlist path must be a literal relative path: $RelativePath"
    }
    $segments = $RelativePath.Replace('/', '\').Trim('\').Split('\')
    $guidPattern = '^(?:[A-Fa-f0-9]{32}|[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{4}-[A-Fa-f0-9]{12})$'
    if ($segments.Length -lt 4 -or $segments[0] -cne 'batches' -or $segments[1] -cne $ExpectedBatchId -or $segments[2] -cne 'scratch') {
        throw "Cache allowlist path must stay under this batch's scratch root: $RelativePath"
    }

    $shape = $null
    $ownerId = $null
    $cacheKind = $null
    $candidate = $null
    if (
        $segments.Length -eq 5 -and $segments[1] -ceq 'ledger-owner-a1' -and
        $segments[3] -cin @(
            'NativeFacade-b2-128-a2', 'NativeFacade-b2-256-a2', 'NativeFacade-c2-256-a2',
            'NativeFacade-b2-128-a3', 'NativeFacade-b2-256-a3', 'NativeFacade-c2-256-a3'
        ) -and
        $segments[4] -cin @('cache', 'global-cache')
    ) {
        $shape = 'ledger-native-facade-cache'
        $ownerId = $segments[3]
        $cacheKind = $segments[4]
        $candidate = ([regex]::Match($segments[3], '^NativeFacade-(c2|b2)-').Groups[1].Value)
    }
    elseif (
        $segments.Length -eq 5 -and $segments[1] -ceq 'ledger-owner-a1' -and
        $segments[3] -cmatch '^Native-[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -and
        $segments[4] -cin @('cache', 'global-cache')
    ) {
        $shape = 'ledger-native-cache'
        $ownerId = $segments[3]
        $cacheKind = $segments[4]
    }
    elseif (
        $segments.Length -eq 5 -and $segments[1] -ceq 'ledger-owner-a1' -and
        $segments[3] -cmatch '^NativeOperations-operation-(?:c2|b2)-(?:clean|work)-[A-Za-z0-9][A-Za-z0-9._-]{0,47}$' -and
        $segments[4] -cin @('cache', 'global-cache')
    ) {
        $shape = 'ledger-native-operations-cache'
        $ownerId = $segments[3]
        $cacheKind = $segments[4]
    }
    elseif (
        $segments.Length -eq 6 -and $segments[1] -ceq 'ledger-owner-a1' -and
        $segments[3] -cmatch '^OwnerConcurrent-[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -and
        $segments[4] -cin @('cache', 'global-cache') -and $segments[5] -cin @('c2', 'b2')
    ) {
        $shape = 'ledger-native-concurrent-cache'
        $ownerId = $segments[3]
        $cacheKind = $segments[4]
        $candidate = $segments[5]
    }
    elseif (
        $segments.Length -eq 6 -and $segments[1] -ceq 'ledger-owner-a1' -and
        $segments[3] -cmatch '^IndependentSource-[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -and
        $segments[4] -cin @('compiler-cache', 'global-cache') -and $segments[5] -cin @('128', '256')
    ) {
        $shape = 'ledger-independent-source-cache'
        $ownerId = $segments[3]
        $cacheKind = $segments[4]
        $candidate = $segments[5]
    }
    elseif (
        $segments.Length -eq 6 -and $segments[3] -ceq 'fresh-package-consumers' -and
        $segments[4] -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -and $segments[5] -in @('nuget', 'bin', 'obj')
    ) {
        $shape = 'fresh-package-consumer'
        $ownerId = $segments[4]
        $cacheKind = $segments[5]
    }
    elseif ($segments.Length -eq 4 -and $segments[3] -ceq 'packages') {
        $shape = 'shared-fresh-package-restore'
        $ownerId = 'batch'
        $cacheKind = 'packages'
    }
    elseif (
        $segments.Length -eq 6 -and $segments[3] -ceq 'consumers' -and $segments[4] -match $guidPattern -and
        $segments[5] -ceq 'packages'
    ) {
        $shape = 'gpu-package-consumer-cache'
        $ownerId = $segments[4]
        $cacheKind = 'packages'
    }
    elseif (
        $segments.Length -eq 7 -and $segments[3] -ceq 'consumers' -and $segments[4] -match $guidPattern -and
        $segments[5] -in @('ReadWriteCore', 'SubscriptionCore') -and $segments[6] -in @('bin', 'obj')
    ) {
        $shape = 'gpu-package-consumer-output'
        $ownerId = $segments[4]
        $cacheKind = "$($segments[5])/$($segments[6])"
    }
    elseif (
        $segments.Length -eq 5 -and $segments[3] -match $guidPattern -and
        $segments[4] -in @('nuget-packages', 'zig-cache', 'zig-global-cache', 'zig-helper-cache', 'zig-helper-global-cache')
    ) {
        $shape = 'sdk-fresh-cache'
        $ownerId = $segments[3]
        $cacheKind = $segments[4]
    }
    elseif (
        $segments.Length -eq 6 -and $segments[3] -match $guidPattern -and
        $segments[4] -in @('consumer', 'specialization') -and $segments[5] -in @('bin', 'obj')
    ) {
        $shape = 'sdk-package-consumer-output'
        $ownerId = $segments[3]
        $cacheKind = "$($segments[4])/$($segments[5])"
    }
    if ($null -eq $shape) {
        throw "Path is not one of the declared fresh validation cache layouts: $RelativePath"
    }
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $ValidationRoot ($segments -join '\')))
    $prefix = ([System.IO.Path]::GetFullPath($ValidationRoot)).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The cache allowlist path escapes the validation workspace: $RelativePath"
    }
    Assert-NoReparseComponents -Path $fullPath -Root $ValidationRoot
    [pscustomobject]@{
        RelativePath = $RelativePath.Replace('\', '/')
        FullPath = $fullPath
        OwnerId = $ownerId
        CacheKind = $cacheKind
        Candidate = $candidate
        Shape = $shape
    }
}

function Get-WorksetBytes {
    param(
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][int]$MaxNodes,
        [Parameter(Mandatory)][int]$MaxMilliseconds
    )
    $accounting = Get-TreeAccounting -Path $ValidationRoot -MaxNodes $MaxNodes -MaxMilliseconds $MaxMilliseconds
    if ($accounting.ReparsePaths.Count -gt 0) {
        throw 'The validation workset contains reparse points; size and prune boundaries are not trusted.'
    }
    [long]$accounting.Bytes
}

function Get-BytesSum {
    param([AllowEmptyCollection()][object[]]$Items = @())
    [long]$sum = 0
    foreach ($item in $Items) { $sum += [long]$item.Bytes }
    $sum
}

# Validate commands before Prepare can invoke retention or create a batch.
if ($Apply -and $Mode -ne 'Prune') { throw '-Apply is valid only with -Mode Prune.' }
if ($Mode -in @('Prepare', 'Complete')) { Assert-Name -Value $BatchId -ParameterName 'BatchId' }
if ($Mode -eq 'Prepare') { Assert-Name -Value $ConsumerId -ParameterName 'ConsumerId' }

$workspaceRootFull = [System.IO.Path]::GetFullPath($WorkspaceRoot)
Assert-WorkspaceRoot -Path $workspaceRootFull
$validationRoot = Join-Path $workspaceRootFull '.validation'
$batchesRoot = Join-Path $validationRoot 'batches'
$maintenanceRoot = Join-Path $PSScriptRoot '..\maintenance'
Import-Module (Join-Path $maintenanceRoot 'GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking
if ([string]::IsNullOrWhiteSpace($RetentionPolicyPath)) {
    $RetentionPolicyPath = Join-Path $maintenanceRoot 'generated-workspace-policy.json'
}
$retentionPolicy = Get-Content -LiteralPath $RetentionPolicyPath -Raw | ConvertFrom-Json
$retentionPolicy.roots = @($retentionPolicy.roots) + @([pscustomobject]@{ id = 'validation-batches'; path = $batchesRoot; excludedNames = @() })
if (-not $PSBoundParameters.ContainsKey('ReferenceRoots')) {
    $repositoryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $ReferenceRoots = @((Join-Path $repositoryPath '.local'),
        (Join-Path (Split-Path -Parent $workspaceRootFull) 'Resource-Manager-Development\.validation\batches'),
        (Join-Path (Split-Path -Parent $workspaceRootFull) 'FreedomPoint\.validation\batches')) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }
}
# Lifecycle protection tracks owners; per-file hashes stay in input manifests.
$References = @(& (Join-Path $PSScriptRoot 'ConvertTo-ValidationLifecycleReferences.ps1') -References $References -Policy $retentionPolicy -ReferenceRoots $ReferenceRoots)
$retentionResult = $null
if ($Mode -eq 'Prepare' -and (Test-Path -LiteralPath $batchesRoot -PathType Container)) {
    # Retire only explicitly completed, unreferenced batches before capacity admission.
    $retentionResult = Invoke-GeneratedWorkspaceRetention -RootPath $batchesRoot -Policy $retentionPolicy -ReferenceRoots $ReferenceRoots -Apply
}
$sharedArtifactsRoot = Join-Path $validationRoot 'shared-artifacts'
$sharedManagedCacheRoot = Join-Path $sharedArtifactsRoot 'managed-cache'
$retentionReceiptsRoot = Join-Path $sharedArtifactsRoot 'retention-receipts'
$workspaceVolume = Get-DriveSnapshot -Path $workspaceRootFull
$fVolume = Get-DriveSnapshot -Path 'F:\'
$maxWorksetBytes = [long]($MaxWorksetGiB * 1GB)
$reserveFreeBytes = [long]($ReserveFreeGiB * 1GB)
$expectedGrowthBytes = [long]($ExpectedGrowthGiB * 1GB)
$worksetBytes = if ($Mode -eq 'Complete') {
    [long]0
} else {
    Get-WorksetBytes -ValidationRoot $validationRoot -MaxNodes $MaxAccountingNodes -MaxMilliseconds $MaxAccountingMilliseconds
}

switch ($Mode) {
    'Inspect' {
        $canStart = ($worksetBytes + $expectedGrowthBytes -le $maxWorksetBytes) -and ($workspaceVolume.AvailableBytes -ge ($reserveFreeBytes + $expectedGrowthBytes))
        Write-JsonResult ([ordered]@{
            Mode = 'Inspect'
            WorkspaceRoot = $workspaceRootFull
            ValidationRoot = $validationRoot
            SharedArtifactsRoot = $sharedArtifactsRoot
            WorksetBytes = $worksetBytes
            AccountingNodeLimit = $MaxAccountingNodes
            AccountingTimeLimitMilliseconds = $MaxAccountingMilliseconds
            MaxWorksetBytes = $maxWorksetBytes
            ExpectedGrowthBytes = $expectedGrowthBytes
            ReserveFreeBytes = $reserveFreeBytes
            CanPrepare = $canStart
            WorkspaceVolumeFreeBytes = $workspaceVolume.AvailableBytes
            WorkspaceVolumeTotalBytes = $workspaceVolume.TotalBytes
            FVolumeFreeBytes = $fVolume.AvailableBytes
            FVolumeTotalBytes = $fVolume.TotalBytes
        })
    }

    'Prepare' {
        Assert-Name -Value $BatchId -ParameterName 'BatchId'
        Assert-Name -Value $ConsumerId -ParameterName 'ConsumerId'
        if ($worksetBytes + $expectedGrowthBytes -gt $maxWorksetBytes) {
            throw "Start capacity preflight would exceed its configured bound: current=$worksetBytes expectedGrowth=$expectedGrowthBytes maximum=$maxWorksetBytes."
        }
        if ($workspaceVolume.AvailableBytes -lt ($reserveFreeBytes + $expectedGrowthBytes)) {
            throw "Insufficient free space on $($workspaceVolume.Drive): available=$($workspaceVolume.AvailableBytes) reserve=$reserveFreeBytes expectedGrowth=$expectedGrowthBytes."
        }

        $batchRoot = Join-Path (Join-Path $validationRoot 'batches') $BatchId
        $batchCreatedHere = $false
        try {
            if (Test-Path -LiteralPath $batchRoot) {
                $batchState = Get-GeneratedWorkspaceState -RootPath $batchesRoot -WorkspacePath $batchRoot
                if ($null -eq $batchState -or $batchState.state -ne 'running') {
                    throw "Batch is unknown or completed; use a new BatchId or explicitly adopt a verified legacy batch: $batchRoot"
                }
            } else {
                New-Item -ItemType Directory -Path $batchesRoot -Force | Out-Null
                $null = New-GeneratedWorkspace -RootPath $batchesRoot -WorkspacePath $batchRoot -Purpose 'validation-batch' -References $References -Policy $retentionPolicy -ReferenceRoots $ReferenceRoots
                $batchCreatedHere = $true
            }
            $isolatedScratchRoot = Join-Path $batchRoot 'scratch'
            $freshConsumerRoot = Join-Path (Join-Path $isolatedScratchRoot 'fresh-package-consumers') $ConsumerId
            $freshNuGet = Join-Path $freshConsumerRoot 'nuget'
            $freshBin = Join-Path $freshConsumerRoot 'bin'
            $freshObj = Join-Path $freshConsumerRoot 'obj'
            $sharedNuGet = Join-Path $sharedManagedCacheRoot 'nuget'
            $sharedBin = Join-Path $sharedManagedCacheRoot 'bin'
            $sharedObj = Join-Path $sharedManagedCacheRoot 'obj'

            if (Test-Path -LiteralPath $freshConsumerRoot) {
                throw "Fresh consumer scratch already exists; use a new BatchId or ConsumerId: $freshConsumerRoot"
            }

            foreach ($directory in @($sharedNuGet, $sharedBin, $sharedObj, $freshNuGet, $freshBin, $freshObj, $retentionReceiptsRoot)) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
            }

            $worksetBytes = Get-WorksetBytes -ValidationRoot $validationRoot -MaxNodes $MaxAccountingNodes -MaxMilliseconds $MaxAccountingMilliseconds
            $workspaceVolume = Get-DriveSnapshot -Path $workspaceRootFull
            if ($worksetBytes -gt $maxWorksetBytes -or $workspaceVolume.AvailableBytes -lt $reserveFreeBytes) {
                throw "Prepared scratch directories exceed the start capacity bound or free-space reserve: used=$worksetBytes bound=$maxWorksetBytes free=$($workspaceVolume.AvailableBytes) reserve=$reserveFreeBytes."
            }

        }
        catch {
            $prepareFailure = $_
            # A failed Prepare owns no Job or useful result. Conclude only the
            # fresh batch created by this call, never a reused running batch.
            if ($batchCreatedHere) {
                try {
                    $null = Complete-GeneratedWorkspace -RootPath $batchesRoot -WorkspacePath $batchRoot -Outcome failed -References @() -Policy $retentionPolicy -ReferenceRoots $ReferenceRoots
                }
                catch {
                    throw "Prepare failed: $($prepareFailure.Exception.Message) Batch closure also failed: $($_.Exception.Message)"
                }
            }
            throw $prepareFailure
        }

        Write-JsonResult ([ordered]@{
            Mode = 'Prepare'
            Retention = $retentionResult
            WorkspaceRoot = $workspaceRootFull
            BatchId = $BatchId
            ConsumerId = $ConsumerId
            SharedArtifactsRoot = $sharedArtifactsRoot
            SharedManagedCacheRoot = $sharedManagedCacheRoot
            SharedManagedNuGetPackages = $sharedNuGet
            SharedManagedBin = $sharedBin
            SharedManagedObj = $sharedObj
            IsolatedScratchRoot = $isolatedScratchRoot
            FreshPackageConsumerRoot = $freshConsumerRoot
            FreshNuGetPackages = $freshNuGet
            FreshBin = $freshBin
            FreshObj = $freshObj
            WorksetBytes = $worksetBytes
            MaxWorksetBytes = $maxWorksetBytes
            ReserveFreeBytes = $reserveFreeBytes
            WorkspaceVolumeFreeBytes = $workspaceVolume.AvailableBytes
            WorkspaceVolumeTotalBytes = $workspaceVolume.TotalBytes
            FVolumeFreeBytes = $fVolume.AvailableBytes
            FVolumeTotalBytes = $fVolume.TotalBytes
        })
    }

    'Complete' {
        Assert-Name -Value $BatchId -ParameterName 'BatchId'
        # The batch owner calls this after its final result is persisted and all Jobs are drained.
        $batchRoot = Join-Path $batchesRoot $BatchId
        $completed = Complete-GeneratedWorkspace -RootPath $batchesRoot -WorkspacePath $batchRoot -Outcome $Outcome -References $References -Policy $retentionPolicy -ReferenceRoots $ReferenceRoots
        Write-JsonResult ([ordered]@{ Mode = 'Complete'; BatchId = $BatchId; Outcome = $Outcome; Retention = $completed })
    }

    'Prune' {
        if ([string]::IsNullOrWhiteSpace($ManifestPath)) { throw '-ManifestPath is required for pruning.' }
        $manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
        if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) { throw "Prune manifest is missing: $manifestFullPath" }
        $manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
        if (
            [int]$manifest.schemaVersion -ne 1 -or
            [System.IO.Path]::GetFullPath([string]$manifest.workspaceRoot) -ine $workspaceRootFull
        ) {
            throw 'Prune manifest schema or workspace root does not match this invocation.'
        }
        Assert-Name -Value ([string]$manifest.batchId) -ParameterName 'manifest.batchId'
        $verifyProperty = $manifest.PSObject.Properties['finalVerify']
        $allowlistProperty = $manifest.PSObject.Properties['freshConsumerCacheDirectories']
        $ledgerProperty = $manifest.PSObject.Properties['ledgerChecks']
        $cacheRelativePaths = @()
        if ($null -ne $allowlistProperty) { $cacheRelativePaths += @($allowlistProperty.Value) }
        if ($null -eq $allowlistProperty -or $cacheRelativePaths.Count -lt 1) {
            throw 'Pruning requires a nonempty explicit cache-directory allowlist.'
        }
        if ($cacheRelativePaths.Count -gt 128) { throw 'Prune allowlist is larger than the 128-target bound.' }
        $ledgerMode = $null -ne $ledgerProperty
        $verifyEvidence = $null
        $ledgerEntries = @()
        $ledgerEvidence = @()
        if ($ledgerMode) {
            if ($null -ne $verifyProperty) { throw 'A Prune manifest cannot mix finalVerify and ledgerChecks evidence.' }
            $ledgerEntries = @($ledgerProperty.Value)
            if ($ledgerEntries.Count -lt 1 -or $ledgerEntries.Count -gt 128) {
                throw 'ledgerChecks must contain one to 128 explicitly listed Native stage evidence entries.'
            }
            $stageNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            $validatedLedger = [System.Collections.Generic.List[object]]::new()
            foreach ($ledgerEntry in $ledgerEntries) {
                $stageEvidence = Get-LedgerCheckEvidence -Evidence $ledgerEntry `
                    -BatchId ([string]$manifest.batchId) -ValidationRoot $validationRoot
                if (-not $stageNames.Add([string]$stageEvidence.StageName)) {
                    throw "ledgerChecks repeats Native stage '$($stageEvidence.StageName)'."
                }
                $validatedLedger.Add($stageEvidence)
            }
            $ledgerEvidence = @($validatedLedger.ToArray())
            $evidenceKind = 'ledger-checks'
        }
        else {
            if ($null -eq $verifyProperty) {
                throw 'Pruning requires final stage evidence or a ledgerChecks evidence array.'
            }
            $verifyEvidence = Get-VerifyEvidence -Evidence $verifyProperty.Value
            $evidenceKind = $verifyEvidence.Kind
        }
        $prunePlanTimer = [System.Diagnostics.Stopwatch]::StartNew()
        $totalPruneNodes = 0
        $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $ledgerStagesWithTargets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $plan = [System.Collections.Generic.List[object]]::new()
        foreach ($relativePath in $cacheRelativePaths) {
            if ($prunePlanTimer.ElapsedMilliseconds -gt $MaxAccountingMilliseconds) {
                throw "Prune exceeded its configured planning time bound: elapsedMs=$($prunePlanTimer.ElapsedMilliseconds)/$MaxAccountingMilliseconds."
            }
            $target = Resolve-FreshCachePath -RelativePath ([string]$relativePath) `
                -ExpectedBatchId ([string]$manifest.batchId) -ValidationRoot $validationRoot
            if (-not $seen.Add($target.FullPath)) { throw "Duplicate cache allowlist path: $relativePath" }
            if ($ledgerMode) {
                if ($target.Shape -cnotin @('ledger-native-cache', 'ledger-native-operations-cache', 'ledger-native-concurrent-cache', 'ledger-independent-source-cache', 'ledger-native-facade-cache')) {
                    throw "ledger-checks only allows exact approved evidence-bound cache roots: $relativePath"
                }
                $boundStages = @($ledgerEvidence | Where-Object { $_.StageName -ceq $target.OwnerId })
                if ($boundStages.Count -ne 1) { throw "The Native cache target has no unique hashed stage evidence: $relativePath" }
                $boundRoot = Get-LedgerCacheBinding -StageEvidence $boundStages[0] -Target $target
                $null = Assert-LedgerExactPath -Actual $target.FullPath -Expected $boundRoot -Description 'Native cache allowlist target'
                $null = $ledgerStagesWithTargets.Add([string]$target.OwnerId)
            }
            elseif ($target.Shape -cin @('ledger-native-cache', 'ledger-native-operations-cache', 'ledger-native-concurrent-cache', 'ledger-independent-source-cache', 'ledger-native-facade-cache')) {
                throw 'Ledger cache roots require their matching ledgerChecks evidence.'
            }
            if (-not (Test-Path -LiteralPath $target.FullPath -PathType Container)) {
                $plan.Add([pscustomobject]@{
                    RelativePath = $target.RelativePath
                    FullPath = $target.FullPath
                    OwnerId = $target.OwnerId
                    CacheKind = $target.CacheKind
                    Shape = $target.Shape
                    Bytes = [long]0
                    Status = 'missing'
                    Protected = @()
                    NodeCount = 0
                    ElapsedMilliseconds = 0
                    Accounting = $null
                })
                continue
            }
            $accounting = Get-TreeAccounting -Path $target.FullPath -IncludeEntries -MaxNodes $MaxAccountingNodes -MaxMilliseconds $MaxAccountingMilliseconds
            $totalPruneNodes += [int]$accounting.NodeCount
            if ($totalPruneNodes -gt $MaxAccountingNodes -or $prunePlanTimer.ElapsedMilliseconds -gt $MaxAccountingMilliseconds) {
                throw "Prune exceeded its configured planning bound (nodes=$totalPruneNodes/$MaxAccountingNodes elapsedMs=$($prunePlanTimer.ElapsedMilliseconds)/$MaxAccountingMilliseconds)."
            }
            $protected = @($accounting.ReparsePaths) + @($accounting.ProtectedPaths)
            $occupancy = $null
            if ($protected.Count -eq 0) {
                $totalPruneNodes += [int]$accounting.Files.Count
                if ($totalPruneNodes -gt $MaxAccountingNodes -or $prunePlanTimer.ElapsedMilliseconds -gt $MaxAccountingMilliseconds) {
                    throw "Prune exceeded its configured planning bound during occupancy checks (nodes=$totalPruneNodes/$MaxAccountingNodes elapsedMs=$($prunePlanTimer.ElapsedMilliseconds)/$MaxAccountingMilliseconds)."
                }
                $occupancy = Test-CacheFilesUnoccupied -FilePaths @($accounting.Files) -CacheRoot $target.FullPath `
                    -ValidationRoot $validationRoot -OperationTimer $prunePlanTimer -MaxMilliseconds $MaxAccountingMilliseconds
                if (-not $occupancy.Unoccupied) { $protected += "Busy or unreadable cache file: $($occupancy.Path)" }
            }
            $status = if ($accounting.ReparsePaths.Count -gt 0 -or $accounting.ProtectedPaths.Count -gt 0) {
                'preserved-protected-material'
            } elseif ($null -ne $occupancy -and -not $occupancy.Unoccupied) {
                'preserved-occupied'
            } else { 'eligible' }
            $plan.Add([pscustomobject]@{
                RelativePath = $target.RelativePath
                FullPath = $target.FullPath
                OwnerId = $target.OwnerId
                CacheKind = $target.CacheKind
                Shape = $target.Shape
                Bytes = [long]$accounting.Bytes
                Status = $status
                Protected = $protected
                NodeCount = $accounting.NodeCount
                ElapsedMilliseconds = $accounting.ElapsedMilliseconds
                Accounting = $accounting
            })
        }
        if ($ledgerMode -and $ledgerStagesWithTargets.Count -ne $ledgerEvidence.Count) {
            throw 'Every ledgerChecks stage must bind at least one explicit cache root in the allowlist.'
        }

        if (-not $Apply) {
            $dryRunResult = [ordered]@{
                Mode = 'Prune'
                DryRun = $true
                ApplyRequired = $true
                EvidenceKind = $evidenceKind
                BatchId = [string]$manifest.batchId
            }
            if ($ledgerMode) {
                $dryRunResult.LedgerStages = @($ledgerEvidence | Select-Object ProofProfile, NativeProbe, OperationMode, Candidate, UidBits, StageName, CacheOwnerStageName, RunName, ManagedRunName, ManagedStageName, ManagedReceiptPath, ManagedReceiptSha256, NativeDllPath, NativeDllSha256, ReceiptPath, ReceiptSha256, BoundedExecutionReceiptPath, BoundedExecutionReceiptSha256, StageManifestPath, StageManifestSha256, ExecutionJobInputManifestPath, ExecutionJobInputManifestSha256, ExecutionJobManifestPath, ExecutionJobManifestSha256, JobReceiptPath, JobReceiptSha256, ArtifactManifestPath, ArtifactManifestSha256, CommandLogPath, CommandLogSha256, CorrectnessResultsPath, CorrectnessResultsSha256, InputCount)
            }
            else {
                $dryRunResult.VerifyReceipt = $verifyEvidence.ReceiptPath
                $dryRunResult.OfficialResult = $verifyEvidence.OfficialResultPath
            }
            $dryRunResult.EligibleBytes = Get-BytesSum -Items @($plan | Where-Object Status -eq 'eligible')
            $dryRunResult.CacheDirectories = @($plan | Select-Object RelativePath, FullPath, Bytes, Status, Protected, NodeCount, ElapsedMilliseconds)
            Write-JsonResult $dryRunResult
            break
        }

        if ($ledgerMode) {
            $ledgerEvidence = @($ledgerEntries | ForEach-Object {
                Get-LedgerCheckEvidence -Evidence $_ -BatchId ([string]$manifest.batchId) -ValidationRoot $validationRoot
            })
        }
        else {
            $null = Get-VerifyEvidence -Evidence $verifyProperty.Value
        }
        $eligible = @($plan | Where-Object Status -eq 'eligible')
        $plannedBytes = Get-BytesSum -Items $eligible
        $receiptId = [guid]::NewGuid().ToString('N')
        $retentionReceiptPath = Join-Path $retentionReceiptsRoot ("prune-{0}-{1}.json" -f $manifest.batchId, $receiptId)
        New-Item -ItemType Directory -Path $retentionReceiptsRoot -Force | Out-Null
        $receipt = [ordered]@{
            SchemaVersion = 1
            Status = 'applying'
            BatchId = [string]$manifest.batchId
            ManifestPath = $manifestFullPath
            ManifestSha256 = (Get-FileHash -LiteralPath $manifestFullPath -Algorithm SHA256).Hash
            EvidenceKind = $evidenceKind
        }
        if ($ledgerMode) {
            $receipt.LedgerStages = @($ledgerEvidence | Select-Object ProofProfile, NativeProbe, OperationMode, Candidate, UidBits, StageName, CacheOwnerStageName, RunName, ManagedRunName, ManagedStageName, ManagedReceiptPath, ManagedReceiptSha256, NativeDllPath, NativeDllSha256, ReceiptPath, ReceiptSha256, BoundedExecutionReceiptPath, BoundedExecutionReceiptSha256, StageManifestPath, StageManifestSha256, ExecutionJobInputManifestPath, ExecutionJobInputManifestSha256, ExecutionJobManifestPath, ExecutionJobManifestSha256, JobReceiptPath, JobReceiptSha256, ArtifactManifestPath, ArtifactManifestSha256, CommandLogPath, CommandLogSha256, CorrectnessResultsPath, CorrectnessResultsSha256, InputCount)
        }
        else {
            $receipt.StageReceiptPath = $verifyEvidence.ReceiptPath
            $receipt.StageReceiptSha256 = $verifyEvidence.ReceiptSha256
            $receipt.OfficialResultPath = $verifyEvidence.OfficialResultPath
            $receipt.OfficialResultSha256 = $verifyEvidence.OfficialResultSha256
            $receipt.StageJobReceiptPath = $verifyEvidence.JobReceiptPath
            $receipt.StageJobReceiptSha256 = $verifyEvidence.JobReceiptSha256
        }
        $receipt.StartedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $receipt.PlannedBytes = $plannedBytes
        $receipt.RemovedBytes = [long]0
        $receipt.Removed = @()
        $receipt.Preserved = @($plan | Where-Object Status -ne 'eligible' | Select-Object RelativePath, FullPath, Bytes, Status, Protected, NodeCount, ElapsedMilliseconds)
        $receipt.Errors = @()
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($retentionReceiptPath, (ConvertTo-Json -InputObject $receipt -Depth 10), $utf8)

        $removed = [System.Collections.Generic.List[object]]::new()
        $errors = [System.Collections.Generic.List[string]]::new()
        $applyTimer = [System.Diagnostics.Stopwatch]::StartNew()
        foreach ($target in $eligible) {
            try {
                $again = Resolve-FreshCachePath -RelativePath $target.RelativePath `
                    -ExpectedBatchId ([string]$manifest.batchId) -ValidationRoot $validationRoot
                if ($again.FullPath -ine $target.FullPath -or -not (Test-Path -LiteralPath $again.FullPath -PathType Container)) {
                    throw 'Cache target changed after planning.'
                }
                if ($ledgerMode) {
                    $currentLedgerEvidence = @($ledgerEntries | ForEach-Object {
                        Get-LedgerCheckEvidence -Evidence $_ -BatchId ([string]$manifest.batchId) -ValidationRoot $validationRoot
                    })
                    $boundStages = @($currentLedgerEvidence | Where-Object { $_.StageName -ceq $target.OwnerId })
                    if ($boundStages.Count -ne 1) { throw 'Ledger cache target lost its unique stage evidence before deletion.' }
                    $boundRoot = Get-LedgerCacheBinding -StageEvidence $boundStages[0] -Target $again
                    $null = Assert-LedgerExactPath -Actual $target.FullPath -Expected $boundRoot -Description 'Revalidated Native cache target'
                }
                Remove-VerifiedCacheTree -Target $target -ValidationRoot $validationRoot -OperationTimer $applyTimer
                $removed.Add([pscustomobject]@{ RelativePath = $target.RelativePath; Bytes = [long]$target.Bytes })
            }
            catch { $errors.Add("$($target.RelativePath): $($_.Exception.Message)") }
        }

        $receipt.Status = if ($errors.Count -gt 0) { 'partial' } elseif (@($plan | Where-Object Status -ne 'eligible').Count -gt 0) { 'applied-with-preserved-paths' } else { 'applied' }
        $receipt.FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        $receipt.Removed = @($removed.ToArray())
        $receipt.RemovedBytes = Get-BytesSum -Items $removed.ToArray()
        $receipt.Errors = @($errors.ToArray())
        [System.IO.File]::WriteAllText($retentionReceiptPath, (ConvertTo-Json -InputObject $receipt -Depth 10), $utf8)
        Write-JsonResult ([ordered]@{
            Mode = 'Prune'
            DryRun = $false
            Status = $receipt.Status
            EvidenceKind = $evidenceKind
            BatchId = [string]$manifest.batchId
            RetentionReceiptPath = $retentionReceiptPath
            PlannedBytes = $plannedBytes
            RemovedBytes = $receipt.RemovedBytes
            Removed = $receipt.Removed
            Preserved = $receipt.Preserved
            Errors = $receipt.Errors
        })
        if ($errors.Count -gt 0) { throw "Prune was partial; inspect $retentionReceiptPath." }
    }
}
