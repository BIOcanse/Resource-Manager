<#
.SYNOPSIS
Promotes a package-consumed Ledger SDK candidate to the local artifacts/sdk source.

.DESCRIPTION
Requires a successful package-consumer receipt bound to the exact candidate
manifest and Native DLL result. This is local publication only; it does not
publish remotely or overwrite an existing package version.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CandidateRoot,
    [Parameter(Mandatory = $true)] [string] $ConsumerReceiptPath,
    [Parameter(Mandatory = $true)] [string] $WorkRoot,
    [string] $LinuxFixtureManifestPath,
    [string] $LinuxRuntimeResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8Json {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)] $Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 32
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Find-RegisteredWorkRoot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $cursor = [System.IO.DirectoryInfo]::new([System.IO.Path]::GetFullPath($Path))
    if (-not $cursor.Exists) { throw "WorkRoot must already exist: $($cursor.FullName)" }
    while ($null -ne $cursor) {
        $recordPath = Join-Path $cursor.FullName '.generated-workspace.json'
        if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
            $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json -Depth 16
            if ([System.IO.Path]::GetFullPath([string]$record.workspacePath) -cne $cursor.FullName -or
                [string]$record.state -cne 'running') {
                throw "WorkRoot lifecycle record is not a matching running registration: $recordPath"
            }
            return $cursor.FullName
        }
        $cursor = $cursor.Parent
    }
    throw 'WorkRoot must be inside an existing workspace with a running .generated-workspace.json registration.'
}

function Assert-PathInside {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Description
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description is outside its allowed root: $fullPath"
    }
}

