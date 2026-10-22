[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PipelineConfigPath,
    [Parameter(Mandatory)][string]$PipelineStatePath,
    [Parameter(Mandatory)][string]$ReviewStepId,
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$AppSummaryPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$PhaseInputsPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-AppTestCompileInputScope.ps1')
function Read-Json([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { throw "Missing review evidence: $Path" }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Hash([string]$Path, [string]$Hash) {
    Require (-not [string]::IsNullOrWhiteSpace($Path) -and [IO.File]::Exists($Path)) "Missing hashed file: $Path"
    Require ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq $Hash) "Evidence hash changed: $Path"
}
function Assert-TreeRows($Rows, [string]$Label) {
    foreach ($row in @($Rows)) {
        Require ([IO.File]::Exists([string]$row.path)) "$Label output is missing: $($row.path)"
        $file = [IO.FileInfo]::new([string]$row.path)
        Require ($file.Length -eq [long]$row.length) "$Label output length changed: $($row.path)"
        Assert-Hash ([string]$row.path) ([string]$row.sha256)
    }
}
function Assert-InputPair([string]$RunDirectory) {
    $beforePath = Join-Path $RunDirectory 'source-inputs.json'
    $afterPath = Join-Path $RunDirectory 'source-inputs-after.json'
    Require ([IO.File]::Exists($beforePath) -and [IO.File]::Exists($afterPath)) "Stage lacks before/after input snapshots: $RunDirectory"
    $before = @(Read-Json $beforePath)
    $after = @(Read-Json $afterPath)
    Require (@(Compare-Object $before $after -Property path,sha256).Count -eq 0) "Stage inputs changed: $RunDirectory"
    $afterByPath = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $after) { $afterByPath.Add([string]$row.path,$row) }
    foreach ($row in $before) {
        $matching = $afterByPath[[string]$row.path]
        if ($row.PSObject.Properties.Name -contains 'length' -and $matching.PSObject.Properties.Name -contains 'length') {
            Require ([long]$row.length -eq [long]$matching.length) "Stage input length changed: $($row.path)"
        }
    }
}
function Assert-DeclaredStageInputReuse([string]$RunDirectory, [string]$Path, $Snapshot, $Frozen) {
    Require ($script:priorStageRuns.Contains([IO.Path]::GetFullPath($RunDirectory))) "Stage input differs without an accepted predecessor: $Path"
    Require ($script:priorInputChanges.ContainsKey($Path)) "Stage input differs without a declared tool repair: $Path"
    $change = $script:priorInputChanges[$Path]
    Require ([string]$Snapshot.sha256 -ieq [string]$change.before.sha256 -and
        [string]$Frozen.sha256 -ieq [string]$change.current.sha256) "Stage input repair does not match its exact old/current bytes: $Path"
}
function Assert-StageMatchesPhaseInputs([string]$RunDirectory, $PhaseFiles) {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    $snapshots = @(Read-Json (Join-Path $RunDirectory 'source-inputs.json'))
    $manifestPath = Join-Path $RunDirectory 'test-compile-manifest.json'
    if ([IO.File]::Exists($manifestPath)) {
        $selection = Read-Json (Join-Path $RunDirectory 'selected-test-project.json')
        $partitionManifest = Read-Json $manifestPath
        Require ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ieq [string]$selection.testCompileManifestSha256) "App partition manifest changed after its stage: $RunDirectory"
        $partitionId = [string]$partitionManifest.partitionId
        $selectedCompile = $null
        if ([string]$selection.stage -ceq 'Build') {
            $selectedCompile = Get-AppTestSelectedCompileEvidence -BuildRunDirectory $RunDirectory `
                -ExpectedPartitionId $partitionId -ExpectedManifestPath ([string]$selection.testCompileManifest) `
                -ExpectedManifestSha256 ([string]$selection.testCompileManifestSha256)
        } else {
            Require ([string]$selection.stage -ceq 'Tests') "Unexpected App test stage in input snapshot: $RunDirectory"
            $testsBuild = Read-Json (Join-Path $RunDirectory 'partition-tests-build.json')
            Require ([string]$testsBuild.partitionId -ceq $partitionId -and
                [IO.Path]::GetFullPath([string]$testsBuild.testCompileManifest) -ieq [IO.Path]::GetFullPath([string]$selection.testCompileManifest) -and
                [string]$testsBuild.testCompileManifestSha256 -ieq [string]$selection.testCompileManifestSha256) "Tests input snapshot points at another partition Build: $RunDirectory"
            $selectedCompile = Get-AppTestSelectedCompileEvidence -BuildRunDirectory ([string]$testsBuild.runDirectory) `
                -ExpectedPartitionId $partitionId -ExpectedManifestPath ([string]$selection.testCompileManifest) `
                -ExpectedManifestSha256 ([string]$selection.testCompileManifestSha256) `
                -ExpectedSelectedCompileSha256 ([string]$testsBuild.selectedCompileSha256)
        }
        $stageReceipt = Read-Json (Join-Path $RunDirectory 'bounded-result.json')
        $snapshots = @(Get-AppTestScopedInputRows -Rows $snapshots -RepositoryRoot $repoRoot -Stage ([string]$selection.stage) `
            -ManifestPath $manifestPath -OriginalCompileMetadataPath (Join-Path $RunDirectory 'original-compile-metadata.json') `
            -SelectedCompilePath $selectedCompile.path -SelectedCompileSha256 $selectedCompile.sha256 `
            -IncludeRuntimeSelection:([bool](([string]$selection.stage -ceq 'Tests') -and ($stageReceipt.PSObject.Properties.Name -contains 'testRuntimeManifest'))))
    }
    foreach ($snapshot in $snapshots) {
        $path = if ([IO.Path]::IsPathRooted([string]$snapshot.path)) {
            [IO.Path]::GetFullPath([string]$snapshot.path)
        } else { [IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$snapshot.path))) }
        if ($PhaseFiles.ContainsKey($path)) {
            $frozen = $PhaseFiles[$path]
            if ([string]$snapshot.sha256 -ine [string]$frozen.sha256) {
                Assert-DeclaredStageInputReuse $RunDirectory $path $snapshot $frozen
            } elseif ($snapshot.PSObject.Properties.Name -contains 'length') {
                Require ([long]$snapshot.length -eq [long]$frozen.length) "Stage input length differs from phase freeze: $path"
            }
        }
    }
}
function Assert-NormalRun([string]$RunDirectory, [string]$ExpectedStage) {
    $receipt = Read-Json (Join-Path $RunDirectory 'bounded-result.json')
    Require ([string]$receipt.stage -ceq $ExpectedStage -and $receipt.accepted -and [int]$receipt.exitCode -eq 0 -and $receipt.normalDrain -and $receipt.sourceInputsUnchangedExceptDeclaredPartitionRefresh) "Stage receipt not accepted/stable: $RunDirectory"
    Require ([IO.Path]::GetFullPath([string]$receipt.runDirectory) -ieq [IO.Path]::GetFullPath($RunDirectory)) "Stage receipt points at another run: $RunDirectory"
    Assert-InputPair $RunDirectory
    $job = Read-Json (Join-Path $RunDirectory 'job-manifest.json')
    $receiptPath = Join-Path ([string]$job.runDirectory) 'receipts.jsonl'
    Require ([IO.File]::Exists($receiptPath)) "Stage Job receipt stream is missing: $receiptPath"
    $events = @(Get-Content -LiteralPath $receiptPath | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $exit = @($events | Where-Object kind -CEQ 'exit')
    $drain = @($events | Where-Object kind -CEQ 'final-job')
    $closed = @($events | Where-Object kind -CEQ 'owned-job-closed')
    Require ($exit.Count -eq 1 -and $exit[0].data.reason -ceq 'normal' -and [int]$exit[0].data.ExitCode -eq 0) "Stage Job did not exit normally: $receiptPath"
    Require ($drain.Count -eq 1 -and $drain[0].data.reason -ceq 'normal' -and $drain[0].data.empty) "Stage Job did not drain empty: $receiptPath"
    Require ($closed.Count -eq 1 -and $closed[0].data.reason -ceq 'normal' -and $closed[0].data.assigned -and $closed[0].data.killOnCloseConfigured) "Owned Job was not closed normally: $receiptPath"
    return $receipt
}
function Assert-Trx([string]$RunDirectory) {
    $files = @(Get-ChildItem -LiteralPath $RunDirectory -Filter '*.trx' -File)
    Require ($files.Count -gt 0) "No TRX result exists in Tests run: $RunDirectory"
    foreach ($file in $files) {
        [xml]$trx = Get-Content -LiteralPath $file.FullName -Raw
        $summary = $trx.TestRun.ResultSummary
        Require ($null -ne $summary -and [string]$summary.outcome -notin @('Failed','Aborted','InProgress')) "TRX did not complete successfully: $($file.FullName)"
        $results = @($trx.TestRun.Results.UnitTestResult)
        Require ($results.Count -gt 0) "TRX contains no test results: $($file.FullName)"
        $counters = $summary.Counters
        foreach ($name in @('failed','error','timeout','aborted','passedButRunAborted','notRunnable','disconnected','inProgress','pending')) {
            if ($counters.PSObject.Properties.Name -contains $name) {
                Require ([int]$counters.$name -eq 0) "TRX has $name results: $($file.FullName)"
            }
        }
        $bad = @($results | Where-Object { [string]$_.outcome -in @('Failed','Aborted','Timeout','InProgress','Pending','NotRunnable','Disconnected') })
        Require ($bad.Count -eq 0) "TRX contains failed/interrupted outcomes: $($file.FullName)"
    }
}
function Get-StageArgument([string[]]$Arguments, [string]$Name) {
    for ($i=0; $i -lt $Arguments.Count-1; $i++) { if ($Arguments[$i] -ceq $Name) { return [string]$Arguments[$i+1] } }
    return $null
}
function Get-ValidationStages($Config) {
    $found = [Collections.Generic.List[object]]::new()
    foreach ($step in $Config.steps) {
        $args = [string[]]@($step.arguments)
        $scriptPath = $null
        for ($i=0; $i -lt $args.Count-1; $i++) {
            if ($args[$i] -ieq '-File') { $scriptPath = [string]$args[$i+1]; break }
        }
        if ($scriptPath -and [IO.Path]::GetFileName($scriptPath) -ieq 'Invoke-Validation.ps1' -and $scriptPath -match 'ProductSdk') {
            $stage = Get-StageArgument $args '-Stage'
            if ($stage) { $found.Add([pscustomobject]@{id=[string]$step.id;stage=$stage;arguments=$args}) }
        }
    }
    return $found.ToArray()
}
function Get-StageOutput([string]$LogPath, [string]$Stage) {
    Require ([IO.File]::Exists($LogPath)) "Pipeline stage log is missing: $LogPath"
    $matches = [Collections.Generic.List[object]]::new()
    foreach ($line in [IO.File]::ReadLines($LogPath)) {
        if (-not $line.TrimStart().StartsWith('{')) { continue }
        try {
            $value = $line | ConvertFrom-Json
            if ([string]$value.stage -ceq $Stage -and -not [string]::IsNullOrWhiteSpace([string]$value.runDirectory)) { $matches.Add($value) }
        } catch { }
    }
    Require ($matches.Count -gt 0) "Pipeline log has no runDirectory result for $($Stage): $LogPath"
    return $matches[$matches.Count-1]
}
function Assert-NativeContext([string]$Path) {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    & (Join-Path $PSScriptRoot 'Assert-ProductNativeContext.ps1') -ContextPath $Path -RepositoryRoot $repoRoot | Out-Null
}
function Assert-App([string]$Path) {
    $summary = Read-Json $Path
    Require ($summary.allSelectedAccepted -and $summary.wholePlanSelected -and @($summary.runtimeTestMethods).Count -eq 0 -and [int]$summary.runtimeMaxMethodsPerGroup -eq 64) 'App summary is focused, incomplete, or not grouped at 64 methods.'
    Assert-Hash ([string]$summary.planPath) ([string]$summary.planSha256)
    $plan = Read-Json ([string]$summary.planPath)
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($partition in @($plan.partitions)) { [void]$expected.Add([string]$partition.partitionId) }
    $selected = [Collections.Generic.HashSet[string]]::new([string[]]@($summary.selectedPartitionIds),[StringComparer]::Ordinal)
    Require ($selected.SetEquals($expected)) 'App run did not select every partition from its current plan.'
    Require (@($summary.results).Count -eq $expected.Count) 'App partition results do not match the current plan.'
    foreach ($result in $summary.results) {
        Require ($result.completed -and $result.build.accepted) "App partition did not complete its accepted Build: $($result.partitionId)"
        $build = Assert-NormalRun ([string]$result.build.runDirectory) 'Build'
        Require ([string]$build.testCompileManifestSha256 -ceq [string]$result.build.testCompileManifestSha256) 'App Build compile manifest binding changed.'
        Require (@($result.runtimeTests).Count -gt 0) "App partition has no executed runtime group: $($result.partitionId)"
        $runtimePath = [string]$result.runtimeTests[0].testRuntimeManifest
        Require ([IO.File]::Exists($runtimePath)) "App runtime manifest is missing: $runtimePath"
        $runtimeHash = (Get-FileHash -LiteralPath $runtimePath).Hash
        $runtime = Read-Json $runtimePath
        Require ((-not $runtime.focused) -and $runtime.wholeAssemblySelected -and [int]$runtime.maxMethodsPerGroup -eq 64) "App runtime selection is not a full 64-method grouping: $runtimePath"
        $expectedGroups = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($group in @($runtime.groups)) { [void]$expectedGroups.Add([string]$group.groupId) }
        $actualGroups = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($test in $result.runtimeTests) {
            Require ($test.accepted -and $test.stage -ceq 'Tests' -and [string]$test.testRuntimeManifest -ieq $runtimePath -and [string]$test.testRuntimeManifestSha256 -ieq $runtimeHash) "App runtime group has a changed manifest or failed receipt: $($result.partitionId)"
            Require ([string]$test.testCompileManifestSha256 -ceq [string]$result.build.testCompileManifestSha256) "App runtime group uses a different Compile selection: $($result.partitionId)"
            [void]$actualGroups.Add([string]$test.testRuntimeGroupId)
            $null = Assert-NormalRun ([string]$test.runDirectory) 'Tests'
            Assert-Trx ([string]$test.runDirectory)
        }
        Require (@($result.runtimeTests).Count -eq $expectedGroups.Count -and $actualGroups.SetEquals($expectedGroups)) "App runtime groups were omitted or repeated: $($result.partitionId)"
    }
    $lastBuild = $summary.results[-1].build
    Require ($lastBuild.accepted -and -not [string]::IsNullOrWhiteSpace([string]$lastBuild.runDirectory)) 'Full App summary has no accepted final Build binding.'
    $null = Assert-NormalRun ([string]$lastBuild.runDirectory) 'Build'
    return [pscustomobject]@{summary=$summary;lastBuild=$lastBuild}
}
function Assert-LogLines([string]$LogPath, [string[]]$RequiredLines) {
    Require ([IO.File]::Exists($LogPath)) "Semantic package consumer log is missing: $LogPath"
    $text = [IO.File]::ReadAllText($LogPath)
    foreach ($line in $RequiredLines) {
        Require ($text.Contains($line,[StringComparison]::Ordinal)) "Required package assertion output is missing from $LogPath. $line"
    }
}
function Assert-Packages([string]$TaskRoot, [hashtable]$StageRuns, $AppBuild) {
    $consumerRun = [string]$StageRuns.Consumers
    $acceptance = Read-Json (Join-Path $consumerRun 'consumer-acceptance.json')
    Require ($acceptance.accepted -and [string]$acceptance.packageContext -ieq (Join-Path $TaskRoot 'package-context.json')) 'Consumer acceptance is not bound to this phase package context.'
    $package = Read-Json ([string]$acceptance.packageContext)
    Require ($package.freshFeed -and [IO.Path]::GetFullPath([string]$package.runDirectory) -ieq [IO.Path]::GetFullPath([string]$StageRuns.Pack)) 'Package feed is not the output of this phase Pack stage.'
    Require (@($package.packages).Count -eq 2) 'Package context must contain the two released packages.'
    foreach ($row in $package.packages) { Assert-Hash ([string]$row.path) ([string]$row.sha256) }
    $lastBuildRun = [IO.Path]::GetFullPath([string]$AppBuild.runDirectory)
    $buildContextPath = Join-Path $TaskRoot 'build-context.json'
    $buildContext = Read-Json $buildContextPath
    Require ([IO.Path]::GetFullPath([string]$buildContext.runDirectory) -ieq $lastBuildRun -and
        [IO.Path]::GetFullPath([string]$package.buildBinding.runDirectory) -ieq $lastBuildRun -and
        [IO.Path]::GetFullPath([string]$acceptance.buildBinding.runDirectory) -ieq $lastBuildRun) 'Pack and Consumers do not use the full App summary final Build.'
    Require ([IO.Path]::GetFullPath([string]$acceptance.buildBinding.buildContextPath) -ieq [IO.Path]::GetFullPath($buildContextPath)) 'Consumer build binding points at another Build context.'
    Assert-Hash ([string]$acceptance.buildBinding.buildContextPath) ([string]$acceptance.buildBinding.buildContextSha256)
    $expectedReceipt = Join-Path $lastBuildRun 'bounded-result.json'
    Require ([IO.Path]::GetFullPath([string]$acceptance.buildBinding.receiptPath) -ieq [IO.Path]::GetFullPath($expectedReceipt)) 'Package consumers are not bound to the full App summary final Build receipt.'
    Assert-Hash ([string]$acceptance.buildBinding.receiptPath) ([string]$acceptance.buildBinding.receiptSha256)
    $buildReceipt = Read-Json $expectedReceipt
    Require ($buildReceipt.accepted -and $buildReceipt.stage -ceq 'Build' -and [int]$buildReceipt.exitCode -eq 0 -and $buildReceipt.normalDrain) 'Pack/Consumers Build receipt is not accepted.'
    Require (@($acceptance.consumers).Count -eq 2) 'Consumer acceptance must bind both public packages.'
    foreach ($name in @('ReadWriteCore','SubscriptionCore')) {
        $consumer = @($acceptance.consumers | Where-Object name -CEQ $name)
        Require ($consumer.Count -eq 1 -and $consumer[0].actualPackageBytesMatched -and $consumer[0].actualPackageArchivesMatched) "Actual package bytes are not accepted for $name."
        Assert-Hash ([string]$consumer[0].binary) ([string]$consumer[0].sha256)
        $log = Join-Path $consumerRun ('consumer-run-'+$name+'.log')
        if ($name -ceq 'ReadWriteCore') {
            $requiredLines = @(
                '[AMD-HARDWARE-TOPOLOGY-PACKAGE] Fn26 full APIC; explicit CCD/complex/core/group links; original owner/cache/failure/recovery; overlapping domain rejection and recovery; recorded only.',
                '[SECURITY-PRODUCT-RW-PACKAGE] nine extensions; exact zero/false/uint32/string; legacy nulls; passive Current; category/whole failure; cancellation/recovery; recorded only',
                '[DISPLAYID13-TYPEVVI-RW] parse; owner/current/direct; failure/recovery; unsupported/raw; recorded-only',
                '[DISPLAYID-MIXED-STRUCTURE-RW] real-wire12/20; separate-counts; same-owner/current; invalid-header/raw/recovery',
                '[DISPLAYID13-COMPACT-RW] normative-II-byte3/10; TypeI-odd-full-frame; actual-owner/current; bad/raw/recovery; no-live-display',
                '[DISPLAYID20-DYNAMIC-RANGE-RW] actual-owner/current; clocks/rates/seamless/raw; bad/recovery; no-live-display',
                '[NPU-ENGINE-PACKAGE-RECORDED] raw-U64/QPC-Native-ratio125/device-busy-max; warmup/failure/reopen; same-current; live-directory-reuse; cancellation/retirement; no-live-hardware.'
            )
            foreach ($variant in 0,1) { $requiredLines += "[DISPLAYID-ADAPTIVE-SYNC-RW] variant=$variant; actual-owner/current; base-rates/flags/exact-duration/raw; bad/recovery; recorded-only" }
        } else {
            $requiredLines = @(
                '[AMD-HARDWARE-TOPOLOGY-SUBSCRIPTION-PACKAGE] original CPU publication and same Current through installed transitive package; recorded only. Host native subscription is qualified separately.',
                '[SECURITY-PRODUCT-SUB-PACKAGE] nine extensions; original native role/owner/Current; passive start/read; stop/drain; direct failure/recovery; recorded only',
                '[DISPLAYID13-TYPEVVI-SUB] parse; role2/current; stop/drain; direct; failure/recovery; unsupported/raw; recorded-only',
                '[DISPLAYID-MIXED-STRUCTURE-SUB] real-wire12/20; separate-counts; actual-native-role2; same-current; stop/drain; invalid-header/raw/recovery',
                '[DISPLAYID13-COMPACT-SUB] actual-native-role2; normative-declaration; same-current; stop/drain; direct-bad/raw/recovery; no-live-display',
                '[DISPLAYID20-DYNAMIC-RANGE-SUB] actual-native-role2; exact-declaration; same-current; stop/drain; bad/raw/recovery; no-live-display',
                '[NPU-ENGINE-PACKAGE-RECORDED] raw-U64/QPC-Native-ratio125/device-busy-max; warmup/failure/reopen; same-current; live-directory-reuse; cancellation/retirement; no-live-hardware.',
                '[NPU-ENGINE-SUBSCRIPTION-PACKAGE-RECORDED] original-borrow/native-planner;125%; exact-engine-fields; stop-drains; no-live-hardware.'
            )
            foreach ($variant in 0,1) { $requiredLines += "[DISPLAYID-ADAPTIVE-SYNC-SUB] variant=$variant; actual-owner/current; base-rates/flags/exact-duration/raw; bad/recovery; recorded-only" }
        }
        Assert-LogLines $log ([string[]]$requiredLines)
    }
    Assert-Hash ([string]$acceptance.compileInputManifest) ([string]$acceptance.compileInputManifestSha256)
    $compileManifest = Read-Json ([string]$acceptance.compileInputManifest)
    Require ($compileManifest.schema -ceq 'rm/product-sdk-corecompile-inputs/v1' -and @($compileManifest.projects).Count -eq 2) 'Actual consumer CoreCompile input proof is incomplete.'
    foreach ($input in @($compileManifest.inputs)) { Assert-Hash ([string]$input.path) ([string]$input.sha256) }
}
function Assert-Release([string]$TaskRoot, [hashtable]$StageRuns, [string]$ConfiguredNativePath) {
    $product = Read-Json (Join-Path $TaskRoot 'product-publish-context.json')
    $image = Read-Json (Join-Path $TaskRoot 'product-image-context.json')
    Require ($product.accepted -and $image.accepted -and $image.installedProductModified -eq $false) 'Release contexts are not accepted.'
    Require ([IO.Path]::GetFullPath([string]$product.nativeContext) -ieq [IO.Path]::GetFullPath($ConfiguredNativePath)) 'Release context does not bind the configured Native composite.'
    Assert-NativeContext $ConfiguredNativePath
    Require ([IO.Path]::GetFullPath([string]$product.backendRun) -ieq [IO.Path]::GetFullPath([string]$StageRuns.ProductBackend) -and
        [IO.Path]::GetFullPath([string]$product.shellRun) -ieq [IO.Path]::GetFullPath([string]$StageRuns.ProductShell) -and
        [IO.Path]::GetFullPath([string]$image.runDirectory) -ieq [IO.Path]::GetFullPath([string]$StageRuns.ProductImage) -and
        [IO.Path]::GetFullPath([string]$image.backendRun) -ieq [IO.Path]::GetFullPath([string]$StageRuns.ProductBackend) -and
        [IO.Path]::GetFullPath([string]$image.shellRun) -ieq [IO.Path]::GetFullPath([string]$StageRuns.ProductShell)) 'Release contexts combine outputs from different configured stages.'
    $native = Read-Json $ConfiguredNativePath
    $backendRows = @(Read-Json (Join-Path ([string]$StageRuns.ProductBackend) 'backend-output.json'))
    foreach ($row in @($native.outputs | Where-Object { [IO.Path]::GetExtension([string]$_.path) -in @('.dll','.exe') })) {
        $matches = @($backendRows | Where-Object { [IO.Path]::GetFileName([string]$_.relativePath) -ieq [IO.Path]::GetFileName([string]$row.path) })
        Require ($matches.Count -eq 1 -and [long]$matches[0].length -eq [long]$row.length -and [string]$matches[0].sha256 -ieq [string]$row.sha256) "Published backend manifest does not bind exactly one Native output: $($row.path)"
        Assert-Hash ([string]$matches[0].path) ([string]$row.sha256)
    }
    $publishParts = @(
        @{run=$StageRuns.ProductBackend;file='backend-output.json';prefix='ResourceManager'},
        @{run=$StageRuns.ProductShell;file='native-ui-output.json';prefix='ResourceManagerNativeUi'},
        @{run=$StageRuns.ProductShell;file='launcher-output.json';prefix='ResourceManagerLauncher'}
    )
    $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($part in $publishParts) {
        $rows = @(Read-Json (Join-Path ([string]$part.run) ([string]$part.file)))
        Assert-TreeRows $rows "Release $($part.prefix)"
        foreach ($row in $rows) {
            $key = [string]$part.prefix+'/'+[string]$row.relativePath
            $expected.Add($key,([long]$row.length).ToString()+'|'+[string]$row.sha256)
        }
    }
    $finalRows = @(Read-Json (Join-Path ([string]$StageRuns.ProductImage) 'final-image-output.json'))
    Assert-TreeRows $finalRows 'Final image'
    $manifestRow = @($finalRows | Where-Object relativePath -CEQ 'resource-manager-final-image.manifest.json')
    Require ($manifestRow.Count -eq 1) 'Final image manifest is missing or ambiguous.'
    $manifest = Read-Json ([string]$manifestRow[0].path)
    Require ($manifest.contract -ceq 'resource-manager-final-image-v1' -and [int]$manifest.schemaVersion -eq 1) 'Final image composition manifest contract is invalid.'
    $actual = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in @($manifest.files)) { $actual.Add([string]$row.path,([long]$row.length).ToString()+'|'+[string]$row.sha256) }
    Require ($actual.Count -eq $expected.Count) 'Final image payload does not contain the exact published backend, Native UI and launcher outputs.'
    foreach ($key in $expected.Keys) { Require ($actual.ContainsKey($key) -and $actual[$key] -ceq $expected[$key]) "Final image payload differs from its release input: $key" }
    $buildLog = Read-Json (Join-Path ([string]$StageRuns.ProductImage) 'final-image-build.log')
    $validateLog = Read-Json (Join-Path ([string]$StageRuns.ProductImage) 'final-image-validation.log')
    $nativeUiLog = Read-Json (Join-Path ([string]$StageRuns.ProductImage) 'native-ui-manifest.log')
    Require ($buildLog.Published -and [IO.Path]::GetFullPath([string]$buildLog.OutputDirectory) -ieq [IO.Path]::GetFullPath([string]$image.imageDirectory)) 'Final-image build log has no matching Published result.'
    Require ($validateLog.Valid -and [IO.Path]::GetFullPath([string]$validateLog.RootDirectory) -ieq [IO.Path]::GetFullPath([string]$image.imageDirectory)) 'Final-image validator has no matching Valid result.'
    Require ($nativeUiLog.Valid -and $nativeUiLog.RequestedExecutionLevel -ceq 'asInvoker' -and $nativeUiLog.UiAccess -ceq 'false' -and -not $nativeUiLog.AutoElevateDeclared) 'Native UI executable manifest validation did not pass the expected contract.'
    $nativeUiRow = @($finalRows | Where-Object relativePath -CEQ 'ResourceManagerNativeUi/ResourceManager.NativeUi.exe')
    Require ($nativeUiRow.Count -eq 1 -and [string]$nativeUiLog.ExecutableSha256 -ieq [string]$nativeUiRow[0].sha256) 'Native UI manifest log is not bound to the composed executable.'
}

