[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuiltRoot,
    [Parameter(Mandatory)][string]$AcceptedSourceManifest,
    [Parameter(Mandatory)][string]$CopyRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$registeredRoot = [IO.Path]::GetFullPath('F:\code\Resource-Manager-Development\ledger-no-demand\real-copy').TrimEnd('\') + '\'
$buildPath = [IO.Path]::GetFullPath($BuiltRoot).TrimEnd('\')
$copyPath = [IO.Path]::GetFullPath($CopyRoot).TrimEnd('\')
if (-not $buildPath.StartsWith($registeredRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $copyPath.StartsWith($registeredRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $copyPath.StartsWith($buildPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Directory]::Exists($copyPath)) { throw 'Build and new copy must be separate children of the registered real-copy task.' }
$sourceManifest = [IO.Path]::GetFullPath($AcceptedSourceManifest)
if (-not [IO.File]::Exists($sourceManifest)) { throw 'An accepted build source-input manifest is required.' }
$accepted = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
if ($accepted.contract -ne 'resource-manager-control-decision-source-inputs-v1') { throw 'Unexpected source-input manifest contract.' }
$inputRows = @($accepted.firstPartySources) + @($accepted.evaluatedCompileAndReferences)
if ($inputRows.Count -lt 3 -or $inputRows.Count -gt 24000) { throw 'Source input manifest exceeds its accepted bounds.' }
foreach ($row in $inputRows) {
    if (-not [IO.Path]::IsPathFullyQualified($row.path) -or -not [IO.File]::Exists($row.path) -or
        ([IO.FileInfo]::new($row.path)).Length -ne $row.length -or
        (Get-FileHash -LiteralPath $row.path -Algorithm SHA256).Hash -ne $row.sha256) {
        throw "Accepted build input changed before copy: $($row.path)"
    }
}
$queue = [Collections.Generic.Queue[object]]::new()
$queue.Enqueue([pscustomobject]@{ Path = $buildPath; Depth = 0 })
$selected = [Collections.Generic.List[object]]::new()
$nodes = 0
$total = 0L
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    $nodes++
    if ($nodes -gt 12000 -or $directory.Depth -gt 16) { throw 'Artifact tree exceeds bounded traversal.' }
    if (([IO.File]::GetAttributes($directory.Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact links are forbidden.' }
    foreach ($file in [IO.Directory]::EnumerateFiles($directory.Path)) {
        $nodes++
        if ($nodes -gt 12000 -or ([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact bounds/link rejection.' }
        $relative = [IO.Path]::GetRelativePath($buildPath, $file)
        if ([IO.Path]::GetExtension($file) -eq '.exe' -or $relative -match '^(wwwroot|Dependencies|GpuPlacementShim)[\\/]') { continue }
        $info = [IO.FileInfo]::new($file)
        $total += $info.Length
        if ($total -gt 2GB) { throw 'Probe image exceeds 2 GiB.' }
        $selected.Add([pscustomobject]@{ Source = $file; Path = $relative; Length = $info.Length; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash })
    }
    foreach ($child in [IO.Directory]::EnumerateDirectories($directory.Path)) {
        $relative = [IO.Path]::GetRelativePath($buildPath, $child)
        if ($relative -match '^(wwwroot|Dependencies|GpuPlacementShim)([\\/]|$)') { continue }
        $queue.Enqueue([pscustomobject]@{ Path = $child; Depth = $directory.Depth + 1 })
    }
}
if ($selected.Count -lt 3) { throw 'No accepted probe image.' }
[void][IO.Directory]::CreateDirectory($copyPath)
$entries = [Collections.Generic.List[object]]::new()
foreach ($entry in $selected) {
    $destination = [IO.Path]::GetFullPath([IO.Path]::Combine($copyPath, $entry.Path))
    if (-not $destination.StartsWith($copyPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination escaped copy root.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($entry.Source, $destination, $false)
    $after = [IO.FileInfo]::new($destination)
    if ($after.Length -ne $entry.Length -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.Sha256 -or
        ([IO.FileInfo]::new($entry.Source)).Length -ne $entry.Length -or (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash -ne $entry.Sha256) {
        throw "Artifact copy identity changed: $($entry.Path)"
    }
    $entries.Add([pscustomobject]@{ path = $entry.Path; length = $entry.Length; sha256 = $entry.Sha256 })
}
$copiedSourceManifest = [IO.Path]::Combine($copyPath, 'source-input-manifest.json')
[IO.File]::Copy($sourceManifest, $copiedSourceManifest, $false)
$sourceHash = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $copiedSourceManifest -Algorithm SHA256).Hash -ne $sourceHash) { throw 'Source manifest copy changed.' }
$manifest = [ordered]@{ contract = 'resource-manager-control-decision-copy-v1'; sourceManifestSha256 = $sourceHash; files = $entries.ToArray(); developmentOverrides = @(
    'Logging uses SimpleConsole only; no Windows EventLog or global log sink',
    'Production effect scope limited to exact own process; cleanup and nonadapted transaction permissions off',
    'Process policy mutators and control write layer reject',
    'Control object catalog reads fail explicitly unavailable before individual writer Probe/Describe',
    'HMP executor excludes every process; reserve stays in private root',
    'IFEO no-op; adapter/GPU actions deny; optional renderer observation unavailable; graphics preference operations disabled',
    'Managed empty fan session; missing window/preparation helper executables',
    'Real session reads; session launches deny; unique trusted invocation pipe',
    'Metric resource driver/OEM providers unavailable; no borrowed control resources; general metric native scratch',
    'Real CPUtopology production provider and Ledger output scratch unchanged'
); omittedProviders = @('AMD SMU/PawnIO', 'AMD SMN/PawnIO', 'Intel MSR/PawnIO', 'LibreHardwareMonitor motherboard/fan/PawnIO', 'OEM EC/ACPI', 'Control object inventory/Probe/Describe') }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ([IO.Path]::Combine($copyPath, 'artifact-manifest.json')) -Encoding utf8
[pscustomobject]@{ CopyRoot = $copyPath; Files = $entries.Count; Bytes = $total; SourceManifestSha256 = $sourceHash }



