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
        if (-not $changedPath.StartsWith($validationRoot,[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetExtension($changedPath) -ine '.ps1') {
            throw "Reported post-compile change is not a validation control script: $($item.path)"
        }
        if ([IO.Path]::GetFullPath([string]$change.before.path) -ine [string]$item.path -or
            [long]$change.before.length -ne [long]$item.before.length -or [string]$change.before.sha256 -ine [string]$item.before.sha256 -or
            [long]$change.current.length -ne [long]$item.current.length -or [string]$change.current.sha256 -ine [string]$item.current.sha256 -or
            [string]::IsNullOrWhiteSpace([string]$change.reason)) {
            throw "Native compile administrative change proof does not match current bytes: $($item.path)"
        }
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
