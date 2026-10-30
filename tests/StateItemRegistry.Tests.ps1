[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repo 'scripts/build/StateItemRegistry.psm1') -Force
$tool = Join-Path $repo 'scripts/tools/Manage-StateItems.ps1'
$generator = Join-Path $repo 'scripts/build/Generate-StateItems.ps1'
$script:count = 0
$fixedTime = [datetime]::new(2001, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:count++
}
function Assert-Rejected([scriptblock]$Operation, [string]$Reason) {
    $caught = $null
    try { & $Operation | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-True ($null -ne $caught -and $caught -match $Reason) "Expected rejection /$Reason/; got: $caught"
}
function Get-Fixture {
    ConvertFrom-Json -AsHashtable @'
{"entries":[
{"uid":1,"kind":"block","process":"backend","name":"Program","comment":"Backend root."},
{"uid":2,"kind":"block","process":"ui","name":"Program","comment":"UI root."},
{"uid":3,"kind":"item","process":"backend","name":"Ready","comment":"Ready: café, 中文, \"quoted\" and \\path.\nSecond line.","storage":"reference","type":"bool","owner":{"block":1},"perBlock":false,"trigger":"event","changeEvent":"Fixture owner transition"},
{"uid":4,"kind":"item","process":"ui","name":"Preference","comment":"Polled preference.","storage":"own","type":"bool","owner":{"component":"Preferences"},"perBlock":true,"trigger":"poll","pollInterval":"/ui/preference/interval"}
]}
'@
}
function Assert-Invalid([scriptblock]$Change, [string]$Reason) {
    $fixture = Get-Fixture
    & $Change $fixture
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $fixture -Depth 8 -Compress))
    Assert-Rejected { Read-StateRegistry -Bytes $bytes } $Reason
}

