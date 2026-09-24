[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackendDirectory,

    [Parameter(Mandatory = $true)]
    [string]$NativeUiDirectory,

    [Parameter(Mandatory = $true)]
    [string]$LauncherDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$contract = 'resource-manager-final-image-v1'
$manifestFileName = 'resource-manager-final-image.manifest.json'
$backend = [System.IO.Path]::GetFullPath($BackendDirectory)
$nativeUi = [System.IO.Path]::GetFullPath($NativeUiDirectory)
$launcher = [System.IO.Path]::GetFullPath($LauncherDirectory)
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$outputParent = [System.IO.Path]::GetDirectoryName($output)
$outputLeaf = [System.IO.Path]::GetFileName($output)
$validator = Join-Path $PSScriptRoot 'Test-ResourceManagerFinalImage.ps1'

function Test-IsSameOrDescendant {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Candidate,

        [Parameter(Mandatory = $true)]
        [string]$Ancestor
    )

    $candidatePath = [System.IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/')
    $ancestorPath = [System.IO.Path]::GetFullPath($Ancestor).TrimEnd('\', '/')
    return $candidatePath.Equals($ancestorPath, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidatePath.StartsWith(
            $ancestorPath + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Copy-DirectoryTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    $pending = [System.Collections.Generic.Queue[object]]::new()
    $pending.Enqueue([pscustomobject]@{ Source = $Source; Destination = $Destination })
    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        $sourceInfo = [System.IO.DirectoryInfo]::new([string]$item.Source)
        if (($sourceInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Published input contains a directory reparse point: $($sourceInfo.FullName)"
        }
        [void][System.IO.Directory]::CreateDirectory([string]$item.Destination)

        foreach ($filePath in [System.IO.Directory]::EnumerateFiles([string]$item.Source)) {
            $file = [System.IO.FileInfo]::new($filePath)
            if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Published input contains a file reparse point: $filePath"
            }
            $destinationPath = Join-Path ([string]$item.Destination) $file.Name
            [System.IO.File]::Copy($file.FullName, $destinationPath, $false)
            [System.IO.File]::SetLastWriteTimeUtc($destinationPath, $file.LastWriteTimeUtc)
        }

        foreach ($childPath in [System.IO.Directory]::EnumerateDirectories([string]$item.Source)) {
            $child = [System.IO.DirectoryInfo]::new($childPath)
            $pending.Enqueue([pscustomobject]@{
                Source = $child.FullName
                Destination = Join-Path ([string]$item.Destination) $child.Name
            })
        }
    }
}

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
        foreach ($filePath in [System.IO.Directory]::EnumerateFiles($directory)) {
            $files.Add([System.IO.FileInfo]::new($filePath))
        }
        foreach ($child in [System.IO.Directory]::EnumerateDirectories($directory)) {
            $directories.Enqueue($child)
        }
    }
    return $files.ToArray()
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

function Remove-OwnedDirectoryTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedPrefix
    )

    if (-not [System.IO.Directory]::Exists($Path)) {
        return
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $parent = [System.IO.Path]::GetDirectoryName($resolved)
    $leaf = [System.IO.Path]::GetFileName($resolved)
    if (-not $parent.Equals($outputParent, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($ExpectedPrefix, [System.StringComparison]::Ordinal)) {
        throw "Refusing to remove an unowned final-image directory: $resolved"
    }
    [System.IO.Directory]::Delete($resolved, $true)
}

if (-not [System.IO.Directory]::Exists($backend)) {
    throw "Backend publish directory does not exist: $backend"
}
if (-not [System.IO.Directory]::Exists($nativeUi)) {
    throw "Native UI publish directory does not exist: $nativeUi"
}
if (-not [System.IO.Directory]::Exists($launcher)) {
    throw "Launcher publish directory does not exist: $launcher"
}
if (-not [System.IO.File]::Exists($validator)) {
    throw "Final image validator does not exist: $validator"
}
if ([string]::IsNullOrWhiteSpace($outputParent) -or
    $output.Equals([System.IO.Path]::GetPathRoot($output), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Final image output path is unsafe: $output"
}
if ((Test-IsSameOrDescendant -Candidate $output -Ancestor $backend) -or
    (Test-IsSameOrDescendant -Candidate $output -Ancestor $nativeUi) -or
    (Test-IsSameOrDescendant -Candidate $output -Ancestor $launcher) -or
    (Test-IsSameOrDescendant -Candidate $launcher -Ancestor $output) -or
    (Test-IsSameOrDescendant -Candidate $backend -Ancestor $output) -or
    (Test-IsSameOrDescendant -Candidate $nativeUi -Ancestor $output)) {
    throw 'Final image output and published inputs must be disjoint.'
}

$requiredInputs = @(
    (Join-Path $backend 'ResourceManager.exe')
    (Join-Path $backend 'wwwroot\index.html')
    (Join-Path $backend 'GpuPlacementShim\ResourceManager.GpuWindowAction.exe')
    (Join-Path $backend 'GpuPlacementShim\ResourceManager.GpuPlacementPreparation.exe')
    (Join-Path $backend 'GpuPlacementShim\ResourceManager.GpuPlacementExternal.exe')
    (Join-Path $backend 'GpuPlacementShim\ResourceManager.GpuRendererExternal.exe')
    (Join-Path $nativeUi 'ResourceManager.NativeUi.exe')
    (Join-Path $nativeUi 'InstallWebView2Runtime.ps1')
    (Join-Path $launcher 'ResourceManager.Launcher.exe')
)
foreach ($requiredInput in $requiredInputs) {
    if (-not [System.IO.File]::Exists($requiredInput)) {
        throw "Published input is incomplete: $requiredInput"
    }
}

[void][System.IO.Directory]::CreateDirectory($outputParent)
$nonce = [Guid]::NewGuid().ToString('N')
$staging = Join-Path $outputParent "$outputLeaf.staging.$nonce"
$backup = Join-Path $outputParent "$outputLeaf.previous.$nonce"
$lockPath = Join-Path $outputParent ".$outputLeaf.publish.lock"
$lock = [System.IO.FileStream]::new(
    $lockPath,
    [System.IO.FileMode]::OpenOrCreate,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None)
$promoted = $false
$hadPrevious = $false
try {
    [void][System.IO.Directory]::CreateDirectory($staging)
    Copy-DirectoryTree -Source $backend -Destination (Join-Path $staging 'ResourceManager')
    Copy-DirectoryTree -Source $nativeUi -Destination (Join-Path $staging 'ResourceManagerNativeUi')
    Copy-DirectoryTree -Source $launcher -Destination (Join-Path $staging 'ResourceManagerLauncher')

    $prefix = $staging.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $entries = [System.Collections.Generic.List[object]]::new()
    $totalBytes = 0L
    foreach ($file in @(Get-TreeFiles -TreeRoot $staging | Sort-Object FullName)) {
        $relativePath = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        $sha256 = Get-FileSha256 -Path $file.FullName
        $entries.Add([pscustomobject]@{
            path = $relativePath
            length = $file.Length
            sha256 = $sha256
        })
        if ($totalBytes -gt [long]::MaxValue - $file.Length) {
            throw 'Final image payload length exceeds Int64 capacity.'
        }
        $totalBytes += $file.Length
    }
    $orderedEntries = @($entries.ToArray() | Sort-Object path)
    $payloadSha256 = Get-PayloadDigest -Entries $orderedEntries
    $manifest = [ordered]@{
        contract = $contract
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        layout = [ordered]@{
            backendDirectory = 'ResourceManager'
            nativeUiDirectory = 'ResourceManagerNativeUi'
            launcherDirectory = 'ResourceManagerLauncher'
        }
        payload = [ordered]@{
            fileCount = $orderedEntries.Count
            totalBytes = $totalBytes
            sha256 = $payloadSha256
        }
        files = $orderedEntries
    }
    $manifestPath = Join-Path $staging $manifestFileName
    $json = ($manifest | ConvertTo-Json -Depth 6) + "`n"
    $stream = [System.IO.FileStream]::new(
        $manifestPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        4096,
        [System.IO.FileOptions]::WriteThrough)
    try {
        $writer = [System.IO.StreamWriter]::new(
            $stream,
            [System.Text.UTF8Encoding]::new($false),
            4096,
            $true)
        try {
            $writer.Write($json)
            $writer.Flush()
            $stream.Flush($true)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $null = & $validator -RootDirectory $staging
    if ([System.IO.File]::Exists($output)) {
        throw "Final image output is a file, not a directory: $output"
    }
    if ([System.IO.Directory]::Exists($output)) {
        [System.IO.Directory]::Move($output, $backup)
        $hadPrevious = $true
    }
    try {
        [System.IO.Directory]::Move($staging, $output)
        $promoted = $true
        $null = & $validator -RootDirectory $output
    }
    catch {
        if ($promoted -and [System.IO.Directory]::Exists($output)) {
            Remove-OwnedDirectoryTree -Path $output -ExpectedPrefix $outputLeaf
            $promoted = $false
        }
        if ($hadPrevious -and [System.IO.Directory]::Exists($backup)) {
            [System.IO.Directory]::Move($backup, $output)
            $hadPrevious = $false
        }
        throw
    }

    if ($hadPrevious) {
        Remove-OwnedDirectoryTree -Path $backup -ExpectedPrefix "$outputLeaf.previous."
        $hadPrevious = $false
    }

    $finalManifestPath = Join-Path $output $manifestFileName
    [pscustomobject]@{
        Contract = $contract
        OutputDirectory = $output
        ManifestPath = $finalManifestPath
        ManifestSha256 = Get-FileSha256 -Path $finalManifestPath
        PayloadFileCount = $orderedEntries.Count
        PayloadBytes = $totalBytes
        PayloadSha256 = $payloadSha256
        Published = $true
    } | ConvertTo-Json -Depth 3
}
finally {
    if ([System.IO.Directory]::Exists($staging)) {
        Remove-OwnedDirectoryTree -Path $staging -ExpectedPrefix "$outputLeaf.staging."
    }
    if ($hadPrevious -and [System.IO.Directory]::Exists($backup) -and
        -not [System.IO.Directory]::Exists($output)) {
        [System.IO.Directory]::Move($backup, $output)
        $hadPrevious = $false
    }
    $lock.Dispose()
    if ([System.IO.File]::Exists($lockPath)) {
        [System.IO.File]::Delete($lockPath)
    }
}
