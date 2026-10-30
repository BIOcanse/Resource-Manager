<#
.SYNOPSIS
Builds a consumer-specific Ledger Native DLL from the packaged source archive.

.DESCRIPTION
Verifies the source archive and the generated ledger-native-build.json before
invoking Zig 0.16 with the module graph and release flags used by the ledger
Native validation route. All output must be inside an explicit existing
WorkRoot. This public script does not depend on Resource Manager workspace
registration and does not generate shape or policy inputs.

.PARAMETER LedgerBuildToolPath
Installed ledger-build command shim or its .NET assembly.

.PARAMETER NativeBuildManifestPath
The generated ledger-native-build.json to verify with ledger-build.

.PARAMETER GeneratedSourceRoot
Directory containing the generated owner shape and Native entry files.

.PARAMETER NativeSourceRoot
Extracted root of ResourceManager.Ledger.NativeSources.<version>.zip.

.PARAMETER ZigPath
Path to zig.exe; the command must report version 0.16.0.

.PARAMETER StructurePath
Selected ledger_structure.zig for this consumer.

.PARAMETER ValuesPath
Selected ledger_values.zig for this consumer.

.PARAMETER UidWidthBits
UID width selected in the generated ledger-native-build.json (128 or 256).

.PARAMETER WorkRoot
Existing directory that will contain this build's output and cache directories.

.PARAMETER OutputDirectory
New directory inside WorkRoot for the DLL, caches, and build result manifest.

.PARAMETER GlobalCacheDirectory
Explicit Zig global cache path inside the same WorkRoot.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $LedgerBuildToolPath,
    [Parameter(Mandatory = $true)] [string] $NativeBuildManifestPath,
    [Parameter(Mandatory = $true)] [string] $GeneratedSourceRoot,
    [Parameter(Mandatory = $true)] [string] $NativeSourceRoot,
    [Parameter(Mandatory = $true)] [string] $ZigPath,
    [Parameter(Mandatory = $true)] [string] $StructurePath,
    [Parameter(Mandatory = $true)] [string] $ValuesPath,
    [Parameter(Mandatory = $true)] [ValidateSet(128, 256)] [int] $UidWidthBits,
    [Parameter(Mandatory = $true)] [string] $WorkRoot,
    [Parameter(Mandatory = $true)] [string] $OutputDirectory,
    [Parameter(Mandatory = $true)] [string] $GlobalCacheDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8Json {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)] $Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 32
    [System.IO.File]::WriteAllText($Path, $json + "`n", [System.Text.UTF8Encoding]::new($false))
}

function Assert-PathInside {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $rootTrimmed = $fullRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $rootPrefix = $rootTrimmed + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must remain inside the explicit WorkRoot: $fullPath"
    }

    $cursor = $fullPath
    while ($true) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Description traverses a reparse point: $cursor"
            }
        }
        if ($cursor.TrimEnd([System.IO.Path]::DirectorySeparatorChar) -ieq $rootTrimmed) { break }
        $parent = [System.IO.Path]::GetDirectoryName($cursor.TrimEnd([System.IO.Path]::DirectorySeparatorChar))
        if ([string]::IsNullOrEmpty($parent)) { throw "$Description could not be resolved beneath its root: $fullPath" }
        $cursor = $parent
    }
}

function Get-BoundedArchiveFiles {
    param([Parameter(Mandatory = $true)][string] $Root)

    $maximumFiles = 512
    $maximumEntries = 2048
    $maximumSeconds = 10
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $files = [System.Collections.Generic.List[string]]::new()
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push([System.IO.Path]::GetFullPath($Root))
    $entryCount = 0
    while ($pending.Count -gt 0) {
        if ($watch.Elapsed.TotalSeconds -gt $maximumSeconds) { throw 'Native source archive enumeration exceeded the 10-second bound.' }
        $directory = $pending.Pop()
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $entryCount++
            if ($entryCount -gt $maximumEntries) { throw 'Native source archive exceeded the 2048-entry enumeration bound.' }
            if ($watch.Elapsed.TotalSeconds -gt $maximumSeconds) { throw 'Native source archive enumeration exceeded the 10-second bound.' }
            $attributes = [System.IO.File]::GetAttributes($entry)
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Native source archive contains a reparse point: $entry"
            }
            if (($attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            } else {
                $files.Add($entry)
                if ($files.Count -gt $maximumFiles) { throw 'Native source archive exceeded the 512-file enumeration bound.' }
            }
        }
    }
    return @($files.ToArray())
}

