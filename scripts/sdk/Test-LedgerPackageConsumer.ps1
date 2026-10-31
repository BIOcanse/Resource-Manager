#Requires -Version 7
<#
.SYNOPSIS
Consumes a packed Ledger SDK candidate the way an application does and writes the package-consumer receipt.

.DESCRIPTION
Installs the candidate's ledger-configuration and ledger-build tools from its own package folder and the official
FreedomPoint feed, then, for each of the four UID128/256 x development-allocation-statistics 0/1 profiles: generates
structure and values, generates and verifies the owner identity, builds the Native library with the builder inside
the candidate's Native source archive, restores a probe application against the candidate ResourceManager.Ledger
package and runs its 15 cases against that library. Writes consumer-result.json and consumer-receipt.json (schema
resource-manager/ledger-package-consumer/v1), which Publish-ResourceManagerLedgerLocal.ps1 requires. The receipt
binds the candidate manifest and the UID128, statistics-off Native build.

OutputRoot must be a new directory inside a running registered WorkRoot. Zig is the explicit 0.17.0 compiler.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$FreedomPointFeed,
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$ZigPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32) + "`n", $utf8) }
$steps = [Collections.Generic.List[object]]::new()
function Invoke-Step([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory = $output) {
    $log = Join-Path $output ('steps/{0:000}-{1}.log' -f ($steps.Count + 1), $Name)
    Push-Location -LiteralPath $WorkingDirectory
    try { & $Executable @Arguments *> $log; $code = $LASTEXITCODE } finally { Pop-Location }
    $steps.Add([pscustomobject]@{ name = $Name; exitCode = $code; log = $log })
    if ($code -ne 0) { throw "$Name exited with code $code; see $log" }
}

$work = [IO.Path]::GetFullPath($WorkRoot)
$record = Get-Content -LiteralPath (Join-Path $work '.generated-workspace.json') -Raw | ConvertFrom-Json
if ([string]$record.state -cne 'running' -or [IO.Path]::GetFullPath([string]$record.workspacePath) -cne $work) {
    throw 'WorkRoot must be a running registered generated workspace.'
}
$output = [IO.Path]::GetFullPath($OutputRoot)
if (-not $output.StartsWith($work.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputRoot must stay inside WorkRoot.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh OutputRoot; prior evidence is immutable.' }
[void][IO.Directory]::CreateDirectory((Join-Path $output 'steps'))

$candidate = [IO.Path]::GetFullPath($CandidateRoot)
$candidateManifestPath = Join-Path $candidate 'manifest.json'
$candidateManifest = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json -Depth 32
if ($candidateManifest.schema -cne 'resource-manager/ledger-sdk-candidate/v1' -or $candidateManifest.status -cne 'candidate-not-consumer-validated') {
    throw 'CandidateRoot does not hold an unvalidated Ledger SDK candidate.'
}
$version = [string]$candidateManifest.version
$zig = [IO.Path]::GetFullPath($ZigPath)
$zigVersion = & $zig version
if ($LASTEXITCODE -ne 0 -or $zigVersion -cne '0.17.0') { throw "Zig 0.17.0 is required; got '$zigVersion'." }
$feed = [IO.Path]::GetFullPath($FreedomPointFeed)
$probeSource = Join-Path $PSScriptRoot 'ledger-package-consumer/Program.cs'

$env:NUGET_PACKAGES = Join-Path $output 'nuget-packages'
$env:DOTNET_CLI_HOME = Join-Path $output 'dotnet-home'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$config = Join-Path $output 'NuGet.Config'
$xml = [xml]'<configuration><packageSources><clear/><add key="ledger-candidate" value=""/><add key="freedompoint" value=""/></packageSources></configuration>'
$xml.configuration.packageSources.add[0].SetAttribute('value', (Join-Path $candidate 'packages'))
$xml.configuration.packageSources.add[1].SetAttribute('value', $feed)
$xml.Save($config)

$tools = Join-Path $output 'tools'
foreach ($id in @('ResourceManager.Ledger.Build', 'ResourceManager.Ledger.Configuration')) {
    Invoke-Step "install-$id" 'dotnet' @('tool', 'install', $id, '--version', $version, '--tool-path', $tools, '--configfile', $config)
}
$configurationTool = Join-Path $tools 'ledger-configuration.exe'
$buildTool = Join-Path $tools 'ledger-build.exe'
$nativeSource = Join-Path $output 'native-source'
Expand-Archive -LiteralPath (Join-Path $candidate ([string]$candidateManifest.nativeSource.file)) -DestinationPath $nativeSource

$ownerPath = Join-Path $output 'consumer-owner.json'
Write-Json $ownerPath ([ordered]@{
    schemaVersion = 1; indexBucketCount = 64
    tables = @(
        [ordered]@{ capacity = 16; partitionCapacity = 0; reservedStart = 0; reservedCount = 0
            attributes = [ordered]@{ recoverability = 'recoverable'; recoveryCostCoefficient = 2; accessLossImpact = 'observable_now' } },
        [ordered]@{ capacity = 8; partitionCapacity = 0; reservedStart = 0; reservedCount = 0
            attributes = [ordered]@{ recoverability = 'recoverable'; recoveryCostCoefficient = 1; accessLossImpact = 'unobservable_now' } })
    fixedResources = @()
})
$structure = Join-Path $output 'structure'
Invoke-Step 'structure' $configurationTool @('structure', $structure, '--table-count', '2')

$profiles = [Collections.Generic.List[object]]::new()
foreach ($bits in @(128, 256)) {
    foreach ($statistics in @(0, 1)) {
        $label = "u$bits-statistics$statistics"
        $root = Join-Path $output "profiles/$label"
        [void][IO.Directory]::CreateDirectory($root)
        $choices = Join-Path $root 'choices.json'
        Write-Json $choices ([ordered]@{ 'ledger.uid_width_bits' = $bits; 'ledger.development_allocation_statistics' = $statistics })
        $values = Join-Path $root 'values'
        $generated = Join-Path $root 'generated'
        $nativeOutput = Join-Path $root 'native-output'
        Invoke-Step "$label-check" $configurationTool @('check', '--table-count', '2', '--choices', $choices)
        Invoke-Step "$label-values" $configurationTool @('values', $values, $structure, '--table-count', '2', '--choices', $choices)
        Invoke-Step "$label-native-generation" $buildTool @('native', $ownerPath, (Join-Path $values 'ledger_values.zig'), $generated, "--uid-width=$bits")
        Invoke-Step "$label-verify" $buildTool @('verify', (Join-Path $generated 'ledger-native-build.json'))
        Invoke-Step "$label-native-build" 'pwsh' @('-NoProfile', '-NonInteractive', '-File', (Join-Path $nativeSource 'tools/Build-LedgerNative.ps1'),
            '-LedgerBuildToolPath', $buildTool, '-NativeBuildManifestPath', (Join-Path $generated 'ledger-native-build.json'),
            '-GeneratedSourceRoot', $generated, '-NativeSourceRoot', $nativeSource, '-ZigPath', $zig,
            '-StructurePath', (Join-Path $structure 'ledger_structure.zig'), '-ValuesPath', (Join-Path $values 'ledger_values.zig'),
            '-UidWidthBits', "$bits", '-WorkRoot', $work, '-OutputDirectory', $nativeOutput,
            '-GlobalCacheDirectory', (Join-Path $output 'zig-global-cache'))
        $dll = Join-Path $nativeOutput "ResourceManager.Ledger.Native.u$bits.dll"
        $nativeResult = Join-Path $nativeOutput 'native-build-result.json'

        $probe = Join-Path $root 'probe'
        [void][IO.Directory]::CreateDirectory($probe)
        Copy-Item -LiteralPath $probeSource -Destination (Join-Path $probe 'Program.cs')
        $project = Join-Path $probe 'LedgerPackageConsumer.csproj'
        [IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <Import Project="$generated\LedgerOwnerBuild.props" />
  <ItemGroup>
    <PackageReference Include="ResourceManager.Ledger" Version="$version" />
    <Compile Include="$generated\LedgerOwnerShape.g.cs" />
    <Compile Include="$generated\LedgerOwnerBuild.g.cs" />
  </ItemGroup>
</Project>
"@, $utf8)
        Invoke-Step "$label-probe-restore" 'dotnet' @('restore', $project, '--configfile', $config, '--disable-build-servers')
        Invoke-Step "$label-probe-build" 'dotnet' @('build', $project, '-c', 'Release', '--no-restore', '--disable-build-servers', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false')
        $assembly = Join-Path $probe 'bin/Release/net10.0/LedgerPackageConsumer.dll'
        $runtimeEntry = Join-Path $env:NUGET_PACKAGES "resourcemanager.ledger/$version/lib/net10.0/ResourceManager.Ledger.dll"
        if ((Get-Hash (Join-Path $probe 'bin/Release/net10.0/ResourceManager.Ledger.dll')) -cne (Get-Hash $runtimeEntry)) {
            throw "$label probe did not run on the restored candidate runtime assembly."
        }
        $probeResultPath = Join-Path $root 'probe-result.json'
        Invoke-Step "$label-probe-run" 'dotnet' @($assembly, $dll, "$statistics", $probeResultPath)
        $probeResult = Get-Content -LiteralPath $probeResultPath -Raw | ConvertFrom-Json
        if ($probeResult.passed -ne $true -or @($probeResult.cases).Count -ne 15) { throw "$label probe did not pass 15 cases." }
        $profiles.Add([pscustomobject]@{
            label = $label; uidWidthBits = $bits; developmentAllocationStatistics = [bool]$statistics
            probeResult = $probeResult
            nativeBuildResultPath = $nativeResult; nativeBuildResultSha256 = Get-Hash $nativeResult
            nativeDllPath = $dll; nativeDllSha256 = Get-Hash $dll
            consumerAssemblyPath = $assembly; consumerAssemblySha256 = Get-Hash $assembly
            restoredRuntimeSha256 = Get-Hash $runtimeEntry
        })
    }
}

$resultPath = Join-Path $output 'consumer-result.json'
Write-Json $resultPath ([pscustomobject]@{
    schema = 'resource-manager/ledger-package-consumer-result/v1'; passed = $true; version = $version
    candidateManifestPath = $candidateManifestPath; candidateManifestSha256 = Get-Hash $candidateManifestPath
    zigVersion = $zigVersion; zigSha256 = Get-Hash $zig
    profiles = $profiles; steps = $steps; completedUtc = [DateTime]::UtcNow.ToString('O')
})
$primary = $profiles | Where-Object { $_.uidWidthBits -eq 128 -and -not $_.developmentAllocationStatistics }
Write-Json (Join-Path $output 'consumer-receipt.json') ([pscustomobject]@{
    schema = 'resource-manager/ledger-package-consumer/v1'; passed = $true; version = $version
    candidateManifestPath = $candidateManifestPath; candidateManifestSha256 = Get-Hash $candidateManifestPath
    consumerResultPath = $resultPath; consumerResultSha256 = Get-Hash $resultPath
    nativeBuildResultPath = $primary.nativeBuildResultPath; nativeBuildResultSha256 = $primary.nativeBuildResultSha256
    nativeDllPath = $primary.nativeDllPath; nativeDllSha256 = $primary.nativeDllSha256
})
Write-Output "Package-consumer receipt: $(Join-Path $output 'consumer-receipt.json') ($($steps.Count) steps, $($profiles.Count) profiles x 15 cases)"
