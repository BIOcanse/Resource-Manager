Set-StrictMode -Version Latest

$script:GeneratedWorkspaceMarkerName = '.generated-workspace.json'
$script:GeneratedWorkspaceMarkerMaxBytes = 1048576
$script:GeneratedWorkspaceLockName = '.generated-workspace-retention.lock'

# Reserve exclusive sharing checks using DELETE + READ_ATTRIBUTES without
# opening every cache payload for data reads. OpenReadDelete supports a caller
# that must verify bytes and mark that same handle for deletion.
if (-not ('ResourceManager.GeneratedWorkspaces.NativeFileGuard' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace ResourceManager.GeneratedWorkspaces {
    public static class NativeFileGuard {
        [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileSizeEx(SafeFileHandle handle, out long length);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileTime(SafeFileHandle handle, IntPtr creation, IntPtr access, out long write);
        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo {
            [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo {
            public uint FileAttributes;
            public uint ReparseTag;
        }
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int fileInformationClass, ref FileDispositionInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int fileInformationClass, out FileAttributeTagInfo info, uint size);
        private static SafeFileHandle OpenWithAccess(string path, uint access, uint share) {
            // Callers have already canonicalized and checked the managed path.
            // Win32 still needs extended syntax for payloads beyond MAX_PATH.
            string nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
                : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2)
                : @"\\?\" + path;
            var handle = CreateFile(nativePath, access, share, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return handle;
        }
        public static SafeFileHandle Open(string path, uint share) {
            return OpenWithAccess(path, 0x00010080, share);
        }
        public static SafeFileHandle OpenReadDelete(string path, uint share) {
            return OpenWithAccess(path, 0x80010080, share);
        }
        public static bool IsReparsePoint(SafeFileHandle handle) {
            FileAttributeTagInfo info;
            if (!GetFileInformationByHandleEx(handle, 9, out info, (uint)Marshal.SizeOf(typeof(FileAttributeTagInfo)))) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return (info.FileAttributes & 0x00000400) != 0;
        }
        public static void Delete(SafeFileHandle handle) {
            var info = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, 4, ref info, (uint)Marshal.SizeOf(typeof(FileDispositionInfo)))) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        public static long Length(SafeFileHandle handle) {
            long length;
            if (!GetFileSizeEx(handle, out length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return length;
        }
        public static long LastWriteTicks(SafeFileHandle handle) {
            long write;
            if (!GetFileTime(handle, IntPtr.Zero, IntPtr.Zero, out write)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return DateTime.FromFileTimeUtc(write).Ticks;
        }
    }
}
'@
}

function Get-GWProperty {
    param([AllowNull()][object]$InputObject, [Parameter(Mandatory)][string]$Name, [AllowNull()][object]$Default = $null)

    if ($null -eq $InputObject) { return $Default }
    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains($Name)) { return $InputObject[$Name] }
        foreach ($key in $InputObject.Keys) {
            if ([string]::Equals([string]$key, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $InputObject[$key]
            }
        }
        return $Default
    }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function ConvertTo-GWFullPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ParameterName)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Path]::IsPathRooted($Path)) {
        throw "$ParameterName must be an absolute path: $Path"
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $volumeRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $volumeRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$ParameterName cannot be a volume root: $fullPath"
    }
    return $fullPath.TrimEnd([char[]]@('\', '/'))
}

function Test-GWPathWithin {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Root, [switch]$AllowRoot)

    $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    if ($AllowRoot -and [string]::Equals($pathFull, $rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    return $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-GWNoReparsePath {
    param([Parameter(Mandatory)][string]$Path, [switch]$RequireLeaf)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $volumeRoot = [System.IO.Path]::GetPathRoot($fullPath)
    $relative = $fullPath.Substring($volumeRoot.Length)
    $current = $volumeRoot
    $visited = 0
    foreach ($segment in $relative.Split([char[]]@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [System.IO.Path]::Combine($current, $segment)
        $visited++
        if ($visited -gt 128) { throw "Path has too many components: $fullPath" }
        # Check every ancestor immediately, including dangling reparse points.
        # Native literal-path attributes avoid two PowerShell provider calls per
        # component for every file in a large completed workspace.
        try { $attributes = [System.IO.File]::GetAttributes($current) }
        catch [System.IO.FileNotFoundException] {
            if ($RequireLeaf -and [string]::Equals($current, $fullPath, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Path does not exist: $current" }
            continue
        }
        catch [System.IO.DirectoryNotFoundException] {
            if ($RequireLeaf -and [string]::Equals($current, $fullPath, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Path does not exist: $current" }
            continue
        }
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Path contains a reparse point: $current"
        }
    }
}

function Get-GWRepositoryRoot {
    return (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
}

function Resolve-GWConfiguredPath {
    param([Parameter(Mandatory)][string]$Path)

    $repositoryRoot = Get-GWRepositoryRoot
    $resolved = $Path.Replace('{RepositoryRoot}', $repositoryRoot)
    return [System.IO.Path]::GetFullPath($resolved)
}

function Get-GeneratedWorkspacePolicy {
    [CmdletBinding()]
    param([string]$Path = (Join-Path $PSScriptRoot 'generated-workspace-policy.json'))

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Retention policy does not exist: $Path" }
    $policy = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([int](Get-GWProperty $policy 'schemaVersion' 0) -ne 1) { throw 'Unsupported generated-workspace policy schema.' }
    if ($null -eq (Get-GWProperty $policy 'defaults')) { throw 'Retention policy has no defaults object.' }
    if (@(Get-GWProperty $policy 'roots' @()).Count -eq 0) { throw 'Retention policy has no managed roots.' }
    return $policy
}

function Get-GWPolicyRoot {
    param([Parameter(Mandatory)][object]$Policy, [Parameter(Mandatory)][string]$RootPath)

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    foreach ($root in @(Get-GWProperty $Policy 'roots' @())) {
        $configuredPath = [string](Get-GWProperty $root 'path' '')
        if ([string]::IsNullOrWhiteSpace($configuredPath)) { continue }
        $configuredFull = ConvertTo-GWFullPath -Path (Resolve-GWConfiguredPath -Path $configuredPath) -ParameterName 'policy root'
        if ([string]::Equals($rootFull, $configuredFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $root }
    }
    throw "RootPath is not listed in the retention policy: $rootFull"
}

function Get-GeneratedWorkspaceRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Policy, [Parameter(Mandatory)][string]$RootId)

    foreach ($root in @(Get-GWProperty $Policy 'roots' @())) {
        if ([string]::Equals([string](Get-GWProperty $root 'id' ''), $RootId, [System.StringComparison]::OrdinalIgnoreCase)) {
            $configuredPath = [string](Get-GWProperty $root 'path' '')
            if ([string]::IsNullOrWhiteSpace($configuredPath)) { throw "Managed root has no path: $RootId" }
            return [pscustomobject]@{
                RootId = $RootId
                Path = Resolve-GWConfiguredPath -Path $configuredPath
                ExcludedNames = @((Get-GWProperty $root 'excludedNames' @()))
            }
        }
    }
    throw "RootId is not listed in the retention policy: $RootId"
}

function Get-GWPolicyLimits {
    param([Parameter(Mandatory)][object]$Policy, [AllowNull()][object]$RootConfig)

    $defaults = Get-GWProperty $Policy 'defaults'
    $recent = [int](Get-GWProperty $RootConfig 'maxRecentCompleted' (Get-GWProperty $defaults 'maxRecentCompleted' 3))
    $maxBytes = [long](Get-GWProperty $RootConfig 'maxBytes' (Get-GWProperty $defaults 'maxBytes' 8589934592))
    $maxNodes = [int](Get-GWProperty $RootConfig 'maxNodes' (Get-GWProperty $defaults 'maxNodes' 750000))
    $maxMilliseconds = [int](Get-GWProperty $RootConfig 'maxMilliseconds' (Get-GWProperty $defaults 'maxMilliseconds' 300000))
    if ($recent -lt 0 -or $maxBytes -lt 0 -or $maxNodes -lt 1 -or $maxMilliseconds -lt 1) {
        throw 'Retention policy limits are outside their valid ranges.'
    }
    return [pscustomobject]@{
        MaxRecentCompleted = $recent
        MaxBytes = $maxBytes
        MaxNodes = $maxNodes
        MaxMilliseconds = $maxMilliseconds
    }
}

function Assert-GWManagedWorkspacePath {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [AllowNull()][object]$RootConfig
    )

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $workspaceFull = ConvertTo-GWFullPath -Path $WorkspacePath -ParameterName 'WorkspacePath'
    if (-not (Test-GWPathWithin -Path $workspaceFull -Root $rootFull)) {
        throw "Workspace path is outside its managed root: $workspaceFull"
    }
    $relative = $workspaceFull.Substring(($rootFull + [System.IO.Path]::DirectorySeparatorChar).Length)
    if ($relative.Contains([System.IO.Path]::DirectorySeparatorChar) -or $relative.Contains([System.IO.Path]::AltDirectorySeparatorChar)) {
        throw "Workspace must be a direct child of its managed root: $workspaceFull"
    }
    $excludedNames = @((Get-GWProperty $RootConfig 'excludedNames' @()) | ForEach-Object { [string]$_ })
    foreach ($excludedName in $excludedNames) {
        if ([string]::Equals($relative, $excludedName, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Workspace name is excluded by policy: $relative"
        }
    }
    return [pscustomobject]@{ RootPath = $rootFull; WorkspacePath = $workspaceFull; Name = $relative }
}

function Get-GWReferenceRootPaths {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots
    )

    $paths = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in (@($RootPath) + @($ReferenceRoots))) {
        if ([string]::IsNullOrWhiteSpace([string]$candidate)) { continue }
        $full = ConvertTo-GWFullPath -Path ([string]$candidate) -ParameterName 'ReferenceRoot'
        if ($seen.Add($full)) { $paths.Add($full) }
    }
    return @($paths.ToArray())
}

function Enter-GWRootLocks {
    param([Parameter(Mandatory)][string[]]$RootPaths)

    $streams = [System.Collections.Generic.List[System.IO.FileStream]]::new()
    try {
        foreach ($rootPath in @($RootPaths | Sort-Object { $_.ToUpperInvariant() })) {
            if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
                throw "Managed or reference root does not exist: $rootPath"
            }
            Assert-GWNoReparsePath -Path $rootPath -RequireLeaf
            $lockPath = Join-Path $rootPath $script:GeneratedWorkspaceLockName
            if (Test-Path -LiteralPath $lockPath) {
                $lockItem = Get-Item -LiteralPath $lockPath -Force
                if (($lockItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    ($lockItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    throw "Retention lock path is unsafe: $lockPath"
                }
            }
            try {
                $stream = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            }
            catch {
                throw "A workspace lifecycle or retention operation already holds the root lock: $rootPath. $($_.Exception.Message)"
            }
            $streams.Add($stream)
        }
        return ,$streams
    }
    catch {
        foreach ($stream in $streams) { $stream.Dispose() }
        throw
    }
}

function Exit-GWRootLocks {
    param([AllowNull()][object]$Streams)
    if ($null -eq $Streams) { return }
    foreach ($stream in $Streams) {
        try { $stream.Dispose() } catch { }
    }
}

function New-GWStateRecord {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$RootId,
        [Parameter(Mandatory)][string]$Purpose,
        [Parameter(Mandatory)][string]$State,
        [AllowEmptyCollection()][AllowNull()][string[]]$References,
        [AllowNull()][string]$CompletedUtc,
        [AllowNull()][string]$Outcome,
        [bool]$Keep = $false,
        [bool]$Adopted = $false
    )

    $referenceValues = [string[]]@()
    if ($null -ne $References) { $referenceValues = [string[]]@($References) }
    return [pscustomobject]@{
        schemaVersion = 1
        workspaceId = [guid]::NewGuid().ToString('D')
        rootId = $RootId
        rootPath = [System.IO.Path]::GetFullPath($RootPath)
        workspacePath = [System.IO.Path]::GetFullPath($WorkspacePath)
        purpose = $Purpose
        state = $State
        createdUtc = [DateTime]::UtcNow.ToString('o')
        completedUtc = $CompletedUtc
        outcome = $Outcome
        keep = $Keep
        adopted = $Adopted
        references = $referenceValues
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }
}

function ConvertTo-GWTimestampText {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime().UtcDateTime.ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime().ToString('o', [Globalization.CultureInfo]::InvariantCulture) }
    return [string]$Value
}

function Copy-GWStateRecord {
    param([Parameter(Mandatory)][object]$State)

    $copy = [pscustomobject]@{
        schemaVersion = [int](Get-GWProperty $State 'schemaVersion' 1)
        workspaceId = [string](Get-GWProperty $State 'workspaceId' '')
        rootId = [string](Get-GWProperty $State 'rootId' '')
        rootPath = [string](Get-GWProperty $State 'rootPath' '')
        workspacePath = [string](Get-GWProperty $State 'workspacePath' '')
        purpose = [string](Get-GWProperty $State 'purpose' '')
        state = [string](Get-GWProperty $State 'state' '')
        createdUtc = ConvertTo-GWTimestampText (Get-GWProperty $State 'createdUtc' '')
        completedUtc = ConvertTo-GWTimestampText (Get-GWProperty $State 'completedUtc' $null)
        outcome = Get-GWProperty $State 'outcome' $null
        keep = [bool](Get-GWProperty $State 'keep' $false)
        adopted = [bool](Get-GWProperty $State 'adopted' $false)
        references = @()
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $rawReferences = Get-GWProperty $State 'references' $null
    if ($null -ne $rawReferences) { $copy.references = [string[]]@($rawReferences) }
    return $copy
}

function ConvertTo-GWReferences {
    param([AllowEmptyCollection()][AllowNull()][string[]]$References)

    $result = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ($null -eq $References) { return @() }
    foreach ($reference in @($References)) {
        if ([string]::IsNullOrWhiteSpace($reference) -or -not [System.IO.Path]::IsPathRooted($reference)) {
            throw "Every workspace reference must be a non-empty absolute path: $reference"
        }
        $full = [System.IO.Path]::GetFullPath($reference)
        if ([string]::Equals($full, [System.IO.Path]::GetPathRoot($full), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "A workspace reference cannot be a volume root: $reference"
        }
        $full = $full.TrimEnd([char[]]@('\', '/'))
        if ($seen.Add($full)) { $result.Add($full) }
    }
    return @($result.ToArray())
}

function New-GWStateRecordPayload {
    param([Parameter(Mandatory)][object]$State)

    $json = ConvertTo-Json -InputObject $State -Depth 12
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine)
    if ($bytes.Length -gt $script:GeneratedWorkspaceMarkerMaxBytes) {
        throw "Lifecycle marker exceeds the 1 MiB limit ($($bytes.Length) bytes): $($State.workspacePath)"
    }
    return [pscustomobject]@{ Bytes = $bytes }
}

function Write-GWStateRecord {
    param([Parameter(Mandatory)][object]$State)

    $workspacePath = [string]$State.workspacePath
    Assert-GWNoReparsePath -Path $workspacePath -RequireLeaf
    $markerPath = Join-Path $workspacePath $script:GeneratedWorkspaceMarkerName
    $payload = New-GWStateRecordPayload -State $State
    $bytes = [byte[]]$payload.Bytes
    $stream = $null
    try {
        $mode = [System.IO.FileMode]::CreateNew
        if ([System.IO.File]::Exists($markerPath)) {
            $markerItem = Get-Item -LiteralPath $markerPath -Force
            if (($markerItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                ($markerItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                throw "Lifecycle marker path is unsafe: $markerPath"
            }
            $mode = [System.IO.FileMode]::Open
        }
        $stream = [System.IO.File]::Open($markerPath, $mode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $stream.SetLength(0)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    catch {
        if ($null -ne $stream) { $stream.Dispose() }
        throw
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Read-GWStateRecord {
    param([Parameter(Mandatory)][string]$RootPath, [Parameter(Mandatory)][string]$WorkspacePath)

    $markerPath = Join-Path $WorkspacePath $script:GeneratedWorkspaceMarkerName
    if (-not [System.IO.File]::Exists($markerPath)) {
        return [pscustomobject]@{ Exists = $false; Valid = $false; State = $null; Reason = 'No lifecycle marker.' }
    }
    try {
        $markerItem = Get-Item -LiteralPath $markerPath -Force
        if (($markerItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($markerItem.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
            throw 'Lifecycle marker is not a regular file.'
        }
        $markerStream = $null
        try {
            $markerStream = [System.IO.File]::Open($markerPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            if ($markerStream.Length -gt [long]$script:GeneratedWorkspaceMarkerMaxBytes) {
                throw "Lifecycle marker exceeds the 1 MiB limit: $markerPath"
            }
            $bufferLength = [int]$script:GeneratedWorkspaceMarkerMaxBytes + 1
            $buffer = [byte[]]::new($bufferLength)
            $bytesRead = 0
            while ($bytesRead -lt $bufferLength) {
                $readCount = $markerStream.Read($buffer, $bytesRead, $bufferLength - $bytesRead)
                if ($readCount -le 0) { break }
                $bytesRead += $readCount
            }
            if ($bytesRead -gt $script:GeneratedWorkspaceMarkerMaxBytes) {
                throw "Lifecycle marker exceeds the 1 MiB limit: $markerPath"
            }
            $markerText = [System.Text.UTF8Encoding]::new($false, $false).GetString($buffer, 0, $bytesRead)
            if ($markerText.Length -gt 0 -and [int]$markerText[0] -eq 0xFEFF) { $markerText = $markerText.Substring(1) }
        }
        finally { if ($null -ne $markerStream) { $markerStream.Dispose() } }
        $state = $markerText | ConvertFrom-Json
        if ([int](Get-GWProperty $state 'schemaVersion' 0) -ne 1) { throw 'Unsupported lifecycle marker schema.' }
        $expectedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd([char[]]@('\', '/'))
        $stateRoot = [string](Get-GWProperty $state 'rootPath' '')
        $stateWorkspace = [string](Get-GWProperty $state 'workspacePath' '')
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($stateRoot).TrimEnd([char[]]@('\', '/')), $expectedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Lifecycle marker root path does not match its containing root.'
        }
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($stateWorkspace).TrimEnd([char[]]@('\', '/')), [System.IO.Path]::GetFullPath($WorkspacePath).TrimEnd([char[]]@('\', '/')), [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Lifecycle marker workspace path does not match its containing directory.'
        }
        $stateName = [string](Get-GWProperty $state 'state' '')
        if ($stateName -notin @('running', 'completed')) { throw "Invalid lifecycle state: $stateName" }
        if ([string]::IsNullOrWhiteSpace([string](Get-GWProperty $state 'workspaceId' '')) -or
            [string]::IsNullOrWhiteSpace([string](Get-GWProperty $state 'rootId' ''))) {
            throw 'Lifecycle marker is missing its workspace or root identity.'
        }
        $rawReferences = Get-GWProperty $state 'references' $null
        $referenceValues = [System.Collections.Generic.List[string]]::new()
        if ($null -ne $rawReferences) {
            foreach ($reference in $rawReferences) {
                if ($null -eq $reference) { throw 'Lifecycle marker contains a null workspace reference.' }
                $referenceValues.Add([string]$reference)
            }
        }
        $normalizedReferences = @(ConvertTo-GWReferences -References ([string[]]$referenceValues.ToArray()))
        $state = Copy-GWStateRecord -State $state
        $state.references = $normalizedReferences
        return [pscustomobject]@{ Exists = $true; Valid = $true; State = $state; Reason = $null }
    }
    catch {
        return [pscustomobject]@{ Exists = $true; Valid = $false; State = $null; Reason = $_.Exception.Message }
    }
}

function Get-GeneratedWorkspaceState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath
    )

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $WorkspacePath -RootConfig $null
    if (-not (Test-Path -LiteralPath $workspace.RootPath -PathType Container) -or
        -not (Test-Path -LiteralPath $workspace.WorkspacePath -PathType Container)) {
        throw 'Managed root and workspace must both exist.'
    }
    Assert-GWNoReparsePath -Path $workspace.RootPath -RequireLeaf
    Assert-GWNoReparsePath -Path $workspace.WorkspacePath -RequireLeaf
    $read = Read-GWStateRecord -RootPath $workspace.RootPath -WorkspacePath $workspace.WorkspacePath
    if (-not $read.Valid) {
        return [pscustomobject]@{
            registered = $false
            path = $workspace.WorkspacePath
            state = 'unknown'
            reason = $read.Reason
        }
    }
    return $read.State
}

function Test-GWPolicyEnabled {
    param([Parameter(Mandatory)][object]$Policy)
    return [bool](Get-GWProperty $Policy 'enabled' $true)
}

function Invoke-GWRetentionAfterLifecycle {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][object]$Policy,
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots
    )
    if (-not (Test-GWPolicyEnabled -Policy $Policy)) {
        return [pscustomobject]@{ enabled = $false; reason = 'Retention is explicitly disabled by policy.' }
    }
    $group = [pscustomobject]@{ RootPath = $RootPath; WorkspacePaths = @() }
    return Invoke-GWRetentionGroups -WorkspaceGroups @($group) -Policy $Policy -ReferenceRoots $ReferenceRoots -Apply -CompletedPool
}

function New-GeneratedWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$Purpose,
        [AllowEmptyCollection()][AllowNull()][string[]]$References = @(),
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )

    if ([string]::IsNullOrWhiteSpace($Purpose)) { throw 'Purpose must be a non-empty description.' }
    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $WorkspacePath -RootConfig $rootConfig
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { throw "Managed root does not exist: $rootFull" }
    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    try {
        Assert-GWNoReparsePath -Path $workspace.RootPath -RequireLeaf
        if (Test-Path -LiteralPath $workspace.WorkspacePath) {
            throw "New refuses an existing workspace. Use Adopt only after verifying its ownership: $($workspace.WorkspacePath)"
        }
        $referencesFull = @(ConvertTo-GWReferences -References $References)
        $state = New-GWStateRecord -RootPath $rootFull -WorkspacePath $workspace.WorkspacePath -RootId ([string](Get-GWProperty $rootConfig 'id' 'managed-root')) -Purpose $Purpose -State 'running' -References $referencesFull -CompletedUtc $null -Outcome $null
        $null = New-GWStateRecordPayload -State $state
        [System.IO.Directory]::CreateDirectory($workspace.WorkspacePath) | Out-Null
        Write-GWStateRecord -State $state
    }
    finally { Exit-GWRootLocks -Streams $locks }

    $retention = Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots
    return [pscustomobject]@{ workspace = $state; retention = $retention }
}

function Get-GWAdoptionRecord {
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][object]$RootConfig,
        [Parameter(Mandatory)][string]$Purpose,
        [AllowEmptyCollection()][AllowNull()][string[]]$References,
        [AllowNull()][string]$CompletedUtc
    )

    if ([string]::IsNullOrWhiteSpace($Purpose)) { throw 'Purpose must be a non-empty description.' }
    Assert-GWNoReparsePath -Path $RootPath -RequireLeaf
    Assert-GWNoReparsePath -Path $WorkspacePath -RequireLeaf
    if (-not [System.IO.Directory]::Exists($RootPath) -or -not [System.IO.Directory]::Exists($WorkspacePath)) {
        throw 'Adopt requires an existing managed root and workspace.'
    }
    $markerPath = Join-Path $WorkspacePath $script:GeneratedWorkspaceMarkerName
    Assert-GWNoReparsePath -Path $markerPath
    try {
        $null = [System.IO.File]::GetAttributes($markerPath)
        throw "Adopt refuses a workspace that already has a lifecycle marker: $WorkspacePath"
    }
    catch [System.IO.FileNotFoundException] { }
    catch [System.IO.DirectoryNotFoundException] { }
    $read = Read-GWStateRecord -RootPath $RootPath -WorkspacePath $WorkspacePath
    if ($read.Exists) { throw "Adopt refuses a workspace that already has a lifecycle marker: $WorkspacePath" }

    $referencesFull = @(ConvertTo-GWReferences -References $References)
    $stateName = 'running'
    $outcome = $null
    if (-not [string]::IsNullOrWhiteSpace($CompletedUtc)) {
        $parsedCompleted = [DateTimeOffset]::Parse($CompletedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        $CompletedUtc = $parsedCompleted.UtcDateTime.ToString('o')
        $stateName = 'completed'
        $outcome = 'adopted-verified'
    }
    $state = New-GWStateRecord -RootPath $RootPath -WorkspacePath $WorkspacePath -RootId ([string](Get-GWProperty $RootConfig 'id' 'managed-root')) -Purpose $Purpose -State $stateName -References $referencesFull -CompletedUtc $CompletedUtc -Outcome $outcome -Adopted $true
    $payload = New-GWStateRecordPayload -State $state
    return [pscustomobject]@{ before = $null; after = $state; payload = [byte[]]$payload.Bytes }
}

function Adopt-GeneratedWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$Purpose,
        [AllowEmptyCollection()][AllowNull()][string[]]$References = @(),
        [AllowNull()][string]$CompletedUtc,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $WorkspacePath -RootConfig $rootConfig
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container) -or -not (Test-Path -LiteralPath $workspace.WorkspacePath -PathType Container)) {
        throw 'Adopt requires an existing managed root and workspace.'
    }
    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    try {
        $record = Get-GWAdoptionRecord -RootPath $rootFull -WorkspacePath $workspace.WorkspacePath -RootConfig $rootConfig -Purpose $Purpose -References $References -CompletedUtc $CompletedUtc
        $state = $record.after
        Write-GWStateRecord -State $state
    }
    finally { Exit-GWRootLocks -Streams $locks }

    $retention = Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots
    return [pscustomobject]@{ workspace = $state; retention = $retention }
}

function Adopt-GeneratedWorkspaceBatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][ValidateCount(1,512)][object[]]$Adoptions,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )

    if (-not (Test-GWPolicyEnabled -Policy $Policy)) { throw 'Batch adoption requires enabled finite retention.' }
    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $null = Get-GWPolicyLimits -Policy $Policy -RootConfig $rootConfig
    $requests = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $missing = [object]::new()
    foreach ($item in $Adoptions) {
        if ($null -eq $item) { throw 'Every batch adoption entry must be an object.' }
        $workspacePath = [string](Get-GWProperty $item 'WorkspacePath' '')
        $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $workspacePath -RootConfig $rootConfig
        if (-not $seen.Add($workspace.WorkspacePath)) { throw 'Duplicate workspace in batch adoption.' }
        $purpose = [string](Get-GWProperty $item 'Purpose' '')
        if ([string]::IsNullOrWhiteSpace($purpose)) { throw 'Every batch adoption requires a non-empty Purpose.' }
        $completedValue = Get-GWProperty $item 'CompletedUtc' $missing
        if ([object]::ReferenceEquals($completedValue,$missing) -or [string]::IsNullOrWhiteSpace([string]$completedValue)) {
            throw 'Every batch adoption requires a verified terminal CompletedUtc value.'
        }
        $completed = [DateTimeOffset]::Parse([string]$completedValue, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime.ToString('o')
        $references = Get-GWProperty $item 'References' $missing
        if ([object]::ReferenceEquals($references,$missing)) { throw 'Every batch adoption requires explicit References.' }
        $referencesFull = @(ConvertTo-GWReferences -References ([string[]]@($references)))
        if (-not [System.IO.Directory]::Exists($rootFull) -or -not [System.IO.Directory]::Exists($workspace.WorkspacePath)) {
            throw 'Batch adoption requires an existing managed root and every workspace.'
        }
        $requests.Add([pscustomobject]@{ path=$workspace.WorkspacePath; purpose=$purpose; completedUtc=$completed; references=$referencesFull })
    }

    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    $plan = [Collections.Generic.List[object]]::new()
    $adopted = [Collections.Generic.List[object]]::new()
    $writeFailure = $null
    $failedRecord = $null
    try {
        [long]$payloadBytes = 0
        # Validate every workspace and marker payload while all reference-root locks are held.
        foreach ($request in $requests) {
            $record = Get-GWAdoptionRecord -RootPath $rootFull -WorkspacePath $request.path -RootConfig $rootConfig -Purpose $request.purpose -References $request.references -CompletedUtc $request.completedUtc
            $payloadBytes += [long]$record.payload.Length
            if ($payloadBytes -gt 8MB) { throw 'Batch marker payloads exceed the 8 MiB bound.' }
            $plan.Add($record)
        }
        foreach ($record in $plan) {
            try { Write-GWStateRecord -State $record.after; $adopted.Add($record.after) }
            catch { $writeFailure=$_; $failedRecord=$record; break }
        }
    }
    finally { Exit-GWRootLocks -Streams $locks }

    # Every write attempt receives one ordinary enabled sweep after the graph locks are released.
    $retention=$null;$retentionFailure=$null
    try { $retention=Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots }
    catch { $retentionFailure=$_ }
    $result=[pscustomobject]@{ adopted=@($adopted.ToArray()); requested=$requests.Count; allAdopted=($null -eq $writeFailure); retention=$retention;
        failedWorkspace=$(if($failedRecord){$failedRecord.after.workspacePath}else{$null});
        remainingWorkspaces=@($plan|Select-Object -Skip $adopted.Count|ForEach-Object {$_.after.workspacePath});
        writeFailureMessage=$(if($writeFailure){$writeFailure.Exception.Message}else{$null});
        retentionFailureMessage=$(if($retentionFailure){$retentionFailure.Exception.Message}else{$null}) }
    if ($writeFailure -or $retentionFailure) {
        $errors=[Collections.Generic.List[Exception]]::new()
        if($writeFailure){$errors.Add($writeFailure.Exception)}
        if($retentionFailure){$errors.Add($retentionFailure.Exception)}
        $exception=[AggregateException]::new('Batch adoption or its required retention sweep failed; inspect adoption progress and retry only verified unmarked remaining paths.',$errors.ToArray())
        $exception.Data['adoption']=$result
        throw $exception
    }
    return $result
}

function Get-GWCompletionRecord {
    param([string]$RootPath,[string]$WorkspacePath,[string]$Outcome,[AllowNull()][string[]]$References,[bool]$ReplaceReferences)
    $read = Read-GWStateRecord -RootPath $RootPath -WorkspacePath $WorkspacePath
    if (-not $read.Valid) { throw "Complete requires a valid registered workspace: $($read.Reason)" }
    if ([string]$read.State.state -ne 'running') { throw "Complete requires running state; current state is $($read.State.state)." }
    $state = Copy-GWStateRecord -State $read.State
    $state.state = 'completed'
    $state.outcome = $Outcome
    $state.completedUtc = [DateTime]::UtcNow.ToString('o')
    if ($ReplaceReferences) { $state.references = @(ConvertTo-GWReferences -References $References) }
    return [pscustomobject]@{ before = $read.State; after = $state }
}

function Complete-GeneratedWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][ValidateSet('success', 'failed')][string]$Outcome,
        [AllowEmptyCollection()][AllowNull()][string[]]$References,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $WorkspacePath -RootConfig $rootConfig
    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    try {
        $record = Get-GWCompletionRecord -RootPath $rootFull -WorkspacePath $workspace.WorkspacePath -Outcome $Outcome -References $References -ReplaceReferences ($PSBoundParameters.ContainsKey('References'))
        $state = $record.after
        Write-GWStateRecord -State $state
    }
    finally { Exit-GWRootLocks -Streams $locks }

    $retention = Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots
    return [pscustomobject]@{ workspace = $state; retention = $retention }
}

function Complete-GeneratedWorkspaceBatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][ValidateCount(1,512)][object[]]$Completions,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )
    if (-not (Test-GWPolicyEnabled -Policy $Policy)) { throw 'Batch completion requires enabled finite retention.' }
    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $null = Get-GWPolicyLimits -Policy $Policy -RootConfig $rootConfig
    $requests = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $missing = [object]::new()
    foreach ($item in $Completions) {
        $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath ([string](Get-GWProperty $item 'WorkspacePath' '')) -RootConfig $rootConfig
        if (-not $seen.Add($workspace.WorkspacePath)) { throw 'Duplicate workspace in batch completion.' }
        $outcome = [string](Get-GWProperty $item 'Outcome' '')
        if ($outcome -cnotin @('success','failed')) { throw 'Batch outcome must be success or failed.' }
        $references = Get-GWProperty $item 'References' $missing
        if ([object]::ReferenceEquals($references,$missing)) { throw 'Batch completion requires explicit References per item.' }
        $requests.Add([pscustomobject]@{ path=$workspace.WorkspacePath; outcome=$outcome; references=@(ConvertTo-GWReferences -References $references) })
    }
    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    $plan = [Collections.Generic.List[object]]::new()
    $completed = [Collections.Generic.List[object]]::new()
    $writeFailure = $null
    $failedRecord = $null
    try {
        [long]$payloadBytes = 0
        # Every path, registered running state, reference and payload is checked
        # under the same locks before the first terminal write.
        foreach ($request in $requests) {
            $record = Get-GWCompletionRecord -RootPath $rootFull -WorkspacePath $request.path -Outcome $request.outcome -References $request.references -ReplaceReferences $true
            $payloadBytes += (New-GWStateRecordPayload -State $record.after).Bytes.Length
            if ($payloadBytes -gt 8MB) { throw 'Batch marker payloads exceed the 8 MiB bound.' }
            $plan.Add($record)
        }
        foreach ($record in $plan) {
            try { Write-GWStateRecord -State $record.after; $completed.Add($record.after) }
            catch { $writeFailure=$_; $failedRecord=$record; break }
        }
    }
    finally { Exit-GWRootLocks -Streams $locks }
    # No deferral/skip option exists. Even a partial write attempt reaches one
    # ordinary enabled sweep; running and invalid markers remain protected.
    $retention=$null;$retentionFailure=$null
    try { $retention=Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots }
    catch { $retentionFailure=$_ }
    $result=[pscustomobject]@{ completed=@($completed.ToArray()); requested=$requests.Count; allCompleted=($null -eq $writeFailure); retention=$retention;
        failedWorkspace=$(if($failedRecord){$failedRecord.after.workspacePath}else{$null});
        failedOriginalState=$(if($failedRecord){$failedRecord.before}else{$null});
        remainingWorkspaces=@($plan|Select-Object -Skip $completed.Count|ForEach-Object {$_.after.workspacePath});
        writeFailureMessage=$(if($writeFailure){$writeFailure.Exception.Message}else{$null});
        retentionFailureMessage=$(if($retentionFailure){$retentionFailure.Exception.Message}else{$null}) }
    if ($writeFailure -or $retentionFailure) {
        $errors=[Collections.Generic.List[Exception]]::new()
        if($writeFailure){$errors.Add($writeFailure.Exception)}
        if($retentionFailure){$errors.Add($retentionFailure.Exception)}
        $exception=[AggregateException]::new('Batch completion or its final retention failed; inspect completion data and retry only verified remaining running paths.',$errors.ToArray())
        $exception.Data['completion']=$result
        throw $exception
    }
    return $result
}

function Set-GeneratedWorkspaceKeep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][bool]$Keep,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $WorkspacePath -RootConfig $rootConfig
    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    try {
        $read = Read-GWStateRecord -RootPath $rootFull -WorkspacePath $workspace.WorkspacePath
        if (-not $read.Valid) { throw "Keep requires a valid registered workspace: $($read.Reason)" }
        $state = Copy-GWStateRecord -State $read.State
        $state.keep = $Keep
        Write-GWStateRecord -State $state
    }
    finally { Exit-GWRootLocks -Streams $locks }

    $retention = Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots
    return [pscustomobject]@{ workspace = $state; retention = $retention }
}

function Set-GeneratedWorkspaceReferences {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [AllowEmptyCollection()][AllowNull()][string[]]$References = @(),
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @()
    )

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
    $workspace = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath $WorkspacePath -RootConfig $rootConfig
    $allRoots = @(Get-GWReferenceRootPaths -RootPath $rootFull -ReferenceRoots $ReferenceRoots)
    $locks = Enter-GWRootLocks -RootPaths $allRoots
    try {
        $read = Read-GWStateRecord -RootPath $rootFull -WorkspacePath $workspace.WorkspacePath
        if (-not $read.Valid) { throw "References requires a valid registered workspace: $($read.Reason)" }
        $state = Copy-GWStateRecord -State $read.State
        $state.references = @(ConvertTo-GWReferences -References $References)
        Write-GWStateRecord -State $state
    }
    finally { Exit-GWRootLocks -Streams $locks }

    $retention = Invoke-GWRetentionAfterLifecycle -RootPath $rootFull -Policy $Policy -ReferenceRoots $allRoots
    return [pscustomobject]@{ workspace = $state; retention = $retention }
}

function New-GWScanBudget {
    param([Parameter(Mandatory)][object]$Limits)
    return [pscustomobject]@{
        Nodes = 0
        MaxNodes = [int]$Limits.MaxNodes
        Timer = [System.Diagnostics.Stopwatch]::StartNew()
        MaxMilliseconds = [int]$Limits.MaxMilliseconds
        Exceeded = $false
    }
}

function Add-GWScanNode {
    param([Parameter(Mandatory)][object]$Budget, [Parameter(Mandatory)][string]$RootPath)
    $Budget.Nodes = [int]$Budget.Nodes + 1
    if ($Budget.Nodes -gt $Budget.MaxNodes -or $Budget.Timer.ElapsedMilliseconds -gt $Budget.MaxMilliseconds) {
        $Budget.Exceeded = $true
        $message = "Retention scan exceeded its bound for $RootPath (nodes=$($Budget.Nodes)/$($Budget.MaxNodes), elapsedMs=$($Budget.Timer.ElapsedMilliseconds)/$($Budget.MaxMilliseconds))."
        $exception = [System.InvalidOperationException]::new($message)
        $exception.Data['GeneratedWorkspaceRetentionBudgetExceeded'] = $true
        throw $exception
    }
}

function Test-GWNestedMarkerReclaimable {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][long]$Length)

    if ($Length -le 0 -or $Length -gt $script:GeneratedWorkspaceMarkerMaxBytes) { return $false }
    try {
        $state = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        return [string]::Equals([string](Get-GWProperty $state 'state' ''), 'completed', [System.StringComparison]::Ordinal) -and
            -not [bool](Get-GWProperty $state 'keep' $false)
    }
    catch { return $false }
}

