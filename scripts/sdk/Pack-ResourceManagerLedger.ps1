<#
.SYNOPSIS
Creates a local candidate drop for the independent Resource Manager Ledger SDK.

.DESCRIPTION
Package, build, restore, and staging outputs stay below an existing registered
running WorkRoot. This command never writes to artifacts/sdk and never publishes
packages.

.PARAMETER Version
Explicit SemVer version used for all three ledger packages.

.PARAMETER FreedomPointSdkVersion
Exact official FreedomPoint.Core and FreedomPoint.Generators package version.

.PARAMETER FreedomPointFeed
Directory containing the exact official FreedomPoint NuGet dependency packages.

.PARAMETER NativeHeaderPath
Path to the paired ledger_native.h file.

.PARAMETER WorkRoot
Existing registered workspace root whose lifecycle state is running.

.EXAMPLE
./scripts/sdk/Pack-ResourceManagerLedger.ps1 -Version 0.1.0-dev.1 `
  -FreedomPointSdkVersion 0.1.0-dev.39 `
  -FreedomPointFeed <FreedomPoint checkout>/artifacts/sdk `
  -NativeHeaderPath .local/ledger-owner-implementation/foundation/sdk-native/ledger_native.h `
  -WorkRoot .local/ledger-owner-implementation
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $FreedomPointSdkVersion,

    [Parameter(Mandatory = $true)]
    [string] $FreedomPointFeed,

    [Parameter(Mandatory = $true)]
    [string] $NativeHeaderPath,

    [Parameter(Mandatory = $true)]
    [string] $WorkRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $json = ConvertTo-Json -InputObject $Value -Depth 32
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Assert-RelativeManifestPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [System.IO.Path]::IsPathRooted($Path) -or
        $Path.Contains(':') -or
        $Path -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "The Native source allowlist contains an unsafe relative path: $Path"
    }
}

