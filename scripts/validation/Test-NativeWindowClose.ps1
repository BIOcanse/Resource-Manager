[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BatchRoot,
    [string]$OfflinePackageCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$batch = [IO.Path]::GetFullPath($BatchRoot)
$marker = Get-Content -LiteralPath (Join-Path $batch '.generated-workspace.json') -Raw | ConvertFrom-Json
if ($marker.state -cne 'running' -or $marker.workspacePath -ine $batch) {
    throw 'Prepare a running validation batch with Manage-ValidationWorkset.ps1 first.'
}
if (-not $batch.StartsWith((Join-Path $repository '.local') + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This shell-only validation requires a batch inside this worktree .local.'
}
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$artifacts = Join-Path $batch 'build'
$env:RESOURCE_MANAGER_PACKAGE_ROOT = Join-Path $batch 'test-package'
$env:RM_LEDGER_WORK_ROOT = Join-Path $artifacts 'obj/ResourceManager.App/release/Ledger'
$env:DOTNET_CLI_HOME = Join-Path $batch 'dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if ($OfflinePackageCache) {
    if (-not (Test-Path -LiteralPath $OfflinePackageCache -PathType Container)) {
        throw 'The explicitly selected offline package cache does not exist.'
    }
    # MSBuild environment properties are inherited by the frontend generator's
    # nested build too. No remote feed or vulnerability audit is available offline.
    $env:RestorePackagesPath = [IO.Path]::GetFullPath($OfflinePackageCache)
    $env:RestoreSources = @('sdk/freedom-point/packages', 'artifacts/sdk/packages',
        'artifacts/fan-control-core/packages', 'artifacts/disk-sdk/packages') |
        ForEach-Object { Join-Path $repository $_ } | Join-String -Separator ';'
    $env:NuGetAudit = 'false'
}
$env:RESOURCE_MANAGER_NATIVE_UI_TEST_APPHOST = Join-Path $artifacts 'bin/ResourceManager.NativeUi/release/ResourceManager.NativeUi.exe'
$results = Join-Path $batch 'test-results'
$filter = 'FullyQualifiedName~NativeUi|FullyQualifiedName~MainForm|FullyQualifiedName~WindowPlacement|FullyQualifiedName~BrowserRuntimeStartup'
$properties = @("-p:ArtifactsPath=$artifacts", "-p:ResourceManagerLedgerWorkRoot=$env:RM_LEDGER_WORK_ROOT",
    '-maxcpucount:1', '-p:BuildInParallel=false', '-p:SkipClientAppBuild=true')
$result = [ordered]@{ generatorExitCode = $null; buildExitCode = $null; testExitCode = $null; filter = $filter }
Push-Location $repository
try {
    # Build once in this batch so NativeUI and App tests consume the same generator
    # and reuse the unchanged App outputs instead of nesting a fresh build twice.
    & dotnet build src/Development/FrontendPreferencesBuild/ResourceManager.FrontendPreferencesBuild.csproj `
        -c Release --disable-build-servers @properties
    $result.generatorExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) { throw "Frontend generator build failed: $LASTEXITCODE" }
    $generator = Join-Path $artifacts 'bin/ResourceManager.FrontendPreferencesBuild/release/ResourceManager.FrontendPreferencesBuild.dll'
    $properties += "-p:ResourceManagerFrontendGeneratorPath=$generator"
    & dotnet build src/UI/Core/ResourceManager.NativeUi.csproj -c Release --disable-build-servers @properties
    $result.buildExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) { throw "NativeUI build failed: $LASTEXITCODE" }
    & dotnet test 'tests/App/Resource Manager-APP.Tests.csproj' -c Release --disable-build-servers `
        --filter $filter --logger 'trx;LogFileName=native-shell.trx' --results-directory $results @properties
    $result.testExitCode = $LASTEXITCODE
    if ($LASTEXITCODE -ne 0) { throw "NativeUI tests failed: $LASTEXITCODE" }
}
finally {
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $batch 'validation-result.json') -Encoding utf8
    Pop-Location
}