function Get-GWTreeSnapshot {
    param(
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][object]$Budget,
        [switch]$CaptureDeletionSnapshot
    )

    $bytes = [long]0
    $fileEntries = $null
    $directories = $null
    $reparsePaths = $null
    if ($CaptureDeletionSnapshot) {
        $directories = [System.Collections.Generic.List[string]]::new()
        $reparsePaths = [System.Collections.Generic.List[string]]::new()
    }
    $reparseCount = 0
    # A nested workspace registered by its own root is protected while it is running, kept or unreadable;
    # deleting this tree would delete it with it. Completed nested batches follow their own root's retention.
    $nestedOwnerPath = $null
    $topMarker = [System.IO.Path]::Combine($WorkspacePath, $script:GeneratedWorkspaceMarkerName)
    $complete = $true
    $errorMessage = $null
    if ($CaptureDeletionSnapshot) {
        $fileEntries = [System.Collections.Generic.Dictionary[string, System.Tuple[long, long]]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($WorkspacePath)
    try {
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            $directory = [System.IO.DirectoryInfo]::new($current)
            foreach ($entry in $directory.EnumerateFileSystemInfos()) {
                Add-GWScanNode -Budget $Budget -RootPath $WorkspacePath
                # Enumeration supplies fresh file metadata for this snapshot.
                # The second full snapshot and immediate deletion checks below
                # still detect mutations; avoid querying each file twice more.
                if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $reparseCount++
                    if ($CaptureDeletionSnapshot) { $reparsePaths.Add($entry.FullName) }
                    continue
                }
                if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -ne 0) {
                    if ($CaptureDeletionSnapshot) { $directories.Add($entry.FullName) }
                    $pending.Push($entry.FullName)
                }
                else {
                    $file = [System.IO.FileInfo]$entry
                    if (-not $file.Exists) { throw "File changed during tree scan: $($entry.FullName)" }
                    if ($null -eq $nestedOwnerPath -and
                        [string]::Equals($file.Name, $script:GeneratedWorkspaceMarkerName, [System.StringComparison]::OrdinalIgnoreCase) -and
                        -not [string]::Equals($file.FullName, $topMarker, [System.StringComparison]::OrdinalIgnoreCase) -and
                        -not (Test-GWNestedMarkerReclaimable -Path $file.FullName -Length ([long]$file.Length))) {
                        $nestedOwnerPath = $file.FullName
                    }
                    $bytes += [long]$file.Length
                    if ($CaptureDeletionSnapshot) {
                        $stamp = [System.Tuple[long, long]]::new([long]$file.Length, [long]$file.LastWriteTimeUtc.Ticks)
                        $fileEntries.Add($file.FullName, $stamp)
                    }
                }
            }
        }
    }
    catch {
        $complete = $false
        $errorMessage = $_.Exception.Message
    }
    $directoryValues = @()
    $reparseValues = @()
    $filesValue = @()
    if ($CaptureDeletionSnapshot) {
        $directoryValues = @($directories.ToArray())
        $reparseValues = @($reparsePaths.ToArray())
        $filesValue = $fileEntries.Keys
    }
    return [pscustomobject]@{
        Bytes = $bytes
        Files = $filesValue
        FileEntries = $fileEntries
        Directories = $directoryValues
        ReparsePaths = $reparseValues
        ReparseCount = $reparseCount
        NestedOwnerPath = $nestedOwnerPath
        DeletionSnapshotCaptured = [bool]$CaptureDeletionSnapshot
        Complete = $complete
        Error = $errorMessage
    }
}

