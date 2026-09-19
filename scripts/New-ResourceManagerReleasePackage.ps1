[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
    [string]$OutputRoot,
    [switch]$Quiet
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
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
& (Join-Path $PSScriptRoot 'Test-ResourceManagerFinalImage.ps1') -RootDirectory $imageRoot | Out-Null
if ((Get-CleanCommit) -cne $sourceCommit) { throw 'Source changed during publication.' }
New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'Config') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'scripts') | Out-Null
foreach ($name in @('Install.cmd', 'LICENSE', 'NOTICE', 'README.md', 'README.zh-CN.md', 'Restart.cmd', 'Start.cmd')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $stage $name)
}
foreach ($name in @('Register-ResourceManager.ps1', 'ResourceManager.DirectoryRegistration.ps1',
        'ResourceManager.LegacyStartup.ps1', 'Start-ResourceManagerInstalled.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $stage "scripts\$name")
}
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
& (Join-Path $PSScriptRoot 'Test-ResourceManagerReleaseContents.ps1') -RootDirectory $stage
& (Join-Path $PSScriptRoot 'Register-ResourceManager.ps1') -PackageRoot $stage -PlanOnly | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Packaged installer preflight failed.' }
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        length = $_.Length
    }
})
[pscustomobject]@{ sourceCommit = $sourceCommit; version = $Version; files = $files } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding utf8
Write-Step 'Creating the archive and checksum.'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
[pscustomobject]@{ Version = $Version; SourceCommit = $sourceCommit; FileCount = $files.Count
    ZipPath = $zipPath; ZipBytes = (Get-Item -LiteralPath $zipPath).Length; ZipSha256 = $zipHash }
