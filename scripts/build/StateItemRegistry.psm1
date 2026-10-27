Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# PowerShell 7 ships Roslyn. Use its C# grammar rather than maintaining a type-name parser.
Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp

function Get-StateRegistryVersion {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    $header = [Text.Encoding]::UTF8.GetBytes("blob $($Bytes.Length)`0")
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try {
        $hash.AppendData($header)
        $hash.AppendData($Bytes)
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    } finally { $hash.Dispose() }
}

function Assert-StateRegistry {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Registry)
    if (@($Registry.Keys).Count -ne 1 -or @($Registry.Keys)[0] -cne 'entries' -or $Registry.entries -isnot [array]) {
        throw 'Registry: expected exactly an entries array.'
    }
    $uids = [Collections.Generic.Dictionary[long, object]]::new()
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $previous = 0L
    $index = 0
    foreach ($entry in $Registry.entries) {
        $label = "Entry[$index]"
        if ($entry -isnot [System.Collections.IDictionary]) { throw "${label}: expected an object." }
        $label += " uid=$($entry['uid']) name=$($entry['name'])"
        foreach ($field in @('uid', 'kind', 'process', 'name', 'comment')) {
            if (-not $entry.Contains($field)) { throw "${label}: missing $field." }
        }
        if (($entry.uid -isnot [long] -and $entry.uid -isnot [int] -and $entry.uid -isnot [bigint]) -or $entry.uid -lt 1) {
            throw "${label}: uid must be an integer >= 1."
        }
        if (-not $uids.TryAdd($entry.uid, $entry)) { throw "${label}: duplicate uid." }
        if ($entry.uid -le $previous) { throw "${label}: entries must be sorted by uid." }
        $previous = $entry.uid
        if ($entry.kind -isnot [string] -or $entry.kind -cnotin @('block', 'item')) { throw "${label}: kind must be block or item." }
        if ($entry.process -isnot [string] -or $entry.process -cnotin @('backend', 'ui')) { throw "${label}: process must be backend or ui." }
        if ($entry.name -isnot [string] -or $entry.name -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or
            [Microsoft.CodeAnalysis.CSharp.SyntaxFacts]::GetKeywordKind($entry.name) -ne [Microsoft.CodeAnalysis.CSharp.SyntaxKind]::None) {
            throw "${label}: name must be an ASCII C# identifier that is not a keyword."
        }
        if (-not $names.Add($entry.process + ':' + $entry.name)) { throw "${label}: duplicate name within process." }
        if ($entry.comment -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.comment)) { throw "${label}: comment must be non-empty text." }
        $allowed = @('uid', 'kind', 'process', 'name', 'comment')
        if ($entry.kind -ceq 'item') {
            $allowed += @('storage', 'type', 'owner', 'perBlock', 'trigger')
            foreach ($field in $allowed) {
                if (-not $entry.Contains($field)) { throw "${label}: missing $field." }
            }
            if ($entry.storage -isnot [string] -or $entry.storage -cnotin @('own', 'reference')) { throw "${label}: storage must be own or reference." }
            if ($entry.type -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.type)) { throw "${label}: type must be a non-empty C# type name." }
            if ([Microsoft.CodeAnalysis.CSharp.SyntaxFactory]::ParseTypeName($entry.type).ContainsDiagnostics) { throw "${label}: type must be a C# type name." }
            if ($entry.perBlock -isnot [bool]) { throw "${label}: perBlock must be boolean." }
            if ($entry.trigger -isnot [string] -or $entry.trigger -cnotin @('event', 'poll')) { throw "${label}: trigger must be event or poll." }
            if ($entry.trigger -ceq 'poll') {
                $allowed += 'pollInterval'
                if (-not $entry.Contains('pollInterval') -or $entry.pollInterval -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.pollInterval)) {
                    throw "${label}: poll requires a pollInterval freedom point path."
                }
            }
            if ($entry.owner -isnot [System.Collections.IDictionary] -or $entry.owner.Count -ne 1) { throw "${label}: owner must contain exactly block or component." }
            $ownerKey = @($entry.owner.Keys)[0]
            if ($ownerKey -ceq 'block') {
                if (($entry.owner.block -isnot [long] -and $entry.owner.block -isnot [int]) -or $entry.owner.block -lt 1) { throw "${label}: owner.block must be a block uid." }
            } elseif ($ownerKey -ceq 'component') {
                if ($entry.owner.component -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.owner.component)) { throw "${label}: owner.component must be non-empty text." }
            } else { throw "${label}: owner must contain exactly block or component." }
        }
        foreach ($field in $entry.Keys) {
            if ($field -cnotin $allowed) { throw "${label}: unknown field $field (or item field on a block)." }
        }
        $index++
    }
    foreach ($entry in $Registry.entries) {
        if ($entry.kind -ceq 'item' -and @($entry.owner.Keys)[0] -ceq 'block') {
            $owner = $null
            if (-not $uids.TryGetValue($entry.owner.block, [ref]$owner) -or $owner.kind -cne 'block' -or $owner.process -cne $entry.process) {
                throw "Entry uid=$($entry.uid) name=$($entry.name): owner.block must reference an existing block in the same process."
            }
        }
    }
}