# The reasons a tree snapshot makes a workspace ineligible: it could not be fully inspected, it holds a
# reparse point, or it holds a nested workspace that is running, kept or unreadable.
function Add-GWSnapshotBlockedReason {
    param([Parameter(Mandatory)][object]$Item, [Parameter(Mandatory)][object]$Snapshot)

    $reasons = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace([string]$Item.BlockedReason)) { $reasons.Add([string]$Item.BlockedReason) }
    if (-not $Snapshot.Complete) {
        $reasons.Add($(if ($reasons.Count -gt 0) { [string]$Snapshot.Error } else { 'Workspace could not be fully inspected: ' + $Snapshot.Error }))
    }
    if ([int]$Snapshot.ReparseCount -gt 0) {
        $reasons.Add($(if ($reasons.Count -gt 0) { 'reparse point found in workspace.' } else { 'Reparse point found in workspace.' }))
    }
    if ($null -ne $Snapshot.NestedOwnerPath) {
        $reasons.Add('Contains a nested workspace that is running, kept or unreadable: ' + $Snapshot.NestedOwnerPath)
    }
    if ($reasons.Count -gt 0) { $Item.BlockedReason = $reasons -join '; ' }
}

function Test-GWFilesUnoccupied {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$FilePaths,
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][object]$Limits
    )

    $budget = New-GWScanBudget -Limits $Limits
    foreach ($filePath in $FilePaths) {
        try {
            Add-GWScanNode -Budget $budget -RootPath $RootPath
            $stream = [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Open($filePath, 0)
            $stream.Dispose()
        }
        catch {
            return [pscustomobject]@{ Unoccupied = $false; Path = $filePath; Reason = $_.Exception.Message }
        }
    }
    return [pscustomobject]@{ Unoccupied = $true; Path = $null; Reason = $null }
}