$marker = Join-Path $WorkRoot '.generated-workspace.json'
if (-not (Test-Path -LiteralPath $marker) -or (Get-Content $marker -Raw | ConvertFrom-Json).state -ne 'running') {
    throw 'WorkRoot must be a running task registered through Manage-GeneratedWorkspace.ps1 New.'
}
$scratch = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ('fixture ' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$path = Join-Path $scratch 'registry.json'
$table = Get-Fixture
Write-StateRegistry $path $table
$bytes = [IO.File]::ReadAllBytes($path)
$read = Read-StateRegistry -Path $path
Assert-True ($read.entries.Count -eq 4) 'Cross-process duplicate names are valid.'
Assert-True ([Convert]::ToBase64String((ConvertTo-StateRegistryBytes $read)) -ceq [Convert]::ToBase64String($bytes)) 'Canonical byte round trip.'
$text = [Text.Encoding]::UTF8.GetString($bytes)
Assert-True ($text.Contains('café, 中文') -and -not $text.Contains('\u4e2d')) 'Non-ASCII is written as-is.'
Assert-True ($text.EndsWith("`n") -and -not ($bytes -contains [byte]13) -and $bytes[0] -eq 123) 'LF, no BOM, final newline.'
Assert-True ($text.StartsWith("{`n  `"entries`": [`n    {`n      `"uid`": 1,")) 'Two-space indentation and field order.'
$table.entries = @($table.entries[3], $table.entries[1], $table.entries[0], $table.entries[2])
Assert-True ([Convert]::ToBase64String((ConvertTo-StateRegistryBytes $table)) -ceq [Convert]::ToBase64String($bytes)) 'Canonical writer sorts by uid.'

Assert-Invalid { param($r) $r.extra = 1 } 'entries array'
Assert-Invalid { param($r) $r.entries = @{} } 'entries array'
Assert-Invalid { param($r) $r.entries[0] = $null } 'Entry\[0\].*object'
foreach ($field in @('uid','kind','process','name','comment')) {
    Assert-Invalid { param($r) $r.entries[0].Remove($field) | Out-Null } "Entry.*missing $field"
}
foreach ($uid in @(0, -1, 1.5, '1', $true)) { Assert-Invalid { param($r) $r.entries[0].uid = $uid } 'Entry.*integer' }
Assert-Invalid { param($r) $r.entries[1].uid = 1 } 'Entry.*duplicate uid'
Assert-Invalid { param($r) $r.entries = @($r.entries[1], $r.entries[0], $r.entries[2], $r.entries[3]) } 'Entry.*sorted'
Assert-Invalid { param($r) $r.entries[0].kind = 'unknown' } 'Entry.*kind'
Assert-Invalid { param($r) $r.entries[0].process = 'other' } 'Entry.*process'
foreach ($name in @('', '1bad', 'bad-name', 'class', '__arglist', '@Program', "Pro$([char]0x200C)gram", 'Prögram')) { Assert-Invalid { param($r) $r.entries[0].name = $name } 'Entry.*identifier' }
Assert-Invalid { param($r) $r.entries[2].name = 'Program' } 'Entry.*duplicate name'
foreach ($comment in @('', '  ', 42, $null)) { Assert-Invalid { param($r) $r.entries[0].comment = $comment } 'Entry.*comment' }
foreach ($field in @('storage','type','owner','perBlock','trigger','pollInterval')) {
    Assert-Invalid { param($r) $r.entries[0][$field] = 'x' } 'Entry.*unknown field'
}
Assert-Invalid { param($r) $r.entries[2].mystery = 1 } 'Entry.*unknown field'
Assert-Invalid { param($r) $r.entries[2].Name = 'Other' } 'Entry.*unknown field'
foreach ($field in @('storage','type','owner','perBlock','trigger')) {
    Assert-Invalid { param($r) $r.entries[2].Remove($field) | Out-Null } "Entry.*missing $field"
}
Assert-Invalid { param($r) $r.entries[2].storage = 'copy' } 'Entry.*storage'
Assert-Invalid { param($r) $r.entries[2].type = '' } 'Entry.*type'
Assert-Invalid { param($r) $r.entries[2].type = 'not a type' } 'Entry.*type'
Assert-Invalid { param($r) $r.entries[2].perBlock = 'false' } 'Entry.*boolean'
Assert-Invalid { param($r) $r.entries[2].trigger = 'timer' } 'Entry.*trigger'
Assert-Invalid { param($r) $r.entries[2].Remove('changeEvent') | Out-Null } 'Entry.*changeEvent'
foreach ($eventName in @('', ' ', 42, $null)) {
    Assert-Invalid { param($r) $r.entries[2].changeEvent = $eventName } 'Entry.*changeEvent'
}
Assert-Invalid { param($r) $r.entries[0].changeEvent = 'Unexpected' } 'Entry.*unknown field'
Assert-Invalid { param($r) $r.entries[3].changeEvent = 'Unexpected' } 'Entry.*unknown field'
Assert-Invalid { param($r) $r.entries[3].Remove('pollInterval') | Out-Null } 'Entry.*pollInterval'
Assert-Invalid { param($r) $r.entries[3].pollInterval = ' ' } 'Entry.*pollInterval'
Assert-Invalid { param($r) $r.entries[2].pollInterval = '/extra' } 'Entry.*unknown field'
foreach ($owner in @(@{}, @{block=1;component='two'}, @{unknown='x'}, @{block=0}, @{block='1'}, @{component=''}, @{component=1})) {
    Assert-Invalid { param($r) $r.entries[2].owner = $owner } 'Entry.*owner'
}
foreach ($owner in @(2, 3, 99)) { Assert-Invalid { param($r) $r.entries[2].owner.block = $owner } 'Entry.*same process' }
Assert-Rejected { Read-StateRegistry -Bytes ([byte[]](@(239,187,191) + $bytes)) } 'BOM'
Assert-Rejected { Read-StateRegistry -Bytes ([byte[]](@(13) + $bytes)) } 'CR bytes'
Assert-Rejected { Read-StateRegistry -Bytes ([byte[]]@(255)) } 'UTF-8'
Assert-Rejected { Read-StateRegistry -Bytes ([Text.Encoding]::UTF8.GetBytes('{"entries":[],"entries":[]}')) } 'duplicate field'
Assert-Rejected { Read-StateRegistry -Bytes ([Text.Encoding]::UTF8.GetBytes('{"entries":[{"uid":1,"uid":2}]}')) } 'duplicate field'
Assert-Rejected { Read-StateRegistry -Bytes ([Text.Encoding]::UTF8.GetBytes('{')) } 'invalid JSON'
Assert-Rejected { Read-StateRegistry -Bytes ([Text.Encoding]::UTF8.GetBytes('[]')) } 'object'

$version = Get-StateRegistryVersion $bytes
$gitVersion = & git hash-object --no-filters -- $path
Assert-True ($LASTEXITCODE -eq 0 -and $version -ceq $gitVersion) 'Version equals git hash-object.'
& git -C $scratch init --quiet
Assert-True ($LASTEXITCODE -eq 0) 'Isolated fixture git repository initialized.'
$storedVersion = & git -C $scratch hash-object -w --no-filters -- $path
Assert-True ($LASTEXITCODE -eq 0 -and $storedVersion -ceq $version) 'Fixture blob stored in isolated repository.'
$current = & $tool -Action Resolve -Registry $path -Uid 3 | ConvertFrom-Json
Assert-True ($current.name -ceq 'Ready' -and $current.owner.block -eq 1) 'Resolve current file includes item fields.'
$export = & $tool -Action Export -Registry $path -Destination (Join-Path $scratch 'tables')
Assert-True ($export -ceq (Join-Path $scratch "tables/$version.json")) 'Export path is versioned.'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($export)) -ceq [Convert]::ToBase64String($bytes)) 'Export preserves exact bytes.'
[IO.File]::SetLastWriteTimeUtc($export, $fixedTime)
$null = & $tool -Action Export -Registry $path -Destination (Join-Path $scratch 'tables')
Assert-True ([IO.File]::GetLastWriteTimeUtc($export) -eq $fixedTime) 'Export is idempotent.'
$published = & $tool -Action Resolve -Registry $path -Uid 4 -Version $version -TableDirectory (Join-Path $scratch 'tables') | ConvertFrom-Json
Assert-True ($published.pollInterval -ceq '/ui/preference/interval') 'Resolve table directory.'
$historical = & $tool -Action Resolve -Registry $path -Uid 3 -Version $version | ConvertFrom-Json
Assert-True ($historical.comment -ceq $current.comment) 'Resolve git preserves UTF-8 and embedded newlines.'
$oldTable = Get-Fixture
$oldTable.entries[2].Remove('changeEvent') | Out-Null
$oldBytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $oldTable -Depth 8 -Compress) + "`n")
$oldPath = Join-Path $scratch 'published-before-named-events.json'
[IO.File]::WriteAllBytes($oldPath, $oldBytes)
$oldVersion = & git -C $scratch hash-object -w --no-filters -- $oldPath
Assert-True ($LASTEXITCODE -eq 0) 'Historical pre-metadata registry fixture stored by exact blob version.'
$oldEntry = & $tool -Action Resolve -Registry $path -Uid 3 -Version $oldVersion | ConvertFrom-Json
Assert-True ($oldEntry.name -ceq 'Ready' -and 'changeEvent' -cnotin $oldEntry.PSObject.Properties.Name) 'Version lookup preserves honestly absent historical event metadata.'
Assert-Rejected { Read-StateRegistry -Bytes $oldBytes } 'Entry.*changeEvent'
Assert-Rejected { & $generator -Registry $oldPath -Process backend -Namespace Invalid -Output (Join-Path $scratch 'old-invalid.g.cs') } 'Entry.*changeEvent'
Assert-Rejected { & $tool -Action Add -Registry $oldPath -Kind block -Process backend -Name Additional -Comment 'Current write must validate old input.' } 'Entry.*changeEvent'
$historicalRead = Read-StateRegistry -Bytes $oldBytes -HistoricalTable
Assert-Rejected { ConvertTo-StateRegistryBytes $historicalRead } 'Entry.*changeEvent'
$fallback = & $tool -Action Resolve -Registry $path -Uid 1 -Version $version -TableDirectory (Join-Path $scratch 'absent') | ConvertFrom-Json
Assert-True ($fallback.name -ceq 'Program') 'Missing published table falls back to git.'
[IO.File]::WriteAllText($export, 'different')
Assert-Rejected { & $tool -Action Export -Registry $path -Destination (Join-Path $scratch 'tables') } 'different bytes'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 3 -Version $version -TableDirectory (Join-Path $scratch 'tables') } 'hash mismatch'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 1 -Version ('0' * 40) } 'could not be found'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 99 } 'uid=99.*not found'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 0 } 'Uid'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 1 -Version '../invalid' } 'Version'

