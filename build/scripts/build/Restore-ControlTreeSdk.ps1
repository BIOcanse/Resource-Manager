#Requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$package = Join-Path $repository 'build/dependencies/control-tree-sdk'
$pin = Get-Content -LiteralPath (Join-Path $package 'dependency-manifest.json') -Raw | ConvertFrom-Json
function Assert-File([string]$Path, $Row) {
    if ((Get-Item -LiteralPath $Path).Length -ne $Row.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Row.sha256) {
        throw "Control Tree SDK fingerprint mismatch: $Path"
    }
}
$manifestPath = Join-Path $package $pin.manifest.file
Assert-File $manifestPath $pin.manifest
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -cne $pin.version -or $manifest.sourceCommit -cne $pin.sourceCommit) {
    throw 'Control Tree SDK version/source commit mismatch.'
}
$archive = Join-Path $package $pin.archive.file
Assert-File $archive $pin.archive
[void][IO.Directory]::CreateDirectory($Output)
$destination = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
# Inspect every member before extraction; the pinned archive may not escape its output.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    if ($zip.Entries.Count -ne $manifest.files.Count) { throw 'Control Tree SDK archive member count mismatch.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $zip.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
        if (-not $target.StartsWith($destination, [StringComparison]::OrdinalIgnoreCase) -or
            -not $names.Add($entry.FullName)) { throw 'Invalid Control Tree SDK archive member.' }
    }
    foreach ($row in $manifest.files) {
        if (-not $names.Contains($row.path)) { throw "Missing SDK member: $($row.path)" }
    }
} finally { $zip.Dispose() }
Expand-Archive -LiteralPath $archive -DestinationPath $Output -Force
foreach ($row in $manifest.files) { Assert-File (Join-Path $Output $row.path) $row }