function New-GWCatalogItem {
    param([string]$RootPath, [string]$RootId, [string]$WorkspacePath)
    return [pscustomobject]@{
        RootPath = $RootPath
        RootId = $RootId
        Path = $WorkspacePath
        Key = $WorkspacePath.ToUpperInvariant()
        Registered = $false
        State = 'unknown'
        Keep = $false
        References = @()
        CompletedUtc = $null
        CompletionTicks = 0L
        Outcome = $null
        StateRecord = $null
        Snapshot = $null
        Bytes = [long]0
        SizeKnown = $false
        BlockedReason = $null
        GraphValid = $true
    }
}

function Get-GWRootCatalog {
    param([Parameter(Mandatory)][string]$RootPath, [Parameter(Mandatory)][object]$Policy, [switch]$MetadataOnly,
        [AllowNull()][System.Collections.Generic.HashSet[string]]$WorkspaceSelection)

    $rootFull = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'catalog root'
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { throw "Managed/reference root does not exist: $rootFull" }
    Assert-GWNoReparsePath -Path $rootFull -RequireLeaf
    $rootConfig = $null
    try { $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull } catch { }
    $rootId = [string](Get-GWProperty $rootConfig 'id' 'reference-root')
    $limits = Get-GWPolicyLimits -Policy $Policy -RootConfig $rootConfig
    $budget = New-GWScanBudget -Limits $limits
    $items = [System.Collections.Generic.List[object]]::new()
    $issues = [System.Collections.Generic.List[object]]::new()
    $excluded = @((Get-GWProperty $rootConfig 'excludedNames' @()) | ForEach-Object { [string]$_ })
    $catalogComplete = $true
    $graphComplete = $true
    try {
        foreach ($entry in ([System.IO.DirectoryInfo]::new($rootFull)).EnumerateFileSystemInfos()) {
            Add-GWScanNode -Budget $budget -RootPath $rootFull
            $entry.Refresh()
            if (($entry.Attributes -band [System.IO.FileAttributes]::Directory) -eq 0) { continue }
            if ($excluded -contains $entry.Name) { continue }
            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                $item = New-GWCatalogItem -RootPath $rootFull -RootId $rootId -WorkspacePath $entry.FullName
                $item.BlockedReason = 'Workspace root is a reparse point.'
                $items.Add($item)
                continue
            }
            $item = New-GWCatalogItem -RootPath $rootFull -RootId $rootId -WorkspacePath $entry.FullName
            $marker = Read-GWStateRecord -RootPath $rootFull -WorkspacePath $entry.FullName
            if ($marker.Valid) {
                $item.Registered = $true
                $item.State = [string]$marker.State.state
                $item.Keep = [bool]$marker.State.keep
                $item.References = @($marker.State.references)
                $item.StateRecord = $marker.State
                $item.CompletedUtc = $marker.State.completedUtc
                $item.Outcome = $marker.State.outcome
                $recordRootId = [string](Get-GWProperty $marker.State 'rootId' '')
                if ($null -ne $rootConfig -and -not [string]::Equals($recordRootId, $rootId, [System.StringComparison]::OrdinalIgnoreCase)) {
                    # Only the root that registered a workspace may reclaim it.
                    $item.BlockedReason = "Registered under managed root '$recordRootId', not '$rootId'."
                }
                if ($item.State -eq 'completed') {
                    try { $item.CompletionTicks = ([DateTimeOffset]::Parse([string]$item.CompletedUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)).UtcDateTime.Ticks }
                    catch { $item.BlockedReason = 'Completed timestamp is invalid.' }
                }
            }
            elseif ($marker.Exists) {
                $item.BlockedReason = 'Lifecycle marker is invalid: ' + $marker.Reason
                $item.GraphValid = $false
                $graphComplete = $false
                $issues.Add([pscustomobject]@{ rootPath = $rootFull; path = $entry.FullName; reason = $item.BlockedReason })
            }
            else {
                $item.BlockedReason = 'Unknown legacy directory; lifecycle and dependencies have not been verified.'
            }

            $outsideSelection = $null -ne $WorkspaceSelection -and -not $WorkspaceSelection.Contains($entry.FullName)
            if ($outsideSelection -and -not $MetadataOnly) {
                # Retain every non-selected owner as a graph seed, including its
                # outgoing references. Only byte accounting is omitted here.
                if (-not $item.BlockedReason) { $item.BlockedReason = 'Outside the explicitly requested cleanup selection.' }
            }
            if ($MetadataOnly -or $outsideSelection) {
                $item.Snapshot = $null
                $item.Bytes = [long]0
                $item.SizeKnown = $false
                $items.Add($item)
                continue
            }

            $captureDeletionSnapshot = $item.Registered -and $item.State -eq 'completed' -and -not $item.Keep -and $null -eq $item.BlockedReason
            $snapshot = Get-GWTreeSnapshot -WorkspacePath $entry.FullName -Budget $budget -CaptureDeletionSnapshot:$captureDeletionSnapshot
            $item.Snapshot = $snapshot
            $item.Bytes = [long]$snapshot.Bytes
            $item.SizeKnown = [bool]$snapshot.Complete -and [int]$snapshot.ReparseCount -eq 0
            Add-GWSnapshotBlockedReason -Item $item -Snapshot $snapshot
            if (-not $snapshot.Complete -and $budget.Exceeded) { $catalogComplete = $false }
            if ($item.Registered -and $item.State -eq 'completed' -and -not $item.Keep -and $null -eq $item.BlockedReason -and $item.SizeKnown) {
                $occupancy = Test-GWFilesUnoccupied -FilePaths @($snapshot.Files) -RootPath $entry.FullName -Limits $limits
                if (-not $occupancy.Unoccupied) { $item.BlockedReason = 'Workspace is locked or a file cannot be checked: ' + $occupancy.Path + ' (' + $occupancy.Reason + ')' }
            }
            $items.Add($item)
            if ($budget.Exceeded) { $catalogComplete = $false; break }
        }
    }
    catch {
        $catalogComplete = $false
        $issues.Add([pscustomobject]@{ rootPath = $rootFull; path = $rootFull; reason = $_.Exception.Message })
    }

    $totalBytes = [long]0
    foreach ($item in $items) { $totalBytes += [long]$item.Bytes }
    return [pscustomobject]@{
        RootPath = $rootFull
        RootConfig = $rootConfig
        Limits = $limits
        Items = @($items.ToArray())
        Complete = $catalogComplete
        GraphComplete = $graphComplete
        AllSizesKnown = (@($items | Where-Object { -not $_.SizeKnown }).Count -eq 0)
        TotalBytes = $totalBytes
        NodeCount = $budget.Nodes
        ElapsedMilliseconds = [long]$budget.Timer.ElapsedMilliseconds
        Issues = @($issues.ToArray())
    }
}