# Catalog maintenance uses the same validator as generation, and preserves the
# original bytes whenever an edit would leave an invalid reference or field.
$mutationPath = Join-Path $scratch 'mutation-registry.json'
Write-StateRegistry $mutationPath (Get-Fixture)
$mutationBytes = [IO.File]::ReadAllBytes($mutationPath)
Assert-Rejected { & $tool -Action Remove -Registry $mutationPath } 'Uid'
Assert-Rejected { & $tool -Action Remove -Registry $mutationPath -Uids 99 } '99.*not found'
Assert-Rejected { & $tool -Action Remove -Registry $mutationPath -Uids 1 } 'owner.block'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($mutationPath)) -ceq [Convert]::ToBase64String($mutationBytes)) 'Rejected removal preserves exact bytes.'
$removed = & $tool -Action Remove -Registry $mutationPath -Uids 2
Assert-True (@((Read-StateRegistry -Path $mutationPath).entries).Count -eq 3) 'Remove leaves a valid registry with the selected UID absent.'
Assert-True (-not @((Read-StateRegistry -Path $mutationPath).entries | Where-Object uid -EQ 2).Count) 'Removed entry is absent.'
$replacement = & $tool -Action Add -Registry $mutationPath -Kind block -Process ui -Name Replacement -Comment 'Reuses a removed identifier.'
Assert-True ($replacement -eq 2) 'Tool removal makes the smallest UID reusable.'
$changed = & $tool -Action SetItem -Registry $mutationPath -Uid 3 -Comment 'Updated: café, 中文.' -OwnerComponent 'Fixture owner' -ChangeEvent 'Fixture owner publication'
$updated = @((Read-StateRegistry -Path $mutationPath).entries | Where-Object uid -EQ 3)[0]
Assert-True ($changed -eq 3 -and $updated.comment -ceq 'Updated: café, 中文.' -and $updated.owner.component -ceq 'Fixture owner') 'SetItem preserves UID and applies canonical owner and comment.'
Assert-True ($updated.changeEvent -ceq 'Fixture owner publication') 'SetItem updates event publication metadata.'
& $tool -Action SetItem -Registry $mutationPath -Uid 3 -ChangeEvent 'Fixture owner changed again' | Out-Null
Assert-True (@((Read-StateRegistry -Path $mutationPath).entries | Where-Object uid -EQ 3)[0].changeEvent -ceq 'Fixture owner changed again') 'SetItem permits a change-event-only update.'
$mutationBytes = [IO.File]::ReadAllBytes($mutationPath)
Assert-True ([Convert]::ToBase64String((ConvertTo-StateRegistryBytes (Read-StateRegistry -Path $mutationPath))) -ceq [Convert]::ToBase64String($mutationBytes)) 'SetItem round trip is canonical.'
Assert-Rejected { & $tool -Action Rename -Registry $mutationPath -Uid 3 -Name Program } 'duplicate name'
Assert-Rejected { & $tool -Action SetItem -Registry $mutationPath -Uid 3 -Kind block } 'does not accept -Kind'
Assert-Rejected { & $tool -Action SetItem -Registry $mutationPath -Uid 3 -OwnerBlock 1 -OwnerComponent Both } 'owner form'
Assert-Rejected { & $tool -Action SetItem -Registry $mutationPath -Uid 99 -Comment 'Missing.' } '99.*not found'
Assert-Rejected { & $tool -Action SetItem -Registry $mutationPath -Uid 3 -ItemReads @(@{item=99}) } 'does not accept -ItemReads'
Assert-Rejected { & $tool -Action SetItem -Registry $mutationPath -Uid 3 -ChangeEvent ' ' } 'changeEvent'
Assert-Rejected { & $tool -Action SetItem -Registry $mutationPath -Uid 4 -ChangeEvent 'Not a poll event' } 'unknown field'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($mutationPath)) -ceq [Convert]::ToBase64String($mutationBytes)) 'Rejected updates preserve exact bytes.'
# Simulate removal of a released UID: allocation must reuse it with no exception.
$table = Get-Fixture
$table.entries = @($table.entries | Where-Object { $_.uid -ne 2 })
Write-StateRegistry $path $table
$added = & $tool -Action Add -Registry $path -Kind block -Process ui -Name Replacement -Comment 'A replacement block.'
Assert-True ($added -eq 2) 'Add fills smallest gap.'
$added = & $tool -Action Add -Registry $path -Kind item -Process backend -Name Active -Comment 'Fixture activity.' -Storage reference -Type bool -OwnerComponent 'Fixture owner' -PerBlock $true -Trigger event -ChangeEvent 'Fixture activity changed'
Assert-True ($added -eq 5) 'Add appends when no gap remains.'
$added = & $tool -Action Add -Registry $path -Kind item -Process backend -Name Poll -Comment 'Polled state.' -Storage own -Type bool -OwnerBlock 1 -PerBlock $false -Trigger poll -PollInterval '/poll/interval'
Assert-True ($added -eq 6) 'Add accepts a block owner and polling interval.'
$afterAdd = [IO.File]::ReadAllBytes($path)
Assert-True ([Convert]::ToBase64String((ConvertTo-StateRegistryBytes (Read-StateRegistry -Path $path))) -ceq [Convert]::ToBase64String($afterAdd)) 'Add writes canonically.'
Assert-Rejected { & $tool -Action Add -Registry $path -Kind block -Process ui -Name Replacement -Comment 'Duplicate.' } 'duplicate name'
Assert-Rejected { & $tool -Action Add -Registry $path -Kind block -Process ui -Name Extra -Comment 'Extra.' -Type bool } 'cannot use'
Assert-Rejected { & $tool -Action Add -Registry $path -Kind item -Process ui -Name Extra -Comment 'Extra.' } 'requires'
Assert-Rejected { & $tool -Action Add -Registry $path -Kind item -Process ui -Name Extra -Comment 'Extra.' -Storage own -Type bool -OwnerBlock 2 -OwnerComponent Both -PerBlock $false -Trigger event } 'exactly one'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq [Convert]::ToBase64String($afterAdd)) 'Rejected adds leave the registry unchanged.'
[IO.File]::WriteAllBytes($export, $bytes)
foreach ($arguments in @(@{ Version=$version }, @{ Version=$version; TableDirectory=(Join-Path $scratch 'tables') })) {
    $old = & $tool -Action Resolve -Registry $path -Uid 2 @arguments | ConvertFrom-Json
    Assert-True ($old.name -ceq 'Program') 'Versioned resolution uses the historical entry after UID reuse.'
}

