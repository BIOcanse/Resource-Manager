<#
The developer machine's own folders, which must never be written into a tracked file.

Two roots are known by name:
  ToolsRoot  the folder that holds the shared developer tools, laid out as
             JobQueue\JobQueue.ps1, CodeQL\<version>\codeql\codeql.exe, Cppcheck\<version>\...,
             PowerShellModules\<module>\<version>\..., Zig\zig-<version>\zig.exe.
  WorkRoot   the folder that holds each project's large work output (work sets, validation batches),
             one subfolder per project, for example <WorkRoot>\Resource-Manager-Development.

Each root is read, in this order, from:
  1. the environment variable  RM_TOOLS_ROOT / RM_WORK_ROOT;
  2. git configuration         rm.toolsRoot / rm.workRoot, read with `git config --get` in the repository,
                               so a value set with `git config --global` or in the repository's own config
                               serves every checkout and worktree without being committed.
Scripts that take an explicit tool or root parameter use the parameter first and ask for a root only when it
is missing. An unset root is an error that names both settings, unless the caller asks with -Optional.

Configured paths in tracked JSON may use the tokens {RepositoryRoot} and {WorkRoot};
Resolve-MachinePathTokens expands them.
#>
Set-StrictMode -Version Latest

$script:MachineRootSources = [ordered]@{
    ToolsRoot = [pscustomobject]@{ Variable = 'RM_TOOLS_ROOT'; GitKey = 'rm.toolsRoot' }
    WorkRoot  = [pscustomobject]@{ Variable = 'RM_WORK_ROOT'; GitKey = 'rm.workRoot' }
}
$script:MachineRootsRepository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-MachineRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('ToolsRoot', 'WorkRoot')][string]$Name,
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
    if (-not [IO.Path]::IsPathRooted($value)) { throw "$Name from $origin must be an absolute path." }
    return [IO.Path]::GetFullPath($value).TrimEnd('\', '/')
}

function Resolve-MachinePathTokens {
    <# Expands {RepositoryRoot} and {WorkRoot}. Returns $null when the path needs a root this machine has not configured. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )
    $resolved = $Path.Replace('{RepositoryRoot}', $RepositoryRoot)
    if ($resolved.Contains('{WorkRoot}')) {
        $workRoot = Get-MachineRoot -Name WorkRoot -RepositoryRoot $RepositoryRoot -Optional
        if (-not $workRoot) { return $null }
        $resolved = $resolved.Replace('{WorkRoot}', $workRoot)
    }
    return $resolved
}

Export-ModuleMember -Function Get-MachineRoot, Resolve-MachinePathTokens
