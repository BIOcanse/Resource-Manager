[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigPath,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal

function Resolve-PathFrom([string]$Value, [string]$BaseDirectory) {
    if ([IO.Path]::IsPathRooted($Value)) { return [IO.Path]::GetFullPath($Value) }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Value))
}

function Read-Json([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { throw "JSON file does not exist: $Path" }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable -Depth 64
}

function Test-ConfiguredValue([object]$Actual, [object]$Expected) {
    if ($Expected -is [bool]) { return $Actual -is [bool] -and [bool]$Actual -eq $Expected }
    return [string]$Actual -ceq [string]$Expected
}

function Assert-ProcessState([object]$Wait) {
    $external = Read-Json $Wait.statePath
    if ([int]$external.pid -ne [int]$Wait.processId -or
        [Math]::Abs([double]$external.creationUnixSeconds - [double]$Wait.creationUnixSeconds) -gt 0.1) {
        throw 'The process state file does not identify the configured process instance.'
    }
    return $external
}

function Assert-TerminalState([object]$Wait) {
    $external = Assert-ProcessState $Wait
    foreach ($name in $Wait.terminalStateEquals.Keys) {
        if (-not (Test-ConfiguredValue $external[$name] $Wait.terminalStateEquals[$name])) {
            throw "Process terminal state check failed for '$name'."
        }
    }
    return $external
}

function Get-ExactLiveProcess([object]$Wait) {
    try { $process = [Diagnostics.Process]::GetProcessById([int]$Wait.processId) }
    catch [ArgumentException] { return $null }
    try {
        if ($process.HasExited) { $process.Dispose(); return $null }
        $created = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
        if ($process.ProcessName -ine [string]$Wait.processName -or
            [Math]::Abs($created - [double]$Wait.creationUnixSeconds) -gt 0.1) {
            throw 'The configured process identity no longer matches this PID.'
        }
        return $process
    } catch {
        $process.Dispose()
        throw
    }
}

function Get-Step([System.Collections.IDictionary]$State, [string]$Id) {
    $matches = @($State.steps | Where-Object { $_.id -ceq $Id })
    if ($matches.Count -ne 1) { throw "Pipeline state has no unique step '$Id'." }
    return $matches[0]
}

function Get-ConfiguredPlainCommand([System.Collections.IDictionary]$Raw, [string]$BaseDirectory) {
    if ($Raw.Contains('executionJob') -and $null -ne $Raw['executionJob']) { throw 'A reused step must be a plain command.' }
    $executableName = [string]$Raw.executable
    if ([IO.Path]::IsPathRooted($executableName) -or $executableName.Contains('\') -or $executableName.Contains('/')) {
        $executable = Resolve-PathFrom $executableName $BaseDirectory
    } else {
        $executable = (Get-Command -Name $executableName -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    }
    $workingDirectory = Resolve-PathFrom ([string]$Raw.workingDirectory) $BaseDirectory
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in $Raw.arguments) {
        if ($argument -isnot [string]) { throw 'Reused plain-command arguments must be strings.' }
        $arguments.Add($argument)
    }
    return [pscustomobject]@{
        executable = [IO.Path]::GetFullPath($executable)
        workingDirectory = $workingDirectory
        arguments = $arguments.ToArray()
    }
}

function Get-VerifiedReuseSource([System.Collections.IDictionary]$ReuseFrom, [object]$Command) {
    $sourceStatePath = [IO.Path]::GetFullPath([string]$ReuseFrom.statePath)
    $sourceState = Read-Json $sourceStatePath
    if ([int]$sourceState.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$sourceState.configPath) -or
        [string]$sourceState.configSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Reuse source state has no valid configuration binding: $sourceStatePath"
    }
    $sourceConfigPath = Resolve-PathFrom ([string]$sourceState.configPath) ([IO.Path]::GetDirectoryName($sourceStatePath))
    if (-not [IO.File]::Exists($sourceConfigPath)) { throw "Reuse source configuration is missing: $sourceConfigPath" }
    $sourceConfigSha256 = (Get-FileHash -LiteralPath $sourceConfigPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($sourceConfigSha256 -cne ([string]$sourceState.configSha256).ToUpperInvariant()) {
        throw "Reuse source state no longer matches its configuration hash: $sourceConfigPath"
    }
    $sourceConfig = Read-Json $sourceConfigPath
    if ([int]$sourceConfig.schemaVersion -ne 1) { throw "Unsupported reuse source configuration: $sourceConfigPath" }

    $sourceStepId = [string]$ReuseFrom.stepId
    $sourceStage = Get-Step $sourceState $sourceStepId
    if ([string]$sourceStage.kind -cne 'command' -or [string]$sourceStage.status -cne 'succeeded' -or [int]$sourceStage.exitCode -ne 0) {
        throw "Reuse source step '$sourceStepId' is not a successful plain command."
    }
    $sourceCommands = @($sourceConfig.steps | Where-Object { [string]$_.id -ceq $sourceStepId })
    if ($sourceCommands.Count -ne 1) { throw "Reuse source configuration has no unique command '$sourceStepId'." }
    $sourceCommand = Get-ConfiguredPlainCommand $sourceCommands[0] ([IO.Path]::GetDirectoryName($sourceConfigPath))
    if ($sourceCommand.executable -ine [string]$Command.executable -or
        $sourceCommand.workingDirectory -ine [string]$Command.workingDirectory -or
        $sourceCommand.arguments.Count -ne $Command.arguments.Count) {
        throw "Reuse source command '$sourceStepId' differs from the configured command."
    }
    for ($i = 0; $i -lt $sourceCommand.arguments.Count; $i++) {
        if ($sourceCommand.arguments[$i] -cne [string]$Command.arguments[$i]) {
            throw "Reuse source command '$sourceStepId' arguments differ from the configured command."
        }
    }

    $sourceLogPath = Resolve-PathFrom ([string]$sourceStage.logPath) ([IO.Path]::GetDirectoryName($sourceStatePath))
    if (-not [IO.File]::Exists($sourceLogPath)) { throw "Reuse source log is missing: $sourceLogPath" }
    $sourceLogSha256 = (Get-FileHash -LiteralPath $sourceLogPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($sourceLogSha256 -cne ([string]$ReuseFrom.logSha256).ToUpperInvariant()) {
        throw "Reuse source log hash differs from reuseFrom.logSha256: $sourceLogPath"
    }

    return [ordered]@{
        statePath = $sourceStatePath
        stateSha256 = (Get-FileHash -LiteralPath $sourceStatePath -Algorithm SHA256).Hash.ToUpperInvariant()
        configPath = $sourceConfigPath
        configSha256 = $sourceConfigSha256
        stepId = $sourceStepId
        logPath = $sourceLogPath
        logSha256 = $sourceLogSha256
    }
}

function Write-PipelineState([System.Collections.IDictionary]$State, [string]$Path) {
    $State.updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $pending = $Path + '.pending'
    if ([IO.File]::Exists($pending)) { throw "Unresolved pending state file exists: $pending" }
    [IO.File]::WriteAllText($pending, (ConvertTo-Json -InputObject $State -Depth 32) + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($pending, $Path, [IO.File]::Exists($Path))
}

$script:ExecutionInputMaxFiles = 8192
$script:ExecutionInputMaxDirectories = 32768
$script:ExecutionInputMaxFileBytes = [long]1073741824
$script:ExecutionInputMaxTotalBytes = [long]2147483648
$script:ExecutionOutputMaxFiles = 16
$script:ExecutionOutputMaxFileBytes = [long]2147483648
$script:ExecutionSnapshotMaxBytes = 16777216
$script:ExecutionJobMemoryLimitBytes = [ulong]1610612736
$script:ExecutionJobActiveProcessLimit = 32
$script:ExecutionJobCpuRate = 2500
$script:ExecutionJobTimeoutSeconds = 900
$script:ExecutionJobPollMilliseconds = 100
$script:ExecutionJobMaxOutputBytes = [long]4194304

function Expand-ExecutionTokens([string]$Value, [System.Collections.IDictionary]$Tokens) {
    $result = $Value
    foreach ($key in $Tokens.Keys) { $result = $result.Replace([string]$key, [string]$Tokens[$key]) }
    return $result
}

function Resolve-JobPath([string]$Value, [string]$BaseDirectory, [System.Collections.IDictionary]$Tokens) {
    $expanded = Expand-ExecutionTokens $Value $Tokens
    return Resolve-PathFrom $expanded $BaseDirectory
}

function Get-ExecutionInputPaths([string[]]$Specifications, [System.Collections.IDictionary]$Tokens) {
    if ($Specifications.Count -lt 1 -or $Specifications.Count -gt 128) { throw 'executionJob.inputFiles must contain 1-128 path specifications.' }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($specification in $Specifications) {
        if ([string]::IsNullOrWhiteSpace($specification)) { throw 'executionJob.inputFiles contains an empty path.' }
        $pattern = Expand-ExecutionTokens $specification $Tokens
        $pattern = $pattern.Replace('/', '\')
        if (-not [IO.Path]::IsPathRooted($pattern)) { throw "executionJob input paths must be absolute: $pattern" }
        $wildcardIndex = $pattern.IndexOfAny([char[]]@('*', '?'))
        if ($wildcardIndex -lt 0) {
            $path = [IO.Path]::GetFullPath($pattern)
            if (-not [IO.File]::Exists($path)) { throw "executionJob input file does not exist: $path" }
            $item = [IO.FileInfo]::new($path)
            $item.Refresh()
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "executionJob input file is a reparse point: $path" }
            $null = $paths.Add($item.FullName)
            continue
        }

        $separatorIndex = $pattern.LastIndexOf('\', $wildcardIndex)
        if ($separatorIndex -lt 3) { throw "Unsupported executionJob input glob: $pattern" }
        $root = [IO.Path]::GetFullPath($pattern.Substring(0, $separatorIndex))
        $tail = $pattern.Substring($separatorIndex + 1)
        $recursive = $tail.StartsWith('**\', [StringComparison]::Ordinal)
        if ($recursive) { $tail = $tail.Substring(3) }
        if ([string]::IsNullOrWhiteSpace($tail) -or $tail.Contains('\') -or $tail.Contains('**')) {
            throw "Only directory-local or '**\*.ext' executionJob input globs are supported: $pattern"
        }
        if (-not [IO.Directory]::Exists($root)) { throw "executionJob input glob root does not exist: $root" }

        $pendingDirectories = [Collections.Generic.Stack[string]]::new()
        $pendingDirectories.Push($root)
        $directoryCount = 0
        while ($pendingDirectories.Count -gt 0) {
            $directoryPath = $pendingDirectories.Pop()
            $directoryCount++
            if ($directoryCount -gt $script:ExecutionInputMaxDirectories) { throw "executionJob input directory walk exceeds the fixed $($script:ExecutionInputMaxDirectories)-directory limit." }
            $directory = [IO.DirectoryInfo]::new($directoryPath)
            $directory.Refresh()
            if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "executionJob input glob crosses a reparse directory: $directoryPath" }
            foreach ($filePath in [IO.Directory]::EnumerateFiles($directoryPath, $tail, [IO.SearchOption]::TopDirectoryOnly)) {
                $item = [IO.FileInfo]::new($filePath)
                $item.Refresh()
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "executionJob input file is a reparse point: $filePath" }
                $null = $paths.Add($item.FullName)
                if ($paths.Count -gt $script:ExecutionInputMaxFiles) { throw "executionJob input file count exceeds the fixed $($script:ExecutionInputMaxFiles)-file limit." }
            }
            if ($recursive) {
                foreach ($childPath in [IO.Directory]::EnumerateDirectories($directoryPath)) {
                    $child = [IO.DirectoryInfo]::new($childPath)
                    $child.Refresh()
                    if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "executionJob input glob crosses a reparse directory: $childPath" }
                    $pendingDirectories.Push($child.FullName)
                }
            }
        }
    }
    if ($paths.Count -lt 1 -or $paths.Count -gt $script:ExecutionInputMaxFiles) { throw 'executionJob input expansion is empty or exceeds its fixed file limit.' }
    $orderedPaths = [string[]]@($paths)
    [Array]::Sort($orderedPaths, [StringComparer]::OrdinalIgnoreCase)
    return $orderedPaths
}

function Get-ExecutionInputSnapshot([string[]]$Paths) {
    $rows = [Collections.Generic.List[object]]::new()
    [long]$totalBytes = 0
    foreach ($path in $Paths) {
        $item = [IO.FileInfo]::new($path)
        $item.Refresh()
        if (-not $item.Exists -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "executionJob input disappeared or became unsafe: $path" }
        if ($item.Length -gt $script:ExecutionInputMaxFileBytes) { throw "executionJob input exceeds the fixed per-file byte limit: $path" }
        $totalBytes += [long]$item.Length
        if ($totalBytes -gt $script:ExecutionInputMaxTotalBytes) { throw "executionJob inputs exceed the fixed total byte limit ($($script:ExecutionInputMaxTotalBytes))." }
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        $rows.Add([ordered]@{ path = $item.FullName; length = [long]$item.Length; sha256 = $hash })
        if ($rows.Count -gt $script:ExecutionInputMaxFiles) { throw 'executionJob input file count exceeds the fixed file limit.' }
    }
    return @($rows.ToArray())
}

function Test-ExecutionSnapshotsEqual([object[]]$Left, [object[]]$Right) {
    if ($Left.Count -ne $Right.Count) { return $false }
    for ($i = 0; $i -lt $Left.Count; $i++) {
        if ([string]$Left[$i].path -ine [string]$Right[$i].path -or
            [long]$Left[$i].length -ne [long]$Right[$i].length -or
            [string]$Left[$i].sha256 -cne [string]$Right[$i].sha256) { return $false }
    }
    return $true
}

function Write-ExecutionJson([string]$Path, [object]$Value, [int]$MaxBytes = $script:ExecutionSnapshotMaxBytes) {
    $json = (ConvertTo-Json -InputObject $Value -Depth 24) + "`n"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    if ($bytes.Length -gt $MaxBytes) { throw "executionJob evidence exceeds the fixed $MaxBytes-byte limit: $Path" }
    [IO.File]::WriteAllBytes($Path, $bytes)
    return [ordered]@{ path = [IO.Path]::GetFullPath($Path); length = [long]$bytes.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
}

function Get-ExecutionFileEvidence([string]$Path, [long]$MaximumBytes) {
    $item = [IO.FileInfo]::new($Path)
    $item.Refresh()
    if (-not $item.Exists -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "executionJob output/evidence file is missing or unsafe: $Path" }
    if ($item.Length -eq 0) { throw "executionJob output/evidence file is empty: $Path" }
    if ($item.Length -gt $MaximumBytes) { throw "executionJob output/evidence file exceeds its fixed byte limit: $Path" }
    return [ordered]@{ path = $item.FullName; length = [long]$item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant() }
}

function Read-ExecutionJobReceipts([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return [pscustomobject]@{ present = $false; normalDrain = $false; jobStarted = $false; summary = $null } }
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -gt 8388608) { throw 'ExecutionJob receipt JSONL exceeds its official 8 MiB budget.' }
    [int]$rowCount = 0; [int]$admissionCount = 0; [int]$configuredCount = 0; [int]$assignedCount = 0
    [int]$exitCount = 0; [int]$finalCount = 0; [int]$closedCount = 0; [int]$failureCount = 0
    $exitData = $null; $finalData = $null; $closedData = $null
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $rowCount++
            if ($rowCount -gt 65536) { throw 'ExecutionJob receipt row count exceeds the fixed parser limit.' }
            $row = $line | ConvertFrom-Json -AsHashtable -Depth 16
            switch ([string]$row.kind) {
                'admission' { $admissionCount++ }
                'configured' { $configuredCount++ }
                'gate-assigned' { $assignedCount++ }
                'exit' {
                    $exitCount++; $exitData = $row.data
                    if (-not $exitData.Contains('ExitCode') -or $null -eq $exitData['ExitCode']) { throw 'ExecutionJob exit receipt is missing its ExitCode field.' }
                }
                'final-job' { $finalCount++; $finalData = $row.data }
                'owned-job-closed' { $closedCount++; $closedData = $row.data }
                'failure' { $failureCount++ }
            }
        }
    }
    $normal = $admissionCount -eq 1 -and $configuredCount -eq 1 -and $assignedCount -eq 1 -and $exitCount -eq 1 -and $finalCount -eq 1 -and $closedCount -eq 1 -and
        $failureCount -eq 0 -and [string]$exitData.reason -ceq 'normal' -and [int]$exitData['ExitCode'] -eq 0 -and
        [string]$finalData.reason -ceq 'normal' -and [bool]$finalData.empty -and
        [string]$closedData.reason -ceq 'normal' -and [bool]$closedData.assigned -and [bool]$closedData.killOnCloseConfigured
    $summary = [ordered]@{
        admissionCount = $admissionCount; configuredCount = $configuredCount; gateAssignedCount = $assignedCount
        exitCount = $exitCount; finalJobCount = $finalCount; ownedJobClosedCount = $closedCount
        failureCount = $failureCount; exitReason = if ($exitCount -eq 1) { [string]$exitData.reason } else { $null }
        exitCode = if ($exitCount -eq 1) { [int]$exitData['ExitCode'] } else { $null }
        finalJobEmpty = if ($finalCount -eq 1) { [bool]$finalData.empty } else { $false }
        closeReason = if ($closedCount -eq 1) { [string]$closedData.reason } else { $null }
    }
    return [pscustomobject]@{ present = $true; normalDrain = [bool]$normal; jobStarted = ($assignedCount -eq 1); summary = $summary }
}

function Assert-ReusedExecutionJobStep([System.Collections.IDictionary]$Stage, [object]$Command, [string]$RepositoryRoot) {
    if ($null -eq $Stage.executionJob -or [string]$Stage.executionJob.status -cne 'succeeded') { throw "Successful executionJob step '$($Command.id)' has no accepted execution evidence; use a new config/state." }
    $evidence = $Stage.executionJob
    $scratch = [IO.Path]::GetFullPath([string]$evidence.scratchPath)
    $tokens = [ordered]@{ '{repositoryRoot}' = $RepositoryRoot; '{taskRoot}' = [IO.Path]::GetFullPath([string]$Command.executionJob.taskRoot); '{scratch}' = $scratch; '{outputRoot}' = [IO.Path]::GetFullPath((Join-Path $scratch 'native-prefix\bin')) }
    $inputs = Get-ExecutionInputPaths -Specifications $Command.executionJob.inputFiles -Tokens $tokens
    $manifestEvidence = Get-ExecutionFileEvidence -Path ([string]$evidence.inputManifestPath) -MaximumBytes $script:ExecutionSnapshotMaxBytes
    if ($manifestEvidence.sha256 -cne [string]$evidence.inputManifestSha256) { throw "Successful executionJob step '$($Command.id)' input evidence changed; use a new config/state." }
    $inputManifest = Read-Json ([string]$evidence.inputManifestPath)
    if (-not [bool]$inputManifest.unchanged -or
        -not (Test-ExecutionSnapshotsEqual @($inputManifest.before) @($inputManifest.after))) { throw "Successful executionJob step '$($Command.id)' has inconsistent before/after inputs; use a new config/state." }
    $currentInputs = Get-ExecutionInputSnapshot -Paths $inputs
    if (-not (Test-ExecutionSnapshotsEqual @($inputManifest.after) $currentInputs)) { throw "Successful executionJob step '$($Command.id)' inputs changed since execution; use a new config/state." }
    foreach ($output in @($evidence.outputs)) {
        $current = Get-ExecutionFileEvidence -Path ([string]$output.path) -MaximumBytes $script:ExecutionOutputMaxFileBytes
        if ($current.length -ne [long]$output.length -or $current.sha256 -cne [string]$output.sha256) { throw "Successful executionJob step '$($Command.id)' output changed since execution; use a new config/state." }
    }
    if (@($evidence.outputs).Count -ne @($Command.executionJob.outputFiles).Count) { throw "Successful executionJob step '$($Command.id)' output set differs from its config; use a new config/state." }
    $expectedOutputs = @($Command.executionJob.outputFiles | ForEach-Object { [IO.Path]::GetFullPath((Expand-ExecutionTokens ([string]$_) $tokens)) })
    foreach ($expectedPath in $expectedOutputs) {
        if (@($evidence.outputs | Where-Object { [string]$_.path -ieq $expectedPath }).Count -ne 1) { throw "Successful executionJob step '$($Command.id)' output paths differ from its config; use a new config/state." }
    }
    foreach ($path in @($evidence.executionManifestPath, $evidence.receiptPath)) {
        $expectedHash = if ([string]$path -ieq [string]$evidence.executionManifestPath) { [string]$evidence.executionManifestSha256 } else { [string]$evidence.receiptSha256 }
        if ([string]::IsNullOrWhiteSpace($path) -or [string]::IsNullOrWhiteSpace($expectedHash)) { throw "Successful executionJob step '$($Command.id)' is missing runner evidence; use a new config/state." }
        $current = Get-ExecutionFileEvidence -Path ([string]$path) -MaximumBytes 8388608
        if ($current.sha256 -cne $expectedHash) { throw "Successful executionJob step '$($Command.id)' runner evidence changed; use a new config/state." }
    }
    $receipts = Read-ExecutionJobReceipts -Path ([string]$evidence.receiptPath)
    if (-not $receipts.normalDrain -or -not [bool]$evidence.normalDrain) { throw "Successful executionJob step '$($Command.id)' has no current normal-drain proof; use a new config/state." }
}

function Invoke-ExecutionJobStep([object]$Command, [System.Collections.IDictionary]$Stage, [System.Collections.IDictionary]$State, [string]$StatePath, [string]$StepLogPath, [string]$RepositoryRoot) {
    $job = $Command.executionJob
    $references = @($job.references)
    $referenceRoots = @($job.referenceRoots)
    $managerPath = $job.managerPath
    $prepareLog = Join-Path $job.taskRoot ('execution-job-prepare-' + $job.batchId + '-' + [guid]::NewGuid().ToString('N') + '.log')
    if ($protected.Contains([IO.Path]::GetFullPath($prepareLog))) { throw "executionJob Prepare log path is protected by the freeze manifest: $prepareLog" }
    $entry = [ordered]@{ status = 'preparing'; batchId = $job.batchId; consumerId = $job.consumerId; workspaceRoot = $job.workspaceRoot; prepareLogPath = $prepareLog; runnerInvoked = $false; jobStarted = $false; normalDrain = $false; outputs = @() }
    $Stage.executionJob = $entry
    Write-PipelineState $State $StatePath

    $prepareParameters = [ordered]@{
        Mode = 'Prepare'
        WorkspaceRoot = [string]$job.workspaceRoot
        BatchId = [string]$job.batchId
        ConsumerId = [string]$job.consumerId
        MaxWorksetGiB = [double]$job.maxWorksetGiB
        ExpectedGrowthGiB = [double]$job.expectedGrowthGiB
        ReserveFreeGiB = [double]$job.reserveFreeGiB
        RetentionPolicyPath = [string]$job.retentionPolicyPath
        References = [string[]]$references
        ReferenceRoots = [string[]]$referenceRoots
    }
    $prepareFailure = $null
    try {
        & $managerPath @prepareParameters *> $prepareLog
        $prepareExitCode = 0
    } catch {
        $prepareFailure = $_.Exception.Message
        $prepareExitCode = 1
        $failureText = "`n[Pipeline] Manage-ValidationWorkset Prepare exception: $prepareFailure`n"
        if ([IO.File]::Exists($prepareLog)) {
            [IO.File]::AppendAllText($prepareLog, $failureText, [Text.UTF8Encoding]::new($false))
        } else {
            [IO.File]::WriteAllText($prepareLog, $failureText.TrimStart("`n"), [Text.UTF8Encoding]::new($false))
        }
    }
    $entry.prepareExitCode = $prepareExitCode
    if ($null -ne $prepareFailure) { $entry.prepareFailure = $prepareFailure }
    if ($prepareExitCode -ne 0) { $entry.status = 'prepare-failed'; $entry.prepareLogSha256 = if ([IO.File]::Exists($prepareLog)) { (Get-FileHash -LiteralPath $prepareLog -Algorithm SHA256).Hash.ToUpperInvariant() } else { $null }; Write-PipelineState $State $StatePath; throw "Manage-ValidationWorkset Prepare failed for '$($Command.id)' (exit $prepareExitCode): $prepareFailure" }
    $prepareReceipt = Read-Json $prepareLog
    $expectedBatchRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path (Join-Path $job.workspaceRoot '.validation\batches') $job.batchId) 'scratch'))
    $scratch = [IO.Path]::GetFullPath([string]$prepareReceipt.IsolatedScratchRoot)
    if ($scratch -ine $expectedBatchRoot) { throw "Prepare returned an unexpected isolated scratch root: $scratch" }
    $entry.worksetBatchRoot = [IO.Path]::GetDirectoryName($scratch)
    $entry.scratchPath = $scratch
    $entry.prepareReceipt = Get-ExecutionFileEvidence -Path $prepareLog -MaximumBytes 1048576
    $entry.status = 'prepared'
    Write-PipelineState $State $StatePath

    $executionRoot = Join-Path $scratch ('execution-job-' + $Command.id)
    $receiptRoot = Join-Path $executionRoot 'runs'
    $runDirectory = Join-Path $receiptRoot ([guid]::NewGuid().ToString('N'))
    $outputRoot = Join-Path $scratch 'native-prefix\bin'
    foreach ($directory in @($executionRoot, $receiptRoot, $outputRoot, (Join-Path $scratch 'zig-cache'), (Join-Path $scratch 'zig-global'))) {
        [void][IO.Directory]::CreateDirectory($directory)
    }
    $tokens = [ordered]@{ '{repositoryRoot}' = $RepositoryRoot; '{taskRoot}' = $job.taskRoot; '{scratch}' = $scratch; '{outputRoot}' = $outputRoot; '{executionRoot}' = $executionRoot; '{runDirectory}' = $runDirectory }
    $inputPaths = Get-ExecutionInputPaths -Specifications $job.inputFiles -Tokens $tokens
    $outputPaths = @($job.outputFiles | ForEach-Object { [IO.Path]::GetFullPath((Expand-ExecutionTokens $_ $tokens)) })
    if ($outputPaths.Count -lt 1 -or $outputPaths.Count -gt $script:ExecutionOutputMaxFiles) { throw "executionJob outputFiles must contain 1-$($script:ExecutionOutputMaxFiles) paths." }
    foreach ($outputPath in $outputPaths) {
        $outputPrefix = $scratch.TrimEnd('\') + '\'
        if (-not $outputPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "executionJob outputs must stay beneath their official isolated scratch root: $outputPath" }
        if ($protected.Contains($outputPath)) { throw "executionJob output path is protected by the freeze manifest: $outputPath" }
    }
    foreach ($outputPath in $outputPaths) {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath))
    }

    $preflight = & $job.preflightScript -TaskRoot $job.taskRoot -Scope 'Product' -JobMemoryLimitBytes $script:ExecutionJobMemoryLimitBytes
    foreach ($receiptPath in @($preflight.headroomPath, $preflight.faultReviewPath)) {
        if ([string]::IsNullOrWhiteSpace([string]$receiptPath) -or -not [IO.File]::Exists([string]$receiptPath)) { throw 'Capture-Preflight did not produce fresh Job admission receipts.' }
    }
    $manifestPath = Join-Path $executionRoot 'job-manifest.json'
    $arguments = @($job.arguments | ForEach-Object { Expand-ExecutionTokens ([string]$_) $tokens })
    if ($arguments.Count -gt 128) { throw 'ExecutionJob supports at most 128 arguments.' }
    $environment = @{}
    foreach ($key in $job.environmentVariables.Keys) { $environment[[string]$key] = Expand-ExecutionTokens ([string]$job.environmentVariables[$key]) $tokens }
    $manifest = [ordered]@{
        executionHoldPath = $job.executionHoldPath; faultReviewPath = [string]$preflight.faultReviewPath; headroomPath = [string]$preflight.headroomPath
        allowedReceiptRoot = $receiptRoot; runDirectory = $runDirectory; executable = $job.executable; arguments = $arguments
        workingDirectory = $job.workingDirectory; jobMemoryLimitBytes = $script:ExecutionJobMemoryLimitBytes
        activeProcessLimit = $script:ExecutionJobActiveProcessLimit; cpuRate = $script:ExecutionJobCpuRate
        commitReserveBytes = [ulong]$preflight.commitReserveBytes; physicalReserveBytes = [ulong]$preflight.physicalReserveBytes
        timeoutSeconds = $script:ExecutionJobTimeoutSeconds; pollMilliseconds = $script:ExecutionJobPollMilliseconds
        maxOutputBytes = $script:ExecutionJobMaxOutputBytes; environmentVariables = $environment
    }
    Write-ExecutionJson -Path $manifestPath -Value $manifest -MaxBytes 65536 | Out-Null
    $manifestEvidence = Get-ExecutionFileEvidence -Path $manifestPath -MaximumBytes 65536
    $receiptPath = Join-Path $runDirectory 'receipts.jsonl'
    $controllerLogPath = Join-Path $executionRoot 'controller.log'
    $inputEvidencePath = Join-Path $executionRoot 'input-evidence.json'
    $entry.executionManifestPath = $manifestEvidence.path
    $entry.executionManifestSha256 = $manifestEvidence.sha256
    $entry.receiptPath = $receiptPath
    $entry.controllerLogPath = $controllerLogPath
    $entry.inputManifestPath = $inputEvidencePath
    $entry.outputPaths = $outputPaths
    $entry.status = 'manifest-ready'
    Write-PipelineState $State $StatePath

    $before = Get-ExecutionInputSnapshot -Paths $inputPaths
    $entry.inputCount = $before.Count
    [long]$inputBytes = 0
    foreach ($inputRow in $before) { $inputBytes += [long]$inputRow['length'] }
    $entry.inputBytes = $inputBytes
    $entry.inputBefore = $before
    $entry.status = 'running'
    Write-PipelineState $State $StatePath
    $code = 1
    $runnerError = $null
    try {
        $entry.runnerInvoked = $true
        Write-PipelineState $State $StatePath
        $global:LASTEXITCODE = 0
        & $job.executionJobPath $manifestPath *> $controllerLogPath
        $code = [int]$LASTEXITCODE
    } catch {
        $runnerError = $_.Exception.Message
        $code = 1
    }

    $after = $null
    $inputHashError = $null
    try { $after = Get-ExecutionInputSnapshot -Paths (Get-ExecutionInputPaths -Specifications $job.inputFiles -Tokens $tokens) }
    catch { $inputHashError = $_.Exception.Message }
    $inputsUnchanged = $null -ne $after -and (Test-ExecutionSnapshotsEqual $before $after)
    $inputEvidence = [ordered]@{ before = $before; after = if ($null -ne $after) { $after } else { @() }; unchanged = [bool]$inputsUnchanged; afterError = $inputHashError }
    $inputManifestEvidence = Write-ExecutionJson -Path $inputEvidencePath -Value $inputEvidence
    $receiptParseError = $null
    try { $receipts = Read-ExecutionJobReceipts -Path $receiptPath }
    catch { $receiptParseError = $_.Exception.Message; $receipts = [pscustomobject]@{ present = [IO.File]::Exists($receiptPath); normalDrain = $false; jobStarted = $false; summary = $null } }
    $entry.runnerExitCode = $code
    $entry.runnerError = $runnerError
    $entry.jobStarted = [bool]$receipts.jobStarted
    $entry.normalDrain = [bool]$receipts.normalDrain
    $entry.jobReceiptSummary = $receipts.summary
    $entry.receiptParseError = $receiptParseError
    $entry.inputManifestSha256 = $inputManifestEvidence.sha256
    $entry.inputBefore = $null
    if ($null -ne $after) { $entry.inputAfterCount = $after.Count }
    $entry.inputsUnchanged = [bool]$inputsUnchanged
    if ([IO.File]::Exists($receiptPath)) { $entry.receiptSha256 = (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToUpperInvariant() }
    if ([IO.File]::Exists($controllerLogPath)) { $entry.controllerLogSha256 = (Get-FileHash -LiteralPath $controllerLogPath -Algorithm SHA256).Hash.ToUpperInvariant() }
    $outputs = [Collections.Generic.List[object]]::new()
    foreach ($outputPath in $outputPaths) {
        if ([IO.File]::Exists($outputPath)) { $outputs.Add((Get-ExecutionFileEvidence -Path $outputPath -MaximumBytes $script:ExecutionOutputMaxFileBytes)) }
    }
    $entry.outputs = @($outputs.ToArray())
    $entry.status = if ($code -eq 0 -and $receipts.normalDrain -and $inputsUnchanged -and $outputs.Count -eq $outputPaths.Count -and $null -eq $runnerError) { 'succeeded' } else { 'failed' }
    Write-PipelineState $State $StatePath

    if ($code -ne 0 -or -not $receipts.normalDrain -or -not $inputsUnchanged -or $outputs.Count -ne $outputPaths.Count -or $null -ne $runnerError) {
        $reasons = [Collections.Generic.List[string]]::new()
        if ($code -ne 0) { $reasons.Add("ExecutionJob exited with code $code") }
        if (-not $receipts.normalDrain) { $reasons.Add('ExecutionJob did not produce a verified normal exit/empty/owned-job-closed receipt set') }
        if (-not $inputsUnchanged) { $reasons.Add('Compile input hashes changed or could not be captured across the Job') }
        if ($outputs.Count -ne $outputPaths.Count) { $reasons.Add('One or more configured output files are missing') }
        if ($null -ne $runnerError) { $reasons.Add($runnerError) }
        if ($null -ne $receiptParseError) { $reasons.Add($receiptParseError) }
        if ($null -ne $inputHashError) { $reasons.Add($inputHashError) }
        throw "executionJob step '$($Command.id)' failed: $($reasons -join '; '). Evidence remains in '$scratch'."
    }
    [IO.File]::WriteAllText($StepLogPath, (ConvertTo-Json -InputObject ([ordered]@{ stepId = $Command.id; batchId = $job.batchId; scratchPath = $scratch; exitCode = $code; normalDrain = $receipts.normalDrain; outputs = @($outputs.ToArray()) }) -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}

$fullConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$configDirectory = [IO.Path]::GetDirectoryName($fullConfigPath)
$configHash = (Get-FileHash -LiteralPath $fullConfigPath -Algorithm SHA256).Hash.ToUpperInvariant()
$config = Read-Json $fullConfigPath
if ([int]$config.schemaVersion -ne 1) { throw 'Unsupported pipeline configuration schemaVersion.' }

$statePath = Resolve-PathFrom ([string]$config.statePath) $configDirectory
$lockPath = $statePath + '.lock'
$pendingPath = $statePath + '.pending'
$stateDirectory = [IO.Path]::GetDirectoryName($statePath)

$freezePath = Resolve-PathFrom ([string]$config.protectWritesFromManifest) $configDirectory
$freeze = Read-Json $freezePath
$protected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $protected.Add($freezePath)
foreach ($row in $freeze.files) { $null = $protected.Add([IO.Path]::GetFullPath([string]$row.path)) }

$wait = $null
if ($config.Contains('waitForProcess') -and $null -ne $config.waitForProcess) {
    $raw = $config.waitForProcess
    $wait = [pscustomobject]@{
        stepId = [string]$raw.stepId
        processId = [int]$raw.processId
        processName = [string]$raw.processName
        creationUnixSeconds = [double]$raw.creationUnixSeconds
        statePath = Resolve-PathFrom ([string]$raw.statePath) $configDirectory
        terminalStateEquals = $raw.terminalStateEquals
    }
    if ([string]::IsNullOrWhiteSpace($wait.stepId) -or $wait.processId -le 0 -or
        [string]::IsNullOrWhiteSpace($wait.processName) -or $wait.creationUnixSeconds -le 0 -or
        $wait.terminalStateEquals.Count -eq 0) { throw 'waitForProcess requires an exact process identity and terminal state checks.' }
    if ($wait.statePath -ieq $statePath) { throw 'Pipeline state must not overwrite the waited process state.' }
}

$stepIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$plannedIds = [Collections.Generic.List[string]]::new()
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($null -ne $wait) {
    if (-not $stepIds.Add($wait.stepId)) { throw "Duplicate step id: $($wait.stepId)" }
    $plannedIds.Add($wait.stepId)
}
$commands = [Collections.Generic.List[object]]::new()
foreach ($raw in $config.steps) {
    $id = [string]$raw.id
    if ([string]::IsNullOrWhiteSpace($id) -or -not $stepIds.Add($id)) { throw "Missing or duplicate step id: $id" }
    if ($raw.Contains('executionJob') -and $null -ne $raw['executionJob']) {
        if ($raw.Contains('executable') -or $raw.Contains('workingDirectory') -or $raw.Contains('arguments') -or $raw.Contains('reuseFrom')) { throw "Step '$id' cannot mix executionJob with a plain command or reuseFrom." }
        $rawJob = $raw.executionJob
        $phaseRootValue = [string]$rawJob.taskRoot
        if ([string]::IsNullOrWhiteSpace($phaseRootValue)) { throw "executionJob.taskRoot is required for step '$id'." }
        $phaseRoot = Resolve-PathFrom $phaseRootValue $configDirectory
        $initialTokens = [ordered]@{ '{repositoryRoot}' = $repositoryRoot; '{taskRoot}' = $phaseRoot }
        $resolveJobField = {
            param([string]$Value, [string]$Name)
            if ([string]::IsNullOrWhiteSpace($Value)) { throw "executionJob.$Name is required for step '$id'." }
            Resolve-JobPath $Value $configDirectory $initialTokens
        }
        $managerPath = & $resolveJobField ([string]$rawJob.managerPath) 'managerPath'
        $preflightPath = & $resolveJobField ([string]$rawJob.preflightPath) 'preflightPath'
        $retentionPolicyPath = & $resolveJobField ([string]$rawJob.retentionPolicyPath) 'retentionPolicyPath'
        $executionJobPath = & $resolveJobField ([string]$rawJob.executionJobPath) 'executionJobPath'
        $executionHoldPath = & $resolveJobField ([string]$rawJob.executionHoldPath) 'executionHoldPath'
        $executable = & $resolveJobField ([string]$rawJob.executable) 'executable'
        $workingDirectory = & $resolveJobField ([string]$rawJob.workingDirectory) 'workingDirectory'
        foreach ($file in @($managerPath, $preflightPath, $retentionPolicyPath, $executionJobPath, $executionHoldPath, $executable)) {
            if (-not [IO.File]::Exists($file)) { throw "executionJob required file does not exist for step '$id': $file" }
        }
        if (-not [IO.Directory]::Exists($phaseRoot) -or -not [IO.Directory]::Exists($workingDirectory)) { throw "executionJob task/working directory does not exist for step '$id'." }
        $batchId = [string]$rawJob.batchId
        $consumerId = [string]$rawJob.consumerId
        if ($batchId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $consumerId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw "executionJob batchId and consumerId must be 1-64 safe name characters for step '$id'." }
        if ([string]::IsNullOrWhiteSpace([string]$rawJob.workspaceRoot)) { throw "executionJob.workspaceRoot is required for step '$id'." }
        $workspaceRoot = Resolve-PathFrom ([string]$rawJob.workspaceRoot) $configDirectory
        if (-not [IO.Directory]::Exists($workspaceRoot)) { throw "executionJob workspaceRoot does not exist for step '$id': $workspaceRoot" }
        $maxWorksetGiB = [double]$rawJob.maxWorksetGiB
        $expectedGrowthGiB = [double]$rawJob.expectedGrowthGiB
        $reserveFreeGiB = [double]$rawJob.reserveFreeGiB
        if ($maxWorksetGiB -lt 1 -or $maxWorksetGiB -gt 1024 -or $expectedGrowthGiB -lt 0 -or $expectedGrowthGiB -gt 1024 -or $reserveFreeGiB -lt 0 -or $reserveFreeGiB -gt 1024) { throw "executionJob workset capacity values are out of range for step '$id'." }
        $arguments = [Collections.Generic.List[string]]::new()
        foreach ($argument in $rawJob.arguments) {
            if ($argument -isnot [string]) { throw "executionJob arguments must be strings for step '$id'." }
            $arguments.Add($argument)
        }
        if ($arguments.Count -lt 1 -or $arguments.Count -gt 128) { throw "executionJob requires 1-128 command arguments for step '$id'." }
        $inputFiles = @($rawJob.inputFiles | ForEach-Object { [string]$_ })
        $outputFiles = @($rawJob.outputFiles | ForEach-Object { [string]$_ })
        if ($outputFiles.Count -lt 1 -or $outputFiles.Count -gt $script:ExecutionOutputMaxFiles) { throw "executionJob.outputFiles must contain 1-$($script:ExecutionOutputMaxFiles) paths for step '$id'." }
        $references = @($rawJob.references | ForEach-Object { [string]$_ })
        $referenceRoots = @($rawJob.referenceRoots | ForEach-Object { [string]$_ })
        if ($references.Count -lt 1 -or $referenceRoots.Count -lt 1) { throw "executionJob references and referenceRoots are required for step '$id'." }
        foreach ($path in @($references) + @($referenceRoots)) { if ([string]::IsNullOrWhiteSpace([string]$path)) { throw "executionJob reference paths cannot be empty for step '$id'." } }
        $references = @($references | ForEach-Object { Resolve-JobPath $_ $configDirectory $initialTokens })
        $referenceRoots = @($referenceRoots | ForEach-Object { Resolve-JobPath $_ $configDirectory $initialTokens })
        $environment = [ordered]@{}
        if ($rawJob.Contains('environmentVariables') -and $null -ne $rawJob['environmentVariables']) {
            if ($rawJob.environmentVariables.Count -gt 32) { throw "executionJob supports at most 32 explicit environment variables for step '$id'." }
            foreach ($key in $rawJob.environmentVariables.Keys) {
                $name = [string]$key
                $value = $rawJob.environmentVariables[$key]
                if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('=') -or $value -isnot [string]) { throw "executionJob environment entry is invalid for step '$id'." }
                $environment[$name] = [string]$value
            }
        }
        $normalizedJob = [ordered]@{
            taskRoot = $phaseRoot; workspaceRoot = $workspaceRoot; batchId = $batchId; consumerId = $consumerId
            maxWorksetGiB = $maxWorksetGiB; expectedGrowthGiB = $expectedGrowthGiB; reserveFreeGiB = $reserveFreeGiB
            managerPath = $managerPath; preflightScript = $preflightPath; retentionPolicyPath = $retentionPolicyPath
            executionJobPath = $executionJobPath; executionHoldPath = $executionHoldPath
            executable = $executable; workingDirectory = $workingDirectory; arguments = $arguments.ToArray()
            inputFiles = $inputFiles; outputFiles = $outputFiles; references = $references; referenceRoots = $referenceRoots
            environmentVariables = $environment
        }
        $planTokens = [ordered]@{ '{repositoryRoot}' = $repositoryRoot; '{taskRoot}' = $phaseRoot }
        $null = Get-ExecutionInputPaths -Specifications $inputFiles -Tokens $planTokens
        foreach ($argument in $arguments) {
            if ($argument -match '\{(?!repositoryRoot\}|taskRoot\}|scratch\}|outputRoot\}|executionRoot\}|runDirectory\})') { throw "executionJob argument contains an unsupported token for step '$id': $argument" }
        }
        $commands.Add([pscustomobject]@{ id = $id; kind = 'execution-job'; executionJob = $normalizedJob })
        $plannedIds.Add($id)
        continue
    }
    $executableName = [string]$raw.executable
    if ([IO.Path]::IsPathRooted($executableName) -or $executableName.Contains('\') -or $executableName.Contains('/')) {
        $executable = Resolve-PathFrom $executableName $configDirectory
        if (-not [IO.File]::Exists($executable)) { throw "Executable does not exist: $executable" }
    } else {
        $executable = (Get-Command -Name $executableName -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    }
    $workingDirectory = Resolve-PathFrom ([string]$raw.workingDirectory) $configDirectory
    if (-not [IO.Directory]::Exists($workingDirectory)) { throw "Working directory does not exist: $workingDirectory" }
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in $raw.arguments) {
        if ($argument -isnot [string]) { throw "Arguments must be strings for step '$id'." }
        $arguments.Add($argument)
    }
    $reuseFrom = $null
    if ($raw.Contains('reuseFrom')) {
        $rawReuse = $raw['reuseFrom']
        if ($rawReuse -isnot [System.Collections.IDictionary] -or
            -not $rawReuse.Contains('statePath') -or -not $rawReuse.Contains('stepId') -or -not $rawReuse.Contains('logSha256') -or
            [string]::IsNullOrWhiteSpace([string]$rawReuse.statePath) -or
            [string]::IsNullOrWhiteSpace([string]$rawReuse.stepId) -or
            [string]$rawReuse.logSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "reuseFrom for step '$id' requires statePath, stepId and a SHA-256 logSha256."
        }
        $reuseStatePath = Resolve-PathFrom ([string]$rawReuse.statePath) $configDirectory
        if ($reuseStatePath -ieq $statePath) { throw "Step '$id' cannot reuse from its own pipeline state." }
        $reuseFrom = [ordered]@{
            statePath = $reuseStatePath
            stepId = [string]$rawReuse.stepId
            logSha256 = ([string]$rawReuse.logSha256).ToUpperInvariant()
        }
    }
    $commands.Add([pscustomobject]@{ id = $id; kind = 'command'; executable = $executable; workingDirectory = $workingDirectory; arguments = $arguments.ToArray(); reuseFrom = $reuseFrom })
    $plannedIds.Add($id)
}
if ($commands.Count -eq 0) { throw 'At least one command step is required.' }

$logPaths = @{}
foreach ($id in $plannedIds) { $logPaths[$id] = [IO.Path]::GetFullPath($statePath + '.' + $id + '.log') }
foreach ($outputPath in @($statePath, $pendingPath, $lockPath) + @($logPaths.Values)) {
    if ($protected.Contains([IO.Path]::GetFullPath([string]$outputPath))) {
        throw "Pipeline output path is present in the protected freeze: $outputPath"
    }
}

$loadedState = $null
if ([IO.File]::Exists($statePath)) {
    $loadedState = Read-Json $statePath
    if ([int]$loadedState.schemaVersion -ne 1 -or
        [string]$loadedState.configPath -ine $fullConfigPath -or
        [string]$loadedState.configSha256 -cne $configHash) {
        throw 'Pipeline state belongs to a different configuration or schema.'
    }
    $stateIds = @($loadedState.steps | ForEach-Object { [string]$_.id })
    if ($stateIds.Count -ne $plannedIds.Count) { throw 'Pipeline state step list differs from the configuration.' }
    for ($i = 0; $i -lt $plannedIds.Count; $i++) {
        if ($stateIds[$i] -cne $plannedIds[$i]) { throw 'Pipeline state step order differs from the configuration.' }
    }
}

$liveProcess = $null
$externalState = $null
if ($null -ne $wait) {
    $externalState = Assert-ProcessState $wait
    $liveProcess = Get-ExactLiveProcess $wait
}
if ($PlanOnly) {
    $executionPlans = [Collections.Generic.List[object]]::new()
    foreach ($command in $commands | Where-Object { $_.kind -ceq 'execution-job' }) {
        $job = $command.executionJob
        $tokens = [ordered]@{ '{repositoryRoot}' = $repositoryRoot; '{taskRoot}' = $job.taskRoot }
        $inputPaths = Get-ExecutionInputPaths -Specifications $job.inputFiles -Tokens $tokens
        $executionPlans.Add([ordered]@{
            id = $command.id; workspaceRoot = $job.workspaceRoot; batchId = $job.batchId; consumerId = $job.consumerId
            executable = $job.executable; workingDirectory = $job.workingDirectory; inputCount = $inputPaths.Count
            inputSpecifications = @($job.inputFiles); outputTemplates = @($job.outputFiles)
            jobMemoryLimitBytes = $script:ExecutionJobMemoryLimitBytes; activeProcessLimit = $script:ExecutionJobActiveProcessLimit
            cpuRate = $script:ExecutionJobCpuRate; timeoutSeconds = $script:ExecutionJobTimeoutSeconds
        })
    }
    $summary = [ordered]@{
        planOnly = $true
        configuration = $fullConfigPath
        statePath = $statePath
        currentPipelineStatus = if ($null -ne $loadedState) { $loadedState.status } else { 'not-started' }
        currentStepId = if ($null -ne $loadedState) { $loadedState.currentStepId } else { $null }
        waitForProcess = if ($null -ne $wait) { @{ processId = $wait.processId; processName = $wait.processName; isRunning = ($null -ne $liveProcess); observedStateStatus = $externalState.status } } else { $null }
        steps = @($plannedIds)
        executionJobs = @($executionPlans.ToArray())
        preparesWorksets = $false
        terminalStateReady = if ($null -ne $wait -and $null -eq $liveProcess) { try { $null = Assert-TerminalState $wait; $true } catch { $false } } else { $null }
        writesState = $false
        launchesCommands = $false
    }
    if ($null -ne $liveProcess) { $liveProcess.Dispose() }
    ConvertTo-Json -InputObject $summary -Depth 8
    return
}

if (-not [IO.Directory]::Exists($stateDirectory)) { [void][IO.Directory]::CreateDirectory($stateDirectory) }
$lock = $null
try {
    try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw "Another pipeline invocation owns the state lock: $lockPath" }

    # Reload under the exclusive state lock so two invocations cannot both launch the same next step.
    $loadedState = if ([IO.File]::Exists($statePath)) { Read-Json $statePath } else { $null }
    if ($null -ne $loadedState -and ([string]$loadedState.configSha256 -cne $configHash -or [string]$loadedState.configPath -ine $fullConfigPath)) {
        throw 'Pipeline state changed to a different configuration before the lock was acquired.'
    }
    if ([IO.File]::Exists($pendingPath)) { throw "Unresolved pending pipeline state exists: $pendingPath" }

    if ($null -eq $loadedState) {
        $stages = [Collections.Generic.List[object]]::new()
        foreach ($id in $plannedIds) {
            $command = @($commands | Where-Object { $_.id -ceq $id } | Select-Object -First 1)
            $kind = if ($null -ne $wait -and $id -ceq $wait.stepId) { 'process-wait' } elseif ($command.Count -eq 1) { [string]$command[0].kind } else { 'command' }
            $stages.Add([ordered]@{ id = $id; kind = $kind; status = 'pending'; startedUtc = $null; completedUtc = $null; exitCode = $null; logPath = $logPaths[$id]; failure = $null })
        }
        $state = [ordered]@{
            schemaVersion = 1; status = 'running'; configPath = $fullConfigPath; configSha256 = $configHash
            startedUtc = [DateTimeOffset]::UtcNow.ToString('O'); updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            currentStepId = $null; steps = $stages.ToArray(); failure = $null; completedUtc = $null
        }
    } else {
        $state = $loadedState
        if ([string]$state.status -ceq 'completed') {
            foreach ($command in $commands | Where-Object { $_.kind -ceq 'execution-job' }) {
                $stage = Get-Step $state $command.id
                if ([string]$stage.status -ceq 'succeeded') { Assert-ReusedExecutionJobStep -Stage $stage -Command $command -RepositoryRoot $repositoryRoot }
            }
            Write-Output "Pipeline already completed with current execution evidence: $statePath"
            return
        }
        if ([string]$state.status -ceq 'failed') { throw "Pipeline failed at '$($state.currentStepId)'; it will not retry that step." }
        if ([string]$state.status -cne 'running') { throw "Unknown pipeline state: $($state.status)" }
        if (-not [string]::IsNullOrWhiteSpace([string]$state.currentStepId)) {
            $interrupted = Get-Step $state ([string]$state.currentStepId)
            if ($interrupted.kind -cne 'process-wait') {
                $state.status = 'failed'
                $state.failure = "Step '$($interrupted.id)' was interrupted; automatic retry is disabled."
                $interrupted.status = 'failed'
                $interrupted.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                $interrupted.failure = $state.failure
                Write-PipelineState $state $statePath
                throw $state.failure
            }
        }
    }

    try {
        if ($null -ne $wait) {
            $stage = Get-Step $state $wait.stepId
            if ([string]$stage.status -cne 'succeeded') {
                $state.status = 'running'; $state.currentStepId = $wait.stepId
                $stage.status = 'running'; if ($null -eq $stage.startedUtc) { $stage.startedUtc = [DateTimeOffset]::UtcNow.ToString('O') }
                Write-PipelineState $state $statePath

                $process = Get-ExactLiveProcess $wait
                $exitCode = $null
                if ($null -ne $process) {
                    Write-Output "Waiting for $($wait.processName) PID $($wait.processId)."
                    while (-not $process.WaitForExit(1000)) { }
                    $exitCode = [int]$process.ExitCode
                    $process.Dispose()
                    if ($exitCode -ne 0) { throw "Waited process exited with code $exitCode." }
                }
                $terminal = Assert-TerminalState $wait
                $stage.status = 'succeeded'; $stage.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                $stage.exitCode = $exitCode; $stage.terminalStateStatus = [string]$terminal.status
                $state.currentStepId = $null
                Write-PipelineState $state $statePath
            }
        }

        foreach ($command in $commands) {
            $stage = Get-Step $state $command.id
            if ([string]$stage.status -ceq 'succeeded') {
                if ($command.kind -ceq 'execution-job') { Assert-ReusedExecutionJobStep -Stage $stage -Command $command -RepositoryRoot $repositoryRoot }
                continue
            }
            if ([string]$stage.status -cne 'pending') { throw "Step '$($command.id)' has unresolved state '$($stage.status)'." }
            $log = [string]$logPaths[$command.id]
            if ([IO.File]::Exists($log)) { throw "Step log already exists without a successful state: $log" }

            $state.status = 'running'; $state.currentStepId = $command.id
            $stage.status = 'running'; $stage.startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            Write-PipelineState $state $statePath
            Write-Output "Starting configured step '$($command.id)'."
            $failure = $null
            try {
                if ($command.kind -ceq 'execution-job') {
                    Invoke-ExecutionJobStep -Command $command -Stage $stage -State $state -StatePath $statePath -StepLogPath $log -RepositoryRoot $repositoryRoot
                    $code = 0
                } elseif ($null -ne $command.reuseFrom) {
                    $reuse = Get-VerifiedReuseSource -ReuseFrom $command.reuseFrom -Command $command
                    if ([IO.Path]::GetFullPath($reuse.logPath) -ieq [IO.Path]::GetFullPath($log)) {
                        throw 'A reused source log cannot be the destination step log.'
                    }
                    [IO.File]::Copy([string]$reuse.logPath, $log, $false)
                    if ((Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash -ine [string]$reuse.logSha256 -or
                        (Get-FileHash -LiteralPath ([string]$reuse.logPath) -Algorithm SHA256).Hash -ine [string]$reuse.logSha256 -or
                        (Get-FileHash -LiteralPath ([string]$reuse.statePath) -Algorithm SHA256).Hash -ine [string]$reuse.stateSha256 -or
                        (Get-FileHash -LiteralPath ([string]$reuse.configPath) -Algorithm SHA256).Hash -ine [string]$reuse.configSha256) {
                        throw 'Reuse source state, configuration, or log changed while copying the accepted step log.'
                    }
                    $reuse['reusedUtc'] = [DateTimeOffset]::UtcNow.ToString('O')
                    $stage['reusedFrom'] = $reuse
                    $code = 0
                    Write-Output "Reused successful configured step '$($reuse.stepId)' from '$($reuse.statePath)' for '$($command.id)'."
                } else {
                    $argumentList = [string[]]$command.arguments
                    $global:LASTEXITCODE = 0
                    Push-Location -LiteralPath $command.workingDirectory
                    try { & $command.executable @argumentList *> $log }
                    finally { Pop-Location }
                    $code = [int]$LASTEXITCODE
                }
            } catch {
                $failure = $_.Exception.Message
                $code = 1
                if (-not [IO.File]::Exists($log)) { [IO.File]::WriteAllText($log, $failure + "`n", [Text.UTF8Encoding]::new($false)) }
            }
            $stage.completedUtc = [DateTimeOffset]::UtcNow.ToString('O'); $stage.exitCode = $code
            if ($null -ne $failure -or $code -ne 0) {
                $stage.status = 'failed'; $stage.failure = if ($null -ne $failure) { $failure } else { "Command exited with code $code." }
                $state.status = 'failed'; $state.failure = $stage.failure
                if ($stage.Contains('executionJob') -and $null -ne $stage['executionJob']) { $stage['executionJob'].failure = $stage.failure }
                Write-PipelineState $state $statePath
                throw "Configured step '$($command.id)' failed: $($stage.failure)"
            }
            $stage.status = 'succeeded'; $stage.failure = $null; $state.currentStepId = $null
            Write-PipelineState $state $statePath
        }

        $state.status = 'completed'; $state.currentStepId = $null; $state.failure = $null
        $state.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Write-PipelineState $state $statePath
        Write-Output "Pipeline completed: $statePath"
    } catch {
        if ([string]$state.status -ceq 'running') {
            $state.status = 'failed'; $state.failure = $_.Exception.Message
            if (-not [string]::IsNullOrWhiteSpace([string]$state.currentStepId)) {
                $failed = Get-Step $state ([string]$state.currentStepId)
                if ([string]$failed.status -ceq 'running') { $failed.status = 'failed'; $failed.completedUtc = [DateTimeOffset]::UtcNow.ToString('O'); $failed.failure = $state.failure }
            }
            Write-PipelineState $state $statePath
        }
        throw
    }
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if ($null -ne $liveProcess) { $liveProcess.Dispose() }
}
