#requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ScratchRoot,
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$utf8 = [Text.UTF8Encoding]::new($false)
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$task = [IO.Path]::GetFullPath($TaskRoot)
$run = [IO.Path]::GetFullPath($RunDirectory)
$scratch = [IO.Path]::GetFullPath($ScratchRoot)
if ([string]::IsNullOrWhiteSpace($env:RM_REVIEW_SCRATCH) -or
    [IO.Path]::GetFullPath($env:RM_REVIEW_SCRATCH) -ine $scratch) { throw 'Use the bounded ProductSdk Job entry.' }
foreach ($path in @($repo, $task, $run, $scratch)) {
    if (-not [IO.Directory]::Exists($path)) { throw "Required existing directory is absent: $path" }
    $ancestor = [IO.DirectoryInfo]::new($path)
    $ancestorCount = 0
    while ($null -ne $ancestor) {
        if (++$ancestorCount -gt 64 -or ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A product input/output root has an unsafe ancestor: $path"
        }
        $ancestor = $ancestor.Parent
    }
}
$frontend = Join-Path $repo 'src/UI/Frontend'
$repositoryAssets = Join-Path $repo 'src/Core/wwwroot'
$candidate = Join-Path $scratch 'frontend'
if ([IO.Directory]::Exists($candidate) -or [IO.File]::Exists($candidate)) { throw 'Frontend candidate must be a fresh path in the current batch.' }
if ($scratch.StartsWith($repo.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Frontend scratch belongs in the registered companion batch, outside repository source.'
}
foreach ($name in @('package.json', 'package-lock.json', 'node_modules/.package-lock.json', 'vite.config.ts')) {
    if (-not [IO.File]::Exists((Join-Path $frontend $name))) { throw "Missing frontend input: $name" }
}
$nodeCommands = @(Get-Command node.exe -All -CommandType Application -ErrorAction Stop)
$npmCommands = @(Get-Command npm.cmd -All -CommandType Application -ErrorAction Stop)
$toolPairs = foreach ($npmCandidate in $npmCommands) {
    $installationDirectory = [IO.Path]::GetDirectoryName([string]$npmCandidate.Source)
    $pairedNode = $nodeCommands | Where-Object {
        [IO.Path]::GetDirectoryName([string]$_.Source) -ieq $installationDirectory
    } | Select-Object -First 1
    $pairedCli = Join-Path $installationDirectory 'node_modules/npm/bin/npm-cli.js'
    if ($null -ne $pairedNode -and [IO.File]::Exists($pairedCli)) {
        [pscustomobject]@{ node = [string]$pairedNode.Source; npm = [string]$npmCandidate.Source; cli = $pairedCli }
    }
}
$toolPair = $toolPairs | Select-Object -First 1
if ($null -eq $toolPair) { throw 'No single discovered installation supplies both node.exe and npm.cmd with an explicit npm CLI.' }
$node = [string]$toolPair.node
$npmCommand = [string]$toolPair.npm
$npmCli = [string]$toolPair.cli
foreach ($path in @($node, $npmCommand, $npmCli)) {
    if (-not [IO.Path]::IsPathRooted($path) -or -not [IO.File]::Exists($path)) { throw "Actual tool must be one existing absolute file: $path" }
}
$nodeDirectory = [IO.Path]::GetDirectoryName($node)
$steps = [Collections.Generic.List[object]]::new()
$savedEnvironment = @{}
$environmentChanges = @{
    NODE_OPTIONS = '--max-old-space-size=768'
    RAYON_NUM_THREADS = '1'
    ROLLDOWN_WORKER_THREADS = '1'
    ROLLDOWN_MAX_BLOCKING_THREADS = '1'
    NODE_COMPILE_CACHE = (Join-Path $scratch 'frontend-node-cache')
    npm_config_cache = (Join-Path $scratch 'frontend-npm-cache')
    npm_config_update_notifier = 'false'
    npm_config_audit = 'false'
    npm_config_fund = 'false'
    TEMP = (Join-Path $scratch 'frontend-temp')
    TMP = (Join-Path $scratch 'frontend-temp')
    PATH = $nodeDirectory + [IO.Path]::PathSeparator + $env:PATH
}
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 32), $utf8)
}
function File-Evidence([string]$Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    return [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Write-TreeManifest([string]$Root, [string]$Path, [switch]$ExcludeNodeModules) {
    if (-not [IO.Directory]::Exists($Root)) { throw "Manifest root is absent: $Root" }
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push(@{ path = [IO.Path]::GetFullPath($Root); depth = 0 })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = [Collections.Generic.List[string]]::new()
    $nodes = 0
    [long]$totalBytes = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if ($directory.depth -gt 64 -or -not $seen.Add($directory.path)) { throw 'Manifest traversal depth/cycle bound failed.' }
        foreach ($entry in [IO.DirectoryInfo]::new($directory.path).EnumerateFileSystemInfos()) {
            $nodes++
            if ($nodes -gt 100000) { throw 'Frontend manifest exceeds its 100000-entry engineering bound.' }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Manifest refuses a reparse entry: $($entry.FullName)" }
            if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                if ($ExcludeNodeModules -and $entry.Name -ceq 'node_modules') { continue }
                $pending.Push(@{ path = $entry.FullName; depth = $directory.depth + 1 })
            } else {
                $totalBytes += $entry.Length
                if ($totalBytes -gt 2GB) { throw 'Frontend manifest exceeds its 2GiB engineering bound.' }
                $files.Add($entry.FullName)
            }
        }
    }
    $files.Sort([StringComparer]::Ordinal)
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        $item = Get-Item -LiteralPath $file
        $rows.Add([ordered]@{ path = [IO.Path]::GetRelativePath($Root, $file).Replace('\','/'); bytes = $item.Length
            sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash })
    }
    Write-Json $Path ([ordered]@{ schema = 'resource-manager/frontend-files/v1'; root = $Root
        fileCount = $files.Count; totalBytes = $totalBytes; files = $rows.ToArray() })
}
function Invoke-Node([string[]]$Arguments, [string]$Name, [switch]$JsonOutput) {
    $stdout = Join-Path $run ($Name + $(if ($JsonOutput) { '.json' } else { '.stdout.log' }))
    $stderr = Join-Path $run ($Name + '.stderr.log')
    Write-Json (Join-Path $run ($Name + '.command.json')) ([ordered]@{ executable = $node; arguments = $Arguments; workingDirectory = $frontend
        environment = [ordered]@{ NODE_OPTIONS = $env:NODE_OPTIONS; RAYON_NUM_THREADS = $env:RAYON_NUM_THREADS
            ROLLDOWN_WORKER_THREADS = $env:ROLLDOWN_WORKER_THREADS; ROLLDOWN_MAX_BLOCKING_THREADS = $env:ROLLDOWN_MAX_BLOCKING_THREADS } })
    & $node @Arguments 1> $stdout 2> $stderr
    $code = $LASTEXITCODE
    $steps.Add([ordered]@{ name = $Name; exitCode = $code; stdout = $stdout; stderr = $stderr })
    Write-Host ("Frontend {0}: exit {1}; {2}" -f $Name, $code, $stdout)
    if (-not $JsonOutput) { Get-Content -LiteralPath $stdout -Tail 8 | ForEach-Object { Write-Host $_ } }
    if ($code -ne 0) {
        Get-Content -LiteralPath $stderr -Tail 12 | ForEach-Object { Write-Host $_ }
        throw "Frontend command $Name failed with real exit code $code."
    }
    return $stdout
}
function Capture-ToolIdentity([string]$Phase) {
    $nodeVersion = Invoke-Node @('--version') ('node-version-' + $Phase)
    $npmVersion = Invoke-Node @($npmCli, '--version') ('npm-version-' + $Phase)
    $bindingDirectory = Join-Path $frontend 'node_modules/@rolldown/binding-win32-x64-msvc'
    $bindingManifestPath = Join-Path $bindingDirectory 'package.json'
    $bindingManifest = Get-Content -LiteralPath $bindingManifestPath -Raw | ConvertFrom-Json
    $bindingPath = [IO.Path]::GetFullPath((Join-Path $bindingDirectory ([string]$bindingManifest.main)))
    if (-not $bindingPath.StartsWith($bindingDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($bindingPath) -cne '.node') { throw 'The actual Rolldown Native binding must remain in its package.' }
    $toolFiles = @($node, $npmCommand, $npmCli,
        (Join-Path ([IO.Path]::GetDirectoryName($npmCli)) '../package.json'),
        (Join-Path $frontend 'node_modules/.bin/vite.cmd'),
        (Join-Path $frontend 'node_modules/vite/package.json'),
        (Join-Path $frontend 'node_modules/vite/bin/vite.js'),
        (Join-Path $frontend 'node_modules/vite/dist/node/cli.js'),
        (Join-Path $frontend 'node_modules/vite/dist/node/chunks/node.js'),
        (Join-Path $frontend 'node_modules/vite-plugin-solid/package.json'),
        (Join-Path $frontend 'node_modules/vite-plugin-solid/dist/esm/index.mjs'),
        (Join-Path $frontend 'node_modules/rolldown/package.json'), $bindingManifestPath, $bindingPath,
        (Join-Path $frontend 'node_modules/typescript/package.json'),
        (Join-Path $frontend 'node_modules/typescript/lib/tsc.js'))
    $rows = foreach ($file in $toolFiles) { File-Evidence ([IO.Path]::GetFullPath($file)) }
    $identity = Join-Path $run ('frontend-tool-identity-' + $Phase + '.json')
    Write-Json $identity ([ordered]@{ node = $node; npmCommand = $npmCommand; npmCli = $npmCli
        nodeVersion = [IO.File]::ReadAllText($nodeVersion).Trim(); npmVersion = [IO.File]::ReadAllText($npmVersion).Trim()
        rolldownBindingVersion = [string]$bindingManifest.version
        environment = [ordered]@{ NODE_OPTIONS = $env:NODE_OPTIONS; RAYON_NUM_THREADS = $env:RAYON_NUM_THREADS
            ROLLDOWN_WORKER_THREADS = $env:ROLLDOWN_WORKER_THREADS; ROLLDOWN_MAX_BLOCKING_THREADS = $env:ROLLDOWN_MAX_BLOCKING_THREADS }
        files = @($rows) })
    return $identity
}
function Assert-SameManifest([string]$Before, [string]$After, [string]$Description) {
    if ((Get-FileHash -LiteralPath $Before -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $After -Algorithm SHA256).Hash) { throw "$Description changed while producing the candidate." }
}

$dependencyInspector = @'
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const [root, graphFile, output] = process.argv.slice(2);
const read = name => JSON.parse(fs.readFileSync(name, 'utf8').replace(/^\uFEFF/, ''));
const hash = name => crypto.createHash('sha256').update(fs.readFileSync(name)).digest('hex').toUpperCase();
const manifest = read(path.join(root, 'package.json'));
const lock = read(path.join(root, 'package-lock.json'));
const hidden = read(path.join(root, 'node_modules/.package-lock.json'));
const graph = read(graphFile);
if (lock.lockfileVersion !== 3 || hidden.lockfileVersion !== 3 || !lock.packages?.['']) throw Error('Expected actual npm v3 package locks.');
if (manifest.name !== lock.name || manifest.version !== lock.version || graph.name !== manifest.name || graph.version !== manifest.version) throw Error('Frontend root package identities differ.');
const canonical = value => JSON.stringify(Object.entries(value ?? {}).sort(([a], [b]) => a.localeCompare(b)));
for (const field of ['dependencies', 'devDependencies', 'optionalDependencies']) {
  if (canonical(manifest[field]) !== canonical(lock.packages[''][field])) throw Error(`Root lock mismatch: ${field}`);
}
const rows = [];
const allowedExtraneous = new Map();
const keys = Object.keys(hidden.packages ?? {}).sort();
if (!keys.length || keys.length > 30000) throw Error('Installed dependency inventory is empty or exceeds capacity.');
for (const key of keys) {
  if (!key.startsWith('node_modules/') || key.split('/').includes('..')) throw Error(`Invalid installed package path: ${key}`);
  const expected = lock.packages[key];
  const actualLock = hidden.packages[key];
  if (!expected || expected.link || actualLock.link) throw Error(`Unbound/linked installed package: ${key}`);
  for (const field of ['version', 'integrity', 'resolved']) {
    if ((expected[field] ?? null) !== (actualLock[field] ?? null)) throw Error(`Installed lock differs from source lock: ${key}/${field}`);
  }
  const file = path.join(root, key, 'package.json');
  const actual = read(file);
  const packageName = key.slice(key.lastIndexOf('node_modules/') + 'node_modules/'.length);
  if (actual.name !== packageName || actual.version !== expected.version) throw Error(`Installed package identity differs: ${key}`);
  const absolutePath = path.resolve(root, key);
  if (fs.realpathSync(absolutePath).toLowerCase() !== absolutePath.toLowerCase()) throw Error(`Installed package is not at its exact locked location: ${key}`);
  rows.push({path:key, name:actual.name, version:actual.version, integrity:expected.integrity ?? null,
    resolved:expected.resolved ?? null, packageJsonSha256:hash(file), absolutePath,
    sourceOptional:expected.optional === true, installedOptional:actualLock.optional === true});
  if (expected.optional === true && actualLock.optional === true) {
    const diagnostic = `extraneous: ${actual.name}@${actual.version} ${absolutePath}`;
    allowedExtraneous.set(diagnostic, {lockPath:key, absolutePath, name:actual.name, version:actual.version,
      sourceOptional:true, installedOptional:true, integrity:expected.integrity ?? null,
      resolved:expected.resolved ?? null, packageJsonSha256:hash(file)});
  }
}
// npm can retain lock-bound optional nodes whose platform parent is not selected.
// Classify only complete diagnostics derived from that actual installed identity.
const diagnostics = [];
const rejected = [];
if (graph.error) rejected.push({error:graph.error});
if (graph.problems !== undefined && !Array.isArray(graph.problems)) throw Error('Invalid npm diagnostic list.');
for (const diagnostic of graph.problems ?? []) {
  const evidence = typeof diagnostic === 'string' ? allowedExtraneous.get(diagnostic) : undefined;
  diagnostics.push({diagnostic, classification:evidence ? 'locked-optional-extraneous' : 'rejected', evidence:evidence ?? null});
  if (!evidence) rejected.push({diagnostic});
}
fs.writeFileSync(output, JSON.stringify({schema:'resource-manager/frontend-dependencies/v1',
  packageJsonSha256:hash(path.join(root,'package.json')), sourceLockSha256:hash(path.join(root,'package-lock.json')),
  installedLockSha256:hash(path.join(root,'node_modules/.package-lock.json')), graphSha256:hash(graphFile),
  packageCount:rows.length, packages:rows, originalDiagnostics:graph.problems ?? [], diagnostics, rejected,
  accepted:rejected.length === 0},null,2));
if (rejected.length) throw Error(`npm graph has ${rejected.length} unaccepted missing/invalid/error/non-lock diagnostics; see ${output}`);
'@
$inspectorPath = Join-Path $scratch 'inspect-frontend-dependencies.mjs'
$contractsPassed = $false
$result = [ordered]@{ schema = 'resource-manager/frontend-product-inputs/v1'; status = 'failed'; accepted = $false
    taskRoot = $task; runDirectory = $run; scratchRoot = $scratch; frontendDirectory = $candidate; steps = $steps }
$savedLocation = Get-Location
try {
    foreach ($entry in $environmentChanges.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    foreach ($directory in @($env:TEMP, $env:NODE_COMPILE_CACHE, $env:npm_config_cache,
        (Join-Path $scratch 'frontend-vite-cache'), (Join-Path $scratch 'node_modules'))) {
        [void][IO.Directory]::CreateDirectory($directory)
    }
    Set-Location -LiteralPath $frontend
    [IO.File]::WriteAllText($inspectorPath, $dependencyInspector, $utf8)
    $sourceBefore = Join-Path $run 'frontend-inputs-before.json'
    $sourceAfter = Join-Path $run 'frontend-inputs-after.json'
    $assetsBefore = Join-Path $run 'repository-frontend-before.json'
    $assetsAfter = Join-Path $run 'repository-frontend-after.json'
    Write-TreeManifest $frontend $sourceBefore -ExcludeNodeModules
    Write-TreeManifest $repositoryAssets $assetsBefore
    $toolsBefore = Capture-ToolIdentity 'before'
    $graphBefore = Invoke-Node @($npmCli, 'ls', '--all', '--json', '--long') 'npm-ls-before' -JsonOutput
    $dependenciesBefore = Join-Path $run 'frontend-dependencies-before.json'
    [void](Invoke-Node @($inspectorPath, $frontend, $graphBefore, $dependenciesBefore) 'dependency-lock-before')

    # The original config remains the only build definition. Override only cache placement.
    # Its default bundle loader finds scratch/node_modules/.vite-temp, outside repository assets.
    $config = Join-Path $scratch 'frontend-product.vite.config.mjs'
    $originalSpecifier = (Join-Path $frontend 'vite.config.ts').Replace('\','/') | ConvertTo-Json -Compress
    $cacheSpecifier = (Join-Path $scratch 'frontend-vite-cache').Replace('\','/') | ConvertTo-Json -Compress
    $wrapper = "import original from $originalSpecifier;`nexport default async environment => {`n" +
        "const value = await (typeof original === 'function' ? original(environment) : original);`n" +
        "return { ...value, cacheDir: $cacheSpecifier };`n};`n"
    [IO.File]::WriteAllText($config, $wrapper, $utf8)
    [void](Invoke-Node @($npmCli, 'run', 'build', '--', '--outDir', $candidate, '--emptyOutDir', '--config', $config,
        '--configLoader', 'bundle') 'frontend-build')
    if (-not [IO.File]::Exists((Join-Path $candidate 'index.html'))) { throw 'The current-source Vite output has no index.html.' }

    [void](Invoke-Node @($npmCli, 'run', 'typecheck') 'frontend-typecheck')
    [void](Invoke-Node @($npmCli, 'run', 'test:localization-coverage') 'frontend-localization-coverage')
    [void](Invoke-Node @('--conditions=browser', '--experimental-strip-types', 'tests/languageSwitching.test.ts') 'frontend-language-switching')
    [void](Invoke-Node @('--experimental-strip-types', 'tests/settingsLocalizationContract.test.ts') 'frontend-settings-localization')
    [void](Invoke-Node @($npmCli, 'run', 'test:wire') 'frontend-wire')
    [void](Invoke-Node @('--experimental-strip-types', 'tests/monitorSourceDecoders.test.ts') 'frontend-metric-wire')
    [void](Invoke-Node @('--experimental-strip-types', 'tests/resourceMonitorApi.test.ts') 'frontend-monitor-api')
    $contractsPassed = $true

    Write-TreeManifest $frontend $sourceAfter -ExcludeNodeModules
    Write-TreeManifest $repositoryAssets $assetsAfter
    $toolsAfter = Capture-ToolIdentity 'after'
    $graphAfter = Invoke-Node @($npmCli, 'ls', '--all', '--json', '--long') 'npm-ls-after' -JsonOutput
    $dependenciesAfter = Join-Path $run 'frontend-dependencies-after.json'
    [void](Invoke-Node @($inspectorPath, $frontend, $graphAfter, $dependenciesAfter) 'dependency-lock-after')
    Assert-SameManifest $sourceBefore $sourceAfter 'Frontend source/config/lock input'
    Assert-SameManifest $assetsBefore $assetsAfter 'Repository wwwroot asset set'
    Assert-SameManifest $toolsBefore $toolsAfter 'Actual Node/npm/Vite/TypeScript tool identity'
    Assert-SameManifest $dependenciesBefore $dependenciesAfter 'Actual installed dependency graph/lock identity'
    $outputManifest = Join-Path $run 'frontend-output-manifest.json'
    Write-TreeManifest $candidate $outputManifest
    $oldRows = (Get-Content -LiteralPath $assetsBefore -Raw | ConvertFrom-Json).files
    $newRows = (Get-Content -LiteralPath $outputManifest -Raw | ConvertFrom-Json).files
    $oldMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $newMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($row in $oldRows) { $oldMap.Add($row.path, $row) }
    foreach ($row in $newRows) { $newMap.Add($row.path, $row) }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $oldMap.Keys) { [void]$names.Add($name) }
    foreach ($name in $newMap.Keys) { [void]$names.Add($name) }
    $differences = [Collections.Generic.List[object]]::new()
    foreach ($name in @($names | Sort-Object -CaseSensitive)) {
        if (-not $oldMap.ContainsKey($name)) { $differences.Add(@{ kind = 'candidate-only'; path = $name; candidate = $newMap[$name] }) }
        elseif (-not $newMap.ContainsKey($name)) { $differences.Add(@{ kind = 'repository-only'; path = $name; repository = $oldMap[$name] }) }
        elseif ($oldMap[$name].sha256 -cne $newMap[$name].sha256 -or $oldMap[$name].bytes -ne $newMap[$name].bytes) {
            $differences.Add(@{ kind = 'content'; path = $name; repository = $oldMap[$name]; candidate = $newMap[$name] })
        }
    }
    $diffPath = Join-Path $run 'frontend-repository-diff.json'
    $identical = $differences.Count -eq 0
    Write-Json $diffPath ([ordered]@{ schema = 'resource-manager/frontend-asset-diff/v1'; repositoryRoot = $repositoryAssets
        candidateRoot = $candidate; identical = $identical; differenceCount = $differences.Count; differences = $differences.ToArray() })
    $context = [ordered]@{ schema = 'resource-manager/frontend-product-context/v1'; runDirectory = $run; scratchRoot = $scratch
        frontendDirectory = $candidate; sourceInputManifest = $sourceBefore; outputManifest = $outputManifest
        sourceInputManifestSha256 = (Get-FileHash -LiteralPath $sourceBefore -Algorithm SHA256).Hash
        outputManifestSha256 = (Get-FileHash -LiteralPath $outputManifest -Algorithm SHA256).Hash
        repositoryAssetManifest = $assetsBefore; assetDiff = $diffPath; toolIdentity = $toolsBefore
        dependencyIdentity = $dependenciesBefore; buildConfig = (File-Evidence $config)
        dependencyInspector = (File-Evidence $inspectorPath); configLoader = 'bundle'; viteCacheDirectory = (Join-Path $scratch 'frontend-vite-cache')
        repositoryAssetsIdentical = $identical
        contractsPassed = $contractsPassed; accepted = $identical; candidateVerified = $true }
    Write-Json (Join-Path $task 'frontend-product-context.json') $context
    $result.status = 'completed'
    $result.accepted = $identical
    $result['contractsPassed'] = $contractsPassed
    $result['context'] = $context
    Write-Host ("Frontend candidate verified; repository assets identical: {0}; exact differences: {1}." -f $identical, $differences.Count)
    if (-not $identical) { throw 'Rebuilt frontend differs from repository assets; resolve the exact saved difference before product publish.' }
} catch {
    $result['error'] = $_.Exception.Message
    $result['contractsPassed'] = $contractsPassed
    throw
} finally {
    if ($result.status -cne 'completed') {
        try {
            if (Test-Path -LiteralPath (Join-Path $run 'frontend-inputs-before.json')) {
                Write-TreeManifest $frontend (Join-Path $run 'frontend-inputs-after.json') -ExcludeNodeModules
                Write-TreeManifest $repositoryAssets (Join-Path $run 'repository-frontend-after.json')
            }
            if ([IO.Directory]::Exists($candidate)) { Write-TreeManifest $candidate (Join-Path $run 'frontend-output-manifest.json') }
        } catch { $result['failureManifestError'] = $_.Exception.Message }
    }
    Write-Json (Join-Path $run 'frontend-product-inputs-result.json') $result
    Set-Location -LiteralPath $savedLocation.Path
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
}
