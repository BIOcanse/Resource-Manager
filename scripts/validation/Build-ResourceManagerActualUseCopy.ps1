[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RegisteredWorkspaceRoot,
    [string]$BuildDirectoryName = 'actual-use-build',
    [string[]]$AdditionalBuildProperties = @(),
    # NuGet package folder the offline restore reads; the default is NuGet's per-user folder.
    [string]$PackageRoot = (Join-Path $env:USERPROFILE '.nuget\packages')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$owner = [IO.Path]::GetFullPath($RegisteredWorkspaceRoot)
if (-not $owner.StartsWith('F:\', [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.File]::Exists((Join-Path $owner '.generated-workspace.json'))) { throw 'An existing registered task root on F: is required.' }
if ($BuildDirectoryName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}$') { throw 'BuildDirectoryName must be one child name.' }
$build = Join-Path $owner $BuildDirectoryName
if (Test-Path -LiteralPath $build) { throw 'Use a fresh build directory for a new accepted build.' }
# The supplied running parent owns every build/run below it. Do not create a
# second lifecycle record or private retention policy for a nested batch.
$registration = Get-Content -LiteralPath (Join-Path $owner '.generated-workspace.json') -Raw | ConvertFrom-Json
if ($registration.state -ne 'running') { throw 'The supplied task must still be running.' }
[void][IO.Directory]::CreateDirectory($build)
$artifacts = Join-Path $build 'artifacts'
$env:RestorePackagesPath = $PackageRoot
$env:RestoreIgnoreFailedSources = 'true'
$env:NuGetAudit = 'false'
$properties = @(
    '-p:ResourceManagerStateNames=true', '-p:BuildInParallel=false', '-maxcpucount:1', '-nodeReuse:false',
    '-p:ResourceManagerZigJobs=1',
    ('-p:ArtifactsPath=' + $artifacts),
    ('-p:ResourceManagerLedgerWorkRoot=' + (Join-Path $build 'ledger')),
    ('-p:RestorePackagesPath=' + $PackageRoot), '-p:RestoreIgnoreFailedSources=true', '-p:NuGetAudit=false',
    '-p:SkipAdapterNativeBuild=true', '-p:AdapterNativeOptimize=ReleaseFast',
    '-p:SkipPerformanceOverlayNativeBuild=true', '-p:SkipGpuPlacementNativeBuild=true',
    '-p:SkipGpuPlacementPreparationNativeBuild=true', '-p:SkipGpuLaunchBrokerNativeBuild=true',
    '-p:SkipAdlxBridgeBuild=true', '-p:SkipGpuWindowActionNativeBuild=true', '-p:SkipGpuPlacementExternalNativeBuild=true',
    '-p:SkipClientAppBuild=true', '-p:UseSharedCompilation=false',
    ('-p:CustomAfterMicrosoftCommonTargets=' + (Join-Path $repo 'scripts/validation/FreedomPointCompilerCapture.targets'))
)
$properties += $AdditionalBuildProperties
# Bind first-party source bytes across the actual build; generated compiler inputs are captured afterwards.
$sourceRoots = @('src/Core','src/ReadWriteCore','src/SubscriptionCore','src/FreedomPointContracts','src/Shared','src/Development/ControlDecisionAcceptance','sdk/csharp/ResourceManager.Adapter.Abstractions','sdk/csharp/ResourceManager.Adapter.SharedMemory')
$sourceRows = [Collections.Generic.List[object]]::new()
$queue = [Collections.Generic.Queue[object]]::new()
foreach ($relative in $sourceRoots) { $queue.Enqueue([pscustomobject]@{ Path = (Join-Path $repo $relative); Depth = 0 }) }
$nodeCount = 0
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    if (++$nodeCount -gt 24000 -or $directory.Depth -gt 24) { throw 'Source traversal exceeded its bounds.' }
    if (([IO.File]::GetAttributes($directory.Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source snapshot encountered a link.' }
    foreach ($file in [IO.Directory]::EnumerateFiles($directory.Path)) {
        if ([IO.Path]::GetExtension($file) -notin @('.cs','.csproj','.json','.props','.targets','.zig','.c','.cpp','.h')) { continue }
        if (++$nodeCount -gt 24000) { throw 'Source input bound exceeded.' }
        $sourceRows.Add([pscustomobject]@{ path = $file; length = ([IO.FileInfo]::new($file)).Length; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash })
    }
    foreach ($child in [IO.Directory]::EnumerateDirectories($directory.Path)) {
        if ([IO.Path]::GetFileName($child) -in @('bin','obj','zig-out','.zig-cache','zig-cache','node_modules','.git')) { continue }
        $queue.Enqueue([pscustomobject]@{ Path = $child; Depth = $directory.Depth + 1 })
    }
}
foreach ($file in @('Directory.Build.props','Directory.Build.targets','scripts/validation/Build-ResourceManagerActualUseCopy.ps1','scripts/validation/Invoke-ResourceManagerActualUseCopy.ps1','src/Development/ControlDecisionAcceptance/Prepare-ArtifactCopy.ps1')) {
    $full = Join-Path $repo $file
    $sourceRows.Add([pscustomobject]@{ path = $full; length = ([IO.FileInfo]::new($full)).Length; sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash })
}
$project = Join-Path $repo 'src/Development/ControlDecisionAcceptance/ResourceManager.ControlDecisionAcceptance.csproj'
& dotnet build $project -c Release @properties --disable-build-servers --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$projectPaths = @(
    'src/Development/ControlDecisionAcceptance/ResourceManager.ControlDecisionAcceptance.csproj',
    'src/Core/ResourceManager.App.csproj', 'src/ReadWriteCore/ResourceManager.ReadWriteCore.csproj',
    'src/SubscriptionCore/ResourceManager.SubscriptionCore.csproj', 'src/FreedomPointContracts/ResourceManager.FreedomPointContracts.csproj',
    'src/Shared/ResourceManager.Shared.csproj',
    'sdk/csharp/ResourceManager.Adapter.Abstractions/ResourceManager.Adapter.Abstractions.csproj',
    'sdk/csharp/ResourceManager.Adapter.SharedMemory/ResourceManager.Adapter.SharedMemory.csproj'
)
& (Join-Path $repo 'scripts/validation/Get-FreedomPointCompilerContext.ps1') -RepositoryRoot $repo -ArtifactsPath $artifacts -OutputPath (Join-Path $build 'compiler-context.json') -BuildProperties (@('-p:Configuration=Release') + $properties) -ProjectRelativePaths $projectPaths
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($row in $sourceRows) {
    if (([IO.FileInfo]::new($row.path)).Length -ne $row.length -or (Get-FileHash -LiteralPath $row.path -Algorithm SHA256).Hash -ne $row.sha256) { throw "Source changed during build: $($row.path)" }
}
$contextPath = Join-Path $build 'compiler-context.json'
$context = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json
$actualPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $context.projects) {
    foreach ($item in @($entry.Items.Compile) + @($entry.Items.ReferencePath)) {
        $path = if ($item.PSObject.Properties.Name -contains 'FullPath') { [string]$item.FullPath } else { [string]$item.Identity }
        if (-not [IO.Path]::IsPathFullyQualified($path)) { $path = [IO.Path]::GetFullPath($path, (Split-Path (Join-Path $repo $entry.projectRelativePath))) }
        if ([IO.File]::Exists($path)) { [void]$actualPaths.Add($path) }
    }
}
$queue.Enqueue([pscustomobject]@{ Path = (Join-Path $artifacts 'freedom-point-generated'); Depth = 0 })
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    if (++$nodeCount -gt 24000 -or $directory.Depth -gt 24) { throw 'Generated source traversal exceeded its bounds.' }
    foreach ($file in [IO.Directory]::EnumerateFiles($directory.Path, '*.cs')) { [void]$actualPaths.Add($file) }
    foreach ($child in [IO.Directory]::EnumerateDirectories($directory.Path)) { $queue.Enqueue([pscustomobject]@{ Path = $child; Depth = $directory.Depth + 1 }) }
}
$actualRows = foreach ($path in $actualPaths) { [pscustomobject]@{ path = $path; length = ([IO.FileInfo]::new($path)).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } }
[ordered]@{ contract = 'resource-manager-control-decision-source-inputs-v1'; sourceRoot = $repo; buildProperties = $properties; firstPartySources = $sourceRows.ToArray(); evaluatedCompileAndReferences = @($actualRows); compilerContext = @{ path = $contextPath; length = ([IO.FileInfo]::new($contextPath)).Length; sha256 = (Get-FileHash -LiteralPath $contextPath -Algorithm SHA256).Hash } } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $build 'source-input-manifest.json') -Encoding utf8
& (Join-Path $repo 'src/Development/ControlDecisionAcceptance/Prepare-ArtifactCopy.ps1') -BuiltRoot (Join-Path $artifacts 'bin/ResourceManager.ControlDecisionAcceptance/release') -AcceptedSourceManifest (Join-Path $build 'source-input-manifest.json') -CopyRoot (Join-Path $build 'image') -RegisteredWorkspaceRoot $owner
exit 0
