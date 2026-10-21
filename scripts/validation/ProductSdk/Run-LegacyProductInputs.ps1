<# Reuses a drained, still-running product Native projection; builds only the
legacy Adapter DLL directly, preserving ReleaseFast and LLVM/LLD semantics. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ScratchRoot,
    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))),
    [string]$NativeProjectionRoot,
    [string]$NativeRunDirectory,
    [switch]$Build
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$task = [IO.Path]::GetFullPath($TaskRoot)
$run = [IO.Path]::GetFullPath($RunDirectory)
$scratch = [IO.Path]::GetFullPath($ScratchRoot)
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$contextPath = Join-Path $task 'product-native-context.json'
function Write-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 24) + "`n", $utf8) }
function Assert-Safe([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "Absolute path required: $Path" }
    $cursor = $Path
    while ($cursor) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    }
}
function Assert-Running([string]$Path) {
    Assert-Safe $Path
    if (-not [IO.Directory]::Exists($Path)) { throw "Missing registered path: $Path" }
    $cursor = $Path
    while ($cursor) {
        $marker = Join-Path $cursor '.generated-workspace.json'
        if ([IO.File]::Exists($marker)) {
            $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ($state.state -cne 'running' -or [IO.Path]::GetFullPath([string]$state.workspacePath) -ine $cursor) { throw "Cannot reopen a closed/invalid workspace: $cursor" }
            return
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\', '/'))
    }
    throw "No registered ancestor: $Path"
}
function Get-Row([string]$Path) {
    Assert-Safe $Path
    [pscustomobject]@{ path = $Path; length = (Get-Item -LiteralPath $Path).Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Assert-Row($Row) {
    $actual = Get-Row ([string]$Row.path)
    if ($actual.length -ne [long]$Row.length -or $actual.sha256 -ine [string]$Row.sha256) { throw "Native recovery input changed: $($Row.path)" }
}
Assert-Running $task
Assert-Running $run
Assert-Running $scratch
if ([IO.Path]::GetPathRoot($scratch) -ine 'F:\') { throw 'Legacy output must be in the new registered F batch.' }
if ($Build -and ([string]::IsNullOrWhiteSpace($env:RM_REVIEW_SCRATCH) -or [IO.Path]::GetFullPath($env:RM_REVIEW_SCRATCH) -ine $scratch)) { throw 'Use the bounded ProductSdk child.' }
if (-not $NativeProjectionRoot -or -not $NativeRunDirectory) {
    $previous = Get-Content -LiteralPath $contextPath -Raw | ConvertFrom-Json
    if (-not $NativeProjectionRoot) { $NativeProjectionRoot = Join-Path ([string]$previous.scratchRoot) 'n' }
    if (-not $NativeRunDirectory) { $NativeRunDirectory = [string]$previous.runDirectory }
}
$projection = [IO.Path]::GetFullPath($NativeProjectionRoot)
$previousRun = [IO.Path]::GetFullPath($NativeRunDirectory)
Assert-Running $projection
Assert-Running $previousRun
if ([IO.Path]::GetFileName($projection) -cne 'n' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($projection)) -cne 'scratch') { throw 'Recovery requires the exact product Native projection root.' }
$beforePath = Join-Path $projection 'native-inputs.before.json'
$before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
$commandPath = Join-Path $projection 'native-commands.json'
$oldCommands = @(Get-Content -LiteralPath $commandPath -Raw | ConvertFrom-Json)
$names = @('AdlxBridge','GpuPlacementShim','GpuLaunchBroker','GpuWindowAction','GpuPlacementExternal','GpuPlacementPreparation','PerformanceOverlay','GpuMigrationProbe','Adapter')
if ($oldCommands.Count -ne 9 -or (($oldCommands.name -join '|') -cne ($names -join '|'))) { throw 'Original sequential Native command list differs.' }
if (-not [IO.File]::Exists((Join-Path $previousRun 'Adapter.log'))) { throw 'Original sequential execution did not reach Adapter.' }
$bounded = Get-Content -LiteralPath (Join-Path $previousRun 'bounded-result.json') -Raw | ConvertFrom-Json
if ($bounded.stage -cne 'ProductNative' -or [int]$bounded.exitCode -ne 125 -or
    -not $bounded.sourceInputsUnchangedExceptDeclaredPartitionRefresh -or
    [IO.Path]::GetFullPath([string]$bounded.workset) -ine [IO.Path]::GetDirectoryName($projection)) { throw 'Recovery requires the recorded product Native limit failure with unchanged inputs.' }
$receiptsPath = Join-Path $previousRun 'job/receipts.jsonl'
$events = @(Get-Content -LiteralPath $receiptsPath | ForEach-Object { $_ | ConvertFrom-Json })
$final = @($events | Where-Object kind -eq 'final-job')
$closed = @($events | Where-Object kind -eq 'owned-job-closed')
if ($final.Count -ne 1 -or -not $final[0].data.empty -or $final[0].data.reason -cne 'observed-job-limit' -or $closed.Count -ne 1) { throw 'Previous Native Job is not proven empty and closed.' }
$originalInputs = @(Get-Content -LiteralPath (Join-Path $previousRun 'source-inputs.json') -Raw | ConvertFrom-Json)
$helperRow = @($originalInputs | Where-Object { [IO.Path]::GetFullPath((Join-Path $repo ([string]$_.path))) -ieq [string]$before.helper.path })
if ($helperRow.Count -ne 1 -or $helperRow[0].sha256 -ine $before.helper.sha256) { throw 'Original helper is not bound to the successful eight-command control flow.' }
$historicalProducer = [pscustomobject]@{ path = (Join-Path $task 'native-helper-before-direct-adapter.ps1'); length = $before.helper.length; sha256 = $before.helper.sha256 }
Assert-Row $historicalProducer
if (@($before.source).Count -gt 5000 -or @($before.projected).Count -ne @($before.source).Count) { throw 'Unexpected Native source manifest size.' }
foreach ($row in @($before.source) + @($before.projected) + @($before.baseline) + @($before.tools) + @($historicalProducer, $before.prerequisites)) { Assert-Row $row }
$nativeRoot = Join-Path $projection 'Native'
$cmdRow = @($before.tools | Where-Object { [IO.Path]::GetFileName($_.path) -ieq 'cmd.exe' })
if ($cmdRow.Count -ne 1) { throw 'Original cmd tool identity is missing.' }
for ($i = 0; $i -lt 8; $i++) {
    $expectedRoot = Join-Path $nativeRoot $names[$i]
    if ([string]$oldCommands[$i].executable -ine [string]$cmdRow[0].path -or [string]$oldCommands[$i].workingDirectory -ine $expectedRoot -or
        ($oldCommands[$i].arguments -join '|') -cne '/d|/c|build.cmd') { throw 'Original component command differs.' }
}
$zigRow = @($before.tools | Where-Object { [IO.Path]::GetFileName($_.path) -ieq 'zig.exe' })
if ($zigRow.Count -ne 1 -or $before.toolIdentities.zigVersion -cne '0.16.0') { throw 'Recovery requires the unchanged Zig 0.16 compiler.' }
$oldAdapterArguments = @('build','-j1','--prefix',(Join-Path $projection 'adapter-install'),'--cache-dir',(Join-Path $projection 'adapter-local-cache'),
    '--global-cache-dir',(Join-Path $projection 'adapter-global-cache'),'-Doptimize=ReleaseFast')
if ([string]$oldCommands[8].executable -ine [string]$zigRow[0].path -or [string]$oldCommands[8].workingDirectory -ine (Join-Path $projection 'adapter') -or
    ($oldCommands[8].arguments -join '|') -cne ($oldAdapterArguments -join '|')) { throw 'Original failed Adapter build command differs.' }
$expected = @('AdlxBridge/bin/win-x64/ResourceManager.AdlxBridge.dll',
 'GpuPlacementShim/bin/win-x64/ResourceManager.GpuPlacementShim.dll','GpuPlacementShim/bin/win-x64/ResourceManager.GpuPlacementBootstrap.dll','GpuPlacementShim/bin/win-x64/ResourceManager.VulkanPlacementLayer.dll',
 'GpuLaunchBroker/bin/win-x64/ResourceManager.GpuLaunchBroker.exe','GpuWindowAction/bin/win-x64/ResourceManager.GpuWindowAction.exe',
 'GpuPlacementExternal/bin/win-x64/ResourceManager.GpuPlacementExternal.exe','GpuPlacementExternal/bin/win-x64/ResourceManager.GpuRendererExternal.exe',
 'GpuPlacementPreparation/bin/win-x64/ResourceManager.GpuPlacementPreparation.exe','PerformanceOverlay/bin/win-x64/ResourceManager.PerformanceOverlay.dll','PerformanceOverlay/bin/win-x64/ResourceManager.VulkanPerformanceOverlayLayer.dll',
 'GpuMigrationProbe/bin/win-x64/ResourceManager.GpuMigrationProbe.exe','GpuMigrationProbe/bin/win-x64/ResourceManager.D3D11WindowReselectProbe.exe','GpuMigrationProbe/bin/win-x64/shim/d3d11.dll',
 'GpuPlacementShim/ResourceManager.VulkanPlacementLayer.json','PerformanceOverlay/ResourceManager.VulkanPerformanceOverlayLayer.json','GpuPlacementShim/third_party/minhook/LICENSE.txt','GpuPlacementShim/third_party/detours/LICENSE.md')
$partialOutputs = @(foreach ($relative in $expected) { Get-Row (Join-Path $nativeRoot $relative) })
$logs = @(foreach ($name in $names) { Get-Row (Join-Path $previousRun ($name + '.log')) })
$checkpoint = [ordered]@{ acceptedComponents = $true; adapterAccepted = $false; nativeProjectionRoot = $projection; previousRunDirectory = $previousRun
    inputManifest = Get-Row $beforePath; commands = Get-Row $commandPath; originalJobReceipts = Get-Row $receiptsPath
    originalBoundedResult = Get-Row (Join-Path $previousRun 'bounded-result.json'); outputs = $partialOutputs; logs = $logs
    historicalProducer = $historicalProducer; currentProducer = Get-Row (Join-Path $repo 'scripts/validation/ProductSdk/Prepare-NativeProductInputs.ps1')
    provenance = 'Original hash-bound helper advances to Adapter only after all eight cmd exit codes are zero; prior job is empty/closed; current sources, tools and partial output bytes are rechecked.' }
Write-Json (Join-Path $task 'native-component-checkpoint.json') $checkpoint
$work = Join-Path $scratch 'l'
Assert-Safe $work
if ([IO.Directory]::Exists($work) -or [IO.File]::Exists($work)) { throw 'Legacy recovery output must be fresh.' }
[void][IO.Directory]::CreateDirectory($work)
$binary = Join-Path $work 'ResourceManager.Adapter.Native.dll'
$adapterRoot = Join-Path $projection 'adapter'
$arguments = @('build-lib','-dynamic','-j1','--name','ResourceManager.Adapter.Native',
 '-OReleaseFast','--dep','activity_decay',('-Mroot=' + (Join-Path $adapterRoot 'src/native_abi.zig')),
 '-OReleaseFast',('-Mactivity_decay=' + (Join-Path $adapterRoot 'src/activity_decay.zig')),
 '--cache-dir',(Join-Path $work 'c'),'--global-cache-dir',(Join-Path $work 'g'),('-femit-bin=' + $binary))
$command = [ordered]@{ executable = [string]$zigRow[0].path; arguments = $arguments; workingDirectory = $work
    semantics = 'Original dynamic library/native host target, ReleaseFast on both modules, unchanged default code generator/linker (no override); no build runner or test step.' }
Write-Json (Join-Path $work 'legacy-command.json') $command
if (-not $Build) { Write-Output 'Verified the drained Native partial result and prepared the direct Adapter command.'; return }
Write-Json $contextPath @{ accepted = $false; status = 'legacy-building'; runDirectory = $run; scratchRoot = $scratch }
try {
    Push-Location $work
    try {
        & ([string]$command.executable) @arguments *> (Join-Path $run 'Adapter-direct.log')
        $code = $LASTEXITCODE
        Get-Content -LiteralPath (Join-Path $run 'Adapter-direct.log') -Tail 12
        if ($code -ne 0) { throw "Direct Adapter build failed: $code" }
    } finally { Pop-Location }
    foreach ($row in @($before.source) + @($before.projected) + @($before.baseline) + @($before.tools) + $partialOutputs) { Assert-Row $row }
    Assert-Row $checkpoint.inputManifest
    Assert-Row $checkpoint.commands
    Assert-Row $historicalProducer
    Assert-Row $before.prerequisites
    $nativeCore = @($before.baseline | Where-Object { [IO.Path]::GetFileName($_.path) -ceq 'ResourceManager.NativeCore.dll' })
    if ($nativeCore.Count -ne 1) { throw 'Missing accepted NativeCore baseline.' }
    $properties = [ordered]@{ NativeCoreOutput = [string]$nativeCore[0].path; SkipNativeCoreBuild = 'true'; AdapterNativeRoot = $adapterRoot; AdapterNativeOutput = $binary; SkipAdapterNativeBuild = 'true' }
    foreach ($name in $names[0..7]) { $properties[$name + 'Root'] = Join-Path $nativeRoot $name }
    foreach ($name in @('SkipAdlxBridgeBuild','SkipGpuPlacementNativeBuild','SkipGpuLaunchBrokerNativeBuild','SkipGpuWindowActionNativeBuild','SkipGpuPlacementExternalNativeBuild','SkipGpuPlacementPreparationNativeBuild','SkipPerformanceOverlayNativeBuild')) { $properties[$name] = 'true' }
    $after = Join-Path $work 'legacy-inputs.after.json'
    Write-Json $after @{ unchanged = $true; originalInputsManifest = Get-Row $beforePath; partialOutputs = $partialOutputs; compiler = $zigRow[0]; helper = Get-Row $PSCommandPath }
    $result = [ordered]@{ accepted = $true; status = 'built'; runDirectory = $run; scratchRoot = $scratch; properties = $properties
        outputs = @($partialOutputs) + @($nativeCore[0], (Get-Row $binary)); sourceInputsManifest = Get-Row $beforePath
        sourceInputsAfterManifest = Get-Row $after; commandManifest = Get-Row (Join-Path $work 'legacy-command.json')
        componentCheckpoint = Get-Row (Join-Path $task 'native-component-checkpoint.json'); reusedNativeProjectionRoot = $projection
        completedUtc = [DateTime]::UtcNow.ToString('O') }
    Write-Json (Join-Path $work 'legacy-result.json') $result
    Write-Json $contextPath $result
    Write-Output 'Completed direct Adapter build with the independently verified existing eight Native components.'
} catch {
    $failure = @{ accepted = $false; status = 'failed'; runDirectory = $run; scratchRoot = $scratch; error = $_.Exception.ToString() }
    Write-Json (Join-Path $work 'legacy-result.json') $failure
    Write-Json $contextPath $failure
    throw
}