function Get-CandidateFile {
    param([Parameter(Mandatory = $true)][string] $RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(':') -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Unsafe candidate-relative path: $RelativePath"
    }
    $path = [System.IO.Path]::GetFullPath((Join-Path $candidate $RelativePath))
    Assert-PathInside -Path $path -Root $candidate -Description 'Candidate file'
    return $path
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$candidate = [System.IO.Path]::GetFullPath($CandidateRoot)
$receiptPath = [System.IO.Path]::GetFullPath($ConsumerReceiptPath)
$workRootFull = [System.IO.Path]::GetFullPath($WorkRoot)
$registeredRoot = Find-RegisteredWorkRoot -Path $workRootFull
if ($registeredRoot -cne $workRootFull) { throw "Pass the registered workspace root itself as WorkRoot: $registeredRoot" }
Assert-PathInside -Path $candidate -Root $workRootFull -Description 'CandidateRoot'
if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { throw "Candidate root is missing: $candidate" }
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Package-consumer receipt is missing: $receiptPath" }

$candidateManifestPath = Join-Path $candidate 'manifest.json'
if (-not (Test-Path -LiteralPath $candidateManifestPath -PathType Leaf)) { throw 'Candidate manifest is missing.' }
$candidateManifestHash = Get-Sha256 -Path $candidateManifestPath
$candidateManifest = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json -Depth 32
if ($candidateManifest.schema -cne 'resource-manager/ledger-sdk-candidate/v1' -or
    $candidateManifest.status -cne 'candidate-not-consumer-validated' -or
    $candidateManifest.candidate -cne 'c2_chain' -or [int]$candidateManifest.abiVersion -ne 1) {
    throw 'Candidate manifest has an unsupported schema or candidate state.'
}

$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 24
if ($receipt.schema -cne 'resource-manager/ledger-package-consumer/v1' -or $receipt.passed -ne $true) {
    throw 'A successful ResourceManager.Ledger package-consumer receipt is required.'
}
if ([System.IO.Path]::GetFullPath([string]$receipt.candidateManifestPath) -cne $candidateManifestPath -or
    [string]$receipt.candidateManifestSha256 -cne $candidateManifestHash) {
    throw 'Package-consumer receipt is not bound to this candidate manifest.'
}
$consumerResultPath = [System.IO.Path]::GetFullPath([string]$receipt.consumerResultPath)
$nativeBuildResultPath = [System.IO.Path]::GetFullPath([string]$receipt.nativeBuildResultPath)
$nativeDllPath = [System.IO.Path]::GetFullPath([string]$receipt.nativeDllPath)
foreach ($path in @($consumerResultPath, $nativeBuildResultPath, $nativeDllPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package-consumer evidence file is missing: $path" }
}
if ((Get-Sha256 -Path $consumerResultPath) -cne [string]$receipt.consumerResultSha256 -or
    (Get-Sha256 -Path $nativeBuildResultPath) -cne [string]$receipt.nativeBuildResultSha256 -or
    (Get-Sha256 -Path $nativeDllPath) -cne [string]$receipt.nativeDllSha256) {
    throw 'Package-consumer result or built Native DLL hash does not match its receipt.'
}
$nativeBuildResult = Get-Content -LiteralPath $nativeBuildResultPath -Raw | ConvertFrom-Json -Depth 32
if ($nativeBuildResult.schema -cne 'resource-manager/ledger-native-build-result/v1' -or
    $nativeBuildResult.status -cne 'built-candidate' -or $nativeBuildResult.candidate -cne 'c2_chain' -or
    [System.IO.Path]::GetFullPath([string]$nativeBuildResult.binary.path) -cne $nativeDllPath -or
    [string]$nativeBuildResult.binary.sha256 -cne [string]$receipt.nativeDllSha256) {
    throw 'The package-consumer receipt is not bound to the recorded C2 Native build result and DLL.'
}

$version = [string]$candidateManifest.version
if (@($candidateManifest.PSObject.Properties.Name) -contains 'capabilities' -and
    @($candidateManifest.capabilities) -contains 'optional-development-allocation-statistics') {
    $statisticsResult = Get-Content -LiteralPath $consumerResultPath -Raw | ConvertFrom-Json -Depth 40
    $profiles = @($statisticsResult.profiles)
    if ($statisticsResult.passed -ne $true -or $profiles.Count -ne 4) {
        throw 'The statistics SDK requires four installed-package profiles.'
    }
    foreach ($bits in @(128,256)) {
        foreach ($enabled in @($false,$true)) {
            $matched = @($profiles | Where-Object {
                [int]$_.uidWidthBits -eq $bits -and [bool]$_.developmentAllocationStatistics -eq $enabled
            })
            if ($matched.Count -ne 1 -or $matched[0].probeResult.passed -ne $true -or
                @($matched[0].probeResult.cases).Count -ne 15 -or
                @($matched[0].probeResult.cases) -notcontains 'development_allocation_statistics') {
                throw "Missing passing statistics profile UID$bits enabled=$enabled."
            }
            foreach ($evidence in @(
                @('nativeBuildResultPath','nativeBuildResultSha256'),
                @('nativeDllPath','nativeDllSha256'),
                @('consumerAssemblyPath','consumerAssemblySha256')
            )) {
                if ((Get-Sha256 -Path ([string]$matched[0].($evidence[0]))) -cne [string]$matched[0].($evidence[1])) {
                    throw 'A statistics profile input or output no longer matches its receipt.'
                }
            }
        }
    }
}
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Candidate manifest version is not a supported SemVer version.'
}
if ([string]$candidateManifest.target -cne 'win-x64') { throw 'Candidate target must be win-x64.' }
$packageRows = @($candidateManifest.packages)
$expectedPackageIds = @('ResourceManager.Ledger', 'ResourceManager.Ledger.Build', 'ResourceManager.Ledger.Configuration')
if ($packageRows.Count -ne $expectedPackageIds.Count) { throw 'Candidate does not contain exactly the three SDK packages.' }
foreach ($packageId in $expectedPackageIds) {
    $rows = @($packageRows | Where-Object { [string]$_.id -ceq $packageId -and [string]$_.version -ceq $version })
    if ($rows.Count -ne 1) { throw "Candidate package row is missing or repeated: $packageId" }
    $expectedFileName = "$packageId.$version.nupkg"
    if ([System.IO.Path]::GetFileName([string]$rows[0].file) -cne $expectedFileName) {
        throw "Candidate package path does not match its package ID and version: $packageId"
    }
    $packagePath = Get-CandidateFile -RelativePath ([string]$rows[0].file)
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
        (Get-Item -LiteralPath $packagePath).Length -ne [long]$rows[0].length -or
        (Get-Sha256 -Path $packagePath) -cne [string]$rows[0].sha256) {
        throw "Candidate package differs from its manifest: $packagePath"
    }
}
$archivePath = Get-CandidateFile -RelativePath ([string]$candidateManifest.nativeSource.file)
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf) -or
    (Get-Item -LiteralPath $archivePath).Length -ne [long]$candidateManifest.nativeSource.length -or
    (Get-Sha256 -Path $archivePath) -cne [string]$candidateManifest.nativeSource.sha256) {
    throw 'Native source archive differs from the candidate manifest.'
}
$toolManifestPath = Get-CandidateFile -RelativePath ([string]$candidateManifest.tools.file)
if (-not (Test-Path -LiteralPath $toolManifestPath -PathType Leaf) -or
    (Get-Sha256 -Path $toolManifestPath) -cne [string]$candidateManifest.tools.sha256) {
    throw 'Candidate dotnet tool manifest differs from the candidate manifest.'
}
$dependencies = @($candidateManifest.freedomPointDependencies)
$expectedDependencyIds = @('FreedomPoint.Core', 'FreedomPoint.Generators')
if ($dependencies.Count -ne $expectedDependencyIds.Count) { throw 'Candidate must bind both official FreedomPoint packages exactly once.' }
foreach ($dependencyId in $expectedDependencyIds) {
    $dependencyRows = @($dependencies | Where-Object { [string]$_.id -ceq $dependencyId })
    if ($dependencyRows.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$dependencyRows[0].version)) {
        throw "Candidate FreedomPoint dependency row is missing or repeated: $dependencyId"
    }
    $expectedDependencyFile = "$dependencyId.$([string]$dependencyRows[0].version).nupkg"
    if ([System.IO.Path]::GetFileName([string]$dependencyRows[0].feedPath) -cne $expectedDependencyFile) {
        throw "Candidate FreedomPoint dependency path does not match its package ID and version: $dependencyId"
    }
}
$dependencyVersions = @($dependencies | ForEach-Object { [string]$_.version } | Sort-Object -Unique)
if ($dependencyVersions.Count -ne 1) { throw 'Both FreedomPoint packages must use the same selected SDK version.' }
foreach ($dependency in $dependencies) {
    if (-not (Test-Path -LiteralPath ([string]$dependency.feedPath) -PathType Leaf) -or
        (Get-Item -LiteralPath ([string]$dependency.feedPath)).Length -ne [long]$dependency.length -or
        (Get-Sha256 -Path ([string]$dependency.feedPath)) -cne [string]$dependency.sha256) {
        throw "Formal FreedomPoint dependency changed since candidate pack: $($dependency.feedPath)"
    }
}

