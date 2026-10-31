<#
.SYNOPSIS
Projects the product Native sources into a registered scratch root, optionally
building only those product components and the legacy Adapter default install.
.DESCRIPTION
Does not build NativeCore or the independent Ledger SDK. -Build must inherit the
bounded ProductSdk child environment. Original source and Native/bin are read-only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ScratchRoot,
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))),
    [string]$PrerequisitesPath,
    [switch]$Build,
    [ValidateRange(1, 100000)][int]$MaximumEntries = 30000,
    [ValidateRange(1, 10000)][int]$MaximumFiles = 5000,
    [ValidateRange(1, 1073741824)][long]$MaximumBytes = 268435456
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$task = [IO.Path]::GetFullPath($TaskRoot)
$scratch = [IO.Path]::GetFullPath($ScratchRoot)
$run = [IO.Path]::GetFullPath($RunDirectory)
# MinGW still fails opening the projected Vulkan video headers at 261 chars.
# Keep this leaf short inside the registered batch; do not shorten source names.
$work = Join-Path $scratch 'n'
$contextPath = Join-Path $task 'product-native-context.json'
if (-not $PrerequisitesPath) { $PrerequisitesPath = Join-Path $task 'package-prerequisites.json' }

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20) + "`n", $utf8)
}
function Assert-SafePath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "An absolute path is required: $Path" }
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Native projection cannot traverse a reparse point: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    }
}
function Assert-Registered([string]$Path) {
    Assert-SafePath $Path
    if (-not [IO.Directory]::Exists($Path)) { throw "Registered directory must already exist: $Path" }
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        $marker = Join-Path $cursor '.generated-workspace.json'
        if ([IO.File]::Exists($marker)) {
            if ((Get-Item -LiteralPath $marker).Length -gt 1048576) { throw 'Lifecycle marker exceeds 1 MiB.' }
            $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ([int]$state.schemaVersion -ne 1 -or $state.state -cne 'running' -or
                [IO.Path]::GetFullPath([string]$state.workspacePath) -ine $cursor) {
                throw "Native build needs a valid running task/batch: $marker"
            }
            return
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    }
    throw "Native projection requires a registered task/batch ancestor: $Path"
}
function Get-Row([string]$Path) {
    Assert-SafePath $Path
    [pscustomobject]@{ path = $Path; length = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Assert-Row($Row) {
    $now = Get-Row ([string]$Row.path)
    if ($now.length -ne $Row.length -or $now.sha256 -ine $Row.sha256) { throw "Native input changed: $($Row.path)" }
}
Assert-Registered $task
Assert-Registered $scratch
Assert-Registered $run
Assert-SafePath $repo
Assert-SafePath $work
if ([IO.Path]::GetPathRoot($scratch) -ine 'F:\') { throw 'Product Native outputs must be in the registered F workset.' }
if ([IO.Directory]::Exists($work) -or [IO.File]::Exists($work)) { throw "Native projection needs a fresh output: $work" }
if ($Build -and ([string]::IsNullOrWhiteSpace($env:RM_REVIEW_SCRATCH) -or
    [IO.Path]::GetFullPath($env:RM_REVIEW_SCRATCH) -ine $scratch)) { throw '-Build requires the bounded ProductSdk child scratch environment.' }
Write-Json $contextPath @{ accepted = $false; status = 'preparing'; runDirectory = $run; scratchRoot = $scratch }
[void][IO.Directory]::CreateDirectory($work)
$watch = [Diagnostics.Stopwatch]::StartNew()
$components = @('AdlxBridge', 'GpuPlacementShim', 'GpuLaunchBroker', 'GpuWindowAction',
    'GpuPlacementExternal', 'GpuPlacementPreparation', 'PerformanceOverlay')
$sourceRows = [Collections.Generic.List[object]]::new()
$copiedRows = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
$tools = [Collections.Generic.List[object]]::new()
$entries = 0
$bytes = 0L
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($ext in @('.c', '.cpp', '.h', '.hpp', '.hxx', '.inc', '.inl', '.cmd', '.json', '.def', '.txt', '.md', '.zig', '.zon', '.rc', '.s')) { [void]$allowed.Add($ext) }
$excluded = @('bin', 'obj', '.git', '.zig-cache', 'zig-out', 'node_modules')

function Copy-Source([string]$Source, [string]$Destination) {
    if ($watch.Elapsed.TotalSeconds -gt 180) { throw 'Native source projection exceeded 180 seconds.' }
    Assert-SafePath $Source
    Assert-SafePath $Destination
    if (-not $Destination.StartsWith($work + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Native projection escapes its work root: $Destination"
    }
    $row = Get-Row $Source
    $script:bytes += [long]$row.length
    if ($sourceRows.Count -ge $MaximumFiles -or $script:bytes -gt $MaximumBytes) { throw 'Native source projection exceeded its file/byte bound.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    [IO.File]::Copy($Source, $Destination, $false)
    $copy = Get-Row $Destination
    if ($copy.sha256 -ine $row.sha256) { throw "Native source copy differs: $Source" }
    $sourceRows.Add($row)
    $copiedRows.Add($copy)
}
function Copy-SourceTree([string]$Relative, [string]$Destination) {
    $sourceRoot = Join-Path $repo $Relative
    Assert-SafePath $sourceRoot
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($sourceRoot)
    while ($pending.Count -gt 0) {
        if ($watch.Elapsed.TotalSeconds -gt 180) { throw 'Native source projection exceeded 180 seconds.' }
        $directory = $pending.Dequeue()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $script:entries++
            if ($script:entries -gt $MaximumEntries) { throw 'Native projection exceeded its entry bound.' }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Native input contains a reparse point: $entry" }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                if ([IO.Path]::GetFileName($entry) -notin $excluded) { $pending.Enqueue($entry) }
            } elseif ($allowed.Contains([IO.Path]::GetExtension($entry)) -or [IO.Path]::GetFileName($entry) -match '^(LICENSE|COPYING|NOTICE)$') {
                Copy-Source $entry (Join-Path $Destination ([IO.Path]::GetRelativePath($sourceRoot, $entry)))
            }
        }
    }
}
function Run-Command($Command) {
    $log = Join-Path $run ($Command.name + '.log')
    $originalPath = $env:PATH
    $env:PATH = $compilerDirectory + [IO.Path]::PathSeparator + $originalPath
    Push-Location ([string]$Command.workingDirectory)
    try {
        & ([string]$Command.executable) @($Command.arguments) *> $log
        $code = $LASTEXITCODE
        Get-Content -LiteralPath $log -Tail 8
        if ($code -ne 0) { throw "Native command $($Command.name) failed: $code; $log" }
    } finally { Pop-Location; $env:PATH = $originalPath }
}
function Resolve-Application([string]$Name) {
    $command = Get-Command $Name -CommandType Application -All -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $command -or -not [IO.File]::Exists([string]$command.Path)) { throw "Native tool is missing: $Name" }
    return [IO.Path]::GetFullPath([string]$command.Path)
}
function Read-ToolIdentity([string]$Executable, [string[]]$Arguments) {
    $value = (& $Executable @Arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Native tool identity failed: $Executable $($Arguments -join ' ')" }
    return $value
}
try {
    $prerequisites = Get-Content -LiteralPath $PrerequisitesPath -Raw | ConvertFrom-Json
    $baselineRows = [Collections.Generic.List[object]]::new()
    foreach ($row in @($prerequisites.nativeSourceInputs) + @($prerequisites.nativeAssets)) {
        $path = if ([IO.Path]::IsPathFullyQualified([string]$row.path)) { [string]$row.path } else { Join-Path $repo ([string]$row.path) }
        $actual = Get-Row $path
        if ($actual.sha256 -ine [string]$row.sha256) { throw "Accepted Native prerequisite differs: $path" }
        $baselineRows.Add($actual)
    }
    $nativeCore = @($prerequisites.nativeAssets | Where-Object { [IO.Path]::GetFileName([string]$_.path) -ceq 'ResourceManager.NativeCore.dll' })
    if ($nativeCore.Count -ne 1 -or @($prerequisites.nativeSourceInputs).Count -eq 0) { throw 'A source-locked accepted NativeCore baseline is required.' }
    $nativeRoot = Join-Path $work 'Native'
    foreach ($component in @($components) + @('GpuPlacementCommon')) {
        Copy-SourceTree ('src/Core/Native/' + $component) (Join-Path $nativeRoot $component)
    }
    # The normal migration build has only these three inputs and no local headers.
    foreach ($name in @('build.cmd', 'ResourceManagerGpuMigrationProbe.cpp', 'D3D11WindowReselectProbe.cpp', 'D3D11GpuPreferenceShim.cpp')) {
        Copy-Source (Join-Path $repo ('src/Core/Native/GpuMigrationProbe/' + $name)) (Join-Path $nativeRoot ('GpuMigrationProbe/' + $name))
    }
    $adapterRoot = Join-Path $work 'adapter'
    Copy-Source (Join-Path $repo 'sdk/zig/build.zig') (Join-Path $adapterRoot 'build.zig')
    Copy-SourceTree 'sdk/zig/src' (Join-Path $adapterRoot 'src')
    $cmd = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
    if (-not [IO.File]::Exists($cmd)) { $cmd = Resolve-Application 'cmd.exe' }
    $gcc = Resolve-Application 'gcc.exe'
    $gxx = Resolve-Application 'g++.exe'
    $zig = Resolve-Application 'zig.exe'
    $compilerDirectory = [IO.Path]::GetDirectoryName($gcc)
    if ([IO.Path]::GetDirectoryName($gxx) -ine $compilerDirectory) { throw 'gcc and g++ must resolve to the same toolchain directory.' }
    foreach ($tool in @($cmd, $gcc, $gxx, $zig)) { $tools.Add((Get-Row $tool)) }
    $toolIdentities = [ordered]@{ compilerDirectory = $compilerDirectory; captured = $false }
    if ($Build) {
        $gccTarget = Read-ToolIdentity $gcc @('-dumpmachine')
        $gxxTarget = Read-ToolIdentity $gxx @('-dumpmachine')
        if ($gccTarget -cne $gxxTarget -or $gccTarget -notmatch '^x86_64-.*mingw') { throw 'gcc/g++ must be the same Windows x64 target.' }
        $gccFullVersion = Read-ToolIdentity $gcc @('-dumpfullversion')
        $gxxFullVersion = Read-ToolIdentity $gxx @('-dumpfullversion')
        if ($gccFullVersion -cne $gxxFullVersion) { throw 'gcc/g++ toolchain versions differ.' }
        $toolIdentities = [ordered]@{ captured = $true; compilerDirectory = $compilerDirectory; gccTarget = $gccTarget; gxxTarget = $gxxTarget
            gccFullVersion = $gccFullVersion; gxxFullVersion = $gxxFullVersion
            gccVersion = Read-ToolIdentity $gcc @('--version'); gxxVersion = Read-ToolIdentity $gxx @('--version')
            zigVersion = Read-ToolIdentity $zig @('version'); zigEnvironment = Read-ToolIdentity $zig @('env') }
        if ($toolIdentities.zigVersion -cne '0.17.0') { throw 'Product Native build requires Zig 0.17.0.' }
        Write-Json (Join-Path $work 'tool-identities.json') $toolIdentities
    }
    foreach ($component in @($components) + @('GpuMigrationProbe')) {
        $commands.Add([pscustomobject]@{ name = $component; executable = $cmd
            workingDirectory = (Join-Path $nativeRoot $component); arguments = @('/d', '/c', 'build.cmd') })
    }
    $adapterPrefix = Join-Path $work 'adapter-install'
    [void][IO.Directory]::CreateDirectory((Join-Path $adapterPrefix 'bin'))
    $commands.Add([pscustomobject]@{ name = 'Adapter'; executable = $zig; workingDirectory = $adapterRoot
        arguments = @('build-lib', '-dynamic', '-j1', '--name', 'ResourceManager.Adapter.Native',
            '-OReleaseFast', '--dep', 'activity_decay', ('-Mroot=' + (Join-Path $adapterRoot 'src/native_abi.zig')),
            '-OReleaseFast', ('-Mactivity_decay=' + (Join-Path $adapterRoot 'src/activity_decay.zig')),
            '--cache-dir', (Join-Path $work 'adapter-local-cache'), '--global-cache-dir', (Join-Path $work 'adapter-global-cache'),
            ('-femit-bin=' + (Join-Path $adapterPrefix 'bin/ResourceManager.Adapter.Native.dll')))
        semantics = 'Equivalent dynamic install artifact; ReleaseFast on both modules and unchanged default code generator/linker; bypass build-runner compilation.' })
    $beforePath = Join-Path $work 'native-inputs.before.json'
    Write-Json $beforePath @{ source = @($sourceRows.ToArray()); projected = @($copiedRows.ToArray()); baseline = @($baselineRows.ToArray()); tools = @($tools.ToArray()); toolIdentities = $toolIdentities
        prerequisites = Get-Row ([IO.Path]::GetFullPath($PrerequisitesPath)); helper = Get-Row $PSCommandPath }
    Write-Json (Join-Path $work 'native-commands.json') @($commands.ToArray())
    if (-not $Build) {
        Write-Json (Join-Path $work 'native-result.json') @{ accepted = $false; status = 'projected'; sourceFiles = $sourceRows.Count; sourceBytes = $bytes; sourceInputsManifest = $beforePath }
        Write-Output "Prepared Native commands without executing: $work"
        return
    }
    foreach ($command in $commands) { Run-Command $command }
    foreach ($row in @($sourceRows.ToArray()) + @($copiedRows.ToArray()) + @($baselineRows.ToArray()) + @($tools.ToArray())) { Assert-Row $row }
    $before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
    Assert-Row $before.prerequisites
    Assert-Row $before.helper
    $afterPath = Join-Path $work 'native-inputs.after.json'
    Write-Json $afterPath @{ unchanged = $true; source = @($sourceRows.ToArray()); projected = @($copiedRows.ToArray()); baseline = @($baselineRows.ToArray()); tools = @($tools.ToArray()); toolIdentities = $toolIdentities }
    $properties = [ordered]@{ NativeCoreOutput = [string]$nativeCore[0].path; SkipNativeCoreBuild = 'true'
        AdapterNativeRoot = $adapterRoot; AdapterNativeOutput = (Join-Path $adapterPrefix 'bin/ResourceManager.Adapter.Native.dll'); SkipAdapterNativeBuild = 'true' }
    foreach ($component in @($components) + @('GpuMigrationProbe')) { $properties[$component + 'Root'] = Join-Path $nativeRoot $component }
    foreach ($name in @('SkipAdlxBridgeBuild', 'SkipGpuPlacementNativeBuild', 'SkipGpuLaunchBrokerNativeBuild', 'SkipGpuWindowActionNativeBuild',
        'SkipGpuPlacementExternalNativeBuild', 'SkipGpuPlacementPreparationNativeBuild', 'SkipPerformanceOverlayNativeBuild')) { $properties[$name] = 'true' }
    $expected = @(
        'AdlxBridge/bin/win-x64/ResourceManager.AdlxBridge.dll',
        'GpuPlacementShim/bin/win-x64/ResourceManager.GpuPlacementShim.dll',
        'GpuPlacementShim/bin/win-x64/ResourceManager.GpuPlacementBootstrap.dll',
        'GpuPlacementShim/bin/win-x64/ResourceManager.VulkanPlacementLayer.dll',
        'GpuLaunchBroker/bin/win-x64/ResourceManager.GpuLaunchBroker.exe',
        'GpuWindowAction/bin/win-x64/ResourceManager.GpuWindowAction.exe',
        'GpuPlacementExternal/bin/win-x64/ResourceManager.GpuPlacementExternal.exe',
        'GpuPlacementExternal/bin/win-x64/ResourceManager.GpuRendererExternal.exe',
        'GpuPlacementPreparation/bin/win-x64/ResourceManager.GpuPlacementPreparation.exe',
        'PerformanceOverlay/bin/win-x64/ResourceManager.PerformanceOverlay.dll',
        'PerformanceOverlay/bin/win-x64/ResourceManager.VulkanPerformanceOverlayLayer.dll',
        'GpuMigrationProbe/bin/win-x64/ResourceManager.GpuMigrationProbe.exe',
        'GpuMigrationProbe/bin/win-x64/ResourceManager.D3D11WindowReselectProbe.exe',
        'GpuMigrationProbe/bin/win-x64/shim/d3d11.dll'
    )
    $outputs = @((Get-Row ([string]$properties.NativeCoreOutput)), (Get-Row ([string]$properties.AdapterNativeOutput)))
    $outputs += @(foreach ($relative in $expected) { Get-Row (Join-Path $nativeRoot $relative) })
    $outputs += @(foreach ($relative in @('GpuPlacementShim/ResourceManager.VulkanPlacementLayer.json', 'PerformanceOverlay/ResourceManager.VulkanPerformanceOverlayLayer.json',
        'GpuPlacementShim/third_party/minhook/LICENSE.txt', 'GpuPlacementShim/third_party/detours/LICENSE.md')) { Get-Row (Join-Path $nativeRoot $relative) })
    $result = [ordered]@{ accepted = $true; status = 'built'; runDirectory = $run; scratchRoot = $scratch; properties = $properties
        outputs = $outputs; sourceInputsManifest = Get-Row $beforePath; sourceInputsAfterManifest = Get-Row $afterPath
        commandManifest = Get-Row (Join-Path $work 'native-commands.json'); sourceFiles = $sourceRows.Count; sourceBytes = $bytes
        limits = @{ maximumEntries = $MaximumEntries; maximumFiles = $MaximumFiles; maximumBytes = $MaximumBytes }
        baselineScope = @{ nativeCore = 'Accepted binary plus original NativeCore source lock rechecked before/after'
            adlx = 'Rebuilt from projected current source; accepted package binary was not substituted'
            dumpAnalysis = 'Only accepted prerequisite binary checked; not rebuilt and no new source proof'
            ledger = 'Independent dev.6 consumer pair remains supplied by product Ledger build target' }
        completedUtc = [DateTime]::UtcNow.ToString('O') }
    Write-Json (Join-Path $work 'native-result.json') $result
    Write-Json $contextPath $result
    Write-Output "Built and verified product Native inputs: $contextPath"
} catch {
    $failure = @{ accepted = $false; status = 'failed'; runDirectory = $run; scratchRoot = $scratch; error = $_.Exception.ToString() }
    Write-Json (Join-Path $work 'native-result.json') $failure
    Write-Json $contextPath $failure
    throw
}