function Get-ProjectInputRows {
    param([Parameter(Mandatory = $true)][string] $ProjectPath)

    $projectRoot = Split-Path -Parent $ProjectPath
    $rows = [System.Collections.Generic.List[object]]::new()
    $rg = Get-Command rg.exe -ErrorAction Stop
    $files = @(& $rg.Source --files --hidden -uu --glob '!bin/**' --glob '!obj/**' --glob '!**/bin/**' --glob '!**/obj/**' $projectRoot)
    $rgExitCode = $LASTEXITCODE
    if ($rgExitCode -ne 0) { throw "Could not enumerate package project inputs with rg.exe: $ProjectPath (exit $rgExitCode)" }
    if ($files.Count -gt 4096) { throw "Package project file count exceeded the 4096 input bound: $ProjectPath" }
    foreach ($listedPath in $files | Sort-Object) {
        $fullPath = [System.IO.Path]::GetFullPath([string]$listedPath)
        $relativePath = [System.IO.Path]::GetRelativePath($projectRoot, $fullPath)
        if ($relativePath -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
        if (-not $fullPath.StartsWith([System.IO.Path]::GetFullPath($projectRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "rg.exe returned a package input outside its project: $fullPath"
        }
        $file = Get-Item -LiteralPath $fullPath
        $rows.Add([pscustomobject]@{
            path = $relativePath.Replace('\', '/')
            length = $file.Length
            sha256 = Get-Sha256 -Path $fullPath
        })
    }
    return @($rows.ToArray())
}

function Test-ProjectInputsStable {
    param(
        [Parameter(Mandatory = $true)][string] $ProjectPath,
        [Parameter(Mandatory = $true)][object[]] $BeforeRows
    )

    $afterRows = @(Get-ProjectInputRows -ProjectPath $ProjectPath)
    if ((ConvertTo-Json -InputObject $afterRows -Depth 8 -Compress) -cne
        (ConvertTo-Json -InputObject $BeforeRows -Depth 8 -Compress)) {
        throw "Package project inputs changed while packing: $ProjectPath"
    }
}

function Invoke-DotNetPack {
    param(
        [Parameter(Mandatory = $true)][string] $ProjectPath,
        [Parameter(Mandatory = $true)][string[]] $Properties,
        [Parameter(Mandatory = $true)][string] $ProjectName,
        [Parameter(Mandatory = $true)][string] $ArtifactsDirectory,
        [Parameter(Mandatory = $true)][string] $RestorePackagesDirectory
    )

    $arguments = @(
        'pack', $ProjectPath,
        '--configuration', 'Release',
        '--artifacts-path', $ArtifactsDirectory,
        '--disable-build-servers',
        '-m:1',
        '-nodeReuse:false',
        '-p:UseSharedCompilation=false',
        "-p:RestorePackagesPath=$RestorePackagesDirectory"
    )
    foreach ($property in $Properties) { $arguments += "-p:$property" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for $ProjectName with exit code $LASTEXITCODE. Candidate staging was retained for inspection."
    }
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

$versionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Version -notmatch $versionPattern) {
    throw 'Version must be an explicit SemVer version without build metadata (for example 0.1.0-dev.1).'
}
if ($FreedomPointSdkVersion -notmatch $versionPattern) {
    throw 'FreedomPointSdkVersion must be an explicit SemVer package version.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$registeredRoot = Find-RegisteredWorkRoot -Path $WorkRoot
$workRootFull = [System.IO.Path]::GetFullPath($WorkRoot)
if ($registeredRoot -cne $workRootFull) {
    throw "Pass the registered workspace root itself as WorkRoot: $registeredRoot"
}

$runtimeProject = Join-Path $repositoryRoot 'sdk\csharp\ResourceManager.Ledger\ResourceManager.Ledger.csproj'
$toolProject = Join-Path $repositoryRoot 'sdk\csharp\ResourceManager.Ledger.Build\ResourceManager.Ledger.Build.csproj'
$configurationProject = Join-Path $repositoryRoot 'sdk\csharp\ResourceManager.Ledger.Configuration\ResourceManager.Ledger.Configuration.csproj'
$sourceManifestPath = Join-Path $PSScriptRoot 'ledger-native-source-files.json'
$nativeBuilderPath = Join-Path $PSScriptRoot 'Build-LedgerNative.ps1'
$zigRootFull = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'sdk\zig'))
$publicRootPath = Join-Path $zigRootFull 'src\ledger.zig'
$headerPath = [System.IO.Path]::GetFullPath($NativeHeaderPath)
$freedomPointFeedFull = [System.IO.Path]::GetFullPath($FreedomPointFeed)

foreach ($requiredFile in @($runtimeProject, $toolProject, $configurationProject, $sourceManifestPath, $nativeBuilderPath, $publicRootPath, $headerPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required package input is missing: $requiredFile"
    }
}
if (-not (Test-Path -LiteralPath $freedomPointFeedFull -PathType Container)) {
    throw "FreedomPoint package feed is missing: $freedomPointFeedFull"
}
if ([System.IO.Path]::GetFileName($headerPath) -cne 'ledger_native.h') {
    throw 'NativeHeaderPath must name the paired ledger_native.h file.'
}

$headerText = [System.IO.File]::ReadAllText($headerPath)
if ($headerText -notmatch '(?m)^#define\s+RM_LEDGER_ABI_VERSION\s+1u\s*$' -or
    $headerText -notmatch '(?m)^#define\s+RM_LEDGER_CANDIDATE_C2_CHAIN\s+0u\s*$') {
    throw 'The paired Native header does not declare the expected ABI v1 and C2 candidate identifiers.'
}

$fpDependencyRows = [System.Collections.Generic.List[object]]::new()
foreach ($id in @('FreedomPoint.Core', 'FreedomPoint.Generators')) {
    $dependencyPath = Join-Path $freedomPointFeedFull "$id.$FreedomPointSdkVersion.nupkg"
    if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
        throw "The selected official FreedomPoint package is absent from the formal feed: $dependencyPath"
    }
    $fpDependencyRows.Add([pscustomobject]@{
        id = $id
        version = $FreedomPointSdkVersion
        feedPath = $dependencyPath
        length = (Get-Item -LiteralPath $dependencyPath).Length
        sha256 = Get-Sha256 -Path $dependencyPath
    })
}

$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json -Depth 8
if ($sourceManifest.schemaVersion -ne 1 -or $sourceManifest.sourceRoot -cne 'sdk/zig' -or
    $sourceManifest.archiveRoot -cne '.' -or @($sourceManifest.files).Count -eq 0) {
    throw 'Unsupported or incomplete Native source allowlist.'
}

$sourceRows = [System.Collections.Generic.List[object]]::new()
foreach ($relativePath in @($sourceManifest.files)) {
    Assert-RelativeManifestPath -Path ([string]$relativePath)
    $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $zigRootFull ([string]$relativePath)))
    if (-not $sourcePath.StartsWith($zigRootFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Native source path escapes the declared source root: $relativePath"
    }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Allowlisted Native source file is missing: $sourcePath"
    }
    $sourceRows.Add([pscustomobject]@{
        relativePath = [string]$relativePath
        sourcePath = $sourcePath
        length = (Get-Item -LiteralPath $sourcePath).Length
        sha256 = Get-Sha256 -Path $sourcePath
    })
}

