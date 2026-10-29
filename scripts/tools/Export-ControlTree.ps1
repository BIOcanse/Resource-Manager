#Requires -Version 7
[CmdletBinding()]
param(
    [string[]]$TreeJson,
    [string]$From = 'Program',
    [string]$Profile,
    [ValidateSet('text', 'json')][string]$Format = 'text',
    [string]$CompareWith,
    # Build caches live in a registered validation batch here. Use the ledger
    # for large outputs. No build or scratch directories when TreeJson is given.
    [string]$WorkRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.local')
)

# The single process discovery table. Add window after its Zig migration (5d).
$Processes = @(
    @{ Name = 'backend'; Directory = 'src/Core/NativeCore' },
    @{ Name = 'desktop'; Directory = 'src/UI/Desktop' }
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$maxNodes = 100000
$maxDepth = 4096

function Read-BoundedText([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -gt 64MB) { throw "Expected a file of at most 64 MiB: $Path" }
    [IO.File]::ReadAllText($file.FullName)
}

function Invoke-ProcessExport([hashtable]$Process, [string]$Scratch) {
    $directory = Join-Path $repo $Process.Directory
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'build.zig'))) {
        [Console]::Error.WriteLine("$($Process.Name): no Zig declaration yet")
        return
    }
    $zig = (Get-Command zig -CommandType Application -ErrorAction Stop).Source
    Import-Module (Join-Path $PSScriptRoot '../dev/MachineRoots.psm1') -DisableNameChecking
    $queue = Join-Path (Get-MachineRoot -Name ToolsRoot) 'JobQueue\JobQueue.ps1'
    if (-not (Test-Path -LiteralPath $queue)) { throw "JobQueue is required for builds: $queue" }
    $args = @('build', 'export-tree', '-j1', '--cache-dir', (Join-Path $Scratch "$($Process.Name)-cache"),
        '--global-cache-dir', (Join-Path $Scratch 'global-cache'))
    # A child pwsh lets JobQueue's exit preserve this script's finally/closure.
    $resultLines = @(& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -CommandWithArgs '& $args[0] -Action Run -Label "control-tree export" -FilePath $args[1] -WorkingDirectory $args[2] -TimeoutMinutes 30 -MemoryLimitGiB 4 -ArgumentList $args[3..($args.Count-1)]' $queue $zig $directory @args)
    $code = $LASTEXITCODE
    if ($resultLines.Count -eq 0) { throw "JobQueue returned no result for $($Process.Name)" }
    $result = $resultLines[-1] | ConvertFrom-Json
    $stderr = if (Test-Path -LiteralPath $result.errorLog) { Read-BoundedText $result.errorLog } else { '' }
    if ($code -ne 0) {
        if ($stderr -match '(?i)(no step named|unknown step).*export-tree') {
            [Console]::Error.WriteLine("$($Process.Name): no Zig declaration yet")
            return
        }
        throw "Export failed for $($Process.Name) (job $($result.id)): $stderr"
    }
    $data = Read-BoundedText $result.log | ConvertFrom-Json -Depth 100
    if ($data.process -cne $Process.Name) { throw "Expected process $($Process.Name), got $($data.process)" }
    $data
}

