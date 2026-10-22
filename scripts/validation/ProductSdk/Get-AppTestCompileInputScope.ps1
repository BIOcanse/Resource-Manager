function Get-AppTestFullPath([string]$Path, [string]$RepositoryRoot) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Path))
}

function Test-AppTestPathUnder([string]$Path, [string]$Directory) {
    $relative = [IO.Path]::GetRelativePath($Directory, $Path)
    return -not [IO.Path]::IsPathRooted($relative) -and $relative -cne '..' -and
        -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)
}

function Get-AppTestSelectedCompileEvidence(
    [string]$BuildRunDirectory,
    [string]$ExpectedPartitionId,
    [string]$ExpectedManifestPath,
    [string]$ExpectedManifestSha256,
    [string]$ExpectedSelectedCompileSha256
) {
    $buildRun = [IO.Path]::GetFullPath($BuildRunDirectory)
    $buildPath = Join-Path $buildRun 'partition-build.json'
    if (-not [IO.File]::Exists($buildPath)) { throw "Missing partition Build evidence: $buildPath" }
    $build = Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($ExpectedPartitionId) -or [string]::IsNullOrWhiteSpace($ExpectedManifestPath) -or
        [string]::IsNullOrWhiteSpace([string]$build.runDirectory) -or [string]::IsNullOrWhiteSpace([string]$build.testCompileManifest)) {
        throw "Partition Build evidence lacks its required identity binding: $buildRun"
    }
    $recordedRun = [IO.Path]::GetFullPath([string]$build.runDirectory)
    $recordedManifest = [IO.Path]::GetFullPath([string]$build.testCompileManifest)
    $expectedManifest = [IO.Path]::GetFullPath($ExpectedManifestPath)
    $selectedPath = [IO.Path]::GetFullPath((Join-Path $buildRun 'compile-partition/selected-compile.txt'))
    if (-not $build.buildSucceeded -or
        $recordedRun -ine $buildRun -or
        [string]$build.partitionId -cne $ExpectedPartitionId -or
        $recordedManifest -ine $expectedManifest -or
        [string]$build.testCompileManifestSha256 -ine $ExpectedManifestSha256 -or
        [string]$build.selectedCompileSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        ($ExpectedSelectedCompileSha256 -and [string]$build.selectedCompileSha256 -ine $ExpectedSelectedCompileSha256) -or
        -not [IO.File]::Exists($selectedPath) -or
        (Get-FileHash -LiteralPath $selectedPath -Algorithm SHA256).Hash -ine [string]$build.selectedCompileSha256) {
        throw "Actual selected Compile evidence does not match its accepted Build: $buildRun"
    }
    return [pscustomobject]@{path=$selectedPath;sha256=[string]$build.selectedCompileSha256;buildRunDirectory=$buildRun;build=$build}
}

