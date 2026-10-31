#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$WorkRoot,
    [string]$ExtractDirectory,
    [ValidateRange(1, 1000000)][int]$MaximumEntries = 100000,
    [ValidateRange(1, [long]::MaxValue)][long]$MaximumExtractedBytes = 8GB
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
Assert-RmRunningWorkspace -Path $WorkRoot
if (-not $ExtractDirectory) { $ExtractDirectory = Join-Path $WorkRoot 'extracted-package' }
$extract = [IO.Path]::GetFullPath($ExtractDirectory)
$prefix = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
if (-not $extract.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ExtractDirectory must be inside WorkRoot.' }
Assert-RmRunningWorkspace -Path $extract
if (Test-Path -LiteralPath $extract) { throw "Extraction destination already exists: $extract" }
. (Join-Path $PSScriptRoot '../ResourceManager.DesktopLayout.ps1')
$layout = Get-ResourceManagerDesktopLayout
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    if ($archive.Entries.Count -gt $MaximumEntries) { throw 'Package entry bound exceeded.' }
    $bytes = 0L
    foreach ($entry in $archive.Entries) {
        if ($entry.Length -gt $MaximumExtractedBytes - $bytes) { throw 'Package extraction size bound exceeded.' }
        $bytes += $entry.Length
    }
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $extract)
} finally { $archive.Dispose() }
$relative = 'Bin/' + $layout.DirectoryName + '/' + $layout.ResidentExecutableName
$resident = Join-Path $extract $relative
if (-not (Test-Path -LiteralPath $resident -PathType Leaf)) { throw "Package omits its resident: $relative" }
$release = Get-Content -LiteralPath (Join-Path $extract 'release-manifest.json') -Raw | ConvertFrom-Json
$member = @($release.files | Where-Object path -CEQ $relative)
if ($member.Count -ne 1 -or $member[0].length -ne ([IO.FileInfo]::new($resident)).Length -or
    $member[0].sha256 -ine (Get-FileHash -LiteralPath $resident -Algorithm SHA256).Hash) { throw 'Release manifest does not identify the resident bytes.' }
$image = Get-Content -LiteralPath (Join-Path $extract 'Bin/resource-manager-final-image.manifest.json') -Raw | ConvertFrom-Json
$imageRelative = $layout.DirectoryName + '/' + $layout.ResidentExecutableName
if ($image.layout.desktopExecutable -cne $imageRelative -or @($image.files | Where-Object path -CEQ $imageRelative).Count -ne 1) {
    throw 'Final image layout/file list omits the resident.'
}
& (Join-Path $PSScriptRoot 'Test-ResourceManagerFinalImage.ps1') -RootDirectory (Join-Path $extract 'Bin') | Out-Null
[pscustomobject]@{ Accepted = $true; ExtractDirectory = $extract; Resident = $relative; Layout = $image.layout.desktopExecutable }