function Get-GWItemOwner {
    param(
        [Parameter(Mandatory)][string]$ReferencePath,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        [System.Collections.Generic.Dictionary[string,object]]$ItemIndex,
        [Parameter(Mandatory)][object]$Budget,
        [Parameter(Mandatory)][string]$RootPath
    )

    $referenceFull = [System.IO.Path]::GetFullPath($ReferencePath).TrimEnd([char[]]@('\', '/'))
    if ($null -eq $ItemIndex) {
        $ItemIndex = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($item in $Items) {
            Add-GWScanNode -Budget $Budget -RootPath $RootPath
            if (-not $ItemIndex.ContainsKey($item.Path)) { $ItemIndex.Add($item.Path, $item) }
        }
    }
    # Catalogue paths are normalized. Walking ancestors yields the same most
    # specific owner without scanning every unrelated workspace per reference.
    while (-not [string]::IsNullOrWhiteSpace($referenceFull)) {
        Add-GWScanNode -Budget $Budget -RootPath $RootPath
        $owner = $null
        if ($ItemIndex.TryGetValue($referenceFull, [ref]$owner)) { return $owner }
        $referenceFull = [System.IO.Path]::GetDirectoryName($referenceFull)
    }
    return $null
}

function Get-GWReferenceClosure {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$SeedItems,
        [Parameter(Mandatory)][object]$Budget,
        [string]$RootPath
    )

    if ([string]::IsNullOrWhiteSpace($RootPath)) {
        if ($Items.Count -gt 0) { $RootPath = [string]$Items[0].RootPath }
        else { $RootPath = 'reference graph' }
    }
    $protected = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $itemIndex = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Items) {
        Add-GWScanNode -Budget $Budget -RootPath $RootPath
        # A nested root catalogue may contain the same workspace; preserve the
        # first item, matching the previous longest-path scan's tie behavior.
        if (-not $itemIndex.ContainsKey($item.Path)) { $itemIndex.Add($item.Path, $item) }
    }
    $ownerCache = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $referenced = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($seed in $SeedItems) {
        Add-GWScanNode -Budget $Budget -RootPath $RootPath
        if ($null -ne $seed -and $protected.Add($seed.Key)) { $queue.Enqueue($seed) }
    }
    $protectors = @{}
    while ($queue.Count -gt 0) {
        $source = $queue.Dequeue()
        foreach ($reference in @($source.References)) {
            Add-GWScanNode -Budget $Budget -RootPath $RootPath
            $referencePath = [string]$reference
            $target = $null
            if (-not $ownerCache.TryGetValue($referencePath, [ref]$target)) {
                $target = Get-GWItemOwner -ReferencePath $referencePath -Items $Items -ItemIndex $itemIndex -Budget $Budget -RootPath $RootPath
                $ownerCache.Add($referencePath, $target)
            }
            if ($null -eq $target) { continue }
            if ([string]::Equals($source.Key, $target.Key, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($referenced.Add($target.Key)) { $protectors[$target.Key] = $source.Path }
            if ($protected.Add($target.Key)) { $queue.Enqueue($target) }
        }
    }
    return [pscustomobject]@{ ProtectedKeys = $protected; ReferencedKeys = $referenced; Protectors = $protectors }
}

function Test-GWItemHasPermanentProtection {
    param([Parameter(Mandatory)][object]$Item)

    if (-not $Item.Registered) { return $true }
    if ($Item.State -eq 'running') { return $true }
    if ($Item.Keep) { return $true }
    if (-not [string]::IsNullOrWhiteSpace([string]$Item.BlockedReason)) { return $true }
    return $false
}

function Get-GWRecentItems {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        [Parameter(Mandatory)][int]$Count,
        [Parameter(Mandatory)][object]$Budget,
        [Parameter(Mandatory)][string]$RootPath
    )

    if ($Count -le 0) { return @() }
    $completed = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $Items) {
        Add-GWScanNode -Budget $Budget -RootPath $RootPath
        if ($item.Registered -and $item.State -eq 'completed' -and $item.CompletionTicks -gt 0) { $completed.Add($item) }
    }
    $sorted = @($completed.ToArray() | Sort-Object -Property @{ Expression = 'CompletionTicks'; Descending = $true }, @{ Expression = { $_.Path.ToUpperInvariant() }; Descending = $false })
    Add-GWScanNode -Budget $Budget -RootPath $RootPath
    return @($sorted | Select-Object -First $Count)
}

