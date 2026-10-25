[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ImageRoot,
    [string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$FrontendNativeRoot,
    [string]$DescriptorPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src/UI/Overlay/startup-modules.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$descriptor = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
if ($descriptor.version -ne 1 -or @($descriptor.modules).Count -gt 16) { throw 'Invalid frontend startup module descriptor.' }
function Get-ModuleAssetSha256([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read,
        65536, [IO.FileOptions]::SequentialScan)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
}
function Copy-ModuleAsset([string]$Relative, [string]$Source) {
    if ($Relative -notmatch '^Bin/[^/]+/.+' -or $Relative.Contains('\') -or
        @($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
        [IO.Path]::IsPathRooted($Source) -or $Source.Contains('\') -or
        @($Source.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) { throw 'Invalid frontend module asset path.' }
    $sourcePath = Join-Path $SourceRoot $Source
    $inputRoot = $SourceRoot
    if ($FrontendNativeRoot -and $Source.StartsWith('src/Core/Native/PerformanceOverlay/', [StringComparison]::Ordinal)) {
        $sourcePath = Join-Path $FrontendNativeRoot $Source.Substring('src/Core/Native/PerformanceOverlay/'.Length)
        $inputRoot = $FrontendNativeRoot
    }
    $cursor = [IO.Path]::GetFullPath($sourcePath)
    $depth = 0
    while ($cursor) {
        if (++$depth -gt 128) { throw 'Frontend module input ancestry exceeds limit.' }
        if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse frontend module input: $cursor" }
        if ($cursor -ieq [IO.Path]::GetFullPath($inputRoot)) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    $destination = Join-Path $ImageRoot $Relative.Substring(4)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.File]::Copy($sourcePath, $destination, $false)
    return [ordered]@{ relativePath = $Relative.Substring(4); length = ([IO.FileInfo]::new($destination)).Length; sha256 = Get-ModuleAssetSha256 $destination }
}
$modules = [Collections.Generic.List[object]]::new()
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($module in @($descriptor.modules)) {
    if ([string]::IsNullOrWhiteSpace($module.id) -or -not $ids.Add([string]$module.id) -or $module.kind -notin @('preload', 'vulkan-layer')) { throw 'Invalid or duplicate frontend startup module.' }
    $image = Copy-ModuleAsset $module.image $module.source
    $manifest = $null; $layerName = $null
    if ($module.kind -ceq 'vulkan-layer') {
        $manifest = Copy-ModuleAsset $module.manifest $module.manifestSource
        $layerName = [string]$module.layerName
        if ([string]::IsNullOrWhiteSpace($layerName)) { throw 'Vulkan module has no layer name.' }
    }
    $modules.Add([ordered]@{ id = $module.id; kind = $module.kind; image = $image; manifest = $manifest; layerName = $layerName })
}
[IO.File]::WriteAllText((Join-Path $ImageRoot 'runtime-modules.json'), (([ordered]@{ version = 1; modules = $modules.ToArray() } | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