$linuxQualification = $null
if ([string]::IsNullOrWhiteSpace($LinuxFixtureManifestPath) -ne [string]::IsNullOrWhiteSpace($LinuxRuntimeResultPath)) {
    throw 'Pass both Linux fixture manifest and runtime result, or neither.'
}
if (-not [string]::IsNullOrWhiteSpace($LinuxFixtureManifestPath)) {
    $linuxQualification = & (Join-Path $PSScriptRoot 'Test-LedgerLinuxQualification.ps1') -CandidateManifestPath $candidateManifestPath -LinuxFixtureManifestPath $LinuxFixtureManifestPath -LinuxRuntimeResultPath $LinuxRuntimeResultPath
}

# The accepted manifests are tracked and published, so they name evidence without this machine's folders: a file
# under the WorkRoot's managed root as <rootId>:<path below that root>, any other file (the FreedomPoint feed, a
# Linux run in another checkout) by its file name. The hashes stay the binding.
$workRootRecord = Get-Content -LiteralPath (Join-Path $workRootFull '.generated-workspace.json') -Raw | ConvertFrom-Json -Depth 16
$managedRootPath = [System.IO.Path]::GetFullPath([string]$workRootRecord.rootPath)
function ConvertTo-RecordedPath {
    param([Parameter(Mandatory = $true)][string] $Path)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $relativePath = [System.IO.Path]::GetRelativePath($managedRootPath, $fullPath)
    if ([System.IO.Path]::IsPathRooted($relativePath) -or $relativePath -eq '..' -or
        $relativePath.StartsWith('..' + [System.IO.Path]::DirectorySeparatorChar)) {
        return [System.IO.Path]::GetFileName($fullPath)
    }
    return "$([string]$workRootRecord.rootId):$($relativePath.Replace('\', '/'))"
}
$recordedDependencies = @(
    foreach ($dependency in $dependencies) {
        $recorded = $dependency.PSObject.Copy()
        $recorded.feedPath = [System.IO.Path]::GetFileName([string]$dependency.feedPath)
        $recorded
    }
)
if ($null -ne $linuxQualification) {
    $linuxQualification.fixtureManifest.path = ConvertTo-RecordedPath -Path ([string]$linuxQualification.fixtureManifest.path)
    $linuxQualification.runtimeResult.path = ConvertTo-RecordedPath -Path ([string]$linuxQualification.runtimeResult.path)
    foreach ($log in @($linuxQualification.logs)) { $log.path = ConvertTo-RecordedPath -Path ([string]$log.path) }
}

