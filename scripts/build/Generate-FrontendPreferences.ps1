[CmdletBinding()]
param(
    [string]$WorkRoot = $env:RM_LEDGER_WORK_ROOT,
    [string]$GeneratorPath = $env:RM_FRONTEND_GENERATOR,
    [string[]]$BuildProperties = @()
)
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $WorkRoot -or -not [IO.Path]::IsPathFullyQualified($WorkRoot)) {
    throw 'Set RM_LEDGER_WORK_ROOT (or -WorkRoot) to a running registered build workspace.'
}
$cursor = [IO.Path]::GetFullPath($WorkRoot)
$registered = $false
while ($cursor) {
    $marker = Join-Path $cursor '.generated-workspace.json'
    if (Test-Path -LiteralPath $marker -PathType Leaf) {
        $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($state.state -cne 'running' -or [IO.Path]::GetFullPath($state.workspacePath) -ine $cursor) {
            throw "Frontend generation requires a running registered owner: $cursor"
        }
        $registered = $true
        break
    }
    $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\','/'))
}
if (-not $registered) { throw 'Register the build workspace with Manage-GeneratedWorkspace.ps1 New first.' }
$output = Join-Path $WorkRoot 'frontend-points'
[IO.Directory]::CreateDirectory($output) | Out-Null
$memoryLimit = Join-Path $repository 'scripts/dev/Invoke-WithMemoryLimit.ps1'
if (-not $GeneratorPath) {
    $artifacts = Join-Path $output 'build'
    # An ordinary product build, including the App's native toolchain; only the self-written generator below
    # runs under the diagnostic memory ceiling.
    & dotnet @(@('build',
        (Join-Path $repository 'src/Development/FrontendPreferencesBuild/ResourceManager.FrontendPreferencesBuild.csproj'),
        '-c','Release','--disable-build-servers','-maxcpucount:1','-p:BuildInParallel=false',
        '-p:SkipClientAppBuild=true',"-p:ResourceManagerLedgerWorkRoot=$WorkRoot", "-p:ArtifactsPath=$artifacts") + $BuildProperties)
    if ($LASTEXITCODE -ne 0) { throw "Frontend generator build failed: $LASTEXITCODE" }
    $GeneratorPath = Join-Path $artifacts 'bin/ResourceManager.FrontendPreferencesBuild/release/ResourceManager.FrontendPreferencesBuild.dll'
}
& $memoryLimit -FilePath dotnet -ArgumentList @([IO.Path]::GetFullPath($GeneratorPath), $repository, $output)
if ($LASTEXITCODE -ne 0) { throw "Frontend projection failed: $LASTEXITCODE" }
