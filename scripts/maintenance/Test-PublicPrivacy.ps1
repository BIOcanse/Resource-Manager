#Requires -Version 7
<#
.SYNOPSIS
Finds this machine's identifiers, local paths and secrets in what a checkout would publish.

.DESCRIPTION
This machine's identifiers are read at run time and kept only in memory. The script stores none of them, so it
can be committed and published. Each hit names the file, the line and column (or byte offset) and the kind of hit,
never the matched text.

What is scanned:
  -Range A..B   the lines and file paths the range adds, every binary it adds or changes, and each commit's
                message, author and committer. Port-ToPublic.ps1 runs this on the commits it has just applied.
  -Base <rev>   the working tree against <rev>: lines added to tracked files, untracked files that are not
                ignored, changed binaries and new paths. Use it before committing.
  neither       every file in the working tree that git tracks or would track (not ignored), and every path.
Binaries are opened when they are zip archives (nupkg) or gzip streams, and their bytes are matched as Latin-1
and as UTF-16LE. Portable PDBs, standalone or embedded in a PE image, are read through System.Reflection.Metadata:
a PDB stores each document path in separate parts, so byte matching cannot see it. Each document name is checked
as a line of <file>!pdb-documents, and each embedded source is inflated and checked as <file>!pdb-source/<document>.
A PDB that cannot be read is an unscanned hit. Nesting is opened through an explicit stack, at most 4 levels deep
and 65536 items per file. -ExcludePath takes git pathspecs, for example
'tests/Validation/NonAdaptedOptimizationLab' or '*.nupkg'.

Rules, all Level Block:
  user-name   the Windows user name as a word.
  profile     the user profile folder, written with \, / or JSON-escaped \\.
  local-root  the parent folders of the checkout and of its main checkout (for a worktree), the configured
              ToolsRoot and WorkRoot (scripts/dev/MachineRoots.psm1),
              -LocalRoots, and the parents of user shell folders moved outside the profile, written with \, \\ or /,
              and for a drive path also as Git Bash /d/... and WSL /mnt/d/.... Drive roots are never local roots.
  machine-id  computer name, MachineGuid, SMBIOS UUID, BIOS and board serials, CPU id, disk and volume serials,
              network adapter MACs, user SID, NVIDIA GPU UUIDs and their first group (GPU-xxxxxxxx), registered
              owner, account full name, account e-mail addresses and their local parts, PnP instance tokens and
              board-specific PCI subsystem ids, also split as 0000VVVV:0000DDDD.
  sid, host   Windows account SIDs and default DESKTOP-/LAPTOP- host names.
  token       API tokens and private keys of common services, and rm_sk_ keys other than repeated-digit fixtures.
  email       any address other than -AllowedEmails (default: the repository's git user.email) and the reserved
              example domains (example.com/.net/.org, .example, .test, .invalid, .localhost). Third-party notices,
              licenses, AUTHORS files and package-lock.json are not checked for addresses, nor is URL user info.
  unscanned   a binary larger than 128 MiB, which this check cannot read.
-IncludeReview adds Level Review hits for shapes that invented fixtures also use: any X:\Users\<name> other than
Public/Default/All Users, GPU UUIDs and MAC addresses. Port-ToPublic.ps1 does not pass it.

Reviewed exceptions live in a local, untracked JSON file (-AllowListPath; by default
<this checkout>\.local\tools\public-privacy-allow.json, absent means none): an array of
{ "path": "<repository path>", "rule": "<rule>", "lineSha256": "<SHA-256 of the line's UTF-8 text>", "reason": "..." }.
An entry allows exactly that line for that rule; a changed line is checked again. The file stays local because an
entry for a user-name hit would show which words of that line the user name is among.

Output: one object per hit with Level, Rule, Path, Line and Column. Line is a line number, 'path' for a file path,
'author' or 'committer' for commit identity, or 'bin' for a binary, where Column is the byte offset. Paths inside
archives are written archive!entry. An identifier inside a printed path is written as <rule>, for example
notes-<user-name>.txt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$Range,
    [string]$Base,
    [string[]]$LocalRoots = @(),
    [string[]]$AllowedEmails,
    [string[]]$ExcludePath = @(),
    [string]$AllowListPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.local\tools\public-privacy-allow.json'),
    [switch]$IncludeReview
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.Reflection.Metadata
Import-Module (Join-Path $PSScriptRoot '..\dev\MachineRoots.psm1') -DisableNameChecking
if ($Range -and $Base) { throw 'Pass -Range or -Base, not both.' }
if ($Range -and $Range -notmatch '^[^.\s]+\.\.[^.\s]+$') { throw '-Range must be A..B.' }
$Repo = [IO.Path]::GetFullPath($Repo).TrimEnd('\', '/')
$maxBinaryBytes = 128MB
$maxArchiveDepth = 4
# Archive entries, inflated streams and embedded PDB sources opened from one scanned file.
$maxNestedItems = 65536
$embeddedSourceKind = [guid]'0E8A571B-6926-466E-B4AD-8AB04611F5FE'
$utf8 = [Text.UTF8Encoding]::new($false)
$sha256 = [Security.Cryptography.SHA256]::Create()

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments, [int[]]$AllowedExitCodes = @(0))
    $psi = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($argument in @('-C', $Repo, '-c', 'core.quotepath=off') + $Arguments) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.StandardOutputEncoding = $utf8
    $process = [Diagnostics.Process]::Start($psi)
    $errors = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    if ($AllowedExitCodes -notcontains $process.ExitCode) { throw "git $($Arguments[0]) failed with exit code $($process.ExitCode): $($errors.Result.Trim())" }
    if ($output.Length -eq 0) { return }
    $output.TrimEnd("`n").Split("`n") | ForEach-Object { $_.TrimEnd("`r") }
}

