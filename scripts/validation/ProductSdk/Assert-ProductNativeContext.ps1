[CmdletBinding()]
param([Parameter(Mandatory)][string]$ContextPath,[string]$RepositoryRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Read-Json([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { throw "Missing evidence: $Path" }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Assert-FileProof($Proof, [string]$Label) {
    $path = [string]$Proof.path
    if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.File]::Exists($path)) { throw "$Label is missing: $path" }
    $file = [IO.FileInfo]::new($path)
    if ($Proof.PSObject.Properties.Name -contains 'length' -and $file.Length -ne [long]$Proof.length) { throw "$Label length changed: $path" }
    if ($Proof.PSObject.Properties.Name -contains 'sha256' -and (Get-FileHash -LiteralPath $path).Hash -ine [string]$Proof.sha256) { throw "$Label hash changed: $path" }
}
function Assert-Context($Context, [string]$Label, [switch]$VerifyInputs) {
    if (-not $Context.accepted -or [string]$Context.status -cne 'built' -or @($Context.outputs).Count -eq 0) { throw "$Label is not an accepted built Native context." }
    foreach ($row in $Context.outputs) { Assert-FileProof $row "$Label output" }
    Assert-FileProof $Context.sourceInputsManifest "$Label source-input manifest"
    if ($Context.PSObject.Properties.Name -contains 'sourceInputsAfterManifest') { Assert-FileProof $Context.sourceInputsAfterManifest "$Label after-input manifest" }
    if ($Context.PSObject.Properties.Name -contains 'commandManifest') { Assert-FileProof $Context.commandManifest "$Label command manifest" }
    if ($VerifyInputs) {
        $manifest = Read-Json ([string]$Context.sourceInputsManifest.path)
        foreach ($group in @('source','projected','baseline','tools')) {
            if ($manifest.PSObject.Properties.Name -contains $group) {
                foreach ($row in @($manifest.$group)) { Assert-FileProof $row "$Label $group input" }
            }
        }
    }
}
function Assert-JobReceipts([string]$ReceiptPath, [string]$Label) {
    if (-not [IO.File]::Exists($ReceiptPath)) { throw "$Label Job receipt stream is missing: $ReceiptPath" }
    $events = @(Get-Content -LiteralPath $ReceiptPath | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $exit = @($events | Where-Object kind -CEQ 'exit')
    $drain = @($events | Where-Object kind -CEQ 'final-job')
    $closed = @($events | Where-Object kind -CEQ 'owned-job-closed')
    if ($exit.Count -ne 1 -or $exit[0].data.reason -cne 'normal' -or [int]$exit[0].data.ExitCode -ne 0 -or
        $drain.Count -ne 1 -or $drain[0].data.reason -cne 'normal' -or -not $drain[0].data.empty -or
        $closed.Count -ne 1 -or $closed[0].data.reason -cne 'normal' -or -not $closed[0].data.assigned -or -not $closed[0].data.killOnCloseConfigured) {
        throw "$Label Job did not exit, drain and close normally: $ReceiptPath"
    }
}
function Assert-NormalReceipt($Context, [string]$Label) {
    $receipt = Read-Json (Join-Path ([string]$Context.runDirectory) 'bounded-result.json')
    if (-not $receipt.accepted -or -not $receipt.normalDrain -or [int]$receipt.exitCode -ne 0 -or
        [IO.Path]::GetFullPath([string]$receipt.runDirectory) -ine [IO.Path]::GetFullPath([string]$Context.runDirectory)) { throw "$Label has no accepted normally drained receipt." }
    $jobManifest = Read-Json (Join-Path ([string]$Context.runDirectory) 'job-manifest.json')
    $jobRun = [IO.Path]::GetFullPath([string]$jobManifest.runDirectory)
    Assert-JobReceipts (Join-Path $jobRun 'receipts.jsonl') $Label
}
function Assert-RetentionPlannerQualification($Change, $Item, $Recovery, [string]$RepositoryRoot) {
    $expectedModule = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'scripts\maintenance\GeneratedWorkspaceRetention.psm1'))
    $changedPath = [IO.Path]::GetFullPath([string]$Item.path)
    if ($changedPath -ine $expectedModule) { throw "Administrative maintenance change is outside the exact retained-workspace module: $changedPath" }
    if ($Change.PSObject.Properties.Name -notcontains 'retentionPlannerQualification') { throw 'Generated workspace retention change has no focused regression qualification evidence.' }
    $evidence = $Change.retentionPlannerQualification
    if ([string]$evidence.schema -cne 'rm/generated-workspace-retention-qualification/v1') { throw 'Generated workspace retention qualification schema is unsupported.' }

    $expectedBeforePath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.local\local-retention-closeout\GeneratedWorkspaceRetention-before-planning-fix.psm1'))
    $expectedTestPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'tests\GeneratedWorkspaceRetentionPlanning.Tests.ps1'))
    $expectedResultsPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.local\local-retention-closeout\planning-regression-results.json'))
    $expectedSuitePath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.local\local-retention-closeout\maintenance-suite-results.json'))

    Assert-FileProof $evidence.originalModule 'Original generated workspace retention module'
    if ([IO.Path]::GetFullPath([string]$evidence.originalModule.path) -ine $expectedBeforePath -or
        [long]$evidence.originalModule.length -ne [long]$Item.before.length -or
        [string]$evidence.originalModule.sha256 -ine [string]$Item.before.sha256) {
        throw 'Generated workspace retention original-module snapshot does not match the Native compile input.'
    }
    if ([long]$Change.current.length -ne [long]$Item.current.length -or [string]$Change.current.sha256 -ine [string]$Item.current.sha256) {
        throw 'Generated workspace retention current-module proof does not match the current Native compile input.'
    }
    if ($Recovery.PSObject.Properties.Name -notcontains 'sourceRecoveryProof') { throw 'Generated workspace retention qualification does not identify its original accepted recovery report.' }
    Assert-FileProof $Recovery.sourceRecoveryProof 'Original Native administrative-recovery report'
    $sourceRecovery = Read-Json ([string]$Recovery.sourceRecoveryProof.path)
    if (-not $sourceRecovery.acceptedCompile -or -not $sourceRecovery.inputsUnchangedAcrossCompile -or
        [IO.Path]::GetFullPath([string]$sourceRecovery.executionJob.executionManifestPath) -ine [IO.Path]::GetFullPath([string]$Recovery.executionJob.executionManifestPath) -or
        [string]$sourceRecovery.executionJob.executionManifestSha256 -ine [string]$Recovery.executionJob.executionManifestSha256 -or
        [IO.Path]::GetFullPath([string]$sourceRecovery.executionJob.inputManifestPath) -ine [IO.Path]::GetFullPath([string]$Recovery.executionJob.inputManifestPath) -or
        [string]$sourceRecovery.executionJob.inputManifestSha256 -ine [string]$Recovery.executionJob.inputManifestSha256 -or
        [IO.Path]::GetFullPath([string]$sourceRecovery.executionJob.receiptPath) -ine [IO.Path]::GetFullPath([string]$Recovery.executionJob.receiptPath) -or
        [string]$sourceRecovery.executionJob.receiptSha256 -ine [string]$Recovery.executionJob.receiptSha256) {
        throw 'Generated workspace retention qualification does not preserve the original accepted Native recovery proof.'
    }
    foreach ($sourceChange in @($sourceRecovery.postCompileAdministrativeToolChanges)) {
        $sourceMatches = @($Recovery.postCompileAdministrativeToolChanges | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq [IO.Path]::GetFullPath([string]$sourceChange.path) })
        if ($sourceMatches.Count -ne 1 -or
            [string]$sourceMatches[0].reason -cne [string]$sourceChange.reason -or
            [IO.Path]::GetFullPath([string]$sourceMatches[0].before.path) -ine [IO.Path]::GetFullPath([string]$sourceChange.before.path) -or
            [long]$sourceMatches[0].before.length -ne [long]$sourceChange.before.length -or [string]$sourceMatches[0].before.sha256 -ine [string]$sourceChange.before.sha256 -or
            [long]$sourceMatches[0].current.length -ne [long]$sourceChange.current.length -or [string]$sourceMatches[0].current.sha256 -ine [string]$sourceChange.current.sha256) {
            throw "Original Native administrative change was not preserved: $($sourceChange.path)"
        }
    }

    Assert-FileProof $evidence.planningTestSource 'Retention planner regression test source'
    if ([IO.Path]::GetFullPath([string]$evidence.planningTestSource.path) -ine $expectedTestPath) { throw 'Retention planner regression test source path is unexpected.' }
    Assert-FileProof $evidence.planningRegressionResults 'Retention planner regression result'
    if ([IO.Path]::GetFullPath([string]$evidence.planningRegressionResults.path) -ine $expectedResultsPath) { throw 'Retention planner regression result path is unexpected.' }
    $regression = Read-Json $expectedResultsPath
    if ([string]$regression.testSha256 -ine [string]$evidence.planningTestSource.sha256 -or
        [string]$regression.moduleSha256 -ine [string]$Item.current.sha256 -or
        [int]$regression.originalExitCode -ne 1 -or [int]$regression.currentPs7ExitCode -ne 0 -or [int]$regression.currentPs5ExitCode -ne 0) {
        throw 'Retention planner RED/GREEN regression results do not bind the original and current module bytes.'
    }
    $regressionLogRoles = @{}
    foreach ($proof in @($evidence.planningRegressionLogs)) {
        Assert-FileProof $proof 'Retention planner regression log'
        if ($regressionLogRoles.ContainsKey([string]$proof.role)) { throw 'Retention planner regression log role is duplicated.' }
        $regressionLogRoles[[string]$proof.role] = $proof
    }
    if ($regressionLogRoles.Count -ne 3 -or -not $regressionLogRoles.ContainsKey('red-ps7') -or
        -not $regressionLogRoles.ContainsKey('green-ps7') -or -not $regressionLogRoles.ContainsKey('green-ps5')) {
        throw 'Retention planner proof must include one PS7 RED and PS7/PS5 GREEN log.'
    }
    $expectedRegressionLogs = @{
        'red-ps7' = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $expectedResultsPath) 'planning-regression-red-ps7.log'))
        'green-ps7' = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $expectedResultsPath) 'planning-regression-green-ps7.log'))
        'green-ps5' = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $expectedResultsPath) 'planning-regression-green-ps5.log'))
    }
    foreach ($role in @('red-ps7','green-ps7','green-ps5')) {
        $proof = $regressionLogRoles[$role]
        if ([IO.Path]::GetFullPath([string]$proof.path) -ine $expectedRegressionLogs[$role]) { throw "Unexpected retention planner log path for $role." }
        $log = Get-Content -LiteralPath ([string]$proof.path) -Raw
        if ($role -eq 'red-ps7' -and $log -notmatch 'nodes=5001/5000') { throw 'Retention planner RED log does not show the original bounded-scan failure.' }
        if ($role -ne 'red-ps7' -and $log -notmatch 'GeneratedWorkspaceRetentionPlanning\.Tests passed \(34 assertions\)') { throw "Retention planner GREEN log is incomplete: $role" }
    }

    Assert-FileProof $evidence.maintenanceSuiteResults 'Maintenance regression suite result'
    if ([IO.Path]::GetFullPath([string]$evidence.maintenanceSuiteResults.path) -ine $expectedSuitePath) { throw 'Maintenance regression suite result path is unexpected.' }
    $suite = @(Read-Json $expectedSuitePath)
    if ($suite.Count -ne 6) { throw 'Maintenance regression suite must contain both hosts for all three suites.' }
    $suiteLogProofs = @{}
    foreach ($proof in @($evidence.maintenanceSuiteLogs)) {
        Assert-FileProof $proof 'Maintenance regression suite log'
        $key = [IO.Path]::GetFullPath([string]$proof.path)
        if ($suiteLogProofs.ContainsKey($key)) { throw "Maintenance regression log is duplicated: $key" }
        $suiteLogProofs[$key] = $proof
    }
    if ($suiteLogProofs.Count -ne 6) { throw 'Maintenance regression proof must bind all six host/suite logs.' }
    $suiteKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $suite) {
        if ([int]$row.exitCode -ne 0) { throw "Maintenance regression suite failed: $($row.host)/$($row.test)" }
        $expectedText = switch ([string]$row.test) {
            'engine' { 'assertions=71' }
            'automatic' { 'assertions=16' }
            'batch' { 'Passed 32 checks;' }
            default { throw "Unexpected maintenance regression suite: $($row.test)" }
        }
        $suiteKey = "$( [string]$row.host )/$( [string]$row.test )"
        if ($row.host -notin @('ps7','ps5') -or -not $suiteKeys.Add($suiteKey)) { throw "Duplicate or invalid maintenance suite row: $suiteKey" }
        $logPath = [IO.Path]::GetFullPath([string]$row.log)
        if (-not $suiteLogProofs.ContainsKey($logPath)) { throw "Maintenance suite log is not hash-bound: $logPath" }
        $log = Get-Content -LiteralPath $logPath -Raw
        if ($log -notmatch [regex]::Escape($expectedText) -or $log -notmatch [regex]::Escape([string]$row.lastLine)) {
            throw "Maintenance suite log does not support its successful result: $suiteKey"
        }
    }
    if ($suiteKeys.Count -ne 6) { throw 'Maintenance regression suite is missing a host/test result.' }
}
function Assert-CurrentInputHashes($InputEvidence, $Context, $ManifestProof, $InputProof, $ReceiptProof) {
    $changed = [Collections.Generic.List[object]]::new()
    foreach ($row in @($InputEvidence.before)) {
        $rowPath = [IO.Path]::GetFullPath([string]$row.path)
        if (-not [IO.File]::Exists($rowPath)) { throw "Native compile input disappeared: $rowPath" }
        $file = [IO.FileInfo]::new($rowPath)
        $hash = (Get-FileHash -LiteralPath $rowPath -Algorithm SHA256).Hash
        if ($file.Length -ne [long]$row.length -or $hash -ine [string]$row.sha256) {
            $changed.Add([pscustomobject]@{path=$rowPath;before=$row;current=[pscustomobject]@{length=$file.Length;sha256=$hash}})
        }
    }
    if ($changed.Count -eq 0) { return $null }
    $recoveryProofs = @()
    if ($Context.reuseProof.PSObject.Properties.Name -contains 'administrativeRecoveries') {
        $recoveryProofs = @($Context.reuseProof.administrativeRecoveries)
    }
    if ($recoveryProofs.Count -eq 0) { throw 'Native compile inputs changed without explicit administrative-recovery proofs.' }
    $matchingRecoveries = [Collections.Generic.List[object]]::new()
    foreach ($recoveryProof in $recoveryProofs) {
        Assert-FileProof $recoveryProof 'Native administrative-recovery report'
        $candidate = Read-Json ([string]$recoveryProof.path)
        if ($candidate.PSObject.Properties.Name -notcontains 'executionJob') { continue }
        $job = $candidate.executionJob
        if ([IO.Path]::GetFullPath([string]$job.executionManifestPath) -ieq [IO.Path]::GetFullPath([string]$ManifestProof.path) -and
            [string]$job.executionManifestSha256 -ieq [string]$ManifestProof.sha256 -and
            [IO.Path]::GetFullPath([string]$job.inputManifestPath) -ieq [IO.Path]::GetFullPath([string]$InputProof.path) -and
            [string]$job.inputManifestSha256 -ieq [string]$InputProof.sha256 -and
            [IO.Path]::GetFullPath([string]$job.receiptPath) -ieq [IO.Path]::GetFullPath([string]$ReceiptProof.path) -and
            [string]$job.receiptSha256 -ieq [string]$ReceiptProof.sha256) {
            $matchingRecoveries.Add([pscustomobject]@{proof=$recoveryProof;report=$candidate})
        }
    }
    if ($matchingRecoveries.Count -ne 1) { throw "Expected exactly one administrative-recovery proof for this Native Job; found $($matchingRecoveries.Count)." }
    $recovery = $matchingRecoveries[0].report
    if (-not $recovery.acceptedCompile -or -not $recovery.inputsUnchangedAcrossCompile) {
        throw 'Native administrative-recovery report does not accept an unchanged compile.'
    }
    $declared = @($recovery.postCompileAdministrativeToolChanges)
    foreach ($item in $changed) {
        $matches = @($declared | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq [string]$item.path })
        if ($matches.Count -ne 1) { throw "Unreported Native compile input changed after its accepted Job: $($item.path)" }
        $change = $matches[0]
        $repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
            [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
        } else { [IO.Path]::GetFullPath($RepositoryRoot) }
        $validationRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'scripts\validation')) + [IO.Path]::DirectorySeparatorChar
        $changedPath = [IO.Path]::GetFullPath([string]$item.path)
        $retentionPlannerPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'scripts\maintenance\GeneratedWorkspaceRetention.psm1'))
        $isRetentionPlanner = $changedPath -ieq $retentionPlannerPath
        if (-not $isRetentionPlanner -and
            (-not $changedPath.StartsWith($validationRoot,[StringComparison]::OrdinalIgnoreCase) -or
             [IO.Path]::GetExtension($changedPath) -ine '.ps1')) {
            throw "Reported post-compile change is not a validation control script: $($item.path)"
        }
        if ([IO.Path]::GetFullPath([string]$change.before.path) -ine [string]$item.path -or
            [long]$change.before.length -ne [long]$item.before.length -or [string]$change.before.sha256 -ine [string]$item.before.sha256 -or
            [long]$change.current.length -ne [long]$item.current.length -or [string]$change.current.sha256 -ine [string]$item.current.sha256 -or
            [string]::IsNullOrWhiteSpace([string]$change.reason)) {
            throw "Native compile administrative change proof does not match current bytes: $($item.path)"
        }
        if ($isRetentionPlanner) { Assert-RetentionPlannerQualification $change $item $recovery $repoRoot }
    }
    if ($declared.Count -ne $changed.Count) { throw 'Native compile recovery report contains undeclared current input changes.' }
    return $recovery.executionJob
}
$path = [IO.Path]::GetFullPath($ContextPath)
$current = Read-Json $path
Assert-Context $current 'Native context' -VerifyInputs
if ($current.PSObject.Properties.Name -notcontains 'reuseProof' -or $null -eq $current.reuseProof) {
    Assert-NormalReceipt $current 'Native context'
    [pscustomobject]@{accepted=$true;contextPath=$path;mode='single-run';outputs=@($current.outputs).Count}
    return
}
$baseProof = $current.reuseProof.baseContext
Assert-FileProof $baseProof 'Base Native context'
$base = Read-Json ([string]$baseProof.path)
Assert-Context $base 'Base Native context'
Assert-NormalReceipt $base 'Base Native context'
$baseOutputs = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($row in $base.outputs) { $baseOutputs.Add([IO.Path]::GetFullPath([string]$row.path),$row) }
$reused = 0
$new = 0
foreach ($row in $current.outputs) {
    $key = [IO.Path]::GetFullPath([string]$row.path)
    if ($baseOutputs.ContainsKey($key)) {
        $old = $baseOutputs[$key]
        if ([long]$old.length -ne [long]$row.length -or [string]$old.sha256 -ine [string]$row.sha256) { throw "Reused Native output changed: $key" }
        $reused++
    } else { $new++ }
}
if ($reused -ne [int]$current.reuseProof.reusedOutputCount -or $new -ne [int]$current.reuseProof.newCompileOutputCount) {
    throw 'Native composite reuse split does not match its path/length/hash rows.'
}
$records = @($current.reuseProof.jobs)
$manifests = @($records | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ceq 'job-manifest.json' })
$inputs = @($records | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ceq 'input-evidence.json' })
$receipts = @($records | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ceq 'receipts.jsonl' })
if ($manifests.Count -eq 0 -or $manifests.Count -ne $inputs.Count -or $manifests.Count -ne $receipts.Count -or $new -ne $manifests.Count) {
    throw 'Each new Native output must have a manifest, input snapshot and real Job receipt.'
}
foreach ($proof in $records) { Assert-FileProof $proof 'Native compile Job evidence' }
foreach ($proof in $manifests) {
    $manifest = Read-Json ([string]$proof.path)
    $jobRoot = Split-Path -Parent ([string]$proof.path)
    $inputPath = Join-Path $jobRoot 'input-evidence.json'
    if (@($inputs | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq [IO.Path]::GetFullPath($inputPath) }).Count -ne 1) { throw "Missing input proof: $inputPath" }
    $inputEvidence = Read-Json $inputPath
    if (-not $inputEvidence.unchanged -or $inputEvidence.afterError -or @($inputEvidence.before).Count -eq 0) { throw "Native compile input snapshot is not stable: $inputPath" }
    foreach ($row in @($inputEvidence.before)+@($inputEvidence.after)) {
        if (-not [IO.Path]::IsPathFullyQualified([string]$row.path) -or [long]$row.length -lt 0 -or [string]$row.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "Native compile input snapshot has an invalid path/length/hash row: $inputPath"
        }
    }
    $before = @($inputEvidence.before | ForEach-Object { ([IO.Path]::GetFullPath([string]$_.path)+'|'+[long]$_.length+'|'+[string]$_.sha256) } | Sort-Object)
    $after = @($inputEvidence.after | ForEach-Object { ([IO.Path]::GetFullPath([string]$_.path)+'|'+[long]$_.length+'|'+[string]$_.sha256) } | Sort-Object)
    if ([string]::Join([Environment]::NewLine,[string[]]$before) -cne [string]::Join([Environment]::NewLine,[string[]]$after)) { throw "Native compile inputs changed: $inputPath" }
    $receiptPath = Join-Path ([string]$manifest.runDirectory) 'receipts.jsonl'
    $receiptProof = @($receipts | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq [IO.Path]::GetFullPath($receiptPath) })
    if ($receiptProof.Count -ne 1) { throw "Missing native Job receipt proof: $receiptPath" }
    $inputProof = @($inputs | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq [IO.Path]::GetFullPath($inputPath) })[0]
    $administrativeRecovery = $null
    $null = $inputProof
    Assert-JobReceipts $receiptPath 'Native compile'
    $arguments = [string[]]@($manifest.arguments)
    $outputPath = $null
    for ($i=0; $i -lt $arguments.Count; $i++) {
        if ($arguments[$i] -match '^-femit-bin=(.+)$') { $outputPath=[IO.Path]::GetFullPath($Matches[1]); break }
        if ($arguments[$i] -ceq '-o' -and $i+1 -lt $arguments.Count) { $outputPath=[IO.Path]::GetFullPath($arguments[$i+1]); break }
    }
    if (-not $outputPath -or @($current.outputs | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq $outputPath }).Count -ne 1) { throw "Native compile output is not bound to the composite context: $outputPath" }
    $administrativeRecovery = Assert-CurrentInputHashes $inputEvidence $current $proof $inputProof $receiptProof[0]
    if ($administrativeRecovery) {
        $reportedOutputs = @($administrativeRecovery.outputs | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq $outputPath })
        $contextOutput = @($current.outputs | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq $outputPath })[0]
        if ($reportedOutputs.Count -ne 1 -or [long]$reportedOutputs[0].length -ne [long]$contextOutput.length -or
            [string]$reportedOutputs[0].sha256 -ine [string]$contextOutput.sha256) { throw "Native compile recovery output differs from its composite context: $outputPath" }
    }
}
[pscustomobject]@{accepted=$true;contextPath=$path;mode='composite';reusedOutputCount=$reused;newCompileOutputCount=$new;normalCompileJobs=$manifests.Count}
