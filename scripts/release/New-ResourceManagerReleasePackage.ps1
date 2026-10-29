[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
    [string]$OutputRoot,
    [switch]$Quiet,
    [switch]$ReviewBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $scriptsRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot 'artifacts\release' }
Import-Module (Join-Path $PSScriptRoot 'ReleaseBuildWorkspace.psm1') -Force -DisableNameChecking
$targets = Assert-ReleaseOutputTargets -OutputRoot $OutputRoot -Version $Version
$OutputRoot = $targets.OutputRoot
$stage = $targets.Stage
$buildRoot = $targets.BuildRoot
$zipPath = $targets.ZipPath
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
        src sdk scripts LICENSE NOTICE README.md README.zh-CN.md .gitignore global.json NuGet.Config)
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
$buildWorkspace = New-ReleaseBuildWorkspace -OutputRoot $OutputRoot -Version $Version
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
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
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
