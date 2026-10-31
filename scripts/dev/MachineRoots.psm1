<#
The developer machine's own folders, which must never be written into a tracked file.

 Roots are known by name:
  ToolsRoot  the folder that holds the shared developer tools, laid out as
             JobQueue\JobQueue.ps1, CodeQL\<version>\codeql\codeql.exe, Cppcheck\<version>\...,
             PowerShellModules\<module>\<version>\..., Zig\zig-<version>\zig.exe.
  WorkRoot   the folder that holds each project's large work output (work sets, validation batches),
             one subfolder per project, for example <WorkRoot>\Resource-Manager-Development.
  ActiveWorkRoot holds new RM workspaces and worktrees; WorkRoot remains the legacy root.
  ColdArchiveRoot holds explicitly retained cold evidence and unique worktree backups.

Each root is read, in this order, from:
  1. the environment variable declared in MachineRootSources below;
  2. its git configuration key, read with `git config --get` in the repository,
                               so a value set with `git config --global` or in the repository's own config
                               serves every checkout and worktree without being committed.
Scripts that take an explicit tool or root parameter use the parameter first and ask for a root only when it
is missing. An unset root is an error that names both settings, unless the caller asks with -Optional.

Configured paths in tracked JSON may use {RepositoryRoot} or any named root token;
Resolve-MachinePathTokens expands them.
#>
Set-StrictMode -Version Latest

