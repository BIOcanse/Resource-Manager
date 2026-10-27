[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$Profile = 'default',
    [string]$GeneratorPath,
    [string[]]$BuildProperties = @()
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
$queue = 'D:\Code\CommonAssets\Tools\JobQueue\JobQueue.ps1'
if (-not $GeneratorPath) {
    $artifacts = Join-Path $output 'build'
    & $queue -Action Run -Label 'desktop freedom-point generator build' -FilePath dotnet -WorkingDirectory $repository -ArgumentList @(@(
        'build', (Join-Path $repository 'src/Development/DesktopBuild/ResourceManager.DesktopBuild.csproj'),
        '-c', 'Release', '--disable-build-servers', '-maxcpucount:1', '-p:BuildInParallel=false',
        "-p:ArtifactsPath=$artifacts", "-p:RestorePackagesPath=$WorkRoot/nuget-packages"
    ) + $BuildProperties)
    if ($LASTEXITCODE -ne 0) { throw "Desktop generator build failed: $LASTEXITCODE" }
    $GeneratorPath = Join-Path $artifacts 'bin/ResourceManager.DesktopBuild/release/ResourceManager.DesktopBuild.dll'
}
& dotnet @([IO.Path]::GetFullPath($GeneratorPath), $repository, $output, $Profile)
if ($LASTEXITCODE -ne 0) { throw "Desktop projection failed: $LASTEXITCODE" }
