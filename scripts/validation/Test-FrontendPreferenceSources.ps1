[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$LedgerWorkRoot = $WorkRoot,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$GeneratorPath,
    [string[]]$BuildProperties = @()
)
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$memoryLimit = Join-Path $repository 'scripts/dev/Invoke-WithMemoryLimit.ps1'
# Generation verifies the registered running workspace before writing anything.
& (Join-Path $repository 'scripts/build/Generate-FrontendPreferences.ps1') -WorkRoot $WorkRoot -GeneratorPath $GeneratorPath `
    -BuildProperties ($BuildProperties + "-p:ResourceManagerLedgerWorkRoot=$LedgerWorkRoot")
if (-not $GeneratorPath) {
    $GeneratorPath = Join-Path $WorkRoot 'frontend-points/build/bin/ResourceManager.FrontendPreferencesBuild/release/ResourceManager.FrontendPreferencesBuild.dll'
}
$output = Join-Path $WorkRoot 'frontend-preference-check'
$artifacts = Join-Path $output 'artifacts'
[IO.Directory]::CreateDirectory($output) | Out-Null
$properties = @($BuildProperties) + @("-p:ArtifactsPath=$artifacts", '-p:Configuration=Release',
    '-p:BuildInParallel=false', '-p:UseSharedCompilation=false',
    "-p:ResourceManagerLedgerWorkRoot=$LedgerWorkRoot", "-p:ResourceManagerFrontendGeneratorPath=$GeneratorPath",
    "-p:CustomAfterMicrosoftCommonTargets=$repository/scripts/validation/FreedomPointCompilerCapture.targets")
# Rebuild emits real Regex/WinForms generator implementations for the closed SDK compiler host.
& $memoryLimit -FilePath dotnet -ArgumentList (@('build',
    (Join-Path $repository 'src/UI/Core/ResourceManager.NativeUi.csproj'), '-t:Rebuild',
    '--disable-build-servers', '-maxcpucount:1') + $properties)
if ($LASTEXITCODE -ne 0) { throw "Native compiler-context build failed: $LASTEXITCODE" }
& (Join-Path $PSScriptRoot 'Get-FreedomPointCompilerContext.ps1') -RepositoryRoot $repository -ArtifactsPath $artifacts `
    -OutputPath (Join-Path $output 'native-context.json') -BuildProperties ($properties + '-p:BuildProjectReferences=false') `
    -ProjectRelativePaths @('src/UI/Core/ResourceManager.NativeUi.csproj')
Push-Location (Join-Path $repository 'src/UI/Frontend')
try {
    & $memoryLimit -FilePath node -ArgumentList @('--experimental-transform-types', '--experimental-strip-types', 'scripts/capturePreferenceCompilerContext.ts',
        (Join-Path $output 'typescript-context.json'), (Join-Path $output 'frontend.tsconfig.json'))
    if ($LASTEXITCODE -ne 0) { throw "TypeScript compiler-context capture failed: $LASTEXITCODE" }
} finally { Pop-Location }
& $memoryLimit -FilePath dotnet -ArgumentList @($GeneratorPath, $repository, $output,
    (Join-Path $output 'native-context.json'), (Join-Path $output 'typescript-context.json'))
if ($LASTEXITCODE -ne 0) { throw "Frontend SDK source check failed: $LASTEXITCODE" }
