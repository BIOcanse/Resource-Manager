[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RootDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contract = 'resource-manager-final-image-v1'
$manifestFileName = 'resource-manager-final-image.manifest.json'
$root = [System.IO.Path]::GetFullPath($RootDirectory)
$manifestPath = Join-Path $root $manifestFileName

function Get-TreeFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TreeRoot
    )

    $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    $directories = [System.Collections.Generic.Queue[string]]::new()
    $directories.Enqueue($TreeRoot)
    while ($directories.Count -gt 0) {
        $directory = $directories.Dequeue()
        $directoryInfo = [System.IO.DirectoryInfo]::new($directory)
        if (($directoryInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Final image contains a directory reparse point: $directory"
        }

        foreach ($filePath in [System.IO.Directory]::EnumerateFiles($directory)) {
            $file = [System.IO.FileInfo]::new($filePath)
            if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Final image contains a file reparse point: $filePath"
            }
            $files.Add($file)
        }

        foreach ($child in [System.IO.Directory]::EnumerateDirectories($directory)) {
            $directories.Enqueue($child)
        }
    }
    return $files.ToArray()
}

function Get-RelativePayloadPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FullPath
    )

    $prefix = $root.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $resolved = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Final image file escapes its root: $resolved"
    }
    return $resolved.Substring($prefix.Length).Replace('\', '/')
}

function Assert-CanonicalManifestPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Contains('\') -or
        [System.IO.Path]::IsPathRooted($Path)) {
        throw "Manifest contains a non-canonical payload path: '$Path'"
    }
    $segments = $Path.Split('/')
    if ($segments.Count -eq 0 -or
        @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "Manifest contains a non-canonical payload path: '$Path'"
    }
}

function Get-PayloadDigest {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Entries
    )

    $builder = [System.Text.StringBuilder]::new()
    foreach ($entry in $Entries) {
        [void]$builder.Append([string]$entry.path)
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$entry.length)
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$entry.sha256)
        [void]$builder.Append("`n")
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $hasher.ComputeHash($encoding.GetBytes($builder.ToString()))).Replace('-', '')
    }
    finally {
        $hasher.Dispose()
    }
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read,
        65536,
        [System.IO.FileOptions]::SequentialScan)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $hasher.Dispose()
        $stream.Dispose()
    }
}

if (-not [System.IO.Directory]::Exists($root)) {
    throw "Final image root does not exist: $root"
}
if (-not [System.IO.File]::Exists($manifestPath)) {
    throw "Final image manifest does not exist: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$manifest.contract -cne $contract -or [int]$manifest.schemaVersion -ne 1) {
    throw 'Final image manifest contract or schema version is invalid.'
}
if ([string]$manifest.layout.backendDirectory -cne 'ResourceManager' -or
    [string]$manifest.layout.nativeUiDirectory -cne 'ResourceManagerNativeUi' -or
    [string]$manifest.layout.launcherDirectory -cne 'ResourceManagerLauncher') {
    throw 'Final image manifest layout is invalid.'
}

$manifestEntries = @($manifest.files)
if ($manifestEntries.Count -eq 0) {
    throw 'Final image manifest contains no payload files.'
}
$manifestByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifestEntries) {
    $path = [string]$entry.path
    Assert-CanonicalManifestPath -Path $path
    if ($manifestByPath.ContainsKey($path)) {
        throw "Final image manifest contains a duplicate payload path: $path"
    }
    $manifestByPath.Add($path, $entry)
    if ([long]$entry.length -lt 0 -or [string]$entry.sha256 -notmatch '^[0-9A-F]{64}$') {
        throw "Final image manifest contains invalid metadata for: $path"
    }
    if ($path -match '(?i)\.(pdb|cs|csproj|sln|zig|c|cc|cpp|h|hpp|ps1|cmd)$') {
        throw "Final image contains a forbidden development artifact: $path"
    }
}

$requiredPaths = @(
    'ResourceManager/ResourceManager.exe'
    'ResourceManager/wwwroot/index.html'
    'ResourceManager/GpuPlacementShim/ResourceManager.GpuWindowAction.exe'
    'ResourceManager/GpuPlacementShim/ResourceManager.GpuPlacementPreparation.exe'
    'ResourceManagerNativeUi/ResourceManager.NativeUi.exe'
    'ResourceManagerLauncher/ResourceManager.Launcher.exe'
)
foreach ($requiredPath in $requiredPaths) {
    if (-not $manifestByPath.ContainsKey($requiredPath)) {
        throw "Final image is missing a required payload: $requiredPath"
    }
}

$actualByPath = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-TreeFiles -TreeRoot $root) {
    $relativePath = Get-RelativePayloadPath -FullPath $file.FullName
    if ($relativePath -ceq $manifestFileName) {
        continue
    }
    if ($actualByPath.ContainsKey($relativePath)) {
        throw "Final image contains duplicate case-insensitive payload paths: $relativePath"
    }
    $actualByPath.Add($relativePath, $file)
}

if ($actualByPath.Count -ne $manifestByPath.Count) {
    throw "Final image payload count differs from its manifest: actual=$($actualByPath.Count), manifest=$($manifestByPath.Count)."
}

$verifiedEntries = [System.Collections.Generic.List[object]]::new()
$totalBytes = 0L
foreach ($path in @($manifestByPath.Keys | Sort-Object)) {
    if (-not $actualByPath.ContainsKey($path)) {
        throw "Manifest payload is missing from the final image: $path"
    }
    $entry = $manifestByPath[$path]
    $file = $actualByPath[$path]
    if ($file.Length -ne [long]$entry.length) {
        throw "Final image payload length mismatch: $path"
    }
    $actualSha256 = Get-FileSha256 -Path $file.FullName
    if ($actualSha256 -cne [string]$entry.sha256) {
        throw "Final image payload hash mismatch: $path"
    }
    if ($totalBytes -gt [long]::MaxValue - $file.Length) {
        throw 'Final image payload length exceeds Int64 capacity.'
    }
    $totalBytes += $file.Length
    $verifiedEntries.Add([pscustomobject]@{
        path = $path
        length = $file.Length
        sha256 = $actualSha256
    })
}

$payloadSha256 = Get-PayloadDigest -Entries $verifiedEntries.ToArray()
if ([int]$manifest.payload.fileCount -ne $verifiedEntries.Count -or
    [long]$manifest.payload.totalBytes -ne $totalBytes -or
    [string]$manifest.payload.sha256 -cne $payloadSha256) {
    throw 'Final image aggregate payload identity is invalid.'
}

[pscustomobject]@{
    Contract = $contract
    RootDirectory = $root
    ManifestPath = $manifestPath
    ManifestSha256 = Get-FileSha256 -Path $manifestPath
    PayloadFileCount = $verifiedEntries.Count
    PayloadBytes = $totalBytes
    PayloadSha256 = $payloadSha256
    Valid = $true
} | ConvertTo-Json -Depth 3
