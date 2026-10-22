#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanPath,
    [string]$PartitionId,
    [ValidateSet('Build','Tests')][string]$Stage = 'Build'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Get-AppTestCompileInputScope.ps1')

function Assert-InputScope([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$planFile = [IO.Path]::GetFullPath($PlanPath)
$plan = Get-Content -LiteralPath $planFile -Raw | ConvertFrom-Json
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName([string]$plan.projectPath)) '../..'))
$partition = if ($PartitionId) {
    @($plan.partitions | Where-Object { [string]$_.partitionId -ceq $PartitionId }) | Select-Object -First 1
} else {
    @($plan.partitions) | Select-Object -First 1
}
Assert-InputScope ($null -ne $partition) "Partition does not exist in plan: $PartitionId"
$manifest = Get-Content -LiteralPath ([string]$partition.manifestPath) -Raw | ConvertFrom-Json
$metadata = Get-Content -LiteralPath ([string]$manifest.originalCompileMetadata.path) -Raw | ConvertFrom-Json
$selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($source in @($manifest.sourceFiles)) { [void]$selected.Add([IO.Path]::GetFullPath([string]$source)) }
$unselected = @($metadata.Items.Compile | Where-Object {
    [IO.Path]::GetExtension([string]$_.FullPath) -ieq '.cs' -and
    (Test-AppTestPathUnder ([IO.Path]::GetFullPath([string]$_.FullPath)) ([IO.Path]::GetFullPath((Join-Path $repositoryRoot 'tests'))) ) -and
    -not $selected.Contains([IO.Path]::GetFullPath([string]$_.FullPath))
} | Select-Object -First 1)
Assert-InputScope ($unselected.Count -eq 1) 'The selected partition has no unselected test Compile source for the isolation check.'

$productSdk = Join-Path $repositoryRoot 'scripts/validation/ProductSdk'
$representativeInputs = @(
    [string]$manifest.projectPath,
    'Directory.Build.props',
    'src/Core/Configuration/FreedomPoints/backend.groups.json',
    'src/UI/Frontend/src/domain/metricFieldsWire.ts',
    'src/UI/Frontend/tests/wire-fixture.json',
    (Join-Path $productSdk 'Run-Stage.ps1'),
    (Join-Path $productSdk 'Get-DiskCheckpointNativeInputs.ps1'),
    (Join-Path $productSdk 'Save-AppTestOutputEvidence.ps1'),
    (Join-Path $productSdk 'AppTestCompilePartition.targets'),
    (Join-Path $productSdk 'serial-compiler.rsp'),
    (Join-Path $repositoryRoot 'scripts/build/Build-ResourceManagerLedger.ps1'),
    (Join-Path $repositoryRoot 'scripts/validation/FreedomPointCompilerCapture.targets'),
    (Join-Path $repositoryRoot '.local/backend-information-collection-20261004/serial.runsettings')
)
$coordinatorInputs = @(
    (Join-Path $productSdk 'Invoke-AppTestPartitions.ps1'),
    (Join-Path $productSdk 'Invoke-Validation.ps1'),
    (Join-Path $productSdk 'Review-BackendValidationPipeline.ps1'),
    (Join-Path $productSdk 'Get-AppTestCompileInputScope.ps1'),
    $PSCommandPath
)
$frontendTestInputs = @('src/UI/Frontend/tests/frontendWorkContract.test.ts','src/UI/Frontend/tests/settingsStore.test.ts','src/UI/Frontend/tests/example.test.tsx') | ForEach-Object { Join-Path $repositoryRoot $_ }
$runtimeSelection = [IO.Path]::GetFullPath((Join-Path $productSdk 'Get-AppTestRuntimeSelection.ps1'))
$selectedCompilePath = Join-Path ([IO.Path]::GetTempPath()) ('rm-app-test-selected-compile-' + [guid]::NewGuid().ToString('N') + '.txt')
$rows = [Collections.Generic.List[object]]::new()
foreach ($source in @($manifest.sourceFiles)) { $rows.Add([pscustomobject]@{path=[IO.Path]::GetFullPath([string]$source);sha256=('0' * 64)}) }
$rows.Add([pscustomobject]@{path=[IO.Path]::GetFullPath([string]$unselected[0].FullPath);sha256=('1' * 64)})
foreach ($path in $representativeInputs + $coordinatorInputs + $frontendTestInputs + @($runtimeSelection)) {
    $rows.Add([pscustomobject]@{path=[IO.Path]::GetFullPath($path);sha256=('2' * 64)})
}
try {
    [IO.File]::WriteAllLines($selectedCompilePath,[string[]]@($manifest.sourceFiles),[Text.UTF8Encoding]::new($false))
    $selectedCompileHash=(Get-FileHash -LiteralPath $selectedCompilePath -Algorithm SHA256).Hash
    $scoped = @(Get-AppTestScopedInputRows -Rows $rows.ToArray() -RepositoryRoot $repositoryRoot -Stage $Stage `
        -ManifestPath ([string]$partition.manifestPath) -OriginalCompileMetadataPath ([string]$manifest.originalCompileMetadata.path) `
        -SelectedCompilePath $selectedCompilePath -SelectedCompileSha256 $selectedCompileHash `
        -IncludeRuntimeSelection:([bool]($Stage -ceq 'Tests')))
    $scopedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $scoped) { [void]$scopedPaths.Add([IO.Path]::GetFullPath([string]$row.path)) }
    foreach ($source in $manifest.sourceFiles) {
        Assert-InputScope ($scopedPaths.Contains([IO.Path]::GetFullPath([string]$source))) "Selected Compile source was dropped: $source"
    }
    Assert-InputScope (-not $scopedPaths.Contains([IO.Path]::GetFullPath([string]$unselected[0].FullPath))) 'Unselected test Compile source remained in stage inputs.'
    foreach ($path in $representativeInputs) {
        Assert-InputScope ($scopedPaths.Contains([IO.Path]::GetFullPath($path))) "App build/import/resource input was dropped: $path"
    }
    foreach ($path in $frontendTestInputs) {
        Assert-InputScope (-not $scopedPaths.Contains([IO.Path]::GetFullPath($path))) "Frontend TS test remained in managed input scope: $path"
    }
    foreach ($path in $coordinatorInputs) {
        Assert-InputScope (-not $scopedPaths.Contains([IO.Path]::GetFullPath($path))) "Coordinator or review tool remained in stage inputs: $path"
    }
    Assert-InputScope ($scopedPaths.Contains($runtimeSelection) -eq ($Stage -ceq 'Tests')) 'Tests runtime selector scope differs from the actual stage.'

    [pscustomobject]@{
        accepted = $true
        stage = $Stage
        partitionId = [string]$partition.partitionId
        selectedCompileSourceCount = $selected.Count
        selectedCompileEvidenceBound = $true
        unselectedSourceExcluded = [IO.Path]::GetFullPath([string]$unselected[0].FullPath)
        retainedExecutionInputs = @($representativeInputs)
        excludedCoordinatorInputs = @($coordinatorInputs)
    } | ConvertTo-Json -Depth 5 -Compress
} finally {
    if ([IO.File]::Exists($selectedCompilePath)) { Remove-Item -LiteralPath $selectedCompilePath -Force }
}
