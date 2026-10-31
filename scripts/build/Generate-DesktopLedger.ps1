#Requires -Version 7
<# Generates a pure Zig public-ABI owner. Does not compile, queue work or start the product.
   WorkRoot must be inside an existing running registered task. Selected values
   and all tool/package/source outputs stay there. ChoicesPath is the SDK's
   ordinary optional FreedomPoint choices input, used by both check and values. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$ChoicesPath,
    [switch]$Verify
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = [IO.Path]::GetFullPath($WorkRoot)
if (-not [IO.Path]::IsPathFullyQualified($WorkRoot)) { throw 'WorkRoot must be absolute.' }
$cursor = $work
$registered = $false
while ($cursor) {
    if (Test-Path -LiteralPath $cursor) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "WorkRoot traverses a reparse point: $cursor"
        }
        $marker = Join-Path $cursor '.generated-workspace.json'
        if (Test-Path -LiteralPath $marker) {
            $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ($state.schemaVersion -ne 1 -or $state.state -cne 'running' -or
                [IO.Path]::GetFullPath($state.workspacePath) -ine $cursor) { throw "Invalid/nonrunning task: $cursor" }
            $registered = $true
        }
    }
    $cursor = [IO.Path]::GetDirectoryName($cursor)
}
if (-not $registered) { throw 'WorkRoot requires a running Manage-GeneratedWorkspace task ancestor.' }
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Text([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value.ReplaceLineEndings("`n"), $utf8) }
function Invoke-Tool([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with $LASTEXITCODE" }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Hash $Path) -cne $Expected.ToLowerInvariant()) { throw "SDK/input fingerprint mismatch: $Path" }
}
$version = '0.1.0-dev.7' # Current product pin; never infer a candidate/latest version.
$sdk = Join-Path $repository 'artifacts/sdk'
$releasePath = Join-Path $sdk "manifest.$version.json"
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
if ($release.version -cne $version -or $release.status -cne 'accepted-package-consumer') { throw 'Accepted pinned SDK required.' }
foreach ($row in $release.packages) {
    $path = Join-Path $sdk $row.file
    Assert-Hash $path $row.sha256
    if ((Get-Item -LiteralPath $path).Length -ne $row.length) { throw "SDK package length mismatch: $path" }
}
$archive = Join-Path $sdk $release.nativeSource.file
Assert-Hash $archive $release.nativeSource.sha256
$declarationPath = Join-Path $repository 'src/Development/DesktopBuild/desktop.ledger.json'
$declaration = Get-Content -LiteralPath $declarationPath -Raw | ConvertFrom-Json
$attributesPath = Join-Path $repository $declaration.attributeSource
$sourceOwner = Get-Content -LiteralPath $attributesPath -Raw | ConvertFrom-Json
$attributes = $sourceOwner.tables[$declaration.attributeTable].attributes
if ($attributes.recoverability -cne 'recoverable' -or $attributes.accessLossImpact -cne 'unobservable_now') {
    throw 'Referenced compute-workspace attributes no longer match Desktop recovery contract.'
}
$names = @($declaration.resources)
if ($names.Count -eq 0 -or @($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Unique named resources required.' }
foreach ($name in $names) { if ($name -cnotmatch '^[a-z][a-z0-9_]*$') { throw "Invalid Zig resource name: $name" } }
$receiptPath = Join-Path $work 'desktop-ledger-generation.json'
if ($Verify -and -not (Test-Path -LiteralPath $receiptPath)) { throw 'Generate Desktop Ledger inputs before building.' }
if ((Test-Path -LiteralPath $receiptPath) -and -not $ChoicesPath) {
    $previous = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $ChoicesPath = [string]$previous.choicesPath
}
[void][IO.Directory]::CreateDirectory($work)
# Existing outputs are reusable only with the same declaration, choices, script,
# attribute source and immutable archive. Changed inputs require a fresh WorkRoot.
$inputPaths = @($PSCommandPath, $releasePath, $declarationPath, $attributesPath)
if ($ChoicesPath) { $ChoicesPath = (Resolve-Path -LiteralPath $ChoicesPath).Path; $inputPaths += $ChoicesPath }
$inputs = @($inputPaths | ForEach-Object { [ordered]@{ path = $_; sha256 = Hash $_ } })
$inputKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes(($inputs | ConvertTo-Json -Compress)))).ToLowerInvariant()
$tools = Join-Path $work 'tools'
$configurationTool = Join-Path $tools 'ledger-configuration.exe'
$buildTool = Join-Path $tools 'ledger-build.exe'
if (Test-Path -LiteralPath $receiptPath) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.inputKey -cne $inputKey) { throw 'Inputs changed: generate in a fresh WorkRoot.' }
    foreach ($row in $receipt.outputs) { Assert-Hash (Join-Path $work $row.file) $row.sha256 }
    Invoke-Tool $buildTool @('verify', (Join-Path $work 'generated/ledger-native-build.json'))
    Write-Output "Verified Desktop Ledger inputs: $work"
    return
}
# Reject an interrupted/unknown output rather than merging source or tool stores.
if (@(Get-ChildItem -LiteralPath $work -Force).Count -ne 0) { throw 'Generation requires an empty WorkRoot or its matching completed generation receipt.' }
$savedEnvironment = @{}
foreach ($key in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_HOME', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE')) { $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
    $env:NUGET_PACKAGES = Join-Path $work 'nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'nuget-http-cache'
    $env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $config = Join-Path $work 'NuGet.Config'
    $xml = [xml]'<configuration><packageSources><clear/><add key="ledger" value=""/><add key="freedompoint39" value=""/></packageSources></configuration>'
    $xml.configuration.packageSources.add[0].SetAttribute('value', (Join-Path $sdk 'packages'))
    $xml.configuration.packageSources.add[1].SetAttribute('value', (Join-Path $repository 'sdk/freedom-point/packages'))
    $xml.Save($config)
    foreach ($id in @('ResourceManager.Ledger.Configuration', 'ResourceManager.Ledger.Build')) {
        Invoke-Tool 'dotnet' @('tool', 'install', $id, '--version', $version, '--tool-path', $tools, '--configfile', $config)
    }
    $structure = Join-Path $work 'structure'
    $values = Join-Path $work 'values'
    $generated = Join-Path $work 'generated'
    # One table, with exactly one slot per named workspace. No capacity point.
    Invoke-Tool $configurationTool @('structure', $structure, '--table-count', '1')
    $choices = if ($ChoicesPath) { @('--choices', $ChoicesPath) } else { @() }
    Invoke-Tool $configurationTool (@('check', '--table-count', '1') + $choices)
    Invoke-Tool $configurationTool (@('values', $values, $structure, '--table-count', '1') + $choices)
    $valuesPath = Join-Path $values 'ledger_values.zig'
    $selected = Get-Content -LiteralPath $valuesPath -Raw
    $widthMatch = [regex]::Matches($selected, '\.@"uid_width_bits"\s*=\s*@as\(u64,\s*@as\(u64,\s*(128|256)\)\)')
    if ($widthMatch.Count -ne 1) { throw 'Cannot read the unique SDK-projected UID width.' }
    $uidBytes = [int]$widthMatch[0].Groups[1].Value / 8
    $fixed = @()
    $uidRows = @()
    for ($index = 0; $index -lt $names.Count; $index++) {
        $hash = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes('ResourceManager.Desktop.Ledger/' + $names[$index]))
        $uid = $hash[0..($uidBytes - 1)]
        $fixed += [ordered]@{ table = 0; parentUid = $null; ordinal = $index; uid = [Convert]::ToHexString([byte[]]$uid).ToLowerInvariant(); allocationBytes = 0; attributes = $null }
        $uidRows += '    .{ ' + (($uid | ForEach-Object { '0x{0:x2}' -f $_ }) -join ', ') + ' },'
    }
    $owner = [ordered]@{ schemaVersion = 1; indexBucketCount = $names.Count; tables = @([ordered]@{
        capacity = $names.Count; partitionCapacity = 0; reservedStart = 0; reservedCount = 0; attributes = $attributes
    }); fixedResources = $fixed }
    $ownerPath = Join-Path $work 'desktop-owner.json'
    Write-Text $ownerPath ($owner | ConvertTo-Json -Depth 12)
    Write-Text (Join-Path $work 'resources.zig') ("// Generated stable Desktop workspace identities; not selected policy values.`npub const Resource = enum(u32) {`n    " + ($names -join ",`n    ") + ",`n};`npub const uids = [_][$uidBytes]u8{`n" + ($uidRows -join "`n") + "`n};`n")
    Invoke-Tool $buildTool @('native', $ownerPath, $valuesPath, $generated, ('--uid-width=' + $widthMatch[0].Groups[1].Value))
    Invoke-Tool $buildTool @('verify', (Join-Path $generated 'ledger-native-build.json'))
    $source = Join-Path $work 'native-source'
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    # The immutable archive is the compilation source. Seal all extracted files
    # using the archive's own finite entry list, without traversing other trees.
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try { $sourceFiles = @($zip.Entries | Where-Object { $_.Name } | ForEach-Object { 'native-source/' + $_.FullName.Replace('\', '/') }) }
    finally { $zip.Dispose() }
    $outputNames = @('desktop-owner.json', 'resources.zig', 'structure/ledger_structure.zig', 'values/ledger_values.zig') +
        @(Get-ChildItem -LiteralPath $generated -File | ForEach-Object { 'generated/' + $_.Name }) + $sourceFiles
    $outputs = @($outputNames | ForEach-Object { [ordered]@{ file = $_; sha256 = Hash (Join-Path $work $_) } })
    Write-Text $receiptPath ([ordered]@{ sdkVersion = $version; inputKey = $inputKey; choicesPath = $ChoicesPath; inputs = $inputs; outputs = $outputs } | ConvertTo-Json -Depth 12)
    Write-Output "Generated Desktop Ledger public-ABI inputs: $work"
} finally {
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }
}