function Assert-PathsDisjoint {
    param(
        [Parameter(Mandatory = $true)][string] $Left,
        [Parameter(Mandatory = $true)][string] $Right,
        [Parameter(Mandatory = $true)][string] $Description
    )

    $leftFull = [System.IO.Path]::GetFullPath($Left).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $rightFull = [System.IO.Path]::GetFullPath($Right).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $leftPrefix = $leftFull + [System.IO.Path]::DirectorySeparatorChar
    $rightPrefix = $rightFull + [System.IO.Path]::DirectorySeparatorChar
    if ($leftFull -ieq $rightFull -or
        $leftFull.StartsWith($rightPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        $rightFull.StartsWith($leftPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description paths must be disjoint: '$leftFull' and '$rightFull'."
    }
}

function Invoke-LedgerBuildTool {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    if ([System.IO.Path]::GetExtension($toolPath) -ieq '.dll') {
        & dotnet $toolPath @Arguments
    } else {
        & $toolPath @Arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "ledger-build exited with code $LASTEXITCODE." }
}

function Assert-HashRow {
    param(
        [Parameter(Mandatory = $true)] $Row,
        [Parameter(Mandatory = $true)][string] $Root
    )

    $relative = [string]$Row.path
    if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative) -or
        $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Unsafe relative path in Native source manifest: $relative"
    }
    $path = [System.IO.Path]::GetFullPath((Join-Path $Root $relative))
    Assert-PathInside -Path $path -Root $Root -Description 'Native source file'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Native source file is missing: $path" }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne [long]$Row.length -or (Get-Sha256 -Path $path) -cne [string]$Row.sha256) {
        throw "Native source archive file differs from its manifest: $relative"
    }
}

function Get-TreeRows {
    param([Parameter(Mandatory = $true)][string[]] $Roots)

    $rows = [System.Collections.Generic.List[object]]::new()
    $directories = [System.Collections.Generic.Stack[string]]::new()
    foreach ($root in $Roots) {
        if (Test-Path -LiteralPath $root -PathType Leaf) {
            $file = Get-Item -LiteralPath $root
            $rows.Add([pscustomobject]@{ path = $file.FullName; length = $file.Length; sha256 = Get-Sha256 -Path $file.FullName })
        } elseif (Test-Path -LiteralPath $root -PathType Container) {
            $directories.Push([System.IO.Path]::GetFullPath($root))
        } else {
            throw "Zig toolchain source input is missing: $root"
        }
    }
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($entry.PSIsContainer) {
                $directories.Push($entry.FullName)
            } else {
                $rows.Add([pscustomobject]@{ path = $entry.FullName; length = $entry.Length; sha256 = Get-Sha256 -Path $entry.FullName })
                if ($rows.Count -gt 100000) { throw 'Zig toolchain source fingerprint exceeded 100,000 files.' }
            }
        }
    }
    return @($rows.ToArray() | Sort-Object -Property path)
}

