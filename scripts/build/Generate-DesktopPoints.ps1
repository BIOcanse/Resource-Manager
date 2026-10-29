[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$Profile = 'default',
    [string]$GeneratorPath,
    [string[]]$BuildProperties = @(),
    # Outside a JobQueue job the generator build is queued; defaults to <ToolsRoot>\JobQueue\JobQueue.ps1 (scripts/dev/MachineRoots.psm1).
    [string]$Queue
)
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not [IO.Path]::IsPathFullyQualified($WorkRoot)) { throw 'WorkRoot must be an absolute running registered workspace.' }
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$cursor = $WorkRoot
$registered = $false
while ($cursor) {
    $marker = Join-Path $cursor '.generated-workspace.json'
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($state.state -cne 'running' -or [IO.Path]::GetFullPath($state.workspacePath) -ine $cursor) {
            throw "Desktop generation requires a running registered owner: $cursor"
        }
        $registered = $true
        break
    }
    $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\','/'))
}
if (-not $registered) { throw 'Register WorkRoot through Manage-GeneratedWorkspace.ps1 New first.' }
$output = Join-Path $WorkRoot 'desktop-points'
if (-not $GeneratorPath) {
    $artifacts = Join-Path $output 'build'
    $build = @(
        'build', (Join-Path $repository 'src/Development/DesktopBuild/ResourceManager.DesktopBuild.csproj'),
        '-c', 'Release', '--disable-build-servers', '-maxcpucount:1', '-p:BuildInParallel=false',
        "-p:ArtifactsPath=$artifacts", "-p:RestorePackagesPath=$WorkRoot/nuget-packages"
    ) + $BuildProperties
    # Inside a JobQueue job the build is already serialized, and queueing again would wait for this job to end.
    if ($env:JOBQUEUE_JOB_ID) { & dotnet @build }
    else {
        if (-not $Queue) {
            Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
            $Queue = Join-Path (Get-MachineRoot -Name ToolsRoot) 'JobQueue\JobQueue.ps1'
        }
        & $Queue -Action Run -Label 'desktop freedom-point generator build' -FilePath dotnet -WorkingDirectory $repository -ArgumentList $build
    }
    if ($LASTEXITCODE -ne 0) { throw "Desktop generator build failed: $LASTEXITCODE" }
    $GeneratorPath = Join-Path $artifacts 'bin/ResourceManager.DesktopBuild/release/ResourceManager.DesktopBuild.dll'
}
& dotnet @([IO.Path]::GetFullPath($GeneratorPath), $repository, $output, $Profile)
if ($LASTEXITCODE -ne 0) { throw "Desktop projection failed: $LASTEXITCODE" }
