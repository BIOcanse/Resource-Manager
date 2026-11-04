[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
    [string]$OutputRoot,
    # Existing registered task parent; all stage/build intermediates stay under its lifecycle.
    [Parameter(Mandatory = $true)][string]$WorkRoot,
    [switch]$Quiet,
    [switch]$ReviewBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Import-Module (Join-Path $PSScriptRoot 'ReleaseOutputSettings.psm1') -Force
$outputPaths = Get-ReleaseOutputPaths -RepositoryRoot $repositoryRoot -Version $Version -OutputRoot $OutputRoot
Import-Module (Join-Path $PSScriptRoot 'ReleaseBuildWorkspace.psm1') -Force -DisableNameChecking
$zipPath = Join-Path $outputPaths.GenerationDirectory "ResourceManager-$Version-win-x64.zip"
if ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($WorkRoot)) -ine [IO.Path]::GetPathRoot($zipPath)) { throw 'WorkRoot and release output must share a volume for atomic publication.' }
function Get-CleanCommit {
    $commit = & git -C $repositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the source commit.' }
    $dirty = @(& git -C $repositoryRoot status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Commit source changes before creating a release.' }
    return ([string]$commit).Trim()
}
# Local installation review is a working-tree build, never a clean release claim.
# Hash the real product inputs, including untracked source and deleted files.
function Get-SourceIdentity {
    if (-not $ReviewBuild) {
        return [pscustomobject]@{ Commit = (Get-CleanCommit); State = 'committed'; TreeSha256 = '' }
    }
    $commit = & git -C $repositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the source commit.' }
    $paths = @(& git -C $repositoryRoot ls-files --cached --others --exclude-standard -- `
        src sdk scripts build LICENSE NOTICE README.md README.zh-CN.md .gitignore global.json NuGet.Config)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate product source inputs.' }
    $lines = [Text.StringBuilder]::new()
    foreach ($relative in @($paths | Sort-Object -Unique)) {
        if ($relative.StartsWith('src/Core/wwwroot/', [StringComparison]::Ordinal)) { continue }
        $path = Join-Path $repositoryRoot $relative
        $digest = if (Test-Path -LiteralPath $path -PathType Leaf) {
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        } else { 'deleted' }
        [void]$lines.Append($relative).Append([char]0).Append($digest).Append("`n")
    }
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $treeHash = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($lines.ToString()))).Replace('-', '').ToLowerInvariant()
    } finally { $hasher.Dispose() }
    return [pscustomobject]@{ Commit = ([string]$commit).Trim(); State = 'working-tree'; TreeSha256 = $treeHash }
}
function Write-Step([string]$Message) {
    if (-not $Quiet) { Write-Host "[release] $Message" }
}
$sourceIdentity = Get-SourceIdentity
$sourceCommit = $sourceIdentity.Commit
if ([IO.File]::Exists($zipPath)) {
    if ($ReviewBuild) { throw 'An existing product ZIP cannot be reused for a working-tree review build.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entry = $archive.GetEntry('release-manifest.json')
        if ($null -eq $entry) { throw 'Existing product ZIP has no release manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $existing = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($existing.sourceCommit -cne $sourceCommit -or $existing.version -cne $Version -or $existing.sourceState -cne 'committed') {
            throw 'Existing product ZIP belongs to a different source or version.'
        }
        $expectedEntries = @{}
        foreach ($file in $existing.files) {
            if ($expectedEntries.ContainsKey($file.path)) { throw 'Existing product manifest repeats a file.' }
            $expectedEntries.Add($file.path, $file)
        }
        foreach ($file in $archive.Entries) {
            if ($file.FullName.EndsWith('/') -or $file.FullName -ceq 'release-manifest.json') { continue }
            if (-not $expectedEntries.ContainsKey($file.FullName)) { throw 'Existing product ZIP contains an unlisted file.' }
            $expectedFile = $expectedEntries[$file.FullName]
            if ($file.Length -ne $expectedFile.length) { throw 'Existing product file length differs.' }
            $stream = $file.Open()
            try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($digest -ine $expectedFile.sha256) { throw 'Existing product file SHA256 differs.' }
            $expectedEntries.Remove($file.FullName)
        }
        if ($expectedEntries.Count -gt 0) { throw 'Existing product ZIP is missing files from its manifest.' }
    } finally { $archive.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = "$zipHash  $([IO.Path]::GetFileName($zipPath))"
    if ([IO.File]::Exists("$zipPath.sha256")) {
        if ([IO.File]::ReadAllText("$zipPath.sha256").Trim() -cne $checksum) { throw 'Existing product checksum differs.' }
    } else {
        [void][IO.Directory]::CreateDirectory($WorkRoot)
        $temporaryChecksum = Join-Path $WorkRoot ([guid]::NewGuid().ToString('n') + '.product-checksum.tmp')
        [IO.File]::WriteAllText($temporaryChecksum, "$checksum`n", [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryChecksum, "$zipPath.sha256")
    }
    [pscustomobject]@{ Version = $Version; SourceCommit = $sourceCommit; SourceState = 'committed'; FileCount = @($existing.files).Count
        ZipPath = $zipPath; ZipBytes = (Get-Item -LiteralPath $zipPath).Length; ZipSha256 = $zipHash }
    return
}
if ([IO.File]::Exists("$zipPath.sha256")) { throw 'A product checksum exists without its ZIP.' }
# The caller ledger is borrowed, never retired. Legacy self-owned build markers can be closed after
# JobQueue confirms their previous producer stopped; the formal generation directory is not touched.
if ($env:JOBQUEUE_RESTART_COUNT -and [int]$env:JOBQUEUE_RESTART_COUNT -gt 0) {
    $null = Close-AbandonedReleaseBuildWorkspace -OutputRoot $WorkRoot -Version $Version -Reason "JobQueue previous attempt stopped"
}
$targets = Assert-ReleaseOutputTargets -OutputRoot $WorkRoot -Version $Version
$stage = Join-Path $targets.BuildRoot 'package'
$buildRoot = Join-Path $targets.BuildRoot 'publish'
$buildWorkspace = New-ReleaseBuildWorkspace -OutputRoot $WorkRoot -Version $Version
$buildOutcome = 'failed'
$publicationFailure = $null
try {
    Write-Step 'Publishing into new directories without stopping or changing installed applications.'
    & (Join-Path $PSScriptRoot 'Publish-ResourceManagerRelease.ps1') -Version $Version -OutputRoot $buildRoot
    $imageRoot = Join-Path $buildRoot 'image'
    & (Join-Path $scriptsRoot 'validation\Test-ResourceManagerFinalImage.ps1') -RootDirectory $imageRoot | Out-Null
    $afterBuild = Get-SourceIdentity
    if ($afterBuild.Commit -cne $sourceCommit -or $afterBuild.TreeSha256 -cne $sourceIdentity.TreeSha256) { throw 'Source changed during publication.' }
    New-Item -ItemType Directory -Path $stage | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage 'Config') | Out-Null
    foreach ($name in @('LICENSE', 'NOTICE', 'README.md', 'README.zh-CN.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $stage $name)
    }
    Copy-Item -LiteralPath (Join-Path $buildRoot 'installer\Install.exe') -Destination (Join-Path $stage 'Install.exe')
    Copy-Item -LiteralPath (Join-Path $buildRoot 'uninstaller\UninstallResourceManager.exe') -Destination (Join-Path $stage 'UninstallResourceManager.exe')
    Copy-Item -LiteralPath (Join-Path $buildRoot 'start-entry\Start.exe') -Destination (Join-Path $stage 'Start.exe')
    New-Item -ItemType Directory -Path (Join-Path $stage 'Internal\UpdateManager') | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildRoot 'update-manager\ResourceManager.UpdateManager.exe') `
        -Destination (Join-Path $stage 'Internal\UpdateManager\ResourceManager.UpdateManager.exe')
    # Only tracked README images accompany the documentation, never the research tree.
    $screenshots = @(& git -C $repositoryRoot ls-files -- docs/screenshots)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate README images.' }
    foreach ($relative in $screenshots) {
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $relative) -Destination $destination
    }
    # The image validator rejects reparse points before copying.
    Copy-Item -LiteralPath $imageRoot -Destination (Join-Path $stage 'Bin') -Recurse
    & (Join-Path $PSScriptRoot 'Write-ResourceManagerReleaseRuntimeModules.ps1') -ImageRoot $imageRoot -PackageRoot $stage
    & (Join-Path $scriptsRoot 'validation\Test-ResourceManagerReleaseContents.ps1') -RootDirectory $stage
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            length = $_.Length
        }
    })
    $settingsDefaults = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Core\Application\Settings\AppSettingsDefaults.cs') -Raw
    $settingsMigrator = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Core\Application\Settings\AppSettingsMigrator.cs') -Raw
    $databaseMigrator = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Core\Infrastructure\Persistence\Schema\SqliteSchemaMigrator.cs') -Raw
    $schemaMatch = [regex]::Match($settingsDefaults, 'CurrentVersion\s*=\s*"(\d+\.\d+\.\d+)"')
    $minimumMatch = [regex]::Match($settingsMigrator, 'MinimumSupportedSchemaPatch\s*=\s*(\d+)')
    $databaseMigrations = [regex]::Matches($databaseMigrator, 'new\s+\w+Migration\(\)')
    if (-not $schemaMatch.Success -or -not $minimumMatch.Success -or $databaseMigrations.Count -eq 0) {
        throw 'Cannot derive update compatibility from source schema declarations.'
    }
    $schemaParts = $schemaMatch.Groups[1].Value.Split('.')
    $minimumSchema = "$($schemaParts[0]).$($schemaParts[1]).$($minimumMatch.Groups[1].Value)"
    [pscustomobject]@{
        sourceCommit = $sourceCommit
        sourceState = $sourceIdentity.State
        sourceTreeSha256 = $sourceIdentity.TreeSha256
        version = $Version
        updateCompatibility = [pscustomobject]@{
            minimumSettingsSchema = $minimumSchema
            maximumSettingsSchema = $schemaMatch.Groups[1].Value
            maximumDatabaseSchema = $databaseMigrations.Count
        }
        files = $files
    } |
        ConvertTo-Json -Depth 4 |
        ForEach-Object {
            # The installer's JSON reader rejects a byte order mark, and Windows PowerShell 5.1 writes
            # one for -Encoding utf8, so write UTF-8 without a BOM on either host.
            [IO.File]::WriteAllText((Join-Path $stage 'release-manifest.json'), $_, [Text.UTF8Encoding]::new($false))
        }
    $preflight = Start-Process -FilePath (Join-Path $stage 'Install.exe') -ArgumentList '--plan-only' `
        -WindowStyle Hidden -Wait -PassThru
    try {
        if ($preflight.ExitCode -ne 0) { throw "Packaged EXE installer preflight failed: $($preflight.ExitCode)" }
    }
    finally { $preflight.Dispose() }
    $updatePreflight = Start-Process -FilePath (Join-Path $stage 'Internal\UpdateManager\ResourceManager.UpdateManager.exe') `
        -ArgumentList @('--verify-package', $stage) -WindowStyle Hidden -Wait -PassThru
    try {
        if ($updatePreflight.ExitCode -ne 0) { throw "Packaged updater preflight failed: $($updatePreflight.ExitCode)" }
    }
    finally { $updatePreflight.Dispose() }
    Write-Step 'Creating the archive and checksum.'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [void][IO.Directory]::CreateDirectory($outputPaths.GenerationDirectory)
    $temporaryZip = Join-Path $targets.BuildRoot 'product.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.File]::Move($temporaryZip, $zipPath)
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $temporaryChecksum = Join-Path $targets.BuildRoot 'product.sha256'
    [IO.File]::WriteAllText($temporaryChecksum, "$zipHash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryChecksum, "$zipPath.sha256")
    $packageResult = [pscustomobject]@{ Version = $Version; SourceCommit = $sourceCommit; SourceState = $sourceIdentity.State; FileCount = $files.Count
        ZipPath = $zipPath; ZipBytes = (Get-Item -LiteralPath $zipPath).Length; ZipSha256 = $zipHash }

    $buildOutcome = 'success'
}
catch {
    $publicationFailure = $_
    throw
}
finally {
    try {
        $failureMessage = if ($null -eq $publicationFailure) { '' } else { $publicationFailure.Exception.Message }
        $buildCleanup = Complete-ReleaseBuildWorkspace -Context $buildWorkspace -Outcome $buildOutcome -FailureMessage $failureMessage
        if (-not $buildCleanup.WorkspaceRetired) {
            Write-Warning "Release build temporary workspace remains protected or occupied: $buildRoot. See $($buildWorkspace.Paths.ReceiptPath)."
        }
    }
    catch {
        if ($null -ne $publicationFailure) {
            Write-Warning "Release build cleanup also failed: $($_.Exception.Message)"
        } else { throw }
    }
}
$packageResult