$sdkRoot = Join-Path $repositoryRoot 'artifacts\sdk'
$packagesRoot = Join-Path $sdkRoot 'packages'
$toolsVersionRoot = Join-Path (Join-Path $sdkRoot 'tools') $version
$nativeVersionRoot = Join-Path (Join-Path $sdkRoot 'native') $version
$acceptedManifestPath = Join-Path $sdkRoot "manifest.$version.json"
foreach ($path in @($toolsVersionRoot, $nativeVersionRoot, $acceptedManifestPath)) {
    if (Test-Path -LiteralPath $path) { throw "Local SDK version already exists; refusing overwrite: $path" }
}
foreach ($package in $packageRows) {
    $destination = Join-Path $packagesRoot ([System.IO.Path]::GetFileName([string]$package.file))
    if (Test-Path -LiteralPath $destination) { throw "Local NuGet feed already contains this version: $destination" }
}

$promotionId = [guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $workRootFull ('.ledger-sdk-publish-stage-' + $version + '-' + $promotionId)
$stagePackages = Join-Path $stageRoot 'packages'
$stageTools = Join-Path $stageRoot 'tools'
$stageNative = Join-Path $stageRoot 'native'
$stageToolConfig = Join-Path $stageTools '.config'
New-Item -ItemType Directory -Path $stagePackages, $stageToolConfig, $stageNative -Force | Out-Null
foreach ($package in $packageRows) {
    Copy-Item -LiteralPath (Join-Path $candidate ([string]$package.file)) -Destination (Join-Path $stagePackages ([System.IO.Path]::GetFileName([string]$package.file)))
}
Copy-Item -LiteralPath $toolManifestPath -Destination (Join-Path $stageToolConfig 'dotnet-tools.json')
Copy-Item -LiteralPath (Join-Path $candidate 'tools\README.md') -Destination (Join-Path $stageTools 'README.md')
Copy-Item -LiteralPath $archivePath -Destination (Join-Path $stageNative ([System.IO.Path]::GetFileName($archivePath)))

$nativeAcceptedManifest = [pscustomobject]@{
    schema = 'resource-manager/ledger-native-local-release/v1'
    status = 'accepted-package-consumer'
    version = $version
    candidateManifestSha256 = $candidateManifestHash
    nativeArchive = [pscustomobject]@{
        file = [System.IO.Path]::GetFileName($archivePath)
        length = (Get-Item -LiteralPath $archivePath).Length
        sha256 = Get-Sha256 -Path $archivePath
    }
    consumerReceipt = [pscustomobject]@{
        path = ConvertTo-RecordedPath -Path $receiptPath
        sha256 = Get-Sha256 -Path $receiptPath
        resultPath = ConvertTo-RecordedPath -Path $consumerResultPath
        resultSha256 = [string]$receipt.consumerResultSha256
        nativeBuildResultPath = ConvertTo-RecordedPath -Path $nativeBuildResultPath
        nativeBuildResultSha256 = [string]$receipt.nativeBuildResultSha256
        nativeDllPath = ConvertTo-RecordedPath -Path $nativeDllPath
        nativeDllSha256 = [string]$receipt.nativeDllSha256
    }
}
if (@($candidateManifest.PSObject.Properties.Name) -contains 'capabilities') {
    $nativeAcceptedManifest | Add-Member -NotePropertyName capabilities -NotePropertyValue @($candidateManifest.capabilities)
}
if ($null -ne $linuxQualification) { $nativeAcceptedManifest | Add-Member -NotePropertyName additionalValidation -NotePropertyValue $linuxQualification }
Write-Utf8Json -Path (Join-Path $stageNative 'manifest.json') -Value $nativeAcceptedManifest
$acceptedManifest = [pscustomobject]@{
    schema = 'resource-manager/ledger-sdk-local-release/v1'
    status = 'accepted-package-consumer'
    version = $version
    target = [string]$candidateManifest.target
    candidateManifest = [pscustomobject]@{ path = (ConvertTo-RecordedPath -Path $candidateManifestPath); sha256 = $candidateManifestHash }
    packages = @(
        foreach ($package in $packageRows) {
            [pscustomobject]@{
                id = [string]$package.id
                version = [string]$package.version
                file = "packages/$([System.IO.Path]::GetFileName([string]$package.file))"
                length = [long]$package.length
                sha256 = [string]$package.sha256
            }
        }
    )
    freedomPointDependencies = $recordedDependencies
    nativeSource = [pscustomobject]@{
        file = "native/$version/$([System.IO.Path]::GetFileName($archivePath))"
        sha256 = [string]$candidateManifest.nativeSource.sha256
        headerSha256 = [string]$candidateManifest.nativeSource.headerSha256
    }
    toolManifest = [pscustomobject]@{
        file = "tools/$version/.config/dotnet-tools.json"
        sha256 = Get-Sha256 -Path (Join-Path $stageToolConfig 'dotnet-tools.json')
    }
    consumer = [pscustomobject]@{
        receiptPath = ConvertTo-RecordedPath -Path $receiptPath
        receiptSha256 = Get-Sha256 -Path $receiptPath
        resultPath = ConvertTo-RecordedPath -Path $consumerResultPath
        resultSha256 = [string]$receipt.consumerResultSha256
        nativeBuildResultPath = ConvertTo-RecordedPath -Path $nativeBuildResultPath
        nativeBuildResultSha256 = [string]$receipt.nativeBuildResultSha256
        nativeDllPath = ConvertTo-RecordedPath -Path $nativeDllPath
        nativeDllSha256 = [string]$receipt.nativeDllSha256
    }
    publishedAtUtc = [DateTime]::UtcNow.ToString('O')
}
if (@($candidateManifest.PSObject.Properties.Name) -contains 'capabilities') {
    $acceptedManifest | Add-Member -NotePropertyName capabilities -NotePropertyValue @($candidateManifest.capabilities)
}
if ($null -ne $linuxQualification) { $acceptedManifest.nativeSource | Add-Member -NotePropertyName additionalValidation -NotePropertyValue $linuxQualification }
Write-Utf8Json -Path (Join-Path $stageRoot 'manifest.json') -Value $acceptedManifest

New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null
$movedFiles = [System.Collections.Generic.List[string]]::new()
$movedDirectories = [System.Collections.Generic.List[string]]::new()
try {
    foreach ($package in $packageRows) {
        $name = [System.IO.Path]::GetFileName([string]$package.file)
        $destination = Join-Path $packagesRoot $name
        if (Test-Path -LiteralPath $destination) { throw "Local feed destination appeared during publication: $destination" }
        Move-Item -LiteralPath (Join-Path $stagePackages $name) -Destination $destination
        $movedFiles.Add($destination)
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $toolsVersionRoot) -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $nativeVersionRoot) -Force | Out-Null
    if ((Test-Path -LiteralPath $toolsVersionRoot) -or (Test-Path -LiteralPath $nativeVersionRoot)) {
        throw 'A versioned local SDK directory appeared during publication.'
    }
    Move-Item -LiteralPath $stageTools -Destination $toolsVersionRoot
    $movedDirectories.Add($toolsVersionRoot)
    Move-Item -LiteralPath $stageNative -Destination $nativeVersionRoot
    $movedDirectories.Add($nativeVersionRoot)
    if (Test-Path -LiteralPath $acceptedManifestPath) { throw "Accepted manifest appeared during publication: $acceptedManifestPath" }
    Move-Item -LiteralPath (Join-Path $stageRoot 'manifest.json') -Destination $acceptedManifestPath
    $movedFiles.Add($acceptedManifestPath)
} catch {
    foreach ($path in $movedFiles) {
        $full = [System.IO.Path]::GetFullPath($path)
        $isPackageFile = $full.StartsWith([System.IO.Path]::GetFullPath($packagesRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
        $isAcceptedManifest = $full -ceq [System.IO.Path]::GetFullPath($acceptedManifestPath)
        if (-not ($isPackageFile -or $isAcceptedManifest)) { throw "Refusing rollback outside expected local publication files: $full" }
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($path in $movedDirectories) {
        $full = [System.IO.Path]::GetFullPath($path)
        $allowedTargets = @([System.IO.Path]::GetFullPath($toolsVersionRoot), [System.IO.Path]::GetFullPath($nativeVersionRoot))
        if ($full -notin $allowedTargets -or
            -not $full.StartsWith([System.IO.Path]::GetFullPath($sdkRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing rollback outside expected local publication directories: $full"
        }
        if (Test-Path -LiteralPath $full -PathType Container) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
    throw
}

Write-Output "Published accepted local SDK version $version to $sdkRoot"
Write-Output "NuGet source: $packagesRoot"
Write-Output "Accepted manifest: $acceptedManifestPath"
