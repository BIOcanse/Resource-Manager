#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    # Defaults to <ToolsRoot>\CodeQL\2.27.2\codeql\codeql.exe (scripts/dev/MachineRoots.psm1).
    [string]$CodeQL,
    [Parameter(Mandatory)][string]$PolicyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $CodeQL) {
    Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
    $CodeQL = Join-Path (Get-MachineRoot -Name ToolsRoot) 'CodeQL\2.27.2\codeql\codeql.exe'
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'StaticAnalysis.Queue.ps1')
$script:maintenancePath = Join-Path $repository 'scripts/maintenance/Manage-GeneratedWorkspace.ps1'
$expectedVersion = '2.27.2'
$queryPackVersion = '1.10.1'
$failure = $null
$registered = $false
$completed = $false
$databaseOutcome = $null
$process = $null
$originalPriority = $null
$priorityChanged = $false
$statusPath = $null
$databaseWorkspace = $null
$databasePath = $null
$sarifPath = $null
$sarifHashBeforeCleanup = $null
$sarifProduced = $false

$status = [ordered]@{
    schemaVersion = 1
    repository = $repository
    output = $null
    codeql = $null
    expectedVersion = $expectedVersion
    actualVersion = $null
    versionExitCode = $null
    databaseWorkspace = $null
    databasePath = $null
    databaseCreateExitCode = $null
    databaseAnalyzeExitCode = $null
    querySuite = 'csharp-security-and-quality.qls'
    querySpecifier = 'codeql/csharp-queries@1.10.1:codeql-suites/csharp-security-and-quality.qls'
    queryPackVersion = $queryPackVersion
    buildMode = 'none'
    dotnetBuildInvoked = $false
    dependencyInference = 'No compiler build tracing is used. CodeQL no-build extraction infers dependencies and optional generated source from repository project metadata; it does not establish the exact compiler build graph. Any restore activity performed by CodeQL is visible in the database-create log.'
    queryPackDownloadsDisabled = $true
    codeScanningConfig = $null
    pathsIgnore = @()
    sourceArchivePath = $null
    sourceArchiveVerification = 'not-run'
    sourceArchiveEntryCount = 0
    sourceArchiveViolations = @()
    sarif = $null
    sarifSha256 = $null
    resultPreservedAfterDatabaseRemoval = $null
    databaseOutcome = $null
    databaseReclaimed = $false
    databaseReclaimedBy = $null
    priorityRequested = 'BelowNormal'
    priorityApplied = $false
    nativeCommands = @()
    failure = $null
}