$script:MachineRootSources = [ordered]@{
    ToolsRoot = [pscustomobject]@{ Variable = 'RM_TOOLS_ROOT'; GitKey = 'rm.toolsRoot' }
    WorkRoot  = [pscustomobject]@{ Variable = 'RM_WORK_ROOT'; GitKey = 'rm.workRoot' }
    ActiveWorkRoot = [pscustomobject]@{ Variable = 'RM_ACTIVE_WORK_ROOT'; GitKey = 'rm.activeWorkRoot' }
    ColdArchiveRoot = [pscustomobject]@{ Variable = 'RM_COLD_ARCHIVE_ROOT'; GitKey = 'rm.coldArchiveRoot' }
}
$script:MachineRootsRepository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-MachineRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('ToolsRoot', 'WorkRoot', 'ActiveWorkRoot', 'ColdArchiveRoot')][string]$Name,
        [string]$RepositoryRoot = $script:MachineRootsRepository,
        [switch]$Optional
    )
    $source = $script:MachineRootSources[$Name]
    $value = [Environment]::GetEnvironmentVariable($source.Variable)
    $origin = $source.Variable
    if ([string]::IsNullOrWhiteSpace($value) -and (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) {
        # git exits 1 for an unset key; keep the caller's exit code intact. Before the caller's first native
        # command LASTEXITCODE does not exist, which StrictMode refuses to read, so it is looked up instead.
        $savedExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        $value = @(& git -C $RepositoryRoot config --get $source.GitKey 2>$null) | Select-Object -First 1
        if ($null -ne $savedExitCode) { $global:LASTEXITCODE = $savedExitCode.Value }
        else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
        $origin = "git config $($source.GitKey)"
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        if ($Optional) { return $null }
        throw "$Name is not configured on this machine. Set the environment variable $($source.Variable), or run: git config --global $($source.GitKey) <folder>"
    }
    $value = $value.Trim()
    if ($value -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+(?:\\|$))') { throw "$Name from $origin must be an absolute path." }
    $full = [IO.Path]::GetFullPath($value)
    if ($full.Length -gt [IO.Path]::GetPathRoot($full).Length) { return $full.TrimEnd('\', '/') }
    return $full
}

function Resolve-MachinePathTokens {
    <# Expands named roots. Returns $null when a required machine root is not configured. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )
    $resolved = $Path.Replace('{RepositoryRoot}', $RepositoryRoot)
    foreach ($name in $script:MachineRootSources.Keys) {
        $token = '{' + $name + '}'
        if (-not $resolved.Contains($token)) { continue }
        $root = Get-MachineRoot -Name $name -RepositoryRoot $RepositoryRoot -Optional
        if (-not $root) { return $null }
        $resolved = $resolved.Replace($token, $root)
    }
    return $resolved
}

function Get-RmStoragePath {
    <# Single default layout for future RM tasks. An explicit Path always wins, including existing legacy tasks. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Workspaces', 'Worktrees', 'ColdArchives', 'WorktreeBackups')][string]$Kind,
        [string]$Path,
        [string]$RepositoryRoot = $script:MachineRootsRepository,
        [switch]$Optional
    )
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        if ($Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+(?:\\|$))') { throw 'An explicit RM storage path must be absolute.' }
        $full = [IO.Path]::GetFullPath($Path)
        if ($full.Length -gt [IO.Path]::GetPathRoot($full).Length) { return $full.TrimEnd('\', '/') }
        return $full
    }
    $rootName = if ($Kind -in 'ColdArchives', 'WorktreeBackups') { 'ColdArchiveRoot' } else { 'ActiveWorkRoot' }
    $root = Get-MachineRoot -Name $rootName -RepositoryRoot $RepositoryRoot -Optional:$Optional
    if (-not $root) { return $null }
    switch ($Kind) {
        'Workspaces' { Join-Path $root 'Resource-Manager-Workspaces' }
        'Worktrees' { Join-Path $root 'Resource-Manager-Worktrees' }
        'ColdArchives' { $root }
        'WorktreeBackups' { Join-Path $root 'worktree-uncommitted' }
    }
}

function Get-RmWorkerPaths {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$Name,
        [string]$Worktree,
        [string]$Ledger,
        [string]$RepositoryRoot = $script:MachineRootsRepository
    )
    $tree = if ($Worktree) { Get-RmStoragePath -Kind Worktrees -Path $Worktree } else {
        $existing=Resolve-RmWorkerCheckout -Name $Name -Rows (Get-RmWorktreeRows -RepositoryRoot $RepositoryRoot)
        if($existing){$existing}else{Join-Path (Get-RmStoragePath -Kind Worktrees -RepositoryRoot $RepositoryRoot) $Name}
    }
    if (-not $Ledger) {
        $Ledger=Find-RmRunningWorkerLedger -Name $Name -RepositoryRoot $RepositoryRoot
        if (-not $Ledger) { $Ledger = Join-Path (Get-RmStoragePath -Kind Workspaces -RepositoryRoot $RepositoryRoot) "ledger-$Name" }
    }
    [pscustomobject]@{ Worktree = $tree; Ledger = (Get-RmStoragePath -Kind Workspaces -Path $Ledger) }
}

function Get-RmWorktreeRows {
    param([string]$RepositoryRoot)
    $rows=@(& git -C $RepositoryRoot worktree list --porcelain)
    if($LASTEXITCODE -ne 0){throw 'Cannot read registered RM worktrees.'}
    return $rows
}

function ConvertFrom-RmWorktreeRows {
    param([string[]]$Rows)
    $records=[Collections.Generic.List[object]]::new();$current=$null
    foreach($row in $Rows){
        if($row.StartsWith('worktree ')){
            $current=[pscustomobject]@{Path=[IO.Path]::GetFullPath($row.Substring(9));Branch=$null}
            $records.Add($current)
        }
        elseif($row.StartsWith('branch ') -and $null -ne $current){$current.Branch=$row.Substring(7)}
    }
    return $records.ToArray()
}

function Resolve-RmDevelopmentCheckout {
    param([string[]]$Rows)
    $matches=@(ConvertFrom-RmWorktreeRows -Rows $Rows|Where-Object Branch -CEQ 'refs/heads/codex/development-mainline')
    if($matches.Count -ne 1){throw 'Exactly one registered checkout of codex/development-mainline is required; worktree row order is not a fallback.'}
    return $matches[0].Path
}

function Resolve-RmWorkerCheckout {
    param([string]$Name,[string[]]$Rows)
    $matches=@(ConvertFrom-RmWorktreeRows -Rows $Rows|Where-Object { [IO.Path]::GetFileName($_.Path) -ieq $Name -or $_.Branch -ieq ('refs/heads/codex/'+$Name) })
    if($matches.Count -gt 1){throw "Multiple registered worktrees match worker ${Name}; pass -Worktree explicitly: $($matches.Path -join ', ')"}
    if($matches.Count -eq 1){return $matches[0].Path}
    return $null
}

function Find-RmRunningWorkerLedger {
    param([string]$Name,[string]$RepositoryRoot)
    $policyPath=Join-Path $RepositoryRoot 'scripts\maintenance\generated-workspace-policy.json'
    $policyFile=Get-Item -LiteralPath $policyPath -Force
    if($policyFile.Length -gt 1MB -or ($policyFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Worker retention policy must be a bounded regular file.'}
    $policy=Get-Content -LiteralPath $policyPath -Raw|ConvertFrom-Json
    if($policy.schemaVersion -ne 1){throw 'Unsupported worker retention policy schema.'}
    # Use the maintenance owner's marker/path validation; this lookup never
    # calls References or changes lifecycle/retention state.
    Import-Module (Join-Path $PSScriptRoot '..\maintenance\GeneratedWorkspaceRetention.psm1') -DisableNameChecking
    $found=[Collections.Generic.List[string]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $timer=[Diagnostics.Stopwatch]::StartNew();$visited=0
    $leaf='ledger-'+$Name
    foreach($root in $policy.roots){
        if(++$visited -gt $policy.defaults.maxNodes -or $timer.ElapsedMilliseconds -gt $policy.defaults.maxMilliseconds){throw 'Worker ledger lookup exceeded the configured retention scan bound.'}
        if($root.PSObject.Properties['archiveOnly'] -and $root.archiveOnly){continue}
        if($root.PSObject.Properties['excludedNames'] -and $leaf -iin @($root.excludedNames)){continue}
        $path=Resolve-MachinePathTokens -Path ([string]$root.path) -RepositoryRoot $RepositoryRoot
        if(-not $path){continue}
        $path=Get-RmStoragePath -Kind Workspaces -Path $path
        if(-not $seen.Add($path)){continue}
        $candidate=Join-Path $path $leaf
        $readErrors=@()
        $marker=Get-Item -LiteralPath (Join-Path $candidate '.generated-workspace.json') -Force -ErrorAction SilentlyContinue -ErrorVariable readErrors
        if(-not $marker){
            foreach($readError in $readErrors){if($readError.CategoryInfo.Category -ne 'ObjectNotFound'){throw "Cannot inspect worker registration ${candidate}: $readError"}}
            continue
        }
        $state=Get-GeneratedWorkspaceState -RootPath $path -WorkspacePath $candidate
        if($state.state -ceq 'unknown'){throw "Invalid worker registration ${candidate}: $($state.reason)"}
        if($state.rootId -ine [string]$root.id){throw "Worker registration does not belong to configured root $($root.id): $candidate"}
        if($state.state -ceq 'running'){$found.Add($candidate)}
    }
    if($found.Count -gt 1){throw "Multiple registered running ledgers match worker ${Name}; pass -Ledger explicitly: $($found -join ', ')"}
    if($found.Count -eq 1){return $found[0]}
    return $null
}

function Get-RmMainCheckout {
    [CmdletBinding()]
    param([string]$RepositoryRoot = $script:MachineRootsRepository)
    $rows = Get-RmWorktreeRows -RepositoryRoot $RepositoryRoot
    # The shared git directory/first worktree may be a public checkout after
    # repository-layout maintenance. Worker briefs and docs belong to development.
    Resolve-RmDevelopmentCheckout -Rows $rows
}

function Assert-RmRunningWorkspace {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    if ($Path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\[^\\]+\\[^\\]+(?:\\|$))') { throw 'A running workspace path must be absolute.' }
    $cursor = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $owner = $null
    for ($depth = 0; $cursor -and $depth -lt 128; $depth++) {
        if ([IO.Directory]::Exists($cursor) -or [IO.File]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Workspace path contains a reparse point: $cursor" }
        }
        if (-not $owner) {
            $marker = Join-Path $cursor '.generated-workspace.json'
            if ([IO.File]::Exists($marker)) {
                $info = Get-Item -LiteralPath $marker -Force
                if ($info.Length -gt 1MB -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Invalid workspace marker.' }
                $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
                if ($state.schemaVersion -ne 1 -or $state.state -cne 'running' -or [IO.Path]::GetFullPath([string]$state.workspacePath).TrimEnd('\') -ine $cursor) { throw "A valid running workspace is required: $cursor" }
                $owner = $cursor
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if ($cursor) { throw 'Workspace ancestor limit exceeded.' }
    if (-not $owner) { throw "No registered running workspace owns: $Path" }
}

Export-ModuleMember -Function Get-MachineRoot, Resolve-MachinePathTokens, Get-RmStoragePath, Get-RmWorkerPaths, Get-RmMainCheckout, Assert-RmRunningWorkspace