Write-StateRegistry $path (Get-Fixture)
foreach ($process in @('backend','ui')) {
    $output = Join-Path $scratch "$process.g.cs"
    & $generator -Registry $path -Process $process -Namespace "Fixture_$process" -Output $output
    $source = [IO.File]::ReadAllText($output)
    $program = if ($process -eq 'backend') { 1 } else { 2 }
    Assert-True ($source.Contains("public const int Program = $program;") -and $source.Contains("Version = `"$version`"")) "Generated $process constants and version."
    Assert-True ($source.Contains('#if RM_STATE_NAMES') -and $source.Contains('internal static class StateNames') -and $source.Contains('_ => null')) "Generated $process conditional names."
    Assert-True ($source.Contains('IReadOnlyList<StateItemDeclaration> Items') -and $source.Contains('IReadOnlyList<int> Blocks')) "Generated $process typed declarations."
    Assert-True ($source.Contains('typeof(bool)') -and $source.Contains('StateItemTrigger.')) "Generated $process declaration type and trigger."
    if ($process -eq 'backend') {
        Assert-True (-not $source.Contains('"Fixture owner transition"')) 'Development event names are validated in the registry without runtime string state.'
    }
    $other = if ($process -eq 'backend') { 'Preference' } else { 'Ready' }
    Assert-True (-not $source.Contains("public const int $other =")) "Generated $process excludes other process."
    [IO.File]::SetLastWriteTimeUtc($output, $fixedTime)
    & $generator -Registry $path -Process $process -Namespace "Fixture_$process" -Output $output
    Assert-True ([IO.File]::GetLastWriteTimeUtc($output) -eq $fixedTime) 'Unchanged generation does not rewrite.'
    # Compile the same output with and without the switch, without a repository or SDK project.
    foreach ($names in @($true, $false)) {
        $ns = "Fixture_${process}_$names"
        $compiled = $source.Replace('using ResourceManager.Shared.BusinessBlocks.State;', "using $ns.SharedState;").Replace("namespace Fixture_$process;", "namespace $ns {")
        $probe = "public class Probe { public static int Program => BlockUids.Program; }"
        # Compile the actual shared declaration contract under a unique fixture namespace.
        $sharedState = [IO.File]::ReadAllText((Join-Path $repo 'src/Shared/BusinessBlocks/State/StateRead.cs')).Replace(
            'namespace ResourceManager.Shared.BusinessBlocks.State;', "namespace $ns.SharedState {")
        $options = if ($names) { @('/define:RM_STATE_NAMES') } else { @('/optimize') }
        $types = Add-Type -TypeDefinition ("using System;`n" + $compiled + "`n" + $probe + "`n}`n" + $sharedState + "`n}") -CompilerOptions $options -PassThru
        $assembly = $types[0].Assembly
        $registryType = $assembly.GetType("$ns.StateRegistry")
        $declarations = @($registryType.GetProperty('Items').GetValue($null))
        Assert-True ($declarations.Count -eq 1) "Generated $process declaration count."
        $declaration = $declarations[0]
        $expectedItem = if ($process -eq 'backend') { 3 } else { 4 }
        Assert-True ($declaration.Uid -eq $expectedItem -and $declaration.ValueType -eq [bool]) 'Declaration preserves UID and type.'
        Assert-True ($declaration.PerBlock -eq ($process -eq 'ui')) 'Declaration preserves per-block shape.'
        if ($process -eq 'backend') {
            Assert-True ($declaration.Storage.ToString() -ceq 'Reference' -and $declaration.Owner.BlockUid -eq 1) 'Reference declaration preserves block owner.'
        } else {
            Assert-True ($declaration.Storage.ToString() -ceq 'Own' -and $declaration.PollInterval -ceq '/ui/preference/interval') 'Owned declaration preserves polling metadata.'
            Assert-True (($null -ne $declaration.Owner.ComponentLabel) -eq $names) 'Component owner label follows names switch.'
        }
        $nameType = $assembly.GetType("$ns.StateNames")
        Assert-True (($null -ne $nameType) -eq $names) "Compiled names present=$names."
        if ($names) {
            Assert-True ($nameType.GetMethod('Name').Invoke($null, @([int]$program)) -ceq 'Program') 'Name returns Program.'
            $item = if ($process -eq 'backend') { 3 } else { 4 }
            $expected = (Get-Fixture).entries[$item - 1].comment
            Assert-True ($nameType.GetMethod('Comment').Invoke($null, @([int]$item)) -ceq $expected) 'Comment strings survive C# escaping.'
        }
    }
}
[IO.File]::WriteAllText($path, '{"entries":[{"uid":0}]}')
Assert-Rejected { & $generator -Registry $path -Process backend -Namespace Invalid -Output (Join-Path $scratch 'invalid.g.cs') } 'Entry.*missing'
$production = Read-StateRegistry -Path (Join-Path $repo 'src/Shared/StateItems/state-items.json')
$volume = @($production.entries | Where-Object name -CEQ 'DeviceTopologyVolumeFreeSpaceCurrent')
Assert-True ($volume.Count -eq 1) 'The independent volume free-space current is registered.'
Assert-True ($volume[0].kind -ceq 'item' -and $volume[0].owner.block -eq 33 -and $volume[0].trigger -ceq 'event') 'Volume free-space current belongs to the topology owner and publishes change events.'
# Catalog counts retain all dev desktop/window entries and include the replayed no-demand facts.
Assert-True ($production.entries.Count -eq 168) 'Production catalog contains 62 blocks, 67 items and 39 conditions.'
Assert-True (@($production.entries | Where-Object kind -CEQ 'block').Count -eq 62) 'Production catalog contains 62 blocks.'
Assert-True (@($production.entries | Where-Object kind -CEQ 'item').Count -eq 67) 'Production catalog contains 67 items.'
Assert-True (@($production.entries | Where-Object kind -CEQ 'condition').Count -eq 39) 'Production catalog contains 39 conditions.'
Assert-True (($production.entries | Where-Object uid -EQ 41).name -ceq 'MainFrontend') 'MainRoot is renamed without changing UID 41.'
Assert-True (($production.entries | Where-Object uid -EQ 42).name -ceq 'Tray') 'TrayRoot is renamed without changing UID 42.'
Assert-True (@($production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq 'Frontend' -and $_.kind -ceq 'block' }).Count -eq 1) 'The resident has one Frontend wrapper declaration.'
$integration = @($production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq 'SystemIntegration' -and $_.kind -ceq 'block' })
Assert-True ($integration.Count -eq 1) 'The resident has one SystemIntegration wrapper declaration.'
foreach ($name in @('ForceTerminateHotkeyWanted','TaskManagerReplacementWanted')) {
    $condition = $production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq $name }
    Assert-True ($condition.node -eq $integration[0].uid) "$name is decided by the system integration node."
}
Assert-True (@($production.entries | Where-Object name -CIn @('MainTreeWanted','TrayTreeWanted')).Count -eq 0) 'The obsolete root-to-two-branches conditions are removed.'
$active = $production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq 'ProgramActive' }
$frontend = $production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq 'Frontend' }
$started = $production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq 'ProgramStarted' }
$ending = $production.entries | Where-Object { $_.process -ceq 'desktop' -and $_.name -ceq 'ProgramShuttingDown' }
Assert-True ($active.decides -eq $frontend.uid -and $active.items.Count -eq 1 -and $active.items[0].item -eq $started.uid) 'ProgramActive decides Frontend using only ProgramStarted.'
Assert-True (@($production.entries | Where-Object kind -CEQ condition | ForEach-Object items | Where-Object item -EQ $ending.uid).Count -eq 0) 'ProgramShuttingDown is reserved for controller shutdown, with no condition readers.'
Assert-True (@($production.entries | Where-Object process -CEQ 'ui').Count -eq 0) 'The old ui process has no remaining catalog entries.'
foreach ($uid in @(40,41,42,43,44,46,47,50,58,59,60,61)) {
    Assert-True (($production.entries | Where-Object uid -EQ $uid).process -ceq 'desktop') "Existing resident UID $uid is preserved."
}
foreach ($uid in @(45,48,49)) {
    Assert-True (($production.entries | Where-Object uid -EQ $uid).process -ceq 'window') "Existing window UID $uid is preserved."
}

