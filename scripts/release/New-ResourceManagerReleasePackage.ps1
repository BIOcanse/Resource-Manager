[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
    [string]$OutputRoot,
    [switch]$Quiet
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $scriptsRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot 'artifacts\release' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$stage = Join-Path $OutputRoot "ResourceManager-$Version-win-x64"
$buildRoot = Join-Path $OutputRoot "build-$Version"
$zipPath = "$stage.zip"
foreach ($path in @($stage, $buildRoot, $zipPath, "$zipPath.sha256")) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists; nothing was removed: $path" }
}
function Get-CleanCommit {
    $commit = & git -C $repositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the source commit.' }
    $dirty = @(& git -C $repositoryRoot status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Commit source changes before creating a release.' }
    return ([string]$commit).Trim()
}
function Write-Step([string]$Message) {
    if (-not $Quiet) { Write-Host "[release] $Message" }
}
$sourceCommit = Get-CleanCommit
New-Item -ItemType Directory -Path $buildRoot | Out-Null
Write-Step 'Publishing into new directories without stopping or changing installed applications.'
& (Join-Path $PSScriptRoot 'Publish-ResourceManagerRelease.ps1') -Version $Version -OutputRoot $buildRoot
$imageRoot = Join-Path $buildRoot 'image'
& (Join-Path $scriptsRoot 'validation\Test-ResourceManagerFinalImage.ps1') -RootDirectory $imageRoot | Out-Null
if ((Get-CleanCommit) -cne $sourceCommit) { throw 'Source changed during publication.' }
New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'Config') | Out-Null
foreach ($name in @('LICENSE', 'NOTICE', 'README.md', 'README.zh-CN.md')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $stage $name)
}
Copy-Item -LiteralPath (Join-Path $buildRoot 'installer\Install.exe') -Destination (Join-Path $stage 'Install.exe')
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
    version = $Version
    updateCompatibility = [pscustomobject]@{
        minimumSettingsSchema = $minimumSchema
        maximumSettingsSchema = $schemaMatch.Groups[1].Value
        maximumDatabaseSchema = $databaseMigrations.Count
    }
    files = $files
} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
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
[pscustomobject]@{ Version = $Version; SourceCommit = $sourceCommit; FileCount = $files.Count
    ZipPath = $zipPath; ZipBytes = (Get-Item -LiteralPath $zipPath).Length; ZipSha256 = $zipHash }