function Get-NativeSourceRows {
    param([Parameter(Mandatory = $true)][string] $Root)

    $manifestPath = Join-Path $Root 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Native source manifest is missing: $manifestPath" }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 16
    if ($manifest.schema -cne 'resource-manager/ledger-native-source/v1' -or
        $manifest.candidate -cne 'c2_chain' -or [int]$manifest.abiVersion -ne 1 -or
        @($manifest.files).Count -lt 3) {
        throw 'Unsupported or incomplete ledger Native source archive manifest.'
    }
    foreach ($row in @($manifest.files)) { Assert-HashRow -Row $row -Root $Root }
    $paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in @($manifest.files)) {
        if (-not $paths.Add([string]$row.path)) { throw "Native source manifest repeats a path: $($row.path)" }
    }
    foreach ($required in @('src/ledger.zig', 'src/local_resource_manager/owned/native.zig', 'include/ledger_native.h', 'tools/Build-LedgerNative.ps1')) {
        if (-not $paths.Contains($required)) { throw "Native source archive omits required package input: $required" }
    }
    $actualFiles = @(Get-BoundedArchiveFiles -Root $Root)
    $actualPaths = @(
        $actualFiles |
            Where-Object { [System.IO.Path]::GetFullPath($_) -cne [System.IO.Path]::GetFullPath($manifestPath) } |
            ForEach-Object { [System.IO.Path]::GetRelativePath($Root, $_).Replace('\', '/') } |
            Sort-Object
    )
    $declaredPaths = @($manifest.files | ForEach-Object { [string]$_.path } | Sort-Object)
    if ($actualPaths.Count -ne $declaredPaths.Count -or
        (Compare-Object -ReferenceObject $declaredPaths -DifferenceObject $actualPaths)) {
        throw 'Native source archive contains an unmanifested file or omits a declared file.'
    }
    return [pscustomobject]@{ manifest = $manifest; path = $manifestPath; rows = @($manifest.files) }
}

$toolPath = [System.IO.Path]::GetFullPath($LedgerBuildToolPath)
$nativeManifestPath = [System.IO.Path]::GetFullPath($NativeBuildManifestPath)
$generatedRoot = [System.IO.Path]::GetFullPath($GeneratedSourceRoot)
$sourceRoot = [System.IO.Path]::GetFullPath($NativeSourceRoot)
$zigPath = [System.IO.Path]::GetFullPath($ZigPath)
$structurePath = [System.IO.Path]::GetFullPath($StructurePath)
$valuesPath = [System.IO.Path]::GetFullPath($ValuesPath)
$workRootFull = [System.IO.Path]::GetFullPath($WorkRoot)
if (-not (Test-Path -LiteralPath $workRootFull -PathType Container)) { throw "WorkRoot must already exist: $workRootFull" }
if (((Get-Item -LiteralPath $workRootFull -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'WorkRoot itself cannot be a reparse point.'
}
if (-not [System.OperatingSystem]::IsWindows() -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This candidate builder is currently validated only for Windows x64.'
}

foreach ($path in @($toolPath, $nativeManifestPath, $structurePath, $valuesPath, $zigPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Native build input is missing: $path" }
}
if (-not (Test-Path -LiteralPath $generatedRoot -PathType Container)) { throw "GeneratedSourceRoot is missing: $generatedRoot" }
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "NativeSourceRoot is missing: $sourceRoot" }
if ([System.IO.Path]::GetFileName($structurePath) -cne 'ledger_structure.zig' -or
    [System.IO.Path]::GetFileName($valuesPath) -cne 'ledger_values.zig') {
    throw 'StructurePath and ValuesPath must name ledger_structure.zig and ledger_values.zig respectively.'
}

$outputFull = [System.IO.Path]::GetFullPath($OutputDirectory)
$globalCacheFull = [System.IO.Path]::GetFullPath($GlobalCacheDirectory)
Assert-PathInside -Path $outputFull -Root $workRootFull -Description 'OutputDirectory'
Assert-PathInside -Path $globalCacheFull -Root $workRootFull -Description 'GlobalCacheDirectory'
foreach ($inputRoot in @($sourceRoot, $generatedRoot)) {
    Assert-PathsDisjoint -Left $outputFull -Right $inputRoot -Description 'OutputDirectory and build input'
    Assert-PathsDisjoint -Left $globalCacheFull -Right $inputRoot -Description 'GlobalCacheDirectory and build input'
}
Assert-PathsDisjoint -Left $outputFull -Right $globalCacheFull -Description 'OutputDirectory and GlobalCacheDirectory'
if (Test-Path -LiteralPath $outputFull) { throw "OutputDirectory must not already exist: $outputFull" }
if (Test-Path -LiteralPath $globalCacheFull -PathType Leaf) { throw "GlobalCacheDirectory is a file: $globalCacheFull" }

$source = Get-NativeSourceRows -Root $sourceRoot
$sourceManifestHash = Get-Sha256 -Path $source.path
$nativeBuildManifestHash = Get-Sha256 -Path $nativeManifestPath
$toolHash = Get-Sha256 -Path $toolPath
$zigHash = Get-Sha256 -Path $zigPath
$builderPathInArchive = Join-Path $sourceRoot 'tools\Build-LedgerNative.ps1'
$builderHash = Get-Sha256 -Path $builderPathInArchive
$headerPath = Join-Path $sourceRoot 'include\ledger_native.h'
$headerText = [System.IO.File]::ReadAllText($headerPath)
if ($headerText -notmatch '(?m)^#define\s+RM_LEDGER_ABI_VERSION\s+1u\s*$' -or
    $headerText -notmatch '(?m)^#define\s+RM_LEDGER_CANDIDATE_C2_CHAIN\s+0u\s*$') {
    throw 'The packaged Native header does not declare ABI v1 and the selected C2 candidate.'
}

$nativeManifest = Get-Content -LiteralPath $nativeManifestPath -Raw | ConvertFrom-Json -Depth 16
if ([int]$nativeManifest.schemaVersion -ne 1 -or [int]$nativeManifest.abiVersion -ne 1 -or
    [string]$nativeManifest.candidate -cne 'c2_chain' -or [int]$nativeManifest.uidWidthBits -ne $UidWidthBits -or
    [System.IO.Path]::GetFullPath([string]$nativeManifest.valuesPath) -cne $valuesPath) {
    throw 'Generated ledger-native-build.json does not match C2, UID width, and selected ledger_values.zig.'
}
$ownerPath = [System.IO.Path]::GetFullPath([string]$nativeManifest.ownerPath)
if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf)) { throw "Generated owner declaration is missing: $ownerPath" }

foreach ($inputPath in @($nativeManifestPath, $ownerPath, $structurePath, $valuesPath, $toolPath, $zigPath)) {
    Assert-PathsDisjoint -Left $outputFull -Right $inputPath -Description 'OutputDirectory and file input'
    Assert-PathsDisjoint -Left $globalCacheFull -Right $inputPath -Description 'GlobalCacheDirectory and file input'
}

$expectedGeneratedFiles = @('LedgerNativeEntry.zig', 'LedgerOwnerShape.zig', 'LedgerOwnerShape.g.cs', 'LedgerOwnerBuild.g.cs', 'LedgerOwnerBuild.props')
foreach ($name in $expectedGeneratedFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $generatedRoot $name) -PathType Leaf)) {
        throw "Generated consumer source is missing: $name"
    }
}
$generatedRows = @($nativeManifest.outputs)
if ($generatedRows.Count -ne $expectedGeneratedFiles.Count -or
    (Compare-Object -ReferenceObject ($expectedGeneratedFiles | Sort-Object) -DifferenceObject (@($generatedRows | ForEach-Object { [string]$_.file }) | Sort-Object))) {
    throw 'Generated ledger-native-build.json does not bind exactly the four expected consumer source files.'
}
foreach ($row in $generatedRows) {
    $generatedPath = [System.IO.Path]::GetFullPath((Join-Path $generatedRoot ([string]$row.file)))
    Assert-PathInside -Path $generatedPath -Root $generatedRoot -Description 'Generated source'
    if (-not (Test-Path -LiteralPath $generatedPath -PathType Leaf) -or
        (Get-Sha256 -Path $generatedPath) -cne [string]$row.sha256) {
        throw "Generated source does not match ledger-native-build.json: $generatedPath"
    }
}
$entryPath = Join-Path $generatedRoot 'LedgerNativeEntry.zig'
$shapePath = Join-Path $generatedRoot 'LedgerOwnerShape.zig'
$sdkRoot = Join-Path $sourceRoot 'src\ledger.zig'
if (-not (Test-Path -LiteralPath $sdkRoot -PathType Leaf)) { throw "Public Native package root is missing: $sdkRoot" }
Invoke-LedgerBuildTool -Arguments @('verify', $nativeManifestPath)