Write-StateRegistry $path (Get-Fixture)
$beforeMove = [IO.File]::ReadAllBytes($path)
Assert-Rejected { & $tool -Action Move -Registry $path -Process desktop -Uids @(1) } 'same process'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq [Convert]::ToBase64String($beforeMove)) 'An invalid owner-only move leaves the file unchanged.'
Assert-Rejected { & $tool -Action Move -Registry $path -Process desktop -Uids @(1,1) } 'distinct'
Assert-Rejected { & $tool -Action Move -Registry $path -Process desktop -Uids @(99) } 'not found'
& $tool -Action Move -Registry $path -Process desktop -Uids @(1,3)
$moved = Read-StateRegistry -Path $path
Assert-True ($moved.entries[0].uid -eq 1 -and $moved.entries[0].process -ceq 'desktop' -and $moved.entries[2].owner.block -eq 1) 'Move atomically migrates owners and items, preserving their UIDs.'
& $tool -Action Move -Registry $path -Process window -Uids @(2,4)
foreach ($process in @('desktop','window')) {
    $output = Join-Path $scratch "$process.g.cs"
    & $generator -Registry $path -Process $process -Namespace "Fixture_$process" -Output $output
    Assert-True ([IO.File]::ReadAllText($output).Contains('public const int Program =')) "The split $process catalog generates independently."
}
# Host-valued items stay with their process owner; only their presence and
# events cross the controller boundary. Conditions continue to read bools.
$zigOutput = Join-Path $scratch 'desktop.g.zig'
& $generator -Registry (Join-Path $repo 'src/Shared/StateItems/state-items.json') -Process desktop -Language Zig -Output $zigOutput
$zigSource = [IO.File]::ReadAllText($zigOutput)
Assert-True ($zigSource.Contains('.process = "desktop"') -and $zigSource.Contains('pub const ConditionUids') -and $zigSource.Contains('pub const @"Program" = 40;')) 'Resident Zig table contains its process, original root UID and registered conditions.'
Assert-True (-not $zigSource.Contains('"Program" ,') -and -not $zigSource.Contains('.name = "')) 'Zig production names are absent by default.'
Assert-True (-not $zigSource.Contains('pub const @"MainWindow"')) 'The resident table excludes child window process blocks.'
Assert-True ($zigSource.Contains('.uid = 109, .storage = .reference, .value_type = .host') -and $zigSource.Contains('pub const @"BackendSession" = 109;')) 'The full resident backend session maps to a host-valued reference.'
$session = $production.entries | Where-Object uid -EQ 109
Assert-True ($session.owner.block -eq 44 -and $session.type -ceq 'ResourceManager.NativeUi.LegacyStateItems.DesktopBackendSession') 'The complete session has the backend connection as its sole owner and a concrete host payload type.'
Assert-True (@($production.entries | Where-Object kind -CEQ 'condition' | ForEach-Object items | Where-Object item -EQ 109).Count -eq 0) 'No decision condition reads a host payload.'
Write-StateRegistry $path (Get-Fixture)
& $tool -Action Rename -Registry $path -Uid 1 -Name MainFrontend -Comment 'Renamed wrapper.'
$renamed = Read-StateRegistry -Path $path
Assert-True ($renamed.entries[0].uid -eq 1 -and $renamed.entries[0].name -ceq 'MainFrontend' -and $renamed.entries[2].owner.block -eq 1) 'Rename preserves UID and references.'
Assert-True ($renamed.entries[0].comment -ceq 'Renamed wrapper.') 'Rename can update the description together with the name.'
$beforeEdit = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
Assert-Rejected { & $tool -Action Rename -Registry $path -Uid 3 -Name MainFrontend } 'duplicate name'
Assert-Rejected { & $tool -Action Rename -Registry $path -Uid 1 -Name 'bad-name' } 'identifier'
Assert-Rejected { & $tool -Action Rename -Registry $path -Uid 99 -Name Missing } 'not found'
Assert-Rejected { & $tool -Action Remove -Registry $path -Uids @(1) } 'same process'
Assert-Rejected { & $tool -Action Remove -Registry $path -Uids @(1,1) } 'distinct'
Assert-Rejected { & $tool -Action Remove -Registry $path -Uids @(99) } 'not found'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq $beforeEdit) 'Rejected renames and removals leave the registry unchanged.'
& $tool -Action Remove -Registry $path -Uids @(1,3)
$removed = Read-StateRegistry -Path $path
Assert-True ($removed.entries.Count -eq 2 -and $removed.entries[0].uid -eq 2 -and $removed.entries[1].uid -eq 4) 'Remove keeps surviving UIDs and validates a whole dependency group.'
Write-StateRegistry $path (Get-Fixture)
$desktopAlways = & $tool -Action Add -Registry $path -Kind condition -Process desktop -Name AlwaysWanted -Comment 'Fixture per-block unconditional top.' -PerBlock $true
$backendAlways = & $tool -Action Add -Registry $path -Kind condition -Process backend -Name AlwaysWanted -Comment 'Fixture backend unconditional top.' -PerBlock $true
Assert-True ($desktopAlways -ne $backendAlways) 'The same condition name in two process catalogs receives different UIDs.'
$scoped = Read-StateRegistry -Path $path
Assert-True (@($scoped.entries | Where-Object name -CEQ AlwaysWanted).Count -eq 2) 'Per-process names permit separate AlwaysWanted declarations.'
$scoped.entries[-1].uid = $desktopAlways
Assert-Rejected { Write-StateRegistry $path $scoped } 'duplicate uid'
$frontend = & $tool -Action Add -Registry $path -Kind block -Process backend -Name Frontend -Comment 'Fixture wrapper.'
$active = & $tool -Action Add -Registry $path -Kind condition -Process backend -Name ProgramActive -Comment 'Fixture program gate.' -PerBlock $false -Node 1 -ItemReads @(@{item=3})
& $tool -Action SetCondition -Registry $path -Uid $active -Decides $frontend -ItemReads @() -Comment 'The program start gate.'
$updated = (Read-StateRegistry -Path $path).entries | Where-Object uid -EQ $active
Assert-True ($updated.decides -eq $frontend -and $updated.node -eq 1 -and $updated.items.Count -eq 0) 'SetCondition preserves identity and node while atomically replacing target and reads.'
Assert-True ($updated.comment -ceq 'The program start gate.') 'SetCondition updates the condition description.'
& $tool -Action SetCondition -Registry $path -Uid $active -ItemReads @(@{item=3})
$updated = (Read-StateRegistry -Path $path).entries | Where-Object uid -EQ $active
Assert-True ($updated.items.Count -eq 1 -and $updated.items[0].item -eq 3 -and $updated.decides -eq $frontend) 'SetCondition changes only explicitly supplied fields.'
$beforeCondition = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
Assert-Rejected { & $tool -Action SetCondition -Registry $path -Uid $active -Decides 2 } 'same process'
Assert-Rejected { & $tool -Action SetCondition -Registry $path -Uid $desktopAlways -Decides $frontend } 'perBlock|per-block'
Assert-Rejected { & $tool -Action SetCondition -Registry $path -Uid $active -ItemReads @(@{item=4}) } 'same process'
Assert-Rejected { & $tool -Action SetCondition -Registry $path -Uid 1 -Decides $frontend } 'condition UID'
Assert-Rejected { & $tool -Action SetCondition -Registry $path -Uid 999 -Decides $frontend } 'not found'
Assert-Rejected { & $tool -Action SetCondition -Registry $path -Uid $active } 'at least one'
Assert-True ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -ceq $beforeCondition) 'Rejected condition edits leave the registry unchanged.'
"Passed $script:count assertions. Fixtures: $scratch"