$taskRootFull = [IO.Path]::GetFullPath($TaskRoot)
$configPath = [IO.Path]::GetFullPath($PipelineConfigPath)
$statePath = [IO.Path]::GetFullPath($PipelineStatePath)
$outputPath = [IO.Path]::GetFullPath($OutputPath)
$phaseInputsPath = [IO.Path]::GetFullPath($PhaseInputsPath)
$config = Read-Json $configPath
$state = Read-Json $statePath
$configHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
$stateHash = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
$phaseInputsHash = (Get-FileHash -LiteralPath $phaseInputsPath -Algorithm SHA256).Hash
$stateConfigPath = [IO.Path]::GetFullPath([string]$state.configPath)
Require ($stateConfigPath -ieq $configPath -and [string]$state.configSha256 -ieq $configHash) 'Pipeline state does not bind this exact configuration file.'
$phaseInputs = Read-Json $phaseInputsPath
$script:priorStageRuns = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$script:priorInputChanges = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
if ($phaseInputs.PSObject.Properties.Name -contains 'priorStageInputReuse') {
    $proof = $phaseInputs.priorStageInputReuse
    Assert-Hash ([string]$proof.path) ([string]$proof.sha256)
    $reuse = Read-Json ([string]$proof.path)
    Require ($reuse.accepted -and $reuse.productionInputsUnchanged) 'Prior-stage tool repair proof was not accepted.'
    foreach ($prior in @($reuse.runs)) {
        Assert-Hash ([string]$prior.receipt) ([string]$prior.sha256)
        $receipt = Read-Json ([string]$prior.receipt)
        Require ($receipt.accepted -and $receipt.normalDrain -and [int]$receipt.exitCode -eq 0 -and
            [string]$receipt.stage -cne 'Consumers') 'Input repair cannot reuse a failed or unexecuted Consumers stage.'
        [void]$script:priorStageRuns.Add([IO.Path]::GetFullPath([string]$receipt.runDirectory))
    }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $validationRoot = (Join-Path $repo 'scripts/validation')+[IO.Path]::DirectorySeparatorChar
    foreach ($change in @($reuse.changes)) {
        $path = [IO.Path]::GetFullPath([string]$change.path)
        Assert-Hash ([string]$change.originalFile) ([string]$change.before.sha256)
        Assert-Hash $path ([string]$change.current.sha256)
        $controlScript = $path.StartsWith($validationRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetExtension($path) -ieq '.ps1'
        $nativeContext = $path -ieq (Join-Path $taskRootFull 'current-native-context.json')
        # App's actual selected Compile set was checked above. A consumer-only
        # fixture is not compiled by Sources/Incomplete/DefaultRestore/Pack.
        $consumerFixture = $path.StartsWith((Join-Path $repo 'tests/Fixtures')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetExtension($path) -ieq '.cs'
        Require ($controlScript -or $nativeContext -or $consumerFixture) "Production source cannot be exempted as a tool repair: $path"
        Require (-not [string]::IsNullOrWhiteSpace([string]$change.reason)) 'Tool repair lacks a scope explanation.'
        if ([IO.Path]::GetFileName($path) -ieq 'Run-PackageStages.ps1') {
            $oldText = [IO.File]::ReadAllText([string]$change.originalFile)
            $newText = [IO.File]::ReadAllText($path)
            $boundary = '}else {'
            $oldEnd = $oldText.IndexOf($boundary,[StringComparison]::Ordinal)
            $newEnd = $newText.IndexOf($boundary,[StringComparison]::Ordinal)
            Require ($oldEnd -gt 0 -and $newEnd -eq $oldEnd -and $oldText.Substring(0,$oldEnd) -ceq $newText.Substring(0,$newEnd)) 'Package repair changed code used by Pack.'
        }
        if ($nativeContext) {
            $oldContext = Read-Json ([string]$change.originalFile)
            $newContext = Read-Json $path
            $oldContext.reuseProof.PSObject.Properties.Remove('administrativeRecoveries')
            $newContext.reuseProof.PSObject.Properties.Remove('administrativeRecoveries')
            Require (($oldContext|ConvertTo-Json -Depth 30 -Compress) -ceq ($newContext|ConvertTo-Json -Depth 30 -Compress)) 'Native outputs, properties or Job bindings changed during tool repair.'
        }
        $script:priorInputChanges.Add($path,$change)
    }
}
Require (@($phaseInputs.files).Count -gt 0) 'Phase input freeze contains no file rows.'
$phaseFiles = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($row in @($phaseInputs.files)) {
    $path = [IO.Path]::GetFullPath([string]$row.path)
    Require ([long]$row.length -ge 0 -and [string]$row.sha256 -match '^[0-9A-Fa-f]{64}$') "Phase input freeze has an invalid row: $path"
    Assert-Hash $path ([string]$row.sha256)
    Require ([IO.FileInfo]::new($path).Length -eq [long]$row.length) "Phase input length differs from its freeze: $path"
    $phaseFiles.Add($path,$row)
}
Require ([string]$state.status -ceq 'running' -and [string]$state.currentStepId -ceq $ReviewStepId) 'Final reviewer must be the currently running pipeline step.'
Require (@($config.steps).Count -gt 0 -and [string]$config.steps[-1].id -ceq $ReviewStepId) 'Final reviewer must be the final configured pipeline step.'
$stateSteps = @($state.steps)
Require ($stateSteps.Count -eq @($config.steps).Count -and [string]$stateSteps[-1].status -ceq 'running') 'Pipeline state does not match the final review step.'
for ($i=0; $i -lt $stateSteps.Count-1; $i++) { Require ([string]$stateSteps[$i].status -ceq 'succeeded') "Configured step did not succeed before final review: $($stateSteps[$i].id)" }

$app = Assert-App $AppSummaryPath
foreach ($partitionResult in @($app.summary.results)) {
    Assert-StageMatchesPhaseInputs ([string]$partitionResult.build.runDirectory) $phaseFiles
    foreach ($testResult in @($partitionResult.runtimeTests)) {
        Assert-StageMatchesPhaseInputs ([string]$testResult.runDirectory) $phaseFiles
    }
}
$validationStages = @(Get-ValidationStages $config)
$expectedStages = @('Sources','Incomplete','DefaultRestore','Pack','Consumers','ProductBackend','ProductShell','ProductImage')
Require ($validationStages.Count -eq $expectedStages.Count) 'Pipeline must configure Sources, Incomplete, DefaultRestore, Pack, Consumers and the three Release stages exactly once.'
for ($i=0; $i -lt $expectedStages.Count; $i++) { Require ([string]$validationStages[$i].stage -ceq $expectedStages[$i]) 'ProductSdk stages must follow Sources/Incomplete/DefaultRestore/Pack/Consumers/Backend/Shell/Image order.' }
$stageRuns = @{}
foreach ($binding in $validationStages) {
    $stateStep = @($stateSteps | Where-Object id -CEQ $binding.id)
    Require ($stateStep.Count -eq 1 -and $stateStep[0].status -ceq 'succeeded') "Pipeline step is not completed: $($binding.id)"
    $output = Get-StageOutput ([string]$stateStep[0].logPath) ([string]$binding.stage)
    $run = [IO.Path]::GetFullPath([string]$output.runDirectory)
    $null = Assert-NormalRun $run ([string]$binding.stage)
    Assert-StageMatchesPhaseInputs $run $phaseFiles
    if ($binding.stage -ceq 'Sources') {
        $source = Read-Json (Join-Path $run 'source-result.json')
        Require ($source.passed -and $source.status -ceq 'complete' -and @($source.issues).Count -eq 0) 'FreedomPoint source contract report is incomplete or contains issues.'
        $checker = Read-Json (Join-Path $run 'checker-build-output.json')
        Require ([string]$checker.stage -ceq 'Sources' -and [IO.Path]::GetFullPath([string]$checker.runDirectory) -ieq $run) 'FreedomPointCheck was not rebuilt in the Sources Job.'
        Require ([IO.FileInfo]::new([string]$checker.binaryPath).Length -eq [long]$checker.length) 'FreedomPointCheck build output length changed.'
        Assert-Hash ([string]$checker.binaryPath) ([string]$checker.sha256)
    }
    if ($binding.stage -ceq 'Incomplete') {
        $negative = Read-Json (Join-Path $run 'negative-acceptance.json')
        $source = Read-Json (Join-Path $run 'source-result.json')
        Require ($negative.rejected -and $negative.completeIndexPreserved -and -not $source.passed -and $source.status -ceq 'incomplete') 'Incomplete-context negative proof did not reject or preserve the complete source index.'
        Assert-Hash (Join-Path $taskRootFull 'sources/source-index.json') ([string]$negative.sha256)
    }
    if ($binding.stage -ceq 'DefaultRestore') {
        $restore = Read-Json (Join-Path $taskRootFull 'default-restore-context.json')
        Require ([IO.Path]::GetFullPath([string]$restore.runDirectory) -ieq $run -and [IO.Directory]::Exists([string]$restore.artifacts)) 'Default restore output is not bound to its configured stage.'
    }
    $stageRuns[[string]$binding.stage] = $run
}
Assert-Packages $taskRootFull $stageRuns $app.lastBuild
$backendNative = Get-StageArgument $validationStages[5].arguments '-NativeContextPath'
$shellNative = Get-StageArgument $validationStages[6].arguments '-NativeContextPath'
$imageNative = Get-StageArgument $validationStages[7].arguments '-NativeContextPath'
Require ($backendNative -and $backendNative -ceq $shellNative -and $backendNative -ceq $imageNative) 'Release stages do not use the same explicit Native context.'
Assert-Release $taskRootFull $stageRuns $backendNative
Require ((Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -ceq $configHash -and (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash -ceq $stateHash) 'Pipeline config or state changed during final review.'
Require ((Get-FileHash -LiteralPath $phaseInputsPath -Algorithm SHA256).Hash -ceq $phaseInputsHash) 'Phase input freeze changed during final review.'
$result = [ordered]@{
    accepted=$true;pipelineConfig=$configPath;pipelineConfigSha256=$configHash
    pipelineState=$statePath;pipelineStateSha256=$stateHash;taskRoot=$taskRootFull
    phaseInputs=$phaseInputsPath;phaseInputsSha256=$phaseInputsHash
    appSummary=$AppSummaryPath;appPartitions=@($app.summary.selectedPartitionIds)
    packageStages=@('Sources','Incomplete','DefaultRestore','Pack','Consumers')
    releaseStages=@('ProductBackend','ProductShell','ProductImage')
    reviewedUtc=[DateTimeOffset]::UtcNow.ToString('O')
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath))
$json = $result | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($outputPath,$json,[Text.UTF8Encoding]::new($false))
Write-Output $json
