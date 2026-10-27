#Requires -Version 5.1
<#
.SYNOPSIS
Join a Resource Manager memory slice into one standard .dmp file.
.DESCRIPTION
DumpPaths (including an action record's dumpPaths property) is the authoritative order,
and may span volumes. RecordPath reads one JSON action record with that property.
FirstSegmentPath discovers numbered siblings only in SegmentDirectories, or in the
first segment's directory when omitted. Supply every reserve directory for a slice
spanning directories; the first segment contains no directory or segment-count manifest.
First-segment mode checks the minidump's Memory64List to reject missing payload tails.
Sources are kept unchanged. OutputPath must be new and its parent must already exist.
The output needs additional disk space equal to the sum of the source file lengths.
.EXAMPLE
pwsh -NoProfile -File .\scripts\tools\Join-MemorySlice.ps1 -RecordPath .\action.json -OutputPath E:\analysis\joined.dmp
.EXAMPLE
& .\scripts\tools\Join-MemorySlice.ps1 -DumpPaths $record.dumpPaths -OutputPath E:\analysis\joined.dmp
.EXAMPLE
& .\scripts\tools\Join-MemorySlice.ps1 -FirstSegmentPath C:\reserve\slice.dmp -SegmentDirectories C:\reserve,F:\reserve -OutputPath E:\analysis\joined.dmp
#>
[CmdletBinding(DefaultParameterSetName = 'Paths')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Paths', ValueFromPipelineByPropertyName)]
    [ValidateNotNullOrEmpty()][string[]]$DumpPaths,
    [Parameter(Mandatory, ParameterSetName = 'Record')]
    [ValidateNotNullOrEmpty()][string]$RecordPath,
    [Parameter(Mandatory, ParameterSetName = 'First', Position = 0)]
    [ValidateNotNullOrEmpty()][string]$FirstSegmentPath,
    [Parameter(ParameterSetName = 'First')][string[]]$SegmentDirectories,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputPath
)

function Assert-CompleteMemory64Dump([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 32 -or $reader.ReadUInt32() -ne 0x504d444d) { throw 'First-segment output is not a minidump.' }
        [void]$reader.ReadUInt32() # Version.
        $streamCount = $reader.ReadUInt32()
        $directoryRva = [long]$reader.ReadUInt32()
        if ($streamCount -eq 0 -or $streamCount -gt 65536 -or $directoryRva -gt $stream.Length -or
            $streamCount -gt [math]::Floor(($stream.Length - $directoryRva) / 12)) {
            throw 'Minidump stream directory is incomplete or exceeds the supported stream count.'
        }
        $stream.Position = $directoryRva
        $memoryRva = [long]-1
        $memoryBytes = [long]0
        for ($index = 0; $index -lt $streamCount; $index++) {
            $type = $reader.ReadUInt32()
            $size = [long]$reader.ReadUInt32()
            $rva = [long]$reader.ReadUInt32()
            if ($type -eq 9) { $memoryRva = $rva; $memoryBytes = $size; break }
        }
        if ($memoryRva -lt 0 -or $memoryBytes -lt 16 -or $memoryRva -gt $stream.Length -or
            $memoryBytes -gt $stream.Length - $memoryRva) { throw 'Minidump Memory64List is missing or incomplete.' }
        $stream.Position = $memoryRva
        $regionCount = $reader.ReadUInt64()
        $payloadRva = $reader.ReadUInt64()
        if ($regionCount -gt 1048576 -or $regionCount -gt [math]::Floor(($memoryBytes - 16) / 16)) {
            throw 'Minidump Memory64List descriptors are incomplete or exceed the supported region count.'
        }
        if ($payloadRva -gt [long]::MaxValue) { throw 'Minidump payload exceeds the supported file length.' }
        $expectedEnd = [long]$payloadRva
        for ($index = 0; $index -lt $regionCount; $index++) {
            [void]$reader.ReadUInt64() # Virtual address.
            $regionBytes = $reader.ReadUInt64()
            if ($regionBytes -gt ([long]::MaxValue - $expectedEnd)) { throw 'Minidump payload exceeds the supported file length.' }
            $expectedEnd += [long]$regionBytes
        }
        if ($stream.Length -lt $expectedEnd) {
            throw "Incomplete minidump: expected at least $expectedEnd bytes, found $($stream.Length). Supply every reserve directory or use the record's dumpPaths."
        }
    }
    finally { $reader.Dispose() }
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$inputs = [System.Collections.Generic.List[System.IO.FileStream]]::new()
$output = $null
$createdOutput = $false
$outputFullPath = $null
$exitCode = 1
try {
    if ($PSCmdlet.ParameterSetName -eq 'Record') {
        $record = Get-Content -LiteralPath $RecordPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $record -or $null -eq $record.PSObject.Properties['dumpPaths'] -or
            $record.dumpPaths -isnot [System.Array] -or $record.dumpPaths.Count -eq 0) {
            throw 'RecordPath must contain one action record with a nonempty dumpPaths array.'
        }
        foreach ($path in $record.dumpPaths) {
            if ($path -isnot [string] -or [string]::IsNullOrWhiteSpace($path)) { throw 'Every dumpPaths entry must be a nonempty string.' }
        }
        $DumpPaths = [string[]]$record.dumpPaths
    }
    elseif ($PSCmdlet.ParameterSetName -eq 'First') {
        $first = Get-Item -LiteralPath $FirstSegmentPath
        if ($first -isnot [System.IO.FileInfo] -or $first.Extension -ine '.dmp') { throw 'FirstSegmentPath must be the first .dmp file.' }
        $parts = [System.Collections.Generic.SortedDictionary[int,string]]::new()
        $parts.Add(1, $first.FullName)
        if ($null -eq $SegmentDirectories -or $SegmentDirectories.Count -eq 0) { $SegmentDirectories = @($first.DirectoryName) }
        $visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $scannedFiles = 0
        $pattern = '^' + [regex]::Escape($first.Name) + '\.part([0-9]{4,})$'
        foreach ($directory in $SegmentDirectories) {
            if ($visited.Count -ge 1024) { throw 'More than 1024 segment directories; use the ordered dumpPaths instead.' }
            $entry = Get-Item -LiteralPath $directory
            if ($entry -isnot [System.IO.DirectoryInfo]) { throw "Segment directory is not a directory: $directory" }
            if (-not $visited.Add($entry.FullName)) { continue }
            foreach ($file in $entry.EnumerateFiles()) {
                $scannedFiles++
                if ($scannedFiles -gt 1000000) { throw 'More than 1000000 directory entries; use the ordered dumpPaths instead.' }
                $match = [regex]::Match($file.Name, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if (-not $match.Success) { continue }
                $number = [int]::Parse($match.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
                if ($number -lt 2 -or $parts.ContainsKey($number)) { throw "Invalid or duplicate segment number: $($file.FullName)" }
                $parts.Add($number, $file.FullName)
            }
        }
        $expected = 1
        foreach ($number in $parts.Keys) {
            if ($number -ne $expected) { throw "Missing segment number $expected. Supply every reserve directory or use the record's dumpPaths." }
            $expected++
        }
        $DumpPaths = [string[]]@($parts.Values)
    }

    $outputFullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    if ([System.IO.Path]::GetExtension($outputFullPath) -ine '.dmp') { throw 'OutputPath must end in .dmp.' }
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $bytes = [long]0
    foreach ($path in $DumpPaths) {
        if ([string]::IsNullOrWhiteSpace($path)) { throw 'Every dumpPaths entry must be a nonempty string.' }
        $source = Get-Item -LiteralPath $path
        if ($source -isnot [System.IO.FileInfo]) { throw "Segment is not a file: $path" }
        if (-not $seen.Add($source.FullName)) { throw "Duplicate segment path: $path" }
        if ([string]::Equals($source.FullName, $outputFullPath, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'OutputPath must differ from every source segment.' }
        # Hold read-only handles until completion so a writer cannot change source bytes mid-copy.
        $sourceStream = [System.IO.File]::Open($source.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        $inputs.Add($sourceStream)
        if ($sourceStream.Length -eq 0) { throw "Empty segment: $path" }
        if ($sourceStream.Length -gt [long]::MaxValue - $bytes) { throw 'Combined slice exceeds the supported file length.' }
        $bytes += $sourceStream.Length
    }
    $output = [System.IO.File]::Open($outputFullPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $createdOutput = $true
    foreach ($sourceStream in $inputs) { $sourceStream.CopyTo($output, 1048576) }
    $output.Flush($true)
    if ($output.Length -ne $bytes) { throw 'Joined file length differs from the source stream.' }
    $output.Dispose()
    $output = $null
    if ($PSCmdlet.ParameterSetName -eq 'First') { Assert-CompleteMemory64Dump $outputFullPath }
    [Console]::Out.WriteLine((@{ outputPath = $outputFullPath; bytesWritten = $bytes; segmentCount = $inputs.Count } | ConvertTo-Json -Compress))
    $exitCode = 0
}
catch { [Console]::Error.WriteLine($_.Exception.Message) }
finally {
    if ($null -ne $output) { $output.Dispose() }
    foreach ($sourceStream in $inputs) { $sourceStream.Dispose() }
    # Only remove the new incomplete output owned by this invocation; never overwrite an existing file.
    if ($exitCode -ne 0 -and $createdOutput) { [System.IO.File]::Delete($outputFullPath) }
}
exit $exitCode