function Get-GWCurrentSurvivors {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$BaseProtected,
        [Parameter(Mandatory)][string]$TargetRootPath,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]]$PlannedKeys,
        [Parameter(Mandatory)][object]$Budget,
        [Parameter(Mandatory)][string]$RootPath
    )

    $survivors = [System.Collections.Generic.List[object]]::new()
    $seedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $BaseProtected) {
        Add-GWScanNode -Budget $Budget -RootPath $RootPath
        $null = $seedKeys.Add($item.Key)
    }
    foreach ($item in $Items) {
        Add-GWScanNode -Budget $Budget -RootPath $RootPath
        if ($PlannedKeys.Contains($item.Key)) { continue }
        if ([string]::Equals($item.RootPath, $TargetRootPath, [System.StringComparison]::OrdinalIgnoreCase) -or $seedKeys.Contains($item.Key)) {
            $survivors.Add($item)
        }
    }
    return @($survivors.ToArray())
}

function New-GWRetentionPlan {
    param([Parameter(Mandatory)][object]$TargetCatalog, [Parameter(Mandatory)][object[]]$Catalogs)

    $budget = New-GWScanBudget -Limits $TargetCatalog.Limits
    $allItemsList = [System.Collections.Generic.List[object]]::new()
    foreach ($catalog in $Catalogs) {
        foreach ($item in $catalog.Items) {
            Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
            $allItemsList.Add($item)
        }
    }
    $allItems = @($allItemsList.ToArray())
    $targetItems = $TargetCatalog.Items
    $planned = [System.Collections.Generic.List[object]]::new()
    $plannedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $permanent = [System.Collections.Generic.List[object]]::new()
    $recentByCatalog = @{}
    foreach ($catalog in $Catalogs) {
        foreach ($item in $catalog.Items) {
            Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
            if (Test-GWItemHasPermanentProtection -Item $item) { $permanent.Add($item) }
        }
        $limit = [int]$catalog.Limits.MaxRecentCompleted
        $recentByCatalog[$catalog.RootPath.ToUpperInvariant()] = @(Get-GWRecentItems -Items $catalog.Items -Count $limit -Budget $budget -RootPath $TargetCatalog.RootPath)
    }
    $recentAllList = [System.Collections.Generic.List[object]]::new()
    foreach ($recentGroup in $recentByCatalog.Values) {
        foreach ($item in $recentGroup) {
            Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
            $recentAllList.Add($item)
        }
    }
    $recentAll = @($recentAllList.ToArray())
    $nonTargetList = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $allItems) {
        Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
        if (-not [string]::Equals($item.RootPath, $TargetCatalog.RootPath, [System.StringComparison]::OrdinalIgnoreCase)) { $nonTargetList.Add($item) }
    }
    $nonTargetItems = @($nonTargetList.ToArray())
    $baseSeeds = @($permanent.ToArray()) + @($recentAll) + @($nonTargetItems)
    $initialClosure = Get-GWReferenceClosure -Items $allItems -SeedItems $baseSeeds -Budget $budget -RootPath $TargetCatalog.RootPath

    $targetRecent = @($recentByCatalog[$TargetCatalog.RootPath.ToUpperInvariant()])
    $targetRecentKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $targetRecent) {
        Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
        $null = $targetRecentKeys.Add($item.Key)
    }
    foreach ($item in $targetItems) {
        Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
        if (-not $item.Registered -or $item.State -ne 'completed') { continue }
        if (Test-GWItemHasPermanentProtection -Item $item) { continue }
        if ($targetRecentKeys.Contains($item.Key) -or $initialClosure.ReferencedKeys.Contains($item.Key)) { continue }
        $planned.Add([pscustomobject]@{ Item = $item; Reason = 'Older than the configured completed-workspace count.' })
        $null = $plannedKeys.Add($item.Key)
    }

    $sizeBudgetKnown = [bool]$TargetCatalog.AllSizesKnown
    $remainingBytes = [long]$TargetCatalog.TotalBytes
    foreach ($entry in $planned) {
        Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
        $remainingBytes -= [long]$entry.Item.Bytes
    }
    $targetMaxBytes = [long]$TargetCatalog.Limits.MaxBytes
    if ($sizeBudgetKnown -and $remainingBytes -gt $targetMaxBytes) {
        # These seeds cannot be evicted by this root's plan. Their dependency
        # closure therefore protects the same candidates in every size pass.
        # Target-root recent items are deliberately excluded: the size budget
        # may still evict them after the original per-candidate closure check.
        $nonEvictableClosure = Get-GWReferenceClosure -Items $allItems -SeedItems (@($permanent.ToArray()) + @($nonTargetItems)) -Budget $budget -RootPath $TargetCatalog.RootPath
        while ($remainingBytes -gt $targetMaxBytes) {
            Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
            $survivors = @(Get-GWCurrentSurvivors -Items $allItems -BaseProtected $baseSeeds -TargetRootPath $TargetCatalog.RootPath -PlannedKeys $plannedKeys -Budget $budget -RootPath $TargetCatalog.RootPath)
            $eligibleList = [System.Collections.Generic.List[object]]::new()
            foreach ($item in $targetItems) {
                Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
                if ($item.Registered -and $item.State -eq 'completed' -and -not $plannedKeys.Contains($item.Key) -and
                    -not (Test-GWItemHasPermanentProtection -Item $item) -and $item.SizeKnown -and
                    -not $nonEvictableClosure.ProtectedKeys.Contains($item.Key)) {
                    $eligibleList.Add($item)
                }
            }
            $eligible = @($eligibleList.ToArray() | Sort-Object -Property @{ Expression = 'CompletionTicks'; Descending = $false }, @{ Expression = { $_.Path.ToUpperInvariant() }; Descending = $false })
            Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
            $candidate = $null
            foreach ($proposed in $eligible) {
                Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
                $candidateSeedsList = [System.Collections.Generic.List[object]]::new()
                foreach ($survivor in $survivors) {
                    Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
                    if (-not [string]::Equals($survivor.Key, $proposed.Key, [System.StringComparison]::OrdinalIgnoreCase)) { $candidateSeedsList.Add($survivor) }
                }
                $candidateSeeds = @($candidateSeedsList.ToArray())
                $closure = Get-GWReferenceClosure -Items $allItems -SeedItems $candidateSeeds -Budget $budget -RootPath $TargetCatalog.RootPath
                if (-not $closure.ReferencedKeys.Contains($proposed.Key)) { $candidate = $proposed; break }
            }
            if ($null -eq $candidate) { break }
            $planned.Add([pscustomobject]@{ Item = $candidate; Reason = 'Root size budget requires evicting this completed workspace.' })
            $null = $plannedKeys.Add($candidate.Key)
            $remainingBytes -= [long]$candidate.Bytes
        }
    }

    $survivorsAfterPlan = @(Get-GWCurrentSurvivors -Items $allItems -BaseProtected $baseSeeds -TargetRootPath $TargetCatalog.RootPath -PlannedKeys $plannedKeys -Budget $budget -RootPath $TargetCatalog.RootPath)
    $finalClosure = Get-GWReferenceClosure -Items $allItems -SeedItems $survivorsAfterPlan -Budget $budget -RootPath $TargetCatalog.RootPath
    $retained = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $targetItems) {
        Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
        if ($plannedKeys.Contains($item.Key)) { continue }
        if (Test-GWItemHasPermanentProtection -Item $item) {
            $reason = [string]$item.BlockedReason
            if ([string]::IsNullOrWhiteSpace($reason)) {
                if (-not $item.Registered) { $reason = 'Unknown legacy workspace.' }
                elseif ($item.State -eq 'running') { $reason = 'Workspace is running.' }
                elseif ($item.Keep) { $reason = 'Workspace is marked Keep.' }
                else { $reason = 'Workspace is protected.' }
            }
            $retained.Add([pscustomobject]@{ path = $item.Path; reason = $reason })
        }
        elseif ($finalClosure.ReferencedKeys.Contains($item.Key)) {
            $protector = $finalClosure.Protectors[$item.Key]
            if ([string]::IsNullOrWhiteSpace([string]$protector)) { $protector = 'recent or retained workspace' }
            $retained.Add([pscustomobject]@{ path = $item.Path; reason = "Referenced by a surviving workspace: $protector" })
        }
        else {
            $retained.Add([pscustomobject]@{ path = $item.Path; reason = 'Within the configured recent-workspace and size limits.' })
        }
    }
    $plannedSummaryList = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $planned) {
        Add-GWScanNode -Budget $budget -RootPath $TargetCatalog.RootPath
        $plannedSummaryList.Add([pscustomobject]@{ path = $entry.Item.Path; bytes = [long]$entry.Item.Bytes; reason = $entry.Reason })
    }
    $plannedSummary = @($plannedSummaryList.ToArray())
    $overBudget = (-not $sizeBudgetKnown) -or ($remainingBytes -gt $targetMaxBytes)
    $overBudgetReason = $null
    if (-not $sizeBudgetKnown) { $overBudgetReason = 'At least one workspace size could not be fully inspected; size-based eviction was skipped.' }
    elseif ($remainingBytes -gt $targetMaxBytes) { $overBudgetReason = 'Protected or otherwise ineligible workspaces keep the root above its configured size limit.' }

    return [pscustomobject]@{
        AllItems = $allItems
        Planned = @($planned.ToArray())
        PlannedSummary = $plannedSummary
        Retained = @($retained.ToArray())
        PlannedKeys = $plannedKeys
        TotalBytes = [long]$TargetCatalog.TotalBytes
        ProjectedBytes = [long]$remainingBytes
        SizeBudgetKnown = $sizeBudgetKnown
        MaxBytes = $targetMaxBytes
        OverBudget = $overBudget
        OverBudgetReason = $overBudgetReason
        InitialClosure = $initialClosure
        FinalClosure = $finalClosure
    }
}