function Read-StateRegistry {
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Path')][string]$Path,
        [Parameter(Mandatory, ParameterSetName = 'Bytes')][AllowEmptyCollection()][byte[]]$Bytes
    )
    if ($PSCmdlet.ParameterSetName -eq 'Path') { $Bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Path)) }
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 239 -and $Bytes[1] -eq 187 -and $Bytes[2] -eq 191) { throw 'Registry: UTF-8 BOM is not allowed.' }
    if ($Bytes -contains [byte]13) { throw 'Registry: CR bytes are not allowed; use LF.' }
    try { $json = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes) } catch { throw "Registry: invalid UTF-8: $_" }
    try {
        # Check duplicate properties before ConvertFrom-Json can collapse them. The schema has only three object levels.
        $document = [Text.Json.JsonDocument]::Parse($json)
        try {
            $pending = [Collections.Generic.Stack[Text.Json.JsonElement]]::new()
            $pending.Push($document.RootElement)
            while ($pending.Count -gt 0) {
                $node = $pending.Pop()
                if ($node.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                    $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    foreach ($property in $node.EnumerateObject()) {
                        if (-not $keys.Add($property.Name)) { throw "duplicate field $($property.Name) in $($node.GetRawText())" }
                        $pending.Push($property.Value)
                    }
                } elseif ($node.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                    foreach ($element in $node.EnumerateArray()) { $pending.Push($element) }
                }
            }
        } finally { $document.Dispose() }
        $registry = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 16
    } catch { throw "Registry: invalid JSON: $_" }
    if ($registry -isnot [System.Collections.IDictionary]) { throw 'Registry: expected an object.' }
    Assert-StateRegistry $registry
    return $registry
}

function ConvertTo-StateRegistryBytes {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Registry)
    # Only output the schema fields, in its declared order. Validation precedes serialization.
    $canonical = [ordered]@{}
    foreach ($key in $Registry.Keys) { $canonical[$key] = $Registry[$key] }
    if ($canonical['entries'] -is [array]) { $canonical.entries = @($canonical.entries | Sort-Object { $_['uid'] }) }
    Assert-StateRegistry $canonical
    $entries = @(foreach ($entry in $canonical.entries) {
        $ordered = [ordered]@{}
        foreach ($field in @('uid', 'kind', 'process', 'name', 'comment', 'storage', 'type', 'owner', 'perBlock', 'trigger', 'pollInterval')) {
            if ($entry.Contains($field)) { $ordered[$field] = $entry[$field] }
        }
        $ordered
    })
    $json = ConvertTo-Json -InputObject ([ordered]@{ entries = $entries }) -Depth 6 -EscapeHandling Default
    return ,([Text.UTF8Encoding]::new($false).GetBytes(($json -replace "`r`n", "`n") + "`n"))
}

function Write-StateRegistry {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][System.Collections.IDictionary]$Registry)
    $bytes = ConvertTo-StateRegistryBytes $Registry
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Path), $bytes)
}

Export-ModuleMember -Function Get-StateRegistryVersion, Assert-StateRegistry, Read-StateRegistry, ConvertTo-StateRegistryBytes, Write-StateRegistry
