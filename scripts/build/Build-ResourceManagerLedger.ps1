<#
.SYNOPSIS
Builds or verifies the product's paired Ledger identity and Native library.
.DESCRIPTION
WorkRoot must already exist below a running registered generated workspace.
The caller owns the bounded ExecutionJob. Each attempt retains its own outputs
and failure receipt. BuildRoot, when supplied, is verified without rebuilding.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$BuildRoot,
    [string]$SdkRoot = (Join-Path $PSScriptRoot '../../artifacts/sdk'),
    [string]$FreedomPointFeed = (Join-Path $PSScriptRoot '../../sdk/freedom-point/packages'),
    [string]$ConfigurationPath = (Join-Path $PSScriptRoot '../../src/Core/Configuration/Ledger/self-owner.json'),
    [string]$ZigPath = 'zig'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$version = '0.1.0-dev.7'
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32) + "`n", $utf8)
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Row([string]$Path) {
    [pscustomobject]@{ path = $Path; length = (Get-Item -LiteralPath $Path).Length; sha256 = Get-Hash $Path }
}
function Assert-Row($Row, [string]$Path) {
    if (-not [IO.File]::Exists($Path) -or (Get-Item -LiteralPath $Path).Length -ne [long]$Row.length -or
        (Get-Hash $Path) -ine [string]$Row.sha256) { throw "Ledger input/output fingerprint differs: $Path" }
}
function Assert-Registered([string]$Path, [bool]$RequireRunning) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or -not [IO.Directory]::Exists($Path)) {
        throw "Ledger work directory must be absolute and already exist: $Path"
    }
    $cursor = [IO.Path]::GetFullPath($Path)
    $found = $null
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Ledger work directory traverses a reparse point: $cursor"
        }
        $marker = Join-Path $cursor '.generated-workspace.json'
        if ([IO.File]::Exists($marker) -and $null -eq $found) {
            $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ([int]$state.schemaVersion -ne 1 -or [IO.Path]::GetFullPath([string]$state.workspacePath) -ine $cursor) {
                throw "Ledger work directory has an invalid lifecycle marker: $marker"
            }
            if ($RequireRunning -and [string]$state.state -cne 'running') {
                throw "Ledger build requires a running registered task/batch: $cursor"
            }
            $found = $cursor
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    }
    if ($null -eq $found) {
        throw 'Ledger build requires a registered task/batch ancestor. Use Manage-GeneratedWorkspace.ps1 New or Manage-ValidationWorkset.ps1 Prepare, then set ResourceManagerLedgerWorkRoot inside that workspace.'
    }
}
function Assert-WorkChild([string]$Path) {
    $prefix = $work.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Ledger output must stay inside WorkRoot: $Path"
    }
    $cursor = $Path
    while ($cursor -ine $work) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Ledger output traverses a reparse point: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Invoke-Tool([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with code $LASTEXITCODE." }
}
function Assert-ProductResult([string]$Root, [string]$ExpectedInputHash) {
    Assert-Registered $Root $false
    $receipt = Get-Content -LiteralPath (Join-Path $Root 'product-ledger-build.json') -Raw | ConvertFrom-Json
    if ($receipt.schema -cne 'resource-manager/product-ledger-build/v1' -or $receipt.status -cne 'built' -or
        $receipt.inputHash -cne $ExpectedInputHash) { throw 'Product Ledger result belongs to different build inputs.' }
    foreach ($row in @($receipt.files)) { Assert-Row $row (Join-Path $Root ([string]$row.file)) }
    $nativeResult = Get-Content -LiteralPath $receipt.nativeBuildResult.path -Raw | ConvertFrom-Json
    Assert-Row $receipt.nativeBuildResult ([string]$receipt.nativeBuildResult.path)
    foreach ($row in @($nativeResult.inputs) + @($nativeResult.zigLibraryFiles)) { Assert-Row $row ([string]$row.path) }
    Assert-Row $nativeResult.binary ([string]$nativeResult.binary.path)
    if ((Get-Hash (Join-Path $Root 'ResourceManager.Ledger.Native.dll')) -ine [string]$nativeResult.binary.sha256) {
        throw 'Published product Ledger DLL differs from the verified Native build.'
    }
    Invoke-Tool ([string]$receipt.buildToolPath) @('verify', [string]$nativeResult.generatedManifestPath)
    $generated = Get-Content -LiteralPath $nativeResult.generatedManifestPath -Raw | ConvertFrom-Json
    if ([int]$generated.uidWidthBits -ne 128 -or [int]$generated.tableCount -ne 2 -or
        [int]$generated.fixedCount -ne 0 -or [int]$generated.resourceCapacity -ne 172 -or
        [int]$generated.partitionCapacity -ne 0 -or [int]$generated.indexNodeCount -ne 172 -or [int]$generated.indexHomeCount -ne 256) {
        throw 'Native manifest does not describe the product two-table owner (eight tree slots and one hundred sixty-four compute scratch slots).'
    }
    foreach ($name in @('LedgerOwnerShape.g.cs', 'LedgerOwnerBuild.g.cs')) {
        $sourceRow = @($generated.outputs | Where-Object { $_.file -ceq $name })
        if ($sourceRow.Count -ne 1 -or (Get-Hash (Join-Path $Root $name)) -ine [string]$sourceRow[0].sha256) {
            throw "Published generated identity does not match Native: $name"
        }
    }
}

if (-not [IO.Path]::IsPathFullyQualified($WorkRoot)) { throw 'WorkRoot must be an absolute existing directory.' }
$work = [IO.Path]::GetFullPath($WorkRoot)
Assert-Registered $work $true
$sdk = [IO.Path]::GetFullPath($SdkRoot)
$owner = [IO.Path]::GetFullPath($ConfigurationPath)
$productShape = Get-Content -LiteralPath $owner -Raw | ConvertFrom-Json
if ($productShape.schemaVersion -ne 1 -or $productShape.indexBucketCount -ne 256 -or
    @($productShape.tables).Count -ne 2 -or @($productShape.fixedResources).Count -ne 0) {
    throw 'The product requires its exact two-table Ledger declaration.'
}
$productTableCount = @($productShape.tables).Count.ToString([Globalization.CultureInfo]::InvariantCulture)
for ($tableIndex = 0; $tableIndex -lt 2; $tableIndex++) {
    $table = $productShape.tables[$tableIndex]
    # 28 existing workspaces + current and pending pools (64 sessions each) + two model workspaces + two portable registry outputs + two display normalization workspaces + current and pending placement workspaces.
    $expectedCapacity = if ($tableIndex -eq 0) { 8 } else { 164 }
    if ($table.capacity -ne $expectedCapacity -or $table.partitionCapacity -ne 0 -or
        $table.reservedStart -ne 0 -or $table.reservedCount -ne 0) {
        throw "The product Ledger table $tableIndex does not match its declared payload role."
    }
}
$zig = (Get-Command $ZigPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$manifestPath = Join-Path $sdk "manifest.$version.json"
$release = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($release.schema -cne 'resource-manager/ledger-sdk-local-release/v1' -or $release.version -cne $version -or
    $release.status -cne 'accepted-package-consumer' -or $release.target -cne 'win-x64') { throw "A formally accepted Windows x64 Ledger $version SDK is required." }
foreach ($row in @($release.packages)) { Assert-Row $row (Join-Path $sdk ([string]$row.file)) }
foreach ($row in @($release.freedomPointDependencies)) {
    Assert-Row $row (Join-Path $FreedomPointFeed ([IO.Path]::GetFileName([string]$row.feedPath)))
}
$archive = Join-Path $sdk ([string]$release.nativeSource.file)
if ((Get-Hash $archive) -ine [string]$release.nativeSource.sha256) { throw 'Native source archive differs from the formal manifest.' }
$toolManifest = Join-Path $sdk ([string]$release.toolManifest.file)
if ((Get-Hash $toolManifest) -ine [string]$release.toolManifest.sha256) { throw 'Ledger tool manifest differs from the formal manifest.' }
$inputRows = @((Get-Row $owner), (Get-Row $manifestPath), (Get-Row $archive), (Get-Row $toolManifest), (Get-Row $zig), (Get-Row $PSCommandPath))
$inputText = ($inputRows | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
$inputHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($inputText))).ToLowerInvariant()
$consumer = if ($BuildRoot) { [IO.Path]::GetFullPath($BuildRoot) } else { Join-Path $work 'consumer' }
if ($BuildRoot) {
    Assert-ProductResult $consumer $inputHash
    Write-Output "Verified paired product Ledger result: $consumer"
    return
}
Assert-WorkChild $consumer
if ([IO.File]::Exists((Join-Path $consumer 'product-ledger-build.json'))) {
    $previous = Get-Content -LiteralPath (Join-Path $consumer 'product-ledger-build.json') -Raw | ConvertFrom-Json
    if ($previous.inputHash -ceq $inputHash) {
        Assert-ProductResult $consumer $inputHash
        Write-Output "Reused verified paired product Ledger result: $consumer"
        return
    }
}
$attempt = Join-Path $work ('attempts/' + $inputHash.Substring(0, 16) + '-' + [Guid]::NewGuid().ToString('N'))
Assert-WorkChild $attempt
[void][IO.Directory]::CreateDirectory($attempt)
try {
    $config = Join-Path $attempt 'NuGet.Config'
    $xml = [xml]'<configuration><packageSources><clear/><add key="ledger" value=""/><add key="freedompoint39" value=""/></packageSources></configuration>'
    $xml.configuration.packageSources.add[0].SetAttribute('value', (Join-Path $sdk 'packages'))
    $xml.configuration.packageSources.add[1].SetAttribute('value', [IO.Path]::GetFullPath($FreedomPointFeed))
    $xml.Save($config)
    $tools = Join-Path $attempt 'tools'
    foreach ($id in @('ResourceManager.Ledger.Build', 'ResourceManager.Ledger.Configuration')) {
        Invoke-Tool 'dotnet' @('tool', 'install', $id, '--version', $version, '--tool-path', $tools, '--configfile', $config)
    }
    $configurationTool = Join-Path $tools 'ledger-configuration.exe'
    $buildTool = Join-Path $tools 'ledger-build.exe'
    $structure = Join-Path $attempt 'structure'
    $values = Join-Path $attempt 'values'
    $generated = Join-Path $attempt 'generated'
    Invoke-Tool $configurationTool @('structure', $structure, '--table-count', $productTableCount)
    Invoke-Tool $configurationTool @('check', '--table-count', $productTableCount)
    Invoke-Tool $configurationTool @('values', $values, $structure, '--table-count', $productTableCount)
    Invoke-Tool $buildTool @('native', $owner, (Join-Path $values 'ledger_values.zig'), $generated, '--uid-width=128')
    Invoke-Tool $buildTool @('verify', (Join-Path $generated 'ledger-native-build.json'))
    $source = Join-Path $attempt 'native-source'
    # Only the small, formally hashed Native archive is extracted, never the repository.
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    $native = Join-Path $attempt 'native-output'
    & (Join-Path $source 'tools/Build-LedgerNative.ps1') -LedgerBuildToolPath $buildTool `
        -NativeBuildManifestPath (Join-Path $generated 'ledger-native-build.json') -GeneratedSourceRoot $generated `
        -NativeSourceRoot $source -ZigPath $zig -StructurePath (Join-Path $structure 'ledger_structure.zig') `
        -ValuesPath (Join-Path $values 'ledger_values.zig') -UidWidthBits 128 -WorkRoot $work `
        -OutputDirectory $native -GlobalCacheDirectory (Join-Path $attempt 'global-cache')
    $sourceFiles = @{
        'LedgerOwnerShape.g.cs' = (Join-Path $generated 'LedgerOwnerShape.g.cs')
        'LedgerOwnerBuild.g.cs' = (Join-Path $generated 'LedgerOwnerBuild.g.cs')
        'ResourceManager.Ledger.Native.dll' = (Join-Path $native 'ResourceManager.Ledger.Native.u128.dll')
    }
    [void][IO.Directory]::CreateDirectory($consumer)
    $files = @(
        foreach ($name in @($sourceFiles.Keys | Sort-Object)) {
            $destination = Join-Path $consumer $name
            Copy-Item -LiteralPath $sourceFiles[$name] -Destination ($destination + '.pending')
            Move-Item -LiteralPath ($destination + '.pending') -Destination $destination -Force
            [pscustomobject]@{ file = $name; length = (Get-Item -LiteralPath $destination).Length; sha256 = Get-Hash $destination }
        }
    )
    $receipt = [pscustomobject]@{
        schema = 'resource-manager/product-ledger-build/v1'; status = 'built'; inputHash = $inputHash
        inputs = $inputRows; buildToolPath = $buildTool
        nativeBuildResult = Get-Row (Join-Path $native 'native-build-result.json')
        files = $files; completedUtc = [DateTime]::UtcNow.ToString('O')
    }
    Write-Json (Join-Path $consumer 'product-ledger-build.json.pending') $receipt
    Move-Item -LiteralPath (Join-Path $consumer 'product-ledger-build.json.pending') -Destination (Join-Path $consumer 'product-ledger-build.json') -Force
    Assert-ProductResult $consumer $inputHash
    Write-Json (Join-Path $attempt 'attempt-result.json') @{status = 'built'; consumer = $consumer; inputHash = $inputHash}
    Write-Output "Built paired product Ledger result: $consumer"
} catch {
    Write-Json (Join-Path $attempt 'attempt-result.json') @{status = 'failed'; inputHash = $inputHash; error = $_.Exception.ToString(); completedUtc = [DateTime]::UtcNow.ToString('O')}
    throw
}