$capturedModuleInputs = @($nativeManifestPath, $ownerPath, $structurePath, $valuesPath, $toolPath, $zigPath)
$moduleInputRows = @(
    foreach ($path in $capturedModuleInputs) {
        [pscustomobject]@{ path = $path; length = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 -Path $path }
    }
    foreach ($row in $generatedRows) {
        $path = Join-Path $generatedRoot ([string]$row.file)
        [pscustomobject]@{ path = $path; length = (Get-Item -LiteralPath $path).Length; sha256 = Get-Sha256 -Path $path }
    }
)

$zigVersion = (& $zigPath version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $zigVersion -cne '0.16.0') {
    throw "Zig 0.16.0 is required; compiler reported '$zigVersion'."
}

$zigLibrary = Join-Path (Split-Path -Parent $zigPath) 'lib'
$zigLibraryRoots = @(
    (Join-Path $zigLibrary 'std'),
    (Join-Path $zigLibrary 'compiler_rt.zig'),
    (Join-Path $zigLibrary 'compiler_rt')
)
$zigLibraryRows = @(Get-TreeRows -Roots $zigLibraryRoots)
foreach ($path in @($entryPath, $shapePath, $sdkRoot, (Join-Path $zigLibrary 'std'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required Zig module input is missing: $path" }
}

$outputDirectoryParent = Split-Path -Parent $outputFull
New-Item -ItemType Directory -Path $outputDirectoryParent -Force | Out-Null
New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
$localCache = Join-Path $outputFull 'cache\zig'
New-Item -ItemType Directory -Path $localCache -Force | Out-Null
if (-not (Test-Path -LiteralPath $globalCacheFull -PathType Container)) {
    New-Item -ItemType Directory -Path $globalCacheFull -Force | Out-Null
}
$binaryPath = Join-Path $outputFull ("ResourceManager.Ledger.Native.u{0}.dll" -f $UidWidthBits)
$arguments = @(
    'build-lib', '-dynamic', '-j1', '-fllvm', '-flld', '-fcompiler-rt', '-OReleaseSafe',
    '--zig-lib-dir', $zigLibrary,
    '--dep', 'ledger_sdk', '--dep', 'ledger_owner_shape', "-Mroot=$entryPath",
    '--dep', 'ledger_values', "-Mledger_sdk=$sdkRoot",
    '--dep', 'ledger_sdk', '--dep', 'ledger_values', "-Mledger_owner_shape=$shapePath",
    '--dep', 'ledger_structure', "-Mledger_values=$valuesPath",
    "-Mledger_structure=$structurePath",
    '--cache-dir', $localCache,
    '--global-cache-dir', $globalCacheFull,
    "-femit-bin=$binaryPath"
)
& $zigPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Zig Native DLL build failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) { throw 'Zig returned success but did not create the expected DLL.' }

foreach ($row in @($source.rows)) { Assert-HashRow -Row $row -Root $sourceRoot }
Invoke-LedgerBuildTool -Arguments @('verify', $nativeManifestPath)
if ((Get-Sha256 -Path $source.path) -cne $sourceManifestHash -or
    (Get-Sha256 -Path $nativeManifestPath) -cne $nativeBuildManifestHash -or
    (Get-Sha256 -Path $toolPath) -cne $toolHash -or
    (Get-Sha256 -Path $zigPath) -cne $zigHash -or
    (Get-Sha256 -Path $builderPathInArchive) -cne $builderHash) {
    throw 'A Native build input changed while compiling; the DLL was retained but no result manifest was written.'
}
$zigLibraryRowsAfter = @(Get-TreeRows -Roots $zigLibraryRoots)
if ((ConvertTo-Json -InputObject $zigLibraryRowsAfter -Depth 8 -Compress) -cne
    (ConvertTo-Json -InputObject $zigLibraryRows -Depth 8 -Compress)) {
    throw 'Zig library input tree changed while compiling.'
}
foreach ($row in $moduleInputRows) {
    if (-not (Test-Path -LiteralPath $row.path -PathType Leaf) -or
        (Get-Item -LiteralPath $row.path).Length -ne [long]$row.length -or
        (Get-Sha256 -Path $row.path) -cne [string]$row.sha256) {
        throw "Native module input changed while compiling: $($row.path)"
    }
}

$inputRows = [System.Collections.Generic.List[object]]::new()
foreach ($row in $moduleInputRows) { $inputRows.Add($row) }
foreach ($row in $source.rows) {
    $sourcePath = Join-Path $sourceRoot ([string]$row.path)
    $inputRows.Add([pscustomobject]@{ path = $sourcePath; length = [long]$row.length; sha256 = [string]$row.sha256 })
}
$pdbPath = [System.IO.Path]::ChangeExtension($binaryPath, '.pdb')
$pdb = if (Test-Path -LiteralPath $pdbPath -PathType Leaf) {
    [pscustomobject]@{ path = $pdbPath; length = (Get-Item -LiteralPath $pdbPath).Length; sha256 = Get-Sha256 -Path $pdbPath }
} else { $null }
$result = [pscustomobject]@{
    schema = 'resource-manager/ledger-native-build-result/v1'
    status = 'built-candidate'
    candidate = 'c2_chain'
    abiVersion = 1
    uidWidthBits = $UidWidthBits
    zigVersion = $zigVersion
    zigPath = $zigPath
    zigSha256 = $zigHash
    zigLibraryRoot = $zigLibrary
    zigLibraryFiles = @($zigLibraryRows)
    ledgerBuildToolPath = $toolPath
    ledgerBuildToolSha256 = $toolHash
    generatedManifestPath = $nativeManifestPath
    generatedManifestSha256 = $nativeBuildManifestHash
    nativeSourceManifestPath = $source.path
    nativeSourceManifestSha256 = $sourceManifestHash
    structurePath = $structurePath
    structureSha256 = Get-Sha256 -Path $structurePath
    valuesPath = $valuesPath
    valuesSha256 = Get-Sha256 -Path $valuesPath
    flags = @('build-lib', '-dynamic', '-j1', '-fllvm', '-flld', '-fcompiler-rt', '-OReleaseSafe')
    moduleGraph = @(
        'root -> ledger_sdk, ledger_owner_shape',
        'ledger_sdk -> ledger_values',
        'ledger_owner_shape -> ledger_sdk, ledger_values',
        'ledger_values -> ledger_structure'
    )
    commandArguments = $arguments
    inputs = @($inputRows)
    binary = [pscustomobject]@{
        path = $binaryPath
        length = (Get-Item -LiteralPath $binaryPath).Length
        sha256 = Get-Sha256 -Path $binaryPath
    }
    debugSymbols = $pdb
    completedUtc = [DateTime]::UtcNow.ToString('O')
}
Write-Utf8Json -Path (Join-Path $outputFull 'native-build-result.json') -Value $result
Write-Output "Built candidate Native DLL: $binaryPath"
Write-Output "Result manifest: $(Join-Path $outputFull 'native-build-result.json')"
