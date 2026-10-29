#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RegisteredWorkspaceRoot,
    [Parameter(Mandatory)][string]$SourceBoundImage,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$BackendProcessId,
    [ValidateRange(0,2147483647)][int]$UiProcessId = 0,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}$')][string]$Label = 'after',
    [ValidateRange(0,3600)][int]$SettleSeconds = 240,
    [ValidateRange(1,3600)][int]$Seconds = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Reuse residency's accepted source/artifact copy-manifest pattern, and the
# existing tray probe. This entry neither starts a product nor contacts a port.
$owner = [IO.Path]::GetFullPath($RegisteredWorkspaceRoot).TrimEnd('\')
$image = [IO.Path]::GetFullPath($SourceBoundImage).TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $owner '.generated-workspace.json')) -or
    -not $image.StartsWith($owner + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A source-bound image under an existing registered workspace is required.'
}
$sourcePath = Join-Path $image 'source-input-manifest.json'
$artifactPath = Join-Path $image 'artifact-manifest.json'
$sources = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json
$artifacts = Get-Content -LiteralPath $artifactPath -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $artifacts.sourceManifestSha256) {
    throw 'The copy does not bind this source-input manifest.'
}
$sourceRows = @($sources.firstPartySources) + @($sources.evaluatedCompileAndReferences)
if ($sourceRows.Count -lt 3 -or $sourceRows.Count -gt 24000 -or
    @($artifacts.files).Count -lt 3 -or @($artifacts.files).Count -gt 12000) { throw 'Manifest bounds rejected.' }
foreach ($row in $sourceRows) {
    if (-not [IO.Path]::IsPathFullyQualified($row.path) -or
        ([IO.FileInfo]::new($row.path)).Length -ne $row.length -or
        (Get-FileHash -LiteralPath $row.path -Algorithm SHA256).Hash -ne $row.sha256) {
        throw "Source input differs from the accepted build: $($row.path)"
    }
}
foreach ($row in $artifacts.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $image $row.path))
    if (-not $file.StartsWith($image + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ([IO.FileInfo]::new($file)).Length -ne $row.length -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $row.sha256) { throw "Copied artifact differs: $($row.path)" }
}
foreach ($processId in @($BackendProcessId, $UiProcessId)) {
    if ($processId -eq 0) { continue }
    $process = Get-Process -Id $processId
    $path = [IO.Path]::GetFullPath($process.Path)
    if (-not $path.StartsWith($image + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Only a process running from this accepted copy may be measured or hidden.' }
    $relative = [IO.Path]::GetRelativePath($image, $path)
    if (@($artifacts.files | Where-Object path -EQ $relative).Count -ne 1) { throw 'The copied process image is absent from its artifact manifest.' }
}
$destination = Join-Path $owner "tray-cpu-$Label"
$arguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Measure-TrayCpu.ps1'),
    '-OutDir', $destination, '-ServiceProcessId', [string]$BackendProcessId, '-UiProcessId', [string]$UiProcessId,
    '-SettleSeconds', [string]$SettleSeconds, '-Seconds', [string]$Seconds)
& (Join-Path $PSScriptRoot 'Invoke-WithMemoryLimit.ps1') -LimitGiB 4 -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[ordered]@{ image = $image; backendPid = $BackendProcessId; uiPid = $UiProcessId;
    sourceManifestSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash;
    artifactManifestSha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'copy-binding.json') -Encoding utf8