function Get-AppTestCompileInputScope(
    [string]$RepositoryRoot,
    [string]$Stage,
    [string]$ManifestPath,
    [string]$OriginalCompileMetadataPath,
    [string]$SelectedCompilePath,
    [string]$SelectedCompileSha256,
    [bool]$IncludeRuntimeSelection
) {
    if ($Stage -notin @('Build','Tests')) { throw "App test input scope does not support stage: $Stage" }
    $repository = [IO.Path]::GetFullPath($RepositoryRoot)
    $manifestFile = [IO.Path]::GetFullPath($ManifestPath)
    $metadataFile = [IO.Path]::GetFullPath($OriginalCompileMetadataPath)
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $metadata = Get-Content -LiteralPath $metadataFile -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or @($manifest.sourceFiles).Count -eq 0 -or
        $metadata.Items.PSObject.Properties.Name -notcontains 'Compile') {
        throw "Invalid partition Compile scope: $manifestFile"
    }
    $expectedMetadataHash = [string]$manifest.originalCompileMetadata.sha256
    if ($expectedMetadataHash -notmatch '^[0-9A-Fa-f]{64}$' -or
        (Get-FileHash -LiteralPath $metadataFile -Algorithm SHA256).Hash -ine $expectedMetadataHash) {
        throw "Original Compile metadata differs from its partition manifest: $metadataFile"
    }

    $selectedSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in @($manifest.sourceFiles)) {
        $full = [IO.Path]::GetFullPath([string]$source)
        if (-not [IO.Path]::IsPathFullyQualified([string]$source) -or
            [IO.Path]::GetExtension($full) -ine '.cs' -or -not $selectedSources.Add($full)) {
            throw "Invalid selected Compile source in partition manifest: $source"
        }
    }
    $originalCompileSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @($metadata.Items.Compile)) {
        if ([string]$item.FullPath -and [IO.Path]::GetExtension([string]$item.FullPath) -ieq '.cs') {
            [void]$originalCompileSources.Add([IO.Path]::GetFullPath([string]$item.FullPath))
        }
    }
    foreach ($source in $selectedSources) {
        if (-not $originalCompileSources.Contains($source)) {
            throw "Selected source is absent from original Compile metadata: $source"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($SelectedCompilePath)) {
        $selectedFile = [IO.Path]::GetFullPath($SelectedCompilePath)
        if ([string]$SelectedCompileSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not [IO.File]::Exists($selectedFile) -or
            (Get-FileHash -LiteralPath $selectedFile -Algorithm SHA256).Hash -ine $SelectedCompileSha256) {
            throw "Actual selected Compile evidence is missing or changed: $selectedFile"
        }
        $testRoot = [IO.Path]::GetFullPath((Join-Path $repository 'tests'))
        $actualTestSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in Get-Content -LiteralPath $selectedFile) {
            if ([string]::IsNullOrWhiteSpace([string]$line)) { continue }
            $full = Get-AppTestFullPath ([string]$line) $repository
            if ([IO.Path]::GetExtension($full) -ieq '.cs' -and (Test-AppTestPathUnder $full $testRoot)) {
                if (-not $originalCompileSources.Contains($full)) {
                    throw "Selected test Compile source is absent from original Compile metadata: $full"
                }
                if (-not $actualTestSources.Add($full)) { throw "Duplicate test source in selected Compile evidence: $full" }
            }
        }
        if ($actualTestSources.Count -eq 0 -or -not $actualTestSources.SetEquals($selectedSources)) {
            throw "Actual selected test Compile set differs from its partition manifest: $selectedFile"
        }
        $selectedSources = $actualTestSources
    } elseif (-not [string]::IsNullOrWhiteSpace($SelectedCompileSha256)) {
        throw 'SelectedCompileSha256 requires SelectedCompilePath.'
    }

    $productSdk = Join-Path $repository 'scripts/validation/ProductSdk'
    # Run-Stage calls Get-DiskCheckpointNativeInputs and Save-AppTestOutputEvidence
    # in both stages, plus Get-AppTestRuntimeSelection for Tests. These and the
    # other non-control ProductSdk inputs remain in the captured input set.
    # Only coordinator, reviewer and this isolated test harness are excluded
    # from broad repository captures. Other scripts/build and validation
    # inputs remain bound, including ProductSdk non-PowerShell files.
    $coordinatorScripts = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @(
        (Join-Path $productSdk 'Invoke-AppTestPartitions.ps1'),
        (Join-Path $productSdk 'Invoke-Validation.ps1'),
        (Join-Path $productSdk 'Review-BackendValidationPipeline.ps1'),
        (Join-Path $productSdk 'Get-AppTestCompileInputScope.ps1'),
        (Join-Path $productSdk 'Test-AppTestCompileInputScope.ps1')
    )) { [void]$coordinatorScripts.Add([IO.Path]::GetFullPath($path)) }
    $runtimeSelectionScript = [IO.Path]::GetFullPath((Join-Path $productSdk 'Get-AppTestRuntimeSelection.ps1'))
    return [pscustomobject]@{
        repositoryRoot = $repository
        selectedSources = $selectedSources
        originalCompileSources = $originalCompileSources
        productSdk = [IO.Path]::GetFullPath($productSdk)
        coordinatorScripts = $coordinatorScripts
        runtimeSelectionScript = $runtimeSelectionScript
        includeRuntimeSelection = $IncludeRuntimeSelection
    }
}

function Get-AppTestScopedInputRows(
    [object[]]$Rows,
    [string]$RepositoryRoot,
    [string]$Stage,
    [string]$ManifestPath,
    [string]$OriginalCompileMetadataPath,
    [string]$SelectedCompilePath,
    [string]$SelectedCompileSha256,
    [switch]$IncludeRuntimeSelection
) {
    $scope = Get-AppTestCompileInputScope $RepositoryRoot $Stage $ManifestPath $OriginalCompileMetadataPath $SelectedCompilePath $SelectedCompileSha256 ([bool]$IncludeRuntimeSelection)
    $testRoot = [IO.Path]::GetFullPath((Join-Path $scope.repositoryRoot 'tests'))
    $result = [Collections.Generic.List[object]]::new()
    $capturedSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in @($Rows)) {
        $rawPath = if ($row -is [string]) { [string]$row } else { [string]$row.path }
        if ([string]::IsNullOrWhiteSpace($rawPath)) { continue }
        $fullPath = Get-AppTestFullPath $rawPath $scope.repositoryRoot
        $extension = [IO.Path]::GetExtension($fullPath)
        # Frontend TypeScript tests are neither managed Compile items nor app
        # resources. Their own frontend gate seals them; keep production TS and
        # JSON fixtures in this scope because managed tests can consume those.
        if ($extension -iin @('.ts', '.tsx') -and
            (Test-AppTestPathUnder $fullPath (Join-Path $scope.repositoryRoot 'src/UI/Frontend/tests'))) {
            continue
        }
        if ($extension -ieq '.cs' -and (Test-AppTestPathUnder $fullPath $testRoot)) {
            if (-not $scope.selectedSources.Contains($fullPath)) { continue }
            [void]$capturedSources.Add($fullPath)
        }
        if ((Test-AppTestPathUnder $fullPath $scope.productSdk) -and $extension -ieq '.ps1' -and
            $scope.coordinatorScripts.Contains($fullPath)) {
            continue
        }
        if ($extension -ieq '.ps1' -and $fullPath -ieq $scope.runtimeSelectionScript -and -not $scope.includeRuntimeSelection) {
            continue
        }
        if ($row -is [string]) { $result.Add($fullPath) } else { $result.Add($row) }
    }
    if (-not $capturedSources.SetEquals($scope.selectedSources)) {
        $missing = @($scope.selectedSources | Where-Object { -not $capturedSources.Contains($_) })
        throw "Partition input snapshot omits selected Compile sources: $([string]::Join(', ',[string[]]$missing))"
    }
    return $result.ToArray()
}