function Remove-GWWorkspaceSnapshot {
    param([Parameter(Mandatory)][object]$Item, [Parameter(Mandatory)][object]$Limits)

    $workspacePath = [System.IO.Path]::GetFullPath($Item.Path)
    $rootPath = [System.IO.Path]::GetFullPath($Item.RootPath)
    if (-not (Test-GWPathWithin -Path $workspacePath -Root $rootPath)) { throw "Workspace escaped its root before deletion: $workspacePath" }
    Assert-GWNoReparsePath -Path $workspacePath -RequireLeaf
    $read = Read-GWStateRecord -RootPath $rootPath -WorkspacePath $workspacePath
    if (-not $read.Valid -or [string]$read.State.state -ne 'completed' -or [bool]$read.State.keep -or
        -not [string]::Equals([string]$read.State.workspaceId, [string]$Item.StateRecord.workspaceId, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Workspace lifecycle state changed after retention planning.'
    }

    $budget = New-GWScanBudget -Limits $Limits
    $current = Get-GWTreeSnapshot -WorkspacePath $workspacePath -Budget $budget -CaptureDeletionSnapshot
    if (-not $current.Complete -or [int]$current.ReparseCount -gt 0) { throw 'Workspace changed or became unsafe after retention planning.' }
    if (-not $Item.Snapshot.DeletionSnapshotCaptured -or -not $current.DeletionSnapshotCaptured) {
        throw 'Workspace deletion snapshot is unavailable.'
    }
    $plannedDirectories = @($Item.Snapshot.Directories | Sort-Object -CaseSensitive)
    $currentDirectories = @($current.Directories | Sort-Object -CaseSensitive)
    if ($Item.Snapshot.FileEntries.Count -ne $current.FileEntries.Count -or ($plannedDirectories -join "`n") -cne ($currentDirectories -join "`n")) {
        throw 'Workspace contents changed after retention planning.'
    }
    $plannedFileMap = $Item.Snapshot.FileEntries
    $currentFileMap = $current.FileEntries
    foreach ($filePath in $plannedFileMap.Keys) {
        if (-not $currentFileMap.ContainsKey($filePath)) {
            throw "File changed after retention planning: $filePath"
        }
        $plannedStamp = $plannedFileMap[$filePath]
        $currentStamp = $currentFileMap[$filePath]
        if ($plannedStamp.Item1 -ne $currentStamp.Item1 -or $plannedStamp.Item2 -ne $currentStamp.Item2) {
            throw "File changed after retention planning: $filePath"
        }
    }
    $occupancy = Test-GWFilesUnoccupied -FilePaths @($current.FileEntries.Keys) -RootPath $workspacePath -Limits $Limits
    if (-not $occupancy.Unoccupied) { throw "Workspace became busy before deletion: $($occupancy.Path)" }

    $removedFiles = 0
    $removedDirectories = 0
    $removedBytes = [long]0
    $markerPath = Join-Path $workspacePath $script:GeneratedWorkspaceMarkerName
    $deletionBudget = New-GWScanBudget -Limits $Limits
    $ordinaryFiles = @($current.FileEntries.Keys | Where-Object { -not [string]::Equals($_, $markerPath, [System.StringComparison]::OrdinalIgnoreCase) })
    $directoryDeletionOrder = [string[]]@($current.Directories)
    [Array]::Reverse($directoryDeletionOrder)
    if ($deletionBudget.Timer.ElapsedMilliseconds -gt $deletionBudget.MaxMilliseconds) {
        throw "Workspace deletion preparation exceeded its time bound: $workspacePath"
    }
    try {
        foreach ($filePath in $ordinaryFiles) {
            Add-GWScanNode -Budget $deletionBudget -RootPath $workspacePath
            Assert-GWNoReparsePath -Path $filePath -RequireLeaf
            $expected = $current.FileEntries[$filePath]
            $stream = [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Open($filePath, 4)
            try {
                if ([ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Length($stream) -ne [long]$expected.Item1 -or
                    [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::LastWriteTicks($stream) -ne [long]$expected.Item2) { throw "File changed immediately before deletion: $filePath" }
                # File.Delete refuses read-only files; git object files in test fixtures are read-only (10-27).
                $attributes = [System.IO.File]::GetAttributes($filePath)
                if ($attributes -band [System.IO.FileAttributes]::ReadOnly) {
                    [System.IO.File]::SetAttributes($filePath, $attributes -band -bnot [System.IO.FileAttributes]::ReadOnly)
                }
                [System.IO.File]::Delete($filePath)
            }
            finally { $stream.Dispose() }
            if ([System.IO.File]::Exists($filePath)) { throw "File remained during workspace deletion: $filePath" }
            $removedFiles++
            $removedBytes += [long]$expected.Item1
        }
        foreach ($directoryPath in $directoryDeletionOrder) {
            Add-GWScanNode -Budget $deletionBudget -RootPath $workspacePath
            Assert-GWNoReparsePath -Path $directoryPath -RequireLeaf
            [System.IO.Directory]::Delete($directoryPath, $false)
            $removedDirectories++
        }

        if ([System.IO.File]::Exists($markerPath)) {
            Add-GWScanNode -Budget $deletionBudget -RootPath $workspacePath
            $markerInfo = [System.IO.FileInfo]::new($markerPath)
            $markerInfo.Refresh()
            $markerLength = [long]$markerInfo.Length
            $markerStream = [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Open($markerPath, 4)
            try {
                if ([ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Length($markerStream) -ne $markerLength -or
                    [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::LastWriteTicks($markerStream) -ne $markerInfo.LastWriteTimeUtc.Ticks) { throw "Lifecycle marker changed immediately before deletion: $markerPath" }
                [System.IO.File]::Delete($markerPath)
            }
            finally { $markerStream.Dispose() }
            if ([System.IO.File]::Exists($markerPath)) { throw "Lifecycle marker remained during workspace deletion: $markerPath" }
            $removedFiles++
            $removedBytes += $markerLength
        }
        Add-GWScanNode -Budget $deletionBudget -RootPath $workspacePath
        [System.IO.Directory]::Delete($workspacePath, $false)
        return [pscustomobject]@{ Complete = $true; RemovedFiles = $removedFiles; RemovedDirectories = $removedDirectories; RemovedBytes = $removedBytes; Error = $null; RemainingPath = $null }
    }
    catch {
        return [pscustomobject]@{ Complete = $false; RemovedFiles = $removedFiles; RemovedDirectories = $removedDirectories; RemovedBytes = $removedBytes; Error = $_.Exception.Message; RemainingPath = $workspacePath }
    }
}

# Metadata is read once while all root locks remain held. Each target receives
# private item copies, so non-selected owners in later roots remain graph seeds.
function Expand-GWTargetCatalog {
    param([object]$MetadataCatalog, [AllowNull()][object]$Selection)
    $budget = New-GWScanBudget -Limits $MetadataCatalog.Limits
    $budget.Nodes = [int]$MetadataCatalog.NodeCount
    $budget.MaxMilliseconds -= [long]$MetadataCatalog.ElapsedMilliseconds
    $items = [System.Collections.Generic.List[object]]::new()
    $complete = [bool]$MetadataCatalog.Complete
    foreach ($original in $MetadataCatalog.Items) {
        $item = $original.PSObject.Copy()
        $outsideSelection = $null -ne $Selection -and -not $Selection.Contains($item.Path)
        if ($outsideSelection) {
            if (-not $item.BlockedReason) { $item.BlockedReason = 'Outside the explicitly requested cleanup selection.' }
            $items.Add($item)
            continue
        }
        # Reparse workspace roots were never traversed by the original catalogue.
        if ($item.BlockedReason -eq 'Workspace root is a reparse point.') { $items.Add($item); continue }
        $capture = $item.Registered -and $item.State -eq 'completed' -and -not $item.Keep -and $null -eq $item.BlockedReason
        $snapshot = Get-GWTreeSnapshot -WorkspacePath $item.Path -Budget $budget -CaptureDeletionSnapshot:$capture
        $item.Snapshot = $snapshot
        $item.Bytes = [long]$snapshot.Bytes
        $item.SizeKnown = [bool]$snapshot.Complete -and [int]$snapshot.ReparseCount -eq 0
        Add-GWSnapshotBlockedReason -Item $item -Snapshot $snapshot
        if (-not $snapshot.Complete -and $budget.Exceeded) { $complete = $false }
        if ($capture -and $null -eq $item.BlockedReason -and $item.SizeKnown) {
            $occupancy = Test-GWFilesUnoccupied -FilePaths @($snapshot.Files) -RootPath $item.Path -Limits $MetadataCatalog.Limits
            if (-not $occupancy.Unoccupied) { $item.BlockedReason = 'Workspace is locked or a file cannot be checked: ' + $occupancy.Path + ' (' + $occupancy.Reason + ')' }
        }
        $items.Add($item)
        if ($budget.Exceeded) { $complete = $false; break }
    }
    $totalBytes = [long]0
    foreach ($item in $items) { $totalBytes += [long]$item.Bytes }
    return [pscustomobject]@{
        RootPath = $MetadataCatalog.RootPath; RootConfig = $MetadataCatalog.RootConfig
        Limits = $MetadataCatalog.Limits; Items = @($items.ToArray())
        Complete = $complete; GraphComplete = $MetadataCatalog.GraphComplete
        AllSizesKnown = (@($items | Where-Object { -not $_.SizeKnown }).Count -eq 0)
        TotalBytes = $totalBytes; NodeCount = $budget.Nodes
        ElapsedMilliseconds = [long]$MetadataCatalog.ElapsedMilliseconds + $budget.Timer.ElapsedMilliseconds
        Issues = $MetadataCatalog.Issues
    }
}

function Invoke-GWCatalogRetention {
    param([string]$RootFull, [object]$TargetCatalog, [object[]]$Catalogs,
        [string[]]$RootPaths, [AllowNull()][object]$Selection, [switch]$Apply)
        $issues = @($catalogs | ForEach-Object { $_.Issues })
        $incomplete = @($catalogs | Where-Object { -not $_.Complete -or -not $_.GraphComplete })
        if ($incomplete.Count -gt 0) {
            return [pscustomobject]@{
                rootPath = $rootFull
                enabled = $true
                applied = [bool]$Apply
                safeToPrune = $false
                reason = 'At least one managed or reference root could not be completely catalogued; no workspace was changed.'
                issues = $issues
                planned = @()
                deleted = @()
                retained = @()
                remainingCount = @($targetCatalog.Items).Count
                totalBytes = [long]$targetCatalog.TotalBytes
                overBudget = $true
                overBudgetReason = 'Retention graph or bounded catalogue is incomplete.'
            }
        }
        try { $plan = New-GWRetentionPlan -TargetCatalog $targetCatalog -Catalogs @($catalogs) }
        catch {
            $budgetExceeded = $false
            if ($null -ne $_.Exception -and $null -ne $_.Exception.Data) {
                $budgetExceeded = [bool]$_.Exception.Data.Contains('GeneratedWorkspaceRetentionBudgetExceeded')
            }
            if (-not $budgetExceeded) { throw }
            $reason = $_.Exception.Message
            return [pscustomobject]@{
                rootPath = $rootFull
                enabled = $true
                applied = [bool]$Apply
                safeToPrune = $false
                reason = 'Retention planning exceeded its configured node or time bound; no workspace was changed.'
                issues = @([pscustomobject]@{ rootPath = $rootFull; path = $rootFull; reason = $reason })
                planned = @()
                deleted = @()
                failures = @()
                retained = @()
                remainingCount = @($targetCatalog.Items).Count
                totalBytes = [long]$targetCatalog.TotalBytes
                overBudget = $true
                overBudgetReason = $reason
            }
        }
        $deleted = [System.Collections.Generic.List[object]]::new()
        $failed = [System.Collections.Generic.List[object]]::new()
        if ($Apply) {
            foreach ($entry in $plan.Planned) {
                try {
                    $removed = Remove-GWWorkspaceSnapshot -Item $entry.Item -Limits $targetCatalog.Limits
                    if ($removed.Complete) {
                        $deleted.Add([pscustomobject]@{
                            path = $entry.Item.Path
                            bytes = [long]$entry.Item.Bytes
                            reason = $entry.Reason
                            removedFiles = [int]$removed.RemovedFiles
                            removedDirectories = [int]$removed.RemovedDirectories
                            removedBytes = [long]$removed.RemovedBytes
                            complete = $true
                        })
                    }
                    else {
                        $failed.Add([pscustomobject]@{
                            path = $entry.Item.Path
                            reason = $removed.Error
                            partialRemoval = [bool](Test-Path -LiteralPath $entry.Item.Path -PathType Container)
                            removedFiles = [int]$removed.RemovedFiles
                            removedDirectories = [int]$removed.RemovedDirectories
                            removedBytes = [long]$removed.RemovedBytes
                        })
                    }
                }
                catch {
                    $failed.Add([pscustomobject]@{
                        path = $entry.Item.Path
                        reason = $_.Exception.Message
                        partialRemoval = Test-Path -LiteralPath $entry.Item.Path -PathType Container
                        removedFiles = 0
                        removedDirectories = 0
                        removedBytes = [long]0
                    })
                }
            }
        }
        $remaining = @($targetCatalog.Items).Count
        if ($Apply) { $remaining -= $deleted.Count }
        $projectedBytes = [long]$plan.ProjectedBytes
        if ($Apply) {
            foreach ($entry in $plan.Planned) {
                $removedBytesForItem = [long]0
                $matchingDeleted = @($deleted | Where-Object { [string]::Equals($_.path, $entry.Item.Path, [System.StringComparison]::OrdinalIgnoreCase) })
                if ($matchingDeleted.Count -gt 0) { $removedBytesForItem = [long]$matchingDeleted[0].removedBytes }
                $matchingFailures = @($failed | Where-Object { [string]::Equals($_.path, $entry.Item.Path, [System.StringComparison]::OrdinalIgnoreCase) })
                if ($matchingFailures.Count -gt 0) { $removedBytesForItem = [long]$matchingFailures[0].removedBytes }
                $projectedBytes += [long]$entry.Item.Bytes - $removedBytesForItem
            }
        }
        $overBudget = $plan.OverBudget -or ($projectedBytes -gt $plan.MaxBytes)
        $overBudgetReason = $plan.OverBudgetReason
        if ($null -ne $selection -and -not $plan.SizeBudgetKnown) {
            $overBudgetReason = 'Whole-root byte budget is unknown because only the selected workspaces were accounted; all other owners remain protected.'
        }
        if ($failed.Count -gt 0 -and $projectedBytes -gt $plan.MaxBytes) { $overBudgetReason = 'One or more eligible workspaces could not be fully removed; see deletion failures.' }
        return [pscustomobject]@{
            rootPath = $rootFull
            enabled = $true
            applied = [bool]$Apply
            safeToPrune = $true
            accountingScope = $(if ($null -ne $selection) { 'selected workspaces; other owner sizes are unknown' } else { 'entire managed root' })
            maxRecentCompleted = [int]$targetCatalog.Limits.MaxRecentCompleted
            maxBytes = [long]$plan.MaxBytes
            totalBytes = [long]$targetCatalog.TotalBytes
            projectedBytes = $projectedBytes
            overBudget = [bool]$overBudget
            overBudgetReason = $overBudgetReason
            planned = $plan.PlannedSummary
            deleted = @($deleted.ToArray())
            failures = @($failed.ToArray())
            retained = @($plan.Retained)
            remainingCount = $remaining
            referenceRootPaths = $rootPaths
            issues = $issues
        }
}

function Invoke-GWRetentionGroups {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$WorkspaceGroups,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @(),
        [switch]$Apply,
        [switch]$RequireSelection,
        [switch]$CompletedPool
    )
    if ($WorkspaceGroups.Count -lt 1 -or $WorkspaceGroups.Count -gt 4096) { throw 'WorkspaceGroups must contain between 1 and 4096 roots.' }
    $inputBudget = New-GWScanBudget -Limits (Get-GWPolicyLimits -Policy $Policy -RootConfig $null)
    $pathCount = [long]0
    $groups = [System.Collections.Generic.List[object]]::new()
    $targets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($group in $WorkspaceGroups) {
        Add-GWScanNode -Budget $inputBudget -RootPath 'batch input'
        $rootFull = ConvertTo-GWFullPath -Path ([string](Get-GWProperty $group 'RootPath' '')) -ParameterName 'RootPath'
        $rootConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $rootFull
        $rootLimits = Get-GWPolicyLimits -Policy $Policy -RootConfig $rootConfig
        $inputBudget.MaxMilliseconds = [Math]::Min($inputBudget.MaxMilliseconds, $rootLimits.MaxMilliseconds)
        if (-not $targets.Add($rootFull)) { throw "A batch may select each managed root only once: $rootFull" }
        $selection = $null
        $paths = @(Get-GWProperty $group 'WorkspacePaths' @())
        $pathCount += $paths.Count
        if ($pathCount -gt 750000) { throw 'Batch input exceeds the 750000 exact-workspace limit.' }
        if ($RequireSelection -and $paths.Count -eq 0) { throw 'Every batch group requires non-empty exact WorkspacePaths.' }
        if ($paths.Count -gt 0) {
            $selection = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($path in $paths) {
                Add-GWScanNode -Budget $inputBudget -RootPath $rootFull
                $validated = Assert-GWManagedWorkspacePath -RootPath $rootFull -WorkspacePath ([string]$path) -RootConfig $rootConfig
                [void]$selection.Add($validated.WorkspacePath)
            }
        }
        $groups.Add([pscustomobject]@{ RootPath = $rootFull; Selection = $selection })
    }
    if (-not (Test-GWPolicyEnabled -Policy $Policy)) {
        foreach ($group in $groups) { [pscustomobject]@{ rootPath = $group.RootPath; enabled = $false; applied = [bool]$Apply; reason = 'Retention is explicitly disabled by policy.' } }
        return
    }
    $rootPaths = @(Get-GWReferenceRootPaths -RootPath $groups[0].RootPath -ReferenceRoots (@($groups | ForEach-Object { $_.RootPath }) + @($ReferenceRoots)))
    $locks = Enter-GWRootLocks -RootPaths $rootPaths
    try {
        $metadata = [System.Collections.Generic.List[object]]::new()
        foreach ($root in $rootPaths) { $metadata.Add((Get-GWRootCatalog -RootPath $root -Policy $Policy -MetadataOnly)) }
        foreach ($group in $groups) {
            $catalogs = [System.Collections.Generic.List[object]]::new()
            $targetCatalog = $null
            foreach ($catalog in $metadata) {
                if ([string]::Equals($catalog.RootPath, $group.RootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    if ($CompletedPool) {
                        # Select from the authoritative metadata under the same locks
                        # as planning/deletion. Empty remains an explicit empty set.
                        $group.Selection = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
                        $selectionBudget = New-GWScanBudget -Limits $catalog.Limits
                        foreach ($item in $catalog.Items) {
                            Add-GWScanNode -Budget $selectionBudget -RootPath $catalog.RootPath
                            if ($item.Registered -and $item.State -eq 'completed' -and -not $item.Keep) {
                                [void]$group.Selection.Add($item.Path)
                            }
                        }
                    }
                    $targetCatalog = Expand-GWTargetCatalog -MetadataCatalog $catalog -Selection $group.Selection
                    $catalogs.Add($targetCatalog)
                }
                else { $catalogs.Add($catalog) }
            }
            # Emit each result immediately, under the same locks; callers may persist
            # it before the next root is processed. Old metadata can only overprotect.
            try {
                $result = Invoke-GWCatalogRetention -RootFull $group.RootPath -TargetCatalog $targetCatalog -Catalogs @($catalogs.ToArray()) -RootPaths $rootPaths -Selection $group.Selection -Apply:$Apply
                if ($CompletedPool) {
                    $wholeRootBytesKnown = $targetCatalog.Complete -and $targetCatalog.AllSizesKnown
                    $result | Add-Member -NotePropertyName accountingScope -NotePropertyValue 'registered completed workspaces without Keep; protected owners outside this pool are not sized' -Force
                    $result | Add-Member -NotePropertyName wholeRootBytesKnown -NotePropertyValue ([bool]$wholeRootBytesKnown)
                    $result | Add-Member -NotePropertyName selectedWorkspaceCount -NotePropertyValue $group.Selection.Count
                }
                $result
            }
            finally {
                # Do not retain completed roots' potentially large file snapshots.
                $targetCatalog = $null
                $catalogs = $null
            }
        }
    }
    finally { Exit-GWRootLocks -Streams $locks }
}

function Invoke-GeneratedWorkspaceRetentionBatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$WorkspaceGroups,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @(),
        [switch]$Apply
    )
    Invoke-GWRetentionGroups -WorkspaceGroups $WorkspaceGroups -Policy $Policy -ReferenceRoots $ReferenceRoots -Apply:$Apply -RequireSelection
}

function Invoke-GeneratedWorkspaceRetention {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [AllowEmptyCollection()][AllowNull()][string[]]$ReferenceRoots = @(),
        [AllowEmptyCollection()][AllowNull()][string[]]$WorkspacePaths = @(),
        [switch]$CompletedOnly,
        [switch]$Apply
    )
    if ($CompletedOnly -and $null -ne $WorkspacePaths -and $WorkspacePaths.Count -gt 0) {
        throw 'CompletedOnly and exact WorkspacePaths are separate cleanup selections.'
    }
    $group = [pscustomobject]@{ RootPath = $RootPath; WorkspacePaths = $WorkspacePaths }
    Invoke-GWRetentionGroups -WorkspaceGroups @($group) -Policy $Policy -ReferenceRoots $ReferenceRoots -Apply:$Apply -CompletedPool:$CompletedOnly
}

Export-ModuleMember -Function @(
    'Get-GeneratedWorkspacePolicy',
    'Get-GeneratedWorkspaceRoot',
    'Get-GeneratedWorkspaceState',
    'New-GeneratedWorkspace',
    'Adopt-GeneratedWorkspace',
    'Adopt-GeneratedWorkspaceBatch',
    'Complete-GeneratedWorkspace',
    'Complete-GeneratedWorkspaceBatch',
    'Set-GeneratedWorkspaceKeep',
    'Set-GeneratedWorkspaceReferences',
    'Invoke-GeneratedWorkspaceRetention',
    'Invoke-GeneratedWorkspaceRetentionBatch'
)