$projectInputRows = @{
    runtime = @(Get-ProjectInputRows -ProjectPath $runtimeProject)
    buildTool = @(Get-ProjectInputRows -ProjectPath $toolProject)
    configuration = @(Get-ProjectInputRows -ProjectPath $configurationProject)
}
$nativeHeaderHash = Get-Sha256 -Path $headerPath
$sourceAllowlistHash = Get-Sha256 -Path $sourceManifestPath
$builderHash = Get-Sha256 -Path $nativeBuilderPath
$publicRootHash = Get-Sha256 -Path $publicRootPath
$packScriptHash = Get-Sha256 -Path $PSCommandPath

$candidateId = [guid]::NewGuid().ToString('N')
$candidatesRoot = Join-Path $workRootFull 'ledger-sdk-candidates'
$stageRoot = Join-Path $workRootFull ('.ledger-sdk-stage-' + $Version + '-' + $candidateId)
$candidateRoot = Join-Path $candidatesRoot ($Version + '-' + $candidateId)
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
$packageDirectory = Join-Path $stageRoot 'packages'
$toolsDirectory = Join-Path $stageRoot 'tools'
$nativeDirectory = Join-Path $stageRoot 'native-source'
$nativeContentRoot = Join-Path $stageRoot 'native-source-expanded'
$buildArtifactsDirectory = Join-Path $stageRoot 'build-artifacts'
$restorePackagesDirectory = Join-Path $stageRoot 'nuget-packages'
$dotnetCliHome = Join-Path $stageRoot 'dotnet-cli-home'
New-Item -ItemType Directory -Path $packageDirectory, $toolsDirectory, $nativeDirectory, $nativeContentRoot, $buildArtifactsDirectory, $restorePackagesDirectory, $dotnetCliHome -Force | Out-Null

$archiveName = "ResourceManager.Ledger.NativeSources.$Version.zip"
$archivePath = Join-Path $nativeDirectory $archiveName
$nativeArchiveRoot = $nativeContentRoot
$publicSourceRoot = Join-Path $nativeContentRoot 'src'
$includeDirectory = Join-Path $nativeContentRoot 'include'
$nativeToolsDirectory = Join-Path $nativeContentRoot 'tools'
New-Item -ItemType Directory -Path $nativeArchiveRoot, $publicSourceRoot, $includeDirectory, $nativeToolsDirectory -Force | Out-Null
foreach ($row in $sourceRows) {
    $destination = Join-Path $nativeArchiveRoot $row.relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $row.sourcePath -Destination $destination
}
Copy-Item -LiteralPath $headerPath -Destination (Join-Path $includeDirectory 'ledger_native.h')
Copy-Item -LiteralPath $nativeBuilderPath -Destination (Join-Path $nativeToolsDirectory 'Build-LedgerNative.ps1')
Copy-Item -LiteralPath $publicRootPath -Destination (Join-Path $publicSourceRoot 'ledger.zig')

$nativeReadme = @'
# ResourceManager.Ledger Native source candidate

This archive contains the production C2 ledger Native source closure under `src/local_resource_manager`, the public `src/ledger.zig` module root, the paired C ABI header, and the public `tools/Build-LedgerNative.ps1` build entry. The package is a candidate until an installed-package consumer run succeeds.

Use the packaged `ledger-configuration` tool to generate the selected FreedomPoint structure and values. Use the packaged `ledger-build native` command with the owner's canonical JSON, exact `ledger_values.zig`, and UID width to generate `LedgerOwnerShape.zig`, `LedgerNativeEntry.zig`, `LedgerOwnerBuild.g.cs`, and `ledger-native-build.json`. The Native build script verifies that manifest, then builds the DLL from this source root using Zig 0.16 and records input/output fingerprints.

Generated owner/value/structure files remain consumer build inputs. They are not copied into this archive. From the extracted archive root, invoke the public builder with the installed build tool, generated files, and an existing output workspace:

