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

# Private schema helpers: callers outside this module always use Assert/Read/Write.
function Assert-StateUid {
    param($Value, [string]$Label)
    if (($Value -isnot [long] -and $Value -isnot [int] -and $Value -isnot [bigint]) -or $Value -lt 1 -or $Value -gt [uint32]::MaxValue) {
        throw "${Label}: uid must be an integer from 1 to 4294967295."
    }
}

function Get-StateReference {
    param($Uids, $Uid, [string]$Kind, [string]$Process, [string]$Label)
    $target = $null
    if (-not $Uids.TryGetValue([long]$Uid, [ref]$target) -or $target.kind -cne $Kind -or $target.process -cne $Process) {
        throw "${Label} must reference an existing $Kind in the same process."
    }
    return $target
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
        Assert-StateUid $entry.uid "${label}"
        if (-not $uids.TryAdd($entry.uid, $entry)) { throw "${label}: duplicate uid." }
        if ($entry.uid -le $previous) { throw "${label}: entries must be sorted by uid." }
        $previous = $entry.uid
        if ($entry.kind -isnot [string] -or $entry.kind -cnotin @('block', 'item', 'condition')) { throw "${label}: kind must be block, item or condition." }
        # desktop and window are the two UI processes after the split (Zig resident, .NET window). ui is the
        # current single UI process; remove it with the commit that moves its last entry to desktop or window.
        if ($entry.process -isnot [string] -or $entry.process -cnotin @('backend', 'ui', 'desktop', 'window')) { throw "${label}: process must be backend, ui, desktop or window." }
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
        if ($entry.kind -ceq 'condition') {
            $allowed += @('node', 'perBlock', 'items', 'conditions', 'children', 'decides')
            foreach ($field in @('perBlock', 'items', 'conditions', 'children')) {
                if (-not $entry.Contains($field)) { throw "${label}: missing $field." }
            }
            if ($entry.perBlock -isnot [bool]) { throw "${label}: perBlock must be boolean." }
            foreach ($field in @('items', 'conditions', 'children')) {
                if ($entry[$field] -isnot [array]) { throw "${label}: $field must be an array." }
            }
            if ($entry.perBlock) {
                if ($entry.Contains('node') -or $entry.Contains('decides')) { throw "${label}: per-block condition must omit node and decides." }
                if ($entry.conditions.Count -ne 0 -or $entry.children.Count -ne 0) { throw "${label}: per-block condition reads items only; conditions and children must be empty." }
            } elseif (-not $entry.Contains('node')) { throw "${label}: non-per-block condition requires node." }
            foreach ($field in @('node', 'decides')) {
                if ($entry.Contains($field)) { Assert-StateUid $entry[$field] "${label}: $field" }
            }
            foreach ($read in $entry.items) {
                if ($read -isnot [System.Collections.IDictionary] -or -not $read.Contains('item')) { throw "${label}: items read must contain item and optional block." }
                foreach ($field in $read.Keys) {
                    if ($field -cnotin @('item', 'block')) { throw "${label}: unknown item read field $field." }
                    Assert-StateUid $read[$field] "${label}: items.$field"
                }
            }
            foreach ($field in @('conditions', 'children')) {
                foreach ($uid in $entry[$field]) { Assert-StateUid $uid "${label}: $field" }
            }
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
    $conditions = @($Registry.entries | Where-Object kind -CEQ 'condition')
    foreach ($entry in $conditions) {
        $label = "Entry uid=$($entry.uid) name=$($entry.name)"
        foreach ($field in @('node', 'decides')) {
            if ($entry.Contains($field)) { $null = Get-StateReference $uids $entry[$field] 'block' $entry.process "${label}: $field" }
        }
        foreach ($read in $entry.items) {
            $item = Get-StateReference $uids $read.item 'item' $entry.process "${label}: items.item"
            if ($read.Contains('block')) {
                $null = Get-StateReference $uids $read.block 'block' $entry.process "${label}: items.block"
                if (-not $item.perBlock) { throw "${label}: items.block is allowed only for a per-block item." }
            } elseif ($item.perBlock -and -not $entry.perBlock) { throw "${label}: per-block item read requires block outside a per-block condition." }
        }
        foreach ($uid in $entry.children) { $null = Get-StateReference $uids $uid 'block' $entry.process "${label}: children" }
        foreach ($uid in $entry.conditions) {
            $lower = Get-StateReference $uids $uid 'condition' $entry.process "${label}: conditions"
            if ($lower.perBlock -or $lower.node -ne $entry.node) { throw "${label}: lower conditions must belong to the same node." }
        }
    }
    # Three-colour DFS. Each frame advances one edge or exits; depth <= condition count,
    # and total work <= 2 * condition count + edge count. No recursive calls.
    $colour = @{}
    $stack = [Collections.Generic.List[object]]::new()
    $edgeCount = 0L
    foreach ($entry in $conditions) { $edgeCount += $entry.conditions.Count }
    $remaining = 2L * $conditions.Count + $edgeCount
    foreach ($entry in $conditions) {
        if ($colour.ContainsKey($entry.uid)) { continue }
        $colour[$entry.uid] = 1
        $stack.Add(@{ entry = $entry; next = 0 })
        while ($stack.Count -gt 0) {
            if ($remaining-- -le 0 -or $stack.Count -gt $conditions.Count) { throw 'Registry: condition traversal bound exceeded.' }
            $frame = $stack[$stack.Count - 1]
            if ($frame.next -eq $frame.entry.conditions.Count) {
                $colour[$frame.entry.uid] = 2
                $stack.RemoveAt($stack.Count - 1)
                continue
            }
            $uid = $frame.entry.conditions[$frame.next++]
            if ($colour.ContainsKey($uid)) {
                if ($colour[$uid] -eq 1) { throw "Entry uid=$($frame.entry.uid) name=$($frame.entry.name): condition cycle in node $($frame.entry.node)." }
            } else {
                $colour[$uid] = 1
                $stack.Add(@{ entry = $uids[$uid]; next = 0 })
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
        # Check duplicate properties before ConvertFrom-Json can collapse them. JsonDocument bounds nesting to 64 levels.
        $document = [Text.Json.JsonDocument]::Parse($json)
        try {
            $pending = [Collections.Generic.Stack[Text.Json.JsonElement]]::new()
            $pending.Push($document.RootElement)
            $remaining = $json.Length + 1L
            while ($pending.Count -gt 0) {
                if ($remaining-- -le 0) { throw 'JSON traversal bound exceeded.' }
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
        foreach ($field in @('uid', 'kind', 'process', 'name', 'comment', 'storage', 'type', 'owner', 'perBlock', 'trigger', 'pollInterval', 'node', 'items', 'conditions', 'children', 'decides')) {
            if ($entry.Contains($field)) {
                if ($field -ceq 'items') {
                    $ordered[$field] = @(foreach ($read in $entry.items) {
                        $itemRead = [ordered]@{ item = $read.item }
                        if ($read.Contains('block')) { $itemRead.block = $read.block }
                        $itemRead
                    })
                } else { $ordered[$field] = $entry[$field] }
            }
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