function Write-Status {
    if ([string]::IsNullOrWhiteSpace($script:statusPath)) { return }
    $json = ConvertTo-Json -InputObject $script:status -Depth 16
    [IO.File]::WriteAllText($script:statusPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Add-Failure([string]$Message) {
    if ([string]::IsNullOrWhiteSpace($script:failure)) {
        $script:failure = $Message
    }
    else {
        $script:failure = $script:failure + [Environment]::NewLine + $Message
    }
    $script:status.failure = $script:failure
    Write-Status
}

function Invoke-CodeQL {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )

    $exitCode = Invoke-StaticAnalysisQueued -FilePath $script:codeqlPath -Arguments $Arguments -Log $LogPath `
        -Label ("rm C# CodeQL $Stage") -Repository $repository
    $record = [pscustomobject][ordered]@{
        stage = $Stage
        executable = $script:codeqlPath
        arguments = $Arguments
        exitCode = $exitCode
        log = $LogPath
    }
    $script:status.nativeCommands = @($script:status.nativeCommands) + @($record)
    switch ($Stage) {
        'version' { $script:status.versionExitCode = $exitCode }
        'database create' { $script:status.databaseCreateExitCode = $exitCode }
        'database analyze' { $script:status.databaseAnalyzeExitCode = $exitCode }
    }
    Write-Status
    if ($exitCode -ne 0) {
        throw "CodeQL $Stage failed with exit code $exitCode. See $LogPath"
    }
    return $exitCode
}

function Invoke-WorkspaceLifecycle {
    param(
        [Parameter(Mandatory)][ValidateSet('New', 'Complete', 'Prune')][string]$Action,
        [Parameter(Mandatory)][string]$LogPath,
        [string]$WorkspacePath,
        [string]$Purpose,
        [ValidateSet('success', 'failed')][string]$Outcome,
        [string]$SelectedPolicyPath = $script:policyPath
    )

    $arguments = @{
        Action = $Action
        RootId = 'rm-local'
        PolicyPath = $SelectedPolicyPath
    }
    if (-not [string]::IsNullOrWhiteSpace($WorkspacePath)) { $arguments.WorkspacePath = $WorkspacePath }
    if (-not [string]::IsNullOrWhiteSpace($Purpose)) { $arguments.Purpose = $Purpose }
    if (-not [string]::IsNullOrWhiteSpace($Outcome)) { $arguments.Outcome = $Outcome }
    if ($Action -eq 'Prune') {
        $arguments.WorkspacePaths = @($script:databaseWorkspace)
        $arguments.Apply = $true
    }

    # The maintenance entry is a PowerShell script. Reset the native-code slot
    # so its explicit `exit 1` on failure cannot be confused with an older code.
    $global:LASTEXITCODE = 0
    & $script:maintenancePath @arguments *> $LogPath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Generated-workspace $Action failed with exit code $exitCode. See $LogPath"
    }
    return $exitCode
}

function Get-ExcludedSourceReason([string]$Path) {
    $normalized = $Path.Replace('\', '/').Trim('/')
    # No-build extraction synthesizes this compilation helper in its own DB.
    # It is not a repository source admitted past paths-ignore.
    $databaseLeaf = Split-Path -Leaf $script:databaseWorkspace
    if ($normalized.Contains("/.local/$databaseLeaf/database/working/implicitUsings/", [StringComparison]::OrdinalIgnoreCase) -and
        $normalized.EndsWith('/GlobalUsings.g.cs', [StringComparison]::OrdinalIgnoreCase)) { return $null }
    if ($normalized -match '(?i)(^|/)tests/Validation/NonAdaptedOptimizationLab(/|$)') {
        return 'tests/Validation/NonAdaptedOptimizationLab'
    }
    if ($normalized -match '(?i)(^|/)(\.local|artifact|artifacts|bin|obj)(/|$)') {
        return 'generated directory'
    }
    return $null
}

function Test-CodeQLSourceExclusions([string]$Database) {
    $archivePath = Join-Path $Database 'src.zip'
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        $entryCount = 0
        $violations = [Collections.Generic.List[string]]::new()
        try {
            foreach ($entry in $archive.Entries) {
                if ([string]::IsNullOrWhiteSpace($entry.Name)) { continue }
                if (++$entryCount -gt 750000) { throw 'CodeQL source archive exceeded the 750000-entry verification bound.' }
                $reason = Get-ExcludedSourceReason $entry.FullName
                if ($null -ne $reason -and $violations.Count -lt 25) {
                    $violations.Add("${reason}: $($entry.FullName)")
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        $script:status.sourceArchivePath = $archivePath
        $script:status.sourceArchiveEntryCount = $entryCount
        $script:status.sourceArchiveViolations = @($violations.ToArray())
        $script:status.sourceArchiveVerification = if ($violations.Count -eq 0) { 'verified' } else { 'failed' }
        Write-Status
        if ($violations.Count -gt 0) {
            throw ('CodeQL source archive contains excluded files: ' + ($violations -join '; '))
        }
        return
    }

    $sourceDirectory = Join-Path $Database 'src'
    if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
        $queue = [Collections.Generic.Queue[string]]::new()
        $queue.Enqueue($sourceDirectory)
        $nodes = 0
        $violations = [Collections.Generic.List[string]]::new()
        $incomplete = $false
        while ($queue.Count -gt 0) {
            $current = $queue.Dequeue()
            foreach ($item in [IO.DirectoryInfo]::new($current).EnumerateFileSystemInfos()) {
                if (++$nodes -gt 750000) { throw 'CodeQL source tree exceeded the 750000-entry verification bound.' }
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $incomplete = $true
                    continue
                }
                if (($item.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    $queue.Enqueue($item.FullName)
                    continue
                }
                $relative = $item.FullName.Substring($sourceDirectory.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $reason = Get-ExcludedSourceReason $relative
                if ($null -ne $reason -and $violations.Count -lt 25) {
                    $violations.Add("${reason}: $relative")
                }
            }
        }

        $script:status.sourceArchivePath = $sourceDirectory
        $script:status.sourceArchiveEntryCount = $nodes
        $script:status.sourceArchiveViolations = @($violations.ToArray())
        $script:status.sourceArchiveVerification = if ($violations.Count -gt 0) { 'failed' } elseif ($incomplete) { 'incomplete' } else { 'verified' }
        Write-Status
        if ($violations.Count -gt 0) {
            throw ('CodeQL source tree contains excluded files: ' + ($violations -join '; '))
        }
        return
    }

    $script:status.sourceArchiveVerification = 'unavailable'
    Write-Status
}

function New-ExactPrunePolicy([string]$Path, [object]$Policy) {
    $copy = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Policy -Depth 16)
    $root = @($copy.roots | Where-Object { [string]$_.id -eq 'rm-local' })
    if ($root.Count -ne 1) { throw 'The exact-prune policy must contain only the rm-local root.' }
    $copy.roots = @($root[0])
    $copy.enabled = $true
    Set-JsonProperty -Object $copy.defaults -Name 'maxRecentCompleted' -Value 0
    Set-JsonProperty -Object $copy.defaults -Name 'maxBytes' -Value 0
    Set-JsonProperty -Object $copy.roots[0] -Name 'maxRecentCompleted' -Value 0
    Set-JsonProperty -Object $copy.roots[0] -Name 'maxBytes' -Value 0
    $json = ConvertTo-Json -InputObject $copy -Depth 16
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Set-JsonProperty {
    param([Parameter(Mandatory)][object]$Object, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][object]$Value)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}

try {
    if (-not [IO.Path]::IsPathFullyQualified($Output)) { throw '-Output must be an absolute registered workspace path.' }
    if (-not [IO.Path]::IsPathFullyQualified($PolicyPath)) { throw '-PolicyPath must be an absolute path.' }
    if (-not [IO.Path]::IsPathFullyQualified($CodeQL)) { throw '-CodeQL must be an absolute executable path.' }

    $script:outputPath = [IO.Path]::GetFullPath($Output)
    $script:policyPath = [IO.Path]::GetFullPath($PolicyPath)
    $script:codeqlPath = [IO.Path]::GetFullPath($CodeQL)
    $status.output = $script:outputPath
    $status.codeql = $script:codeqlPath

    if (-not (Test-Path -LiteralPath $script:maintenancePath -PathType Leaf)) {
        throw "Generated-workspace maintenance entry was not found: $script:maintenancePath"
    }
    if (-not (Test-Path -LiteralPath $script:policyPath -PathType Leaf)) { throw "Policy file was not found: $script:policyPath" }
    if (-not (Test-Path -LiteralPath $script:outputPath -PathType Container)) { throw "Output workspace was not found: $script:outputPath" }

    $policy = Get-Content -LiteralPath $script:policyPath -Raw | ConvertFrom-Json
    if (-not [bool]$policy.enabled) { throw 'The supplied generated-workspace policy is disabled.' }
    if (@($policy.roots).Count -ne 1 -or [string]$policy.roots[0].id -ne 'rm-local') {
        throw 'The supplied policy must contain only the rm-local root.'
    }
    $policyDefaults = $policy.defaults
    $policyRoot = $policy.roots[0]
    $policyMaxRecent = if ($null -ne $policyRoot.PSObject.Properties['maxRecentCompleted']) { [int]$policyRoot.maxRecentCompleted } else { [int]$policyDefaults.maxRecentCompleted }
    $policyMaxBytes = if ($null -ne $policyRoot.PSObject.Properties['maxBytes']) { [long]$policyRoot.maxBytes } else { [long]$policyDefaults.maxBytes }
    if ($policyMaxRecent -lt 0 -or $policyMaxBytes -lt 0 -or [int]$policyDefaults.maxNodes -lt 1 -or [int]$policyDefaults.maxMilliseconds -lt 1) {
        throw 'The supplied rm-local retention policy must have enabled finite nonnegative retention limits.'
    }

    $expectedRoot = [IO.Path]::GetFullPath((Join-Path $repository '.local')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $configuredRoot = [IO.Path]::GetFullPath(([string]$policyRoot.path).Replace('{RepositoryRoot}', $repository)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not [string]::Equals($configuredRoot, $expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The supplied rm-local root is not this worktree's .local directory: $configuredRoot"
    }

    $outputStatePath = Join-Path $script:outputPath '.generated-workspace.json'
    if (-not (Test-Path -LiteralPath $outputStatePath -PathType Leaf)) { throw 'The supplied Output path is not a registered workspace.' }
    $outputState = Get-Content -LiteralPath $outputStatePath -Raw | ConvertFrom-Json
    $outputParent = [IO.Path]::GetFullPath((Split-Path -Parent $script:outputPath)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ([string]$outputState.state -ne 'running' -or
        [string]$outputState.rootId -ne 'rm-local' -or
        -not [string]::Equals($outputParent, $expectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$outputState.workspacePath), $script:outputPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$outputState.rootPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output must be the exact running rm-local registered analysis workspace.'
    }

    $outputLeaf = Split-Path -Leaf $script:outputPath
    $outputParent = Split-Path -Parent $script:outputPath
    $script:databaseWorkspace = Join-Path $outputParent ($outputLeaf + '-codeql-db')
    $script:databasePath = Join-Path $script:databaseWorkspace 'database'
    $script:sarifPath = Join-Path $script:outputPath 'raw/CodeQL.sarif'
    $temporarySarifPath = Join-Path $script:databaseWorkspace 'CodeQL.sarif'
    if (Test-Path -LiteralPath $script:sarifPath) { throw 'An existing CodeQL SARIF must be explicitly reused or retained in its original task; use a new RunName for a fresh stage.' }
    $configPath = Join-Path $script:outputPath 'config/codeql-codescanning-config.yml'
    $prunePolicyPath = Join-Path $script:outputPath 'config/codeql-exact-prune-policy.json'
    $logRoot = Join-Path $script:outputPath 'logs'
    $codeqlLogRoot = Join-Path $logRoot 'codeql'
    $cachePath = Join-Path $script:databaseWorkspace 'cache'
    foreach ($directory in @((Join-Path $script:outputPath 'raw'), (Join-Path $script:outputPath 'config'), $logRoot, $codeqlLogRoot)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $script:statusPath = Join-Path $logRoot 'codeql-status.json'
    $status.databaseWorkspace = $script:databaseWorkspace
    $status.databasePath = $script:databasePath
    $status.codeScanningConfig = $configPath
    $status.pathsIgnore = @(
        'tests/Validation/NonAdaptedOptimizationLab/**',
        '.local/**',
        '**/.local/**',
        'artifact/**',
        '**/artifact/**',
        'artifacts/**',
        '**/artifacts/**',
        '**/bin/**',
        '**/obj/**'
    )
    Write-Status
    if (-not (Test-Path -LiteralPath $script:codeqlPath -PathType Leaf)) {
        throw "CodeQL CLI was not found: $script:codeqlPath. No alternate executable or download fallback was used."
    }
    $suitePath = Join-Path (Split-Path -Parent $script:codeqlPath) "qlpacks/codeql/csharp-queries/$queryPackVersion/codeql-suites/csharp-security-and-quality.qls"
    if (-not (Test-Path -LiteralPath $suitePath -PathType Leaf)) { throw "Pinned bundled C# query suite is missing: $suitePath" }

    $process = [Diagnostics.Process]::GetCurrentProcess()
    $originalPriority = $process.PriorityClass
    $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
    $priorityChanged = $true
    $status.priorityApplied = $true
    Write-Status

    Push-Location -LiteralPath $repository
    try {
        $versionLog = Join-Path $logRoot 'codeql-version.log'
        [void](Invoke-CodeQL -Stage 'version' -Arguments @('version', '--format=terse') -LogPath $versionLog)
        $versionLines = @(Get-Content -LiteralPath $versionLog | Where-Object { $_ -match '^\d+\.\d+\.\d+$' })
        if ($versionLines.Count -ne 1) { throw "Cannot read the queued CodeQL version: $versionLog" }
        $actualVersion = $versionLines[0]
        $status.actualVersion = $actualVersion
        Write-Status
        if ($actualVersion -ne $expectedVersion) {
            throw "Expected CodeQL CLI $expectedVersion, found '$actualVersion'. See $versionLog"
        }

        $yamlLines = [Collections.Generic.List[string]]::new()
        $yamlLines.Add('paths-ignore:')
        foreach ($pattern in $status.pathsIgnore) { $yamlLines.Add("  - '$pattern'") }
        [IO.File]::WriteAllText($configPath, ($yamlLines -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        $fingerprintExit = Invoke-StaticAnalysisQueued -FilePath (Get-Command pwsh).Source -Arguments @('-NoProfile','-File',
            (Join-Path $PSScriptRoot 'StaticAnalysis.Inputs.ps1'),'-SourceRepository',$repository,'-SnapshotOutput',
            (Join-Path $script:outputPath 'codeql-source-inputs.json')) -Log (Join-Path $logRoot 'codeql-source-inputs.log') `
            -Label 'rm C# CodeQL source input fingerprint' -Repository $repository
        if($fingerprintExit -ne 0) { throw 'CodeQL source input fingerprint failed; see codeql-source-inputs.log.' }

        if (Test-Path -LiteralPath $script:databaseWorkspace) {
            throw "CodeQL database workspace already exists; refusing to reuse or adopt it: $script:databaseWorkspace"
        }
        [void](Invoke-WorkspaceLifecycle -Action New -WorkspacePath $script:databaseWorkspace -Purpose 'Temporary CodeQL C# source database for the registered static-analysis output' -LogPath (Join-Path $logRoot 'workspace-new.log'))
        $registered = $true
        [IO.Directory]::CreateDirectory($cachePath) | Out-Null

        $createArguments = @(
            'database', 'create',
            '--language=csharp',
            '--build-mode=none',
            '--threads=2',
            '--ram=3072',
            "--source-root=$repository",
            "--codescanning-config=$configPath",
            "--common-caches=$cachePath",
            "--logdir=$codeqlLogRoot",
            '--', $script:databasePath
        )
        [void](Invoke-CodeQL -Stage 'database create' -Arguments $createArguments -LogPath (Join-Path $logRoot 'codeql-database-create.log'))
        Test-CodeQLSourceExclusions -Database $script:databasePath

        $analyzeArguments = @(
            'database', 'analyze',
            '--format=sarif-latest',
            "--output=$temporarySarifPath",
            '--threads=2',
            '--ram=3072',
            '--no-download',
            "--common-caches=$cachePath",
            "--logdir=$codeqlLogRoot",
            '--', $script:databasePath, $suitePath
        )
        [void](Invoke-CodeQL -Stage 'database analyze' -Arguments $analyzeArguments -LogPath (Join-Path $logRoot 'codeql-database-analyze.log'))

        if (-not (Test-Path -LiteralPath $temporarySarifPath -PathType Leaf)) { throw "CodeQL did not produce SARIF: $temporarySarifPath" }
        $sarifItem = Get-Item -LiteralPath $temporarySarifPath -Force
        if ($sarifItem.Length -le 0 -or ($sarifItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'CodeQL SARIF must be a nonempty regular file.'
        }
        $sarifDocument = Get-Content -LiteralPath $temporarySarifPath -Raw | ConvertFrom-Json
        if ([string]$sarifDocument.version -ne '2.1.0' -or $null -eq $sarifDocument.PSObject.Properties['runs']) {
            throw 'CodeQL output is not a valid SARIF 2.1.0 document.'
        }
        # Both paths are in this worktree's .local on the same volume. Publish
        # only a completed SARIF by atomic rename; partial files stay with the
        # database task and are reclaimed by its lifecycle on every failure.
        [IO.File]::Move($temporarySarifPath, $script:sarifPath)
        $sarifHashBeforeCleanup = (Get-FileHash -LiteralPath $script:sarifPath -Algorithm SHA256).Hash
        $sarifProduced = $true
        $status.sarif = $script:sarifPath
        $status.sarifSha256 = $sarifHashBeforeCleanup
        Write-Status
    }
    finally {
        Pop-Location
    }
}
catch {
    Add-Failure $_.Exception.Message
    [Console]::Error.WriteLine($_.Exception.Message)
}
finally {
    if ($registered) {
        $databaseOutcome = if ([string]::IsNullOrWhiteSpace($failure)) { 'success' } else { 'failed' }
        try {
            [void](Invoke-WorkspaceLifecycle -Action Complete -WorkspacePath $script:databaseWorkspace -Outcome $databaseOutcome -LogPath (Join-Path $logRoot 'workspace-complete.log'))
            $completed = $true
            $status.databaseOutcome = $databaseOutcome
            Write-Status
        }
        catch {
            Add-Failure $_.Exception.Message
            $markerPath = Join-Path $script:databaseWorkspace '.generated-workspace.json'
            if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
                $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
                $completed = [string]$marker.state -eq 'completed'
                if ($completed) { $status.databaseOutcome = [string]$marker.outcome }
            }
        }

        if ($completed) {
            try {
                New-ExactPrunePolicy -Path $prunePolicyPath -Policy $policy
                [void](Invoke-WorkspaceLifecycle -Action Prune -SelectedPolicyPath $prunePolicyPath -LogPath (Join-Path $logRoot 'workspace-prune.log'))
                if (Test-Path -LiteralPath $script:databaseWorkspace) {
                    throw "Exact retention prune left the CodeQL database workspace in place: $script:databaseWorkspace"
                }
                $status.databaseReclaimed = $true
                $status.databaseReclaimedBy = 'exact-workspace-prune'
            }
            catch {
                if (-not (Test-Path -LiteralPath $script:databaseWorkspace)) {
                    $status.databaseReclaimed = $true
                    $status.databaseReclaimedBy = 'official-retention'
                }
                else {
                    Add-Failure $_.Exception.Message
                }
            }
        }
    }

    if ($sarifProduced) {
        try {
            if (-not (Test-Path -LiteralPath $script:sarifPath -PathType Leaf)) { throw 'The CodeQL SARIF disappeared during database removal.' }
            $sarifAfter = (Get-FileHash -LiteralPath $script:sarifPath -Algorithm SHA256).Hash
            if ($sarifAfter -ne $sarifHashBeforeCleanup) { throw 'The CodeQL SARIF changed during database removal.' }
            $status.resultPreservedAfterDatabaseRemoval = $true
        }
        catch {
            $status.resultPreservedAfterDatabaseRemoval = $false
            Add-Failure $_.Exception.Message
        }
    }

    if ($priorityChanged -and $null -ne $process -and $null -ne $originalPriority) {
        try { $process.PriorityClass = $originalPriority }
        catch { Add-Failure ("Could not restore process priority: " + $_.Exception.Message) }
        finally { $process.Dispose() }
    }
    $status.failure = $failure
    Write-Status
}

if (-not [string]::IsNullOrWhiteSpace($failure)) { exit 1 }
exit 0