```powershell
pwsh ./tools/Build-LedgerNative.ps1 `
  -LedgerBuildToolPath '<installed-ledger-build-command>' `
  -NativeBuildManifestPath '<generated-directory>/ledger-native-build.json' `
  -GeneratedSourceRoot '<generated-directory>' `
  -NativeSourceRoot '<extracted-archive-root>' `
  -ZigPath '<zig-0.16.0>/zig.exe' `
  -StructurePath '<structure-directory>/ledger_structure.zig' `
  -ValuesPath '<values-directory>/ledger_values.zig' `
  -UidWidthBits 128 `
  -WorkRoot '<existing-output-workspace>' `
  -OutputDirectory '<existing-output-workspace>/native-build-128' `
  -GlobalCacheDirectory '<existing-output-workspace>/zig-global-cache'
```

The command is repeated with `-UidWidthBits 256` and distinct output/cache directories for a 256-bit consumer. Output and cache must be separate from each other and from the generated inputs. See `manifest.json` for source hashes.
'@
[System.IO.File]::WriteAllText((Join-Path $nativeContentRoot 'README.md'), $nativeReadme + "`n", [System.Text.UTF8Encoding]::new($false))

$archiveFileRows = [System.Collections.Generic.List[object]]::new()
foreach ($row in $sourceRows) {
    $archiveFileRows.Add([pscustomobject]@{
        path = $row.relativePath.Replace('\', '/')
        length = $row.length
        sha256 = $row.sha256
    })
}
$archiveFileRows.Add([pscustomobject]@{
    path = 'include/ledger_native.h'
    length = (Get-Item -LiteralPath $headerPath).Length
    sha256 = $nativeHeaderHash
})
$archiveFileRows.Add([pscustomobject]@{
    path = 'tools/Build-LedgerNative.ps1'
    length = (Get-Item -LiteralPath $nativeBuilderPath).Length
    sha256 = $builderHash
})
$archiveFileRows.Add([pscustomobject]@{
    path = 'src/ledger.zig'
    length = (Get-Item -LiteralPath $publicRootPath).Length
    sha256 = $publicRootHash
})
$readmePath = Join-Path $nativeContentRoot 'README.md'
$archiveFileRows.Add([pscustomobject]@{
    path = 'README.md'
    length = (Get-Item -LiteralPath $readmePath).Length
    sha256 = Get-Sha256 -Path $readmePath
})
$nativeArchiveManifest = [pscustomobject]@{
    schema = 'resource-manager/ledger-native-source/v1'
    status = 'candidate-not-consumer-validated'
    candidate = 'c2_chain'
    abiVersion = 1
    version = $Version
    files = @($archiveFileRows)
}
Write-Utf8Json -Path (Join-Path $nativeContentRoot 'manifest.json') -Value $nativeArchiveManifest
Compress-Archive -Path (Join-Path $nativeContentRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

$toolManifest = [ordered]@{
    version = 1
    isRoot = $true
    tools = [ordered]@{
        'resourcemanager.ledger.build' = [ordered]@{ version = $Version; commands = @('ledger-build') }
        'resourcemanager.ledger.configuration' = [ordered]@{ version = $Version; commands = @('ledger-configuration') }
    }
}
Write-Utf8Json -Path (Join-Path $toolsDirectory 'dotnet-tools.json') -Value $toolManifest
$toolsReadme = @'
# Ledger SDK local tools

This tool manifest pins `ledger-build` and `ledger-configuration` to the candidate's package version. Configure NuGet with the ledger package directory and the official FreedomPoint feed, then run `dotnet tool restore` from this directory. For the local feed layout, the ledger source is `../../packages`; add the formal FreedomPoint feed for the `FreedomPoint.Core` and `FreedomPoint.Generators` dependencies.

`ledger-configuration` generates/checks the FreedomPoint-backed ledger structure and values. `ledger-build` generates and verifies the per-consumer owner and Native entry source. The candidate manifest records which official FreedomPoint package versions were used to build the configuration tool.
'@
[System.IO.File]::WriteAllText((Join-Path $toolsDirectory 'README.md'), $toolsReadme + "`n", [System.Text.UTF8Encoding]::new($false))

# Published packages carry no machine path: the compiler maps the stage (obj, generated sources) to /_build/ and the
# checkout to /_/, in the DLL's PDB path, the PDB documents and the SourceLink map. The stage is listed first because
# a WorkRoot may sit inside the checkout. MSBuild reads a comma in -p as a property separator, hence %2C.
$pathMap = (Join-Path $stageRoot '') + '=/_build/%2C' + (Join-Path $repositoryRoot '') + '=/_/'
$baseProperties = @(
    "PackageVersion=$Version",
    'IsPackable=true',
    ('PackageOutputPath=' + $packageDirectory),
    'Authors=BIOcanse',
    'ContinuousIntegrationBuild=true',
    'Deterministic=true',
    "PathMap=$pathMap"
)
$previousNugetPackages = $env:NUGET_PACKAGES
$previousDotnetCliHome = $env:DOTNET_CLI_HOME
$previousWorkloadNotify = $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE
try {
    $env:NUGET_PACKAGES = $restorePackagesDirectory
    $env:DOTNET_CLI_HOME = $dotnetCliHome
    # The staged CLI home is new each run; skip its workload advertising download.
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    Invoke-DotNetPack -ProjectPath $runtimeProject -Properties ($baseProperties + @(
        'PackageId=ResourceManager.Ledger',
        'Description=Independent Resource Manager ledger runtime library.'
    )) -ProjectName 'ResourceManager.Ledger' -ArtifactsDirectory $buildArtifactsDirectory -RestorePackagesDirectory $restorePackagesDirectory
    Invoke-DotNetPack -ProjectPath $toolProject -Properties ($baseProperties + @(
        'PackageId=ResourceManager.Ledger.Build',
        'PackAsTool=true',
        'ToolCommandName=ledger-build',
        'Description=Build-time shape and Native source generator for ResourceManager.Ledger.'
    )) -ProjectName 'ResourceManager.Ledger.Build' -ArtifactsDirectory $buildArtifactsDirectory -RestorePackagesDirectory $restorePackagesDirectory
    Invoke-DotNetPack -ProjectPath $configurationProject -Properties ($baseProperties + @(
        'PackageId=ResourceManager.Ledger.Configuration',
        'PackAsTool=true',
        'ToolCommandName=ledger-configuration',
        "FreedomPointSdkVersion=$FreedomPointSdkVersion",
        ('RestoreSources=' + $freedomPointFeedFull),
        'Description=FreedomPoint-backed configuration generator for ResourceManager.Ledger.'
    )) -ProjectName 'ResourceManager.Ledger.Configuration' -ArtifactsDirectory $buildArtifactsDirectory -RestorePackagesDirectory $restorePackagesDirectory
} finally {
    if ($null -eq $previousNugetPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_PACKAGES = $previousNugetPackages }
    if ($null -eq $previousDotnetCliHome) { Remove-Item Env:DOTNET_CLI_HOME -ErrorAction SilentlyContinue } else { $env:DOTNET_CLI_HOME = $previousDotnetCliHome }
    if ($null -eq $previousWorkloadNotify) { Remove-Item Env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE -ErrorAction SilentlyContinue } else { $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = $previousWorkloadNotify }
}

$expectedPackages = @(
    "ResourceManager.Ledger.$Version.nupkg",
    "ResourceManager.Ledger.Build.$Version.nupkg",
    "ResourceManager.Ledger.Configuration.$Version.nupkg"
)
$actualPackages = @(Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nupkg' | Select-Object -ExpandProperty Name | Sort-Object)
if (Compare-Object -ReferenceObject ($expectedPackages | Sort-Object) -DifferenceObject $actualPackages) {
    throw 'Packed NuGet package set does not match the three declared package IDs. Candidate staging was retained for inspection.'
}

foreach ($row in $sourceRows) {
    if ((Get-Sha256 -Path $row.sourcePath) -cne $row.sha256) {
        throw "Native source changed during packaging: $($row.sourcePath)"
    }
}
if ((Get-Sha256 -Path $headerPath) -cne $nativeHeaderHash -or
    (Get-Sha256 -Path $sourceManifestPath) -cne $sourceAllowlistHash -or
    (Get-Sha256 -Path $nativeBuilderPath) -cne $builderHash -or
    (Get-Sha256 -Path $publicRootPath) -cne $publicRootHash -or
    (Get-Sha256 -Path $PSCommandPath) -cne $packScriptHash) {
    throw 'A Native packaging input changed during pack; candidate staging was retained for inspection.'
}
Test-ProjectInputsStable -ProjectPath $runtimeProject -BeforeRows $projectInputRows.runtime
Test-ProjectInputsStable -ProjectPath $toolProject -BeforeRows $projectInputRows.buildTool
Test-ProjectInputsStable -ProjectPath $configurationProject -BeforeRows $projectInputRows.configuration
foreach ($dependency in $fpDependencyRows) {
    if ((Get-Sha256 -Path $dependency.feedPath) -cne $dependency.sha256) {
        throw "FreedomPoint package changed during pack: $($dependency.feedPath)"
    }
}

$packageRows = @(
    foreach ($packageId in @('ResourceManager.Ledger', 'ResourceManager.Ledger.Build', 'ResourceManager.Ledger.Configuration')) {
        $name = "$packageId.$Version.nupkg"
        $path = Join-Path $packageDirectory $name
        [pscustomobject]@{
            id = $packageId
            version = $Version
            file = "packages/$name"
            length = (Get-Item -LiteralPath $path).Length
            sha256 = Get-Sha256 -Path $path
        }
    }
)
$expandedSourceRows = [System.Collections.Generic.List[object]]::new()
foreach ($path in @(
    $archiveFileRows | ForEach-Object { Join-Path $nativeContentRoot ([string]$_.path) }
)) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $expandedSourceRows.Add([pscustomobject]@{
        path = [System.IO.Path]::GetRelativePath($nativeContentRoot, $fullPath).Replace('\', '/')
        length = (Get-Item -LiteralPath $fullPath).Length
        sha256 = Get-Sha256 -Path $fullPath
    })
}
$expandedManifestPath = Join-Path $nativeContentRoot 'manifest.json'
$expandedSourceRows.Add([pscustomobject]@{
    path = 'manifest.json'
    length = (Get-Item -LiteralPath $expandedManifestPath).Length
    sha256 = Get-Sha256 -Path $expandedManifestPath
})
$manifest = [pscustomobject]@{
    schema = 'resource-manager/ledger-sdk-candidate/v1'
    status = 'candidate-not-consumer-validated'
    candidate = 'c2_chain'
    abiVersion = 1
    version = $Version
    target = 'win-x64'
    capabilities = @('optional-development-allocation-statistics')
    workRoot = $workRootFull
    createdAtUtc = [DateTime]::UtcNow.ToString('O')
    packages = $packageRows
    tools = [pscustomobject]@{
        file = 'tools/dotnet-tools.json'
        sha256 = Get-Sha256 -Path (Join-Path $toolsDirectory 'dotnet-tools.json')
    }
    freedomPointDependencies = @($fpDependencyRows)
    nativeSource = [pscustomobject]@{
        file = "native-source/$archiveName"
        length = (Get-Item -LiteralPath $archivePath).Length
        sha256 = Get-Sha256 -Path $archivePath
        headerSha256 = $nativeHeaderHash
        sourceAllowlistSha256 = $sourceAllowlistHash
        publicBuilderSha256 = $builderHash
        files = @($archiveFileRows)
    }
    expandedNativeSource = [pscustomobject]@{
        root = 'native-source-expanded'
        manifestSha256 = Get-Sha256 -Path $expandedManifestPath
        files = @($expandedSourceRows)
    }
    isolatedBuild = [pscustomobject]@{
        artifactsPath = 'build-artifacts'
        restorePackagesPath = 'nuget-packages'
        dotnetCliHome = 'dotnet-cli-home'
    }
    projectSources = [pscustomobject]@{
        runtime = @($projectInputRows.runtime)
        buildTool = @($projectInputRows.buildTool)
        configuration = @($projectInputRows.configuration)
    }
    packagingScriptSha256 = $packScriptHash
}
Write-Utf8Json -Path (Join-Path $stageRoot 'manifest.json') -Value $manifest

$candidateParent = Split-Path -Parent $candidateRoot
New-Item -ItemType Directory -Path $candidateParent -Force | Out-Null
if (Test-Path -LiteralPath $candidateRoot) { throw "Candidate destination already exists: $candidateRoot" }
$resolvedStage = [System.IO.Path]::GetFullPath($stageRoot)
$resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRootFull).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedStage.StartsWith($resolvedWorkRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to finalize a candidate outside the registered WorkRoot.'
}
Move-Item -LiteralPath $resolvedStage -Destination $candidateRoot
Write-Output "Created local candidate package drop: $candidateRoot"
Write-Output 'No output was written under artifacts/sdk; no publication was attempted.'