function Get-TargetTree([string]$Path) {
    $text = Read-BoundedText $Path
    $fence = [regex]::Match($text, '(?ms)^```control-tree\s*\r?\n(.*?)^```\s*$')
    if ($fence.Success) { $text = $fence.Groups[1].Value }
    elseif ([IO.Path]::GetExtension($Path) -in @('.md', '.markdown')) { throw "No control-tree fenced block in $Path" }
    $target = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $ancestors = [Collections.Generic.List[string]]::new()
    $rows = 0
    foreach ($line in ($text -split '\r?\n')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if (++$rows -gt $maxNodes) { throw 'Target tree exceeds node limit' }
        if ($line -notmatch '^(?<indent>(?:(?:│  |   ))*)(?:(?<branch>├─ |└─ ))?(?<name>[^\s]+)(?:  .*)?$') { throw "Invalid tree line: $line" }
        $depth = [int]($Matches.indent.Length / 3)
        if ($Matches.ContainsKey('branch') -and $Matches.branch) { $depth++ }
        $name = $Matches.name
        if ($depth -gt $maxDepth -or $depth -gt $ancestors.Count -or ($depth -eq 0 -and $target.Count -gt 0)) { throw "Invalid indentation: $line" }
        if ($target.ContainsKey($name)) { throw "Duplicate target block: $name" }
        $parent = if ($depth -eq 0) { $null } else { $ancestors[$depth - 1] }
        $target.Add($name, $parent)
        while ($ancestors.Count -gt $depth) { $ancestors.RemoveAt($ancestors.Count - 1) }
        $ancestors.Add($name)
    }
    if ($target.Count -eq 0) { throw 'Target tree is empty' }
    return ,$target
}

