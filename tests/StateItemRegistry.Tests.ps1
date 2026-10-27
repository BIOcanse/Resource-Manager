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
{"uid":3,"kind":"item","process":"backend","name":"Ready","comment":"Ready: café, 中文, \"quoted\" and \\path.\nSecond line.","storage":"reference","type":"bool","owner":{"block":1},"perBlock":false,"trigger":"event"},
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
Assert-Invalid { param($r) $r.entries[0].kind = 'condition' } 'Entry.*kind'
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
$fallback = & $tool -Action Resolve -Registry $path -Uid 1 -Version $version -TableDirectory (Join-Path $scratch 'absent') | ConvertFrom-Json
Assert-True ($fallback.name -ceq 'Program') 'Missing published table falls back to git.'
[IO.File]::WriteAllText($export, 'different')
Assert-Rejected { & $tool -Action Export -Registry $path -Destination (Join-Path $scratch 'tables') } 'different bytes'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 3 -Version $version -TableDirectory (Join-Path $scratch 'tables') } 'hash mismatch'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 1 -Version ('0' * 40) } 'could not be found'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 99 } 'uid=99.*not found'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 0 } 'Uid'
Assert-Rejected { & $tool -Action Resolve -Registry $path -Uid 1 -Version '../invalid' } 'Version'

# Simulate removal of a released UID: allocation must reuse it with no exception.
$table = Get-Fixture
$table.entries = @($table.entries | Where-Object { $_.uid -ne 2 })
Write-StateRegistry $path $table
$added = & $tool -Action Add -Registry $path -Kind block -Process ui -Name Replacement -Comment 'A replacement block.'
Assert-True ($added -eq 2) 'Add fills smallest gap.'
$added = & $tool -Action Add -Registry $path -Kind item -Process backend -Name Demand -Comment 'External demand.' -Storage reference -Type bool -OwnerComponent 'Demand table' -PerBlock $true -Trigger event
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
    $other = if ($process -eq 'backend') { 'Preference' } else { 'Ready' }
    Assert-True (-not $source.Contains("public const int $other =")) "Generated $process excludes other process."
    [IO.File]::SetLastWriteTimeUtc($output, $fixedTime)
    & $generator -Registry $path -Process $process -Namespace "Fixture_$process" -Output $output
    Assert-True ([IO.File]::GetLastWriteTimeUtc($output) -eq $fixedTime) 'Unchanged generation does not rewrite.'
    # Compile the same output with and without the switch, without a repository or SDK project.
    foreach ($names in @($true, $false)) {
        $ns = "Fixture_${process}_$names"
        $compiled = $source.Replace("namespace Fixture_$process;", "namespace $ns;")
        $probe = "public class Probe { public static int Program => BlockUids.Program; }"
        $options = if ($names) { @('/define:RM_STATE_NAMES') } else { @('/optimize') }
        $types = Add-Type -TypeDefinition ($compiled + "`n" + $probe) -CompilerOptions $options -PassThru
        $assembly = $types[0].Assembly
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
Assert-True ($production.entries.Count -eq 65) 'Production catalog contains 51 blocks and 14 items.'
"Passed $script:count assertions. Fixtures: $scratch"