function Get-GitBlob([Parameter(Mandatory)][string]$Spec) {
    $psi = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($argument in @('-C', $Repo, 'cat-file', 'blob', $Spec)) { $psi.ArgumentList.Add($argument) }
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($psi)
    $errors = $process.StandardError.ReadToEndAsync()
    $buffer = [IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($buffer)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "git cat-file failed for ${Spec}: $($errors.Result.Trim())" }
    return , $buffer.ToArray()
}

function Get-Grab([scriptblock]$Read) { try { @(& $Read) } catch { @() } }
function Get-Sha256Hex([string]$Text) { [Convert]::ToHexString($sha256.ComputeHash($utf8.GetBytes($Text))) }
# One or more \ or / between path segments; this also covers JSON-escaped \\.
$separator = '(?:\\|/)+'
function ConvertTo-PathPattern([string]$Path) {
    (($Path.TrimEnd('\', '/') -split '[\\/]+' | ForEach-Object { [regex]::Escape($_) }) -join $separator)
}
# The POSIX spelling of a drive path, as Git Bash (/d/...) and WSL (/mnt/d/...) write it.
function ConvertTo-PosixPathPattern([string]$Path) {
    $rest = ($Path.Substring(3).TrimEnd('\', '/') -split '[\\/]+' | ForEach-Object { [regex]::Escape($_) }) -join '/+'
    "(?<![A-Za-z0-9_.:-])(?:/mnt)?/$([regex]::Escape($Path.Substring(0, 1)))/+$rest"
}

# This machine's identifiers, read now and kept only in memory.
$noise = '^(0+|none|default string|standard|to be filled by o\.e\.m\.|system serial number)$'
$raw = [Collections.Generic.List[string]]::new()
$emails = [Collections.Generic.List[string]]::new()
foreach ($value in @(
    $env:COMPUTERNAME
    Get-Grab { (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid }
    Get-Grab { (Get-CimInstance Win32_ComputerSystemProduct).UUID }
    Get-Grab { (Get-CimInstance Win32_BIOS).SerialNumber }
    Get-Grab { (Get-CimInstance Win32_BaseBoard).SerialNumber }
    Get-Grab { (Get-CimInstance Win32_Processor).ProcessorId }
    Get-Grab { (Get-CimInstance Win32_DiskDrive).SerialNumber }
    Get-Grab { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
    Get-Grab { (Get-LocalUser -Name $env:USERNAME).FullName }
    Get-Grab { (& nvidia-smi -L 2>$null) | ForEach-Object { [regex]::Matches($_, 'GPU-[0-9a-fA-F-]{36}') | ForEach-Object Value } }
)) { if ($value) { $raw.Add([string]$value) } }
foreach ($value in @(
    Get-Grab { (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').RegisteredOwner }
    Get-Grab { (Get-ChildItem 'HKCU:\Software\Microsoft\IdentityCRL\UserExtendedProperties').PSChildName }
)) { if ($value) { $raw.Add([string]$value); if ($value -match '^[^@\s]+@[^@\s]+$') { $emails.Add([string]$value) } } }
foreach ($email in $emails) {
    $local = $email.Split('@')[0]
    $raw.Add($local)
    $raw.Add($local.TrimEnd('0123456789'.ToCharArray()))
}
foreach ($serial in (Get-Grab { (Get-CimInstance Win32_LogicalDisk).VolumeSerialNumber })) {
    if ($serial -and $serial.Length -eq 8) { $raw.Add($serial); $raw.Add($serial.Insert(4, '-')) }
}
foreach ($mac in (Get-Grab { (Get-NetAdapter -IncludeHidden).MacAddress })) {
    if ($mac) { $raw.Add($mac); $raw.Add($mac.Replace('-', ':')); $raw.Add($mac.Replace('-', '')) }
}
foreach ($instance in (Get-Grab { (Get-PnpDevice).InstanceId })) {
    $parts = $instance -split '\\'
    $last = $parts[-1] -split '&'
    if ($last.Count -ge 2 -and $last[1].Length -ge 6 -and $last[1] -match '\d') { $raw.Add($last[1]) }
    elseif ($last[0].Length -ge 8 -and $last[0] -match '\d' -and $last[0] -notmatch '^\{?[0-9A-Fa-f]{8}-') { $raw.Add($last[0]) }
    # Board-specific subsystem ids only; 0000xxxx is a vendor reference value, not this machine's board.
    if ($parts.Count -ge 2 -and $parts[1] -match 'SUBSYS_([0-9A-Fa-f]{8})' -and -not $Matches[1].StartsWith('0000')) {
        $subsystem = $Matches[1]
        $raw.Add($subsystem)
        # The same id split into subsystem vendor and device, as 0000VVVV:0000DDDD.
        $raw.Add("0000$($subsystem.Substring(4, 4)):0000$($subsystem.Substring(0, 4))")
    }
}
# The first group of a GPU UUID, as GPU-xxxxxxxx: text may quote a truncated UUID.
foreach ($value in @($raw | Where-Object { $_ -match '^GPU-([0-9A-Fa-f]{8})-' })) { $raw.Add($value.Substring(0, 12)) }
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($value in $raw) { $id = $value.Trim().TrimEnd('.'); if ($id.Length -ge 6 -and $id -notmatch $noise) { [void]$ids.Add($id) } }

# Local roots: the checkout's parent, the configured machine roots, the caller's roots, moved shell folders.
$roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$candidateRoots = [Collections.Generic.List[string]]::new()
$candidateRoots.Add((Split-Path -Parent $Repo))
# A worktree's main checkout sits elsewhere; its parent is a local root too.
$commonDirectory = @(Invoke-Git -Arguments @('rev-parse', '--path-format=absolute', '--git-common-dir') -AllowedExitCodes @(0, 128)) | Select-Object -First 1
if ($commonDirectory) { $candidateRoots.Add((Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($commonDirectory))))) }
foreach ($name in @('ToolsRoot', 'WorkRoot')) {
    $root = Get-MachineRoot -Name $name -Optional
    if ($root) { $candidateRoots.Add($root) }
    else { Write-Warning "$name is not configured, so paths under it are only caught through the other local roots (scripts/dev/MachineRoots.psm1)." }
}
foreach ($root in $LocalRoots) { if ($root) { $candidateRoots.Add([IO.Path]::GetFullPath($root)) } }
$shellFolders = Get-Grab {
    (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders').PSObject.Properties |
        Where-Object { $_.Value -is [string] } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_.Value) }
}
foreach ($folder in $shellFolders) {
    if ($folder -match '^[A-Za-z]:\\' -and -not $folder.StartsWith($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase)) { $candidateRoots.Add((Split-Path -Parent $folder)) }
}
foreach ($root in $candidateRoots) { $trimmed = $root.TrimEnd('\', '/'); if ($trimmed.Length -gt 3) { [void]$roots.Add($trimmed) } }

if (-not $PSBoundParameters.ContainsKey('AllowedEmails')) { $AllowedEmails = @(Invoke-Git -Arguments @('config', '--get', 'user.email') -AllowedExitCodes @(0, 1)) }
$allowedEmailSet = [Collections.Generic.HashSet[string]]::new([string[]]@($AllowedEmails | Where-Object { $_ }), [StringComparer]::OrdinalIgnoreCase)
$reservedEmail = '(?i)@(?:[A-Za-z0-9-]+\.)*(?:example\.(?:com|net|org)|[A-Za-z0-9-]+\.(?:example|test|invalid|localhost))$'
$thirdParty = '(?i)(third[-_ ]?party|ThirdPartyNotices|NOTICES?(\.|$)|AUTHORS|LICENSE|package-lock\.json$|/DeviceIds/(pci|usb)\.ids$)'

$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($AllowListPath -and [IO.File]::Exists($AllowListPath)) {
    foreach ($entry in @(Get-Content -LiteralPath $AllowListPath -Raw | ConvertFrom-Json)) {
        [void]$allowed.Add("$($entry.path)|$($entry.rule)|$($entry.lineSha256)")
    }
}

$rules = [ordered]@{
    'user-name'  = @('Block', "(?i)(?<![A-Za-z0-9])$([regex]::Escape($env:USERNAME))(?![A-Za-z0-9])")
    'profile'    = @('Block', ('(?i)(?<![A-Za-z0-9])' + (ConvertTo-PathPattern $env:USERPROFILE) + '(?![A-Za-z0-9])'))
    'local-root' = @('Block', ('(?i)(?:' + ((@($roots | ForEach-Object { '(?<![A-Za-z0-9])' + (ConvertTo-PathPattern $_) }) +
        @($roots | Where-Object { $_ -match '^[A-Za-z]:[\\/]' } | ForEach-Object { ConvertTo-PosixPathPattern $_ })) -join '|') + ')(?![A-Za-z0-9])'))
    'machine-id' = @('Block', ('(?i)(?:(?<=0x)|(?<![A-Za-z0-9]))(?:' + (($ids | Sort-Object Length -Descending | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?![A-Za-z0-9])'))
    'sid'        = @('Block', '\bS-1-5-21-\d{6,}-\d{6,}-\d{6,}')
    'host'       = @('Block', '\b(?:DESKTOP|LAPTOP)-[A-Z0-9]{7}\b')
    'token'      = @('Block', '(?:ghp_|gho_|ghu_|ghs_|github_pat_)[A-Za-z0-9_]{20,}|\bsk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|xox[abposr]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY|eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}|AIza[0-9A-Za-z_-]{35}|\bnpm_[A-Za-z0-9]{36}|rm_sk_(?!([0-9a-f])\1{63}|(?:0123456789abcdef){4})[0-9A-Za-z]{64}')
    'email'      = @('Block', '(?<![A-Za-z0-9._%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}')
}
# An empty alternation would match everywhere; a rule with nothing to look for is dropped.
if (-not $env:USERNAME) { $rules.Remove('user-name') }
if (-not $env:USERPROFILE) { $rules.Remove('profile') }
if ($roots.Count -eq 0) { $rules.Remove('local-root') }
if ($ids.Count -eq 0) { $rules.Remove('machine-id') }
if ($IncludeReview) {
    $rules['any-profile'] = @('Review', ('(?i)\b[A-Z]:' + $separator + 'Users' + $separator + '(?!(?:Public|Default|All Users)\b)[^\\/"''\s]+'))
    $rules['gpu-uuid'] = @('Review', '(?i)\bGPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b')
    $rules['mac'] = @('Review', '(?<![0-9A-Fa-f:-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:-])')
}
$compiled = [ordered]@{}
foreach ($key in $rules.Keys) { $compiled[$key] = [regex]::new($rules[$key][1]) }
$urlUserInfo = [regex]::new('(?i)\b[a-z][a-z0-9+.-]*://[^/\s@]+@')

# A hit's Path is printed, so an identifier inside a file or entry name is written as <rule>.
function Hide-Identifiers([string]$Text) {
    foreach ($key in $compiled.Keys) { if ($rules[$key][0] -eq 'Block') { $Text = $compiled[$key].Replace($Text, "<$key>") } }
    $Text
}

function Test-Text([string]$Path, [string]$Line, [string]$Text, [bool]$Binary, [int]$ByteStride = 1, [int]$ByteShift = 0) {
    $lineKey = $null
    foreach ($key in $compiled.Keys) {
        if ($Binary -and $rules[$key][0] -ne 'Block') { continue }
        foreach ($match in $compiled[$key].Matches($Text)) {
            if ($key -eq 'email') {
                if ($Binary -or $Path -match $thirdParty -or $allowedEmailSet.Contains($match.Value) -or $match.Value -match $reservedEmail) { continue }
                # A domain with an all-digit label is a dotted identifier such as field@gpu.0.usage, not a mail host.
                if ($match.Value -match '@(?:[A-Za-z0-9-]+\.)*[0-9]+\.') { continue }
                $at = $match.Index + $match.Value.IndexOf('@')
                if (@($urlUserInfo.Matches($Text) | Where-Object { $_.Index + $_.Length - 1 -eq $at }).Count -gt 0) { continue }
            }
            if (-not $Binary -and $allowed.Count -gt 0) {
                if ($null -eq $lineKey) { $lineKey = Get-Sha256Hex $Text }
                if ($allowed.Contains("$Path|$key|$lineKey")) { continue }
            }
            $column = if ($Binary) { $match.Index * $ByteStride + $ByteShift } else { $match.Index + 1 }
            [pscustomobject]@{ Level = $rules[$key][0]; Rule = $key; Path = (Hide-Identifiers $Path); Line = $Line; Column = $column }
        }
    }
}

function Test-PortablePdb([string]$Name, [Reflection.Metadata.MetadataReader]$Reader, [Collections.Generic.Stack[object]]$Pending, [int]$Depth) {
    # A portable PDB stores each document name as separate part blobs and embedded sources deflated, so byte
    # matching sees neither: names are joined and checked as lines, embedded sources are inflated and queued.
    $number = 0
    foreach ($handle in $Reader.Documents) {
        $number++
        Test-Text "$Name!pdb-documents" ([string]$number) $Reader.GetString($Reader.GetDocument($handle).Name) $false
    }
    foreach ($handle in $Reader.CustomDebugInformation) {
        $information = $Reader.GetCustomDebugInformation($handle)
        if ($Reader.GetGuid($information.Kind) -ne $embeddedSourceKind) { continue }
        $blob = $Reader.GetBlobBytes($information.Value)
        if ($blob.Length -lt 4) { continue }
        $document = [Reflection.Metadata.Ecma335.MetadataTokens]::GetRowNumber($information.Parent)
        # Format 0 is stored as is; any other value is the inflated length of a deflate stream.
        if ([BitConverter]::ToInt32($blob, 0) -eq 0) {
            $source = [byte[]]::new($blob.Length - 4)
            [Array]::Copy($blob, 4, $source, 0, $source.Length)
        } else {
            $buffer = [IO.MemoryStream]::new()
            $deflate = [IO.Compression.DeflateStream]::new([IO.MemoryStream]::new($blob, 4, $blob.Length - 4), [IO.Compression.CompressionMode]::Decompress)
            try { $deflate.CopyTo($buffer) } finally { $deflate.Dispose() }
            $source = $buffer.ToArray()
        }
        $Pending.Push([pscustomobject]@{ Name = "$Name!pdb-source/$document"; Bytes = $source; Depth = $Depth + 1 })
    }
}

function Test-Bytes([string]$Name, [byte[]]$Bytes) {
    # Archives, compressed streams and PDBs inside the bytes are opened through this stack, never by recursion.
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push([pscustomobject]@{ Name = $Name; Bytes = $Bytes; Depth = 0 })
    $opened = 0
    while ($pending.Count -gt 0) {
        $item = $pending.Pop()
        $itemBytes = [byte[]]$item.Bytes
        if ($itemBytes.Length -gt $maxBinaryBytes -or ++$opened -gt $maxNestedItems) {
            [pscustomobject]@{ Level = 'Block'; Rule = 'unscanned'; Path = (Hide-Identifiers $item.Name); Line = 'bin'; Column = 0 }
            continue
        }
        $nested = $item.Depth -lt $maxArchiveDepth
        $zip = $null
        if ($nested -and $itemBytes.Length -gt 4 -and $itemBytes[0] -eq 0x50 -and $itemBytes[1] -eq 0x4B -and $itemBytes[2] -eq 0x03 -and $itemBytes[3] -eq 0x04) {
            # Bytes that only start like a zip archive are matched as plain bytes below.
            try { $zip = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($itemBytes)) } catch { $zip = $null }
        }
        if ($null -ne $zip) {
            try {
                foreach ($entry in $zip.Entries) {
                    $entryName = "$($item.Name)!$($entry.FullName)"
                    Test-Text $entryName 'path' $entry.FullName $false
                    if ($entry.Length -eq 0) { continue }
                    if ($entry.Length -gt $maxBinaryBytes) { [pscustomobject]@{ Level = 'Block'; Rule = 'unscanned'; Path = (Hide-Identifiers $entryName); Line = 'bin'; Column = 0 }; continue }
                    $buffer = [IO.MemoryStream]::new()
                    $stream = $entry.Open()
                    try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
                    $pending.Push([pscustomobject]@{ Name = $entryName; Bytes = $buffer.ToArray(); Depth = $item.Depth + 1 })
                }
            } finally { $zip.Dispose() }
            continue
        }
        if ($nested -and $itemBytes.Length -gt 2 -and $itemBytes[0] -eq 0x1F -and $itemBytes[1] -eq 0x8B) {
            $buffer = [IO.MemoryStream]::new()
            $gzip = [IO.Compression.GZipStream]::new([IO.MemoryStream]::new($itemBytes), [IO.Compression.CompressionMode]::Decompress)
            $inflated = $true
            try { $gzip.CopyTo($buffer) } catch { $inflated = $false } finally { $gzip.Dispose() }
            if ($inflated) {
                $pending.Push([pscustomobject]@{ Name = "$($item.Name)!gunzip"; Bytes = $buffer.ToArray(); Depth = $item.Depth + 1 })
                continue
            }
        }
        # A standalone portable PDB starts with the metadata signature BSJB.
        if ($nested -and $itemBytes.Length -gt 4 -and $itemBytes[0] -eq 0x42 -and $itemBytes[1] -eq 0x53 -and $itemBytes[2] -eq 0x4A -and $itemBytes[3] -eq 0x42) {
            $provider = $null
            try {
                $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbImage([Collections.Immutable.ImmutableArray]::Create[byte]($itemBytes))
                Test-PortablePdb $item.Name $provider.GetMetadataReader() $pending $item.Depth
            } catch {
                # A PDB this check cannot read may hide paths; it blocks like any other unscanned content.
                [pscustomobject]@{ Level = 'Block'; Rule = 'unscanned'; Path = (Hide-Identifiers "$($item.Name)!pdb-documents"); Line = 'bin'; Column = 0 }
            } finally { if ($null -ne $provider) { $provider.Dispose() } }
        }
        # A PE image may embed its portable PDB, deflated, in the debug directory.
        if ($nested -and $itemBytes.Length -gt 64 -and $itemBytes[0] -eq 0x4D -and $itemBytes[1] -eq 0x5A) {
            $pe = $null
            try {
                $pe = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new($itemBytes))
                $embedded = @($pe.ReadDebugDirectory() | Where-Object Type -eq ([Reflection.PortableExecutable.DebugDirectoryEntryType]::EmbeddedPortablePdb))
            } catch { $embedded = @() }
            try {
                foreach ($entry in $embedded) {
                    $provider = $null
                    try {
                        $provider = $pe.ReadEmbeddedPortablePdbDebugDirectoryData($entry)
                        Test-PortablePdb "$($item.Name)!embedded-pdb" $provider.GetMetadataReader() $pending $item.Depth
                    } catch {
                        [pscustomobject]@{ Level = 'Block'; Rule = 'unscanned'; Path = (Hide-Identifiers "$($item.Name)!embedded-pdb"); Line = 'bin'; Column = 0 }
                    } finally { if ($null -ne $provider) { $provider.Dispose() } }
                }
            } finally { if ($null -ne $pe) { $pe.Dispose() } }
        }
        $views = @(
            @([Text.Encoding]::GetEncoding(28591).GetString($itemBytes), 1, 0),
            @([Text.Encoding]::Unicode.GetString($itemBytes), 2, 0)
        )
        if ($itemBytes.Length -gt 1) { $views += , @([Text.Encoding]::Unicode.GetString($itemBytes, 1, $itemBytes.Length - 1), 2, 1) }
        $seen = [Collections.Generic.HashSet[string]]::new()
        foreach ($view in $views) {
            foreach ($hit in (Test-Text $item.Name 'bin' $view[0] $true $view[1] $view[2])) { if ($seen.Add($hit.Rule)) { $hit } }
        }
    }
}

function Test-TextFileBytes([string]$Path, [byte[]]$Bytes) {
    # Git's binary heuristic: a NUL in the first 8000 bytes.
    $probe = [Math]::Min($Bytes.Length, 8000)
    if ([Array]::IndexOf($Bytes, [byte]0, 0, $probe) -ge 0) { Test-Bytes $Path $Bytes; return }
    $number = 0
    foreach ($line in $utf8.GetString($Bytes).Split("`n")) { $number++; Test-Text $Path ([string]$number) $line.TrimEnd("`r") $false }
}

function Test-DiffLines([string[]]$Lines) {
    $path = $null
    $number = 0
    $header = $false
    foreach ($line in $Lines) {
        # File headers run from 'diff --git' to the first hunk; an added line may itself start with '++'.
        if ($line.StartsWith('diff --git ')) { $header = $true; $path = $null; continue }
        if ($header) {
            if ($line.StartsWith('+++ ')) { $path = if ($line.StartsWith('+++ b/')) { $line.Substring(6) } else { $null } }
            elseif ($line -match '^@@ -\d+(?:,\d+)? \+(\d+)') { $number = [int]$Matches[1]; $header = $false }
            continue
        }
        if ($line -match '^@@ -\d+(?:,\d+)? \+(\d+)') { $number = [int]$Matches[1]; continue }
        if ($path -and $line.StartsWith('+')) { Test-Text $path ([string]$number) $line.Substring(1) $false; $number++ }
    }
}

$pathspec = @('--', '.') + @($ExcludePath | Where-Object { $_ } | ForEach-Object { ":(exclude)$_" })
if ($Range) {
    Test-DiffLines @(Invoke-Git -Arguments (@('diff', '-U0', '--no-color', '--no-ext-diff', $Range) + $pathspec))
    foreach ($path in @(Invoke-Git -Arguments (@('diff', '--name-only', '--diff-filter=AR', $Range) + $pathspec))) { Test-Text $path 'path' $path $false }
    $revision = ($Range -split '\.\.')[-1]
    foreach ($row in @(Invoke-Git -Arguments (@('diff', '--numstat', '--diff-filter=AMR', '--no-renames', $Range) + $pathspec))) {
        if ($row -match "^-`t-`t(.+)$") { Test-Bytes $Matches[1] (Get-GitBlob "${revision}:$($Matches[1])") }
    }
    foreach ($commit in @(Invoke-Git -Arguments @('rev-list', '--reverse', $Range))) {
        $label = "commit $($commit.Substring(0, 12))"
        $identity = @(Invoke-Git -Arguments @('show', '-s', '--format=%an%n%ae%n%cn%n%ce', $commit))
        Test-Text $label 'author' "$($identity[0]) <$($identity[1])>" $false
        Test-Text $label 'committer' "$($identity[2]) <$($identity[3])>" $false
        $number = 0
        foreach ($line in @(Invoke-Git -Arguments @('show', '-s', '--format=%B', $commit))) { $number++; Test-Text $label ([string]$number) $line $false }
    }
}
elseif ($Base) {
    Test-DiffLines @(Invoke-Git -Arguments (@('diff', '-U0', '--no-color', '--no-ext-diff', $Base) + $pathspec))
    foreach ($path in @(Invoke-Git -Arguments (@('diff', '--name-only', '--diff-filter=AR', $Base) + $pathspec))) { Test-Text $path 'path' $path $false }
    foreach ($row in @(Invoke-Git -Arguments (@('diff', '--numstat', '--diff-filter=AMR', '--no-renames', $Base) + $pathspec))) {
        if ($row -match "^-`t-`t(.+)$") { Test-Bytes $Matches[1] ([IO.File]::ReadAllBytes((Join-Path $Repo $Matches[1]))) }
    }
    foreach ($path in @(Invoke-Git -Arguments (@('ls-files', '--others', '--exclude-standard') + $pathspec))) {
        Test-Text $path 'path' $path $false
        Test-TextFileBytes $path ([IO.File]::ReadAllBytes((Join-Path $Repo $path)))
    }
}
else {
    $patternFile = [IO.Path]::GetTempFileName()
    try {
        # Patterns hold this machine's identifiers; the temporary file is removed as soon as git has read it.
        [IO.File]::WriteAllLines($patternFile, [string[]]@($rules.Values | ForEach-Object { $_[1] }), $utf8)
        foreach ($row in @(Invoke-Git -Arguments (@('grep', '--untracked', '-z', '-n', '-I', '-P', '-f', $patternFile) + $pathspec) -AllowedExitCodes @(0, 1))) {
            $fields = $row.Split([char[]]@([char]0), 3)
            if ($fields.Count -eq 3) { Test-Text $fields[0] $fields[1] $fields[2] $false }
        }
    } finally { Remove-Item -LiteralPath $patternFile -Force }
    $textFiles = [Collections.Generic.HashSet[string]]::new([string[]]@(Invoke-Git -Arguments (@('grep', '--untracked', '-I', '-l', '-e', '^') + $pathspec) -AllowedExitCodes @(0, 1)))
    foreach ($path in @(Invoke-Git -Arguments (@('ls-files', '--cached', '--others', '--exclude-standard') + $pathspec) | Sort-Object -Unique)) {
        Test-Text $path 'path' $path $false
        $full = Join-Path $Repo $path
        if (-not $textFiles.Contains($path) -and [IO.File]::Exists($full)) { Test-Bytes $path ([IO.File]::ReadAllBytes($full)) }
    }
}