$batchId = $null
$batchPrepared = $false
$outcome = 'failed'
try {
    $sources = [Collections.Generic.List[object]]::new()
    if ($PSBoundParameters.ContainsKey('TreeJson')) {
        if ($TreeJson.Count -eq 0 -or $TreeJson.Count -gt 64) { throw 'Provide between 1 and 64 per-process JSON files' }
        foreach ($path in $TreeJson) { $sources.Add((Read-BoundedText $path | ConvertFrom-Json -Depth 100)) }
    } else {
        New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
        $batchId = 'control-tree-' + [guid]::NewGuid().ToString('N')
        $workset = & (Join-Path $repo 'scripts/validation/Manage-ValidationWorkset.ps1') -Mode Prepare -WorkspaceRoot $WorkRoot -BatchId $batchId -ConsumerId export -ExpectedGrowthGiB 1 -ReserveFreeGiB 2 -MaxWorksetGiB 8 | ConvertFrom-Json
        $batchPrepared = $true
        foreach ($process in $Processes) {
            $data = Invoke-ProcessExport $process $workset.IsolatedScratchRoot
            if ($null -ne $data) { $sources.Add($data) }
        }
    }
    $processesByName = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $allBlocks = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $nodes = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $nodes.Add('Program', [ordered]@{ id='Program'; process=$null; uid=$null; name='Program'; parent=$null; children=[Collections.Generic.List[string]]::new(); profiles=@(); declarations=@() })
    $profileFound = [string]::IsNullOrEmpty($Profile)
    foreach ($source in $sources) {
        if ($source.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($source.process) -or $source.process -match '[:\s]') { throw 'Invalid process export header' }
        $processName = [string]$source.process
        if ($processesByName.ContainsKey($processName)) { throw "Duplicate process: $processName" }
        $processesByName.Add($processName, $source)
        if ($Profile -cin @($source.profiles)) { $profileFound = $true }
        $rootFound = $false
        foreach ($block in $source.blocks) {
            if ($allBlocks.Count -ge $maxNodes) { throw 'Export exceeds node limit' }
            $key = "${processName}:$($block.uid)"
            if ($allBlocks.ContainsKey($key)) { throw "Duplicate block UID: $key" }
            if ($block.uid -le 0 -or [string]::IsNullOrWhiteSpace($block.name) -or $block.name -match '[:\s]') { throw "Invalid block name/UID in $processName" }
            $allBlocks.Add($key, $block)
            if ($block.uid -eq $source.program) {
                if ($null -ne $block.parent) { throw "Process root has a parent: ${processName}:$($block.name)" }
                $rootFound = $true
                continue
            }
            if ($null -eq $block.parent) { throw "Unknown process root for ${processName}:$($block.name)" }
            $declarations = @($block.declarations | Where-Object { -not $Profile -or $_.profile -ceq $Profile })
            if ($declarations.Count -eq 0) { continue }
            $nodes.Add($key, [ordered]@{ id="${processName}:$($block.name)"; process=$processName; uid=$block.uid; name=$block.name; parent="${processName}:$($block.parent)"; children=[Collections.Generic.List[string]]::new(); profiles=@($declarations.profile); declarations=$declarations })
        }
        if (-not $rootFound) { throw "Missing program root for $processName" }
    }
    if (-not $profileFound) { throw "Unknown profile: $Profile" }
    # A process has one lifetime owner across all declarations. Check even if a
    # selected profile omits that owner; never float its children up to Program.
    $launchers = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($source in $sources) {
        foreach ($block in $source.blocks) {
            $launcherKey = "$($source.process):$($block.uid)"
            foreach ($declaration in $block.declarations) {
                $launch = $declaration.launchesProcess
                if ($null -eq $launch) { continue }
                if (-not $processesByName.ContainsKey($launch)) { throw "Unknown launched process $launch from $($source.process):$($block.name)" }
                if ($block.uid -eq $source.program) { throw "Process root cannot launch ${launch}: $($source.process):$($block.name)" }
                if ($launchers.ContainsKey($launch) -and $launchers[$launch] -cne $launcherKey) {
                    $earlier = $launchers[$launch]; $earlierBlock = $allBlocks[$earlier]
                    throw "Process $launch launched twice: ${earlier}:$($earlierBlock.name), $($source.process):$($block.name)"
                }
                $launchers[$launch] = $launcherKey
            }
        }
    }
    # Resolve process-root parents to their lifetime owners, then walk the
    # stitched tree once. This also rejects cycles and unknown local parents.
    foreach ($key in @($nodes.Keys)) {
        if ($key -ceq 'Program') { continue }
        $node = $nodes[$key]
        $source = $processesByName[$node.process]
        if ($node.parent -ceq "$($node.process):$($source.program)") {
            $node.parent = if ($launchers.ContainsKey($node.process)) { $launchers[$node.process] } else { 'Program' }
            if ($Profile -and $launchers.ContainsKey($node.process) -and $nodes.ContainsKey($node.parent)) {
                $active = @($nodes[$node.parent].declarations | Where-Object { $_.launchesProcess -ceq $node.process })
                if ($active.Count -eq 0) { continue }
            }
        }
        if (-not $nodes.ContainsKey($node.parent)) {
            # A filtered parent removes its whole branch. Unknown declarations
            # are errors, rather than profile filtering.
            if (-not $allBlocks.ContainsKey($node.parent)) { throw "Unknown parent $($node.parent) for $($node.id)" }
            continue
        }
        $nodes[$node.parent].children.Add($key)
    }
    $reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push(@{ key='Program'; depth=0 })
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop()
        if ($entry.depth -gt $maxDepth -or -not $reachable.Add($entry.key)) { throw "Control tree cycle/depth limit at $($nodes[$entry.key].id)" }
        foreach ($child in $nodes[$entry.key].children) { $pending.Push(@{key=$child;depth=$entry.depth+1}) }
    }
    foreach ($key in $nodes.Keys) {
        if ($reachable.Contains($key)) { continue }
        # A bounded parent walk distinguishes legitimate profile exclusion
        # from malformed or cyclic process launches.
        $cursor = $key; $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        while ($nodes.ContainsKey($cursor) -and -not $reachable.Contains($cursor)) {
            if (-not $seen.Add($cursor) -or $seen.Count -gt $maxDepth) { throw "Control tree cycle at $($nodes[$cursor].id)" }
            $cursor = $nodes[$cursor].parent
        }
        if (-not $Profile) { throw "Unreachable block: $($nodes[$key].id)" }
    }
    $selection = $From
    foreach ($source in $sources) {
        $programBlock = $allBlocks["$($source.process):$($source.program)"]
        if ($From -ceq "$($source.process):$($source.program)" -or $From -ceq "$($source.process):$($programBlock.name)") { $selection = 'Program' }
    }
    $matches = @($reachable | Where-Object { $node=$nodes[$_]; $_ -ceq $selection -or $node.id -ceq $selection -or $node.name -ceq $selection })
    if ($matches.Count -ne 1) { throw "From '$From' must identify exactly one block; matches: $($matches.Count)" }
    $chosen = $matches[0]
    $ordered = [Collections.Generic.List[object]]::new()
    $pending.Push(@{key=$chosen;prefix='';branch='';depth=0;parent=$null})
    while ($pending.Count -gt 0) {
        $entry = $pending.Pop(); $node=$nodes[$entry.key]
        $ordered.Add(@{node=$node;prefix=$entry.prefix;branch=$entry.branch;parent=$entry.parent})
        $children = @($node.children | Where-Object { $reachable.Contains($_) })
        for ($i=$children.Count-1; $i -ge 0; $i--) {
            $prefix = $entry.prefix
            if ($entry.depth -gt 0) { $prefix += $(if ($entry.branch -ceq '└─ ') { '   ' } else { '│  ' }) }
            $pending.Push(@{ key=$children[$i]; prefix=$prefix; branch=$(if ($i -eq $children.Count-1) { '└─ ' } else { '├─ ' }); depth=$entry.depth+1; parent=$node.id })
        }
    }
    if ($CompareWith) {
        $target = Get-TargetTree $CompareWith
        $actual = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($entry in $ordered) { $actual.Add($entry.node.id, $entry.parent) }
        $missing = @($target.Keys | Where-Object { -not $actual.ContainsKey($_) } | Sort-Object -CaseSensitive)
        $extra = @($actual.Keys | Where-Object { -not $target.ContainsKey($_) } | Sort-Object -CaseSensitive)
        $moved = @($actual.Keys | Where-Object { $target.ContainsKey($_) -and $actual[$_] -cne $target[$_] } | Sort-Object -CaseSensitive | ForEach-Object { [ordered]@{block=$_;targetParent=$target[$_];codeParent=$actual[$_]} })
        if ($Format -eq 'json') { [ordered]@{missingInCode=$missing;extraInCode=$extra;parentDiffers=$moved} | ConvertTo-Json -Depth 8 }
        else {
            'Blocks in target but not in code:'; foreach ($name in $missing) { "  $name" }
            'Blocks in code but not in target:'; foreach ($name in $extra) { "  $name" }
            'Blocks whose parent differs:'; foreach ($item in $moved) { "  $($item.block): target=$($item.targetParent); code=$($item.codeParent)" }
        }
    } elseif ($Format -eq 'json') {
        $rows = @($ordered | ForEach-Object {
            $node=$_.node
            [ordered]@{id=$node.id;process=$node.process;uid=$node.uid;name=$node.name;parent=$_.parent;children=@($node.children | Where-Object { $reachable.Contains($_) } | ForEach-Object { $nodes[$_].id });profiles=$node.profiles;declarations=$node.declarations}
        })
        $processMetadata = @($sources | ForEach-Object {
            [ordered]@{process=$_.process;registryVersion=$_.registryVersion;program=$_.program;profiles=$_.profiles;conditions=$_.conditions;programBlock=$allBlocks["$($_.process):$($_.program)"]}
        })
        [ordered]@{schemaVersion=1;from=$nodes[$chosen].id;profile=$Profile;processes=$processMetadata;blocks=$rows} | ConvertTo-Json -Depth 100
    } else {
        foreach ($entry in $ordered) {
            $conditions = @($entry.node.declarations | ForEach-Object { if ($null -ne $_.decidingCondition) { $_.decidingCondition.name } } | Select-Object -Unique)
            $comment = if ($conditions.Count -gt 0) { '  ' + ($conditions -join ' / ') } else { '' }
            "$($entry.prefix)$($entry.branch)$($entry.node.id)$comment"
        }
    }
    $outcome = 'success'
} finally {
    if ($batchPrepared) {
        & (Join-Path $repo 'scripts/validation/Manage-ValidationWorkset.ps1') -Mode Complete -WorkspaceRoot $WorkRoot -BatchId $batchId -Outcome $outcome | Out-Null
    }
}
