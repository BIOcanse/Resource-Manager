[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TaskRoot,
    [string]$ProjectPolicyPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$taskPath=[IO.Path]::GetFullPath($TaskRoot)
# A task-local registration profile supplies extra roots, never a permanent
# exemption from the project's current automatic retention limits.
if([string]::IsNullOrWhiteSpace($ProjectPolicyPath)) {$ProjectPolicyPath=Join-Path $repo 'scripts/maintenance/generated-workspace-policy.json'}
$projectPolicy=Get-Content -LiteralPath $ProjectPolicyPath -Raw|ConvertFrom-Json
$profilePath=Join-Path $taskPath 'retention-policy.json'
$policy=if([IO.File]::Exists($profilePath)) {
    Get-Content -LiteralPath $profilePath -Raw|ConvertFrom-Json
} else {
    # A newly registered task needs no private policy copy when it has no
    # additional roots. Use the current enabled project policy directly.
    Get-Content -LiteralPath $ProjectPolicyPath -Raw|ConvertFrom-Json
}
$policy.enabled=[bool]$projectPolicy.enabled
$policy.defaults=$projectPolicy.defaults
$policy.referenceRoots=@(@($projectPolicy.referenceRoots)+@($policy.referenceRoots)|Sort-Object -Unique)
Import-Module (Join-Path $repo 'scripts/dev/MachineRoots.psm1') -DisableNameChecking
$rootPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$roots=[Collections.Generic.List[object]]::new()
# Current project root rules win over stale copies in historical task profiles.
foreach($root in @($projectPolicy.roots)+@($policy.roots)) {
    # A root under an unconfigured {WorkRoot} is kept by its configured text and protects nothing on this machine.
    $resolved=Resolve-MachinePathTokens -Path ([string]$root.path) -RepositoryRoot $repo
    $path=if($null -eq $resolved){[string]$root.path}else{[IO.Path]::GetFullPath($resolved)}
    if($rootPaths.Add($path)) {$roots.Add($root)}
}
$policy.roots=@($roots.ToArray())
foreach($path in @($policy.referenceRoots)) {
    if(-not [string]::IsNullOrWhiteSpace([string]$path)) {
        $resolved=Resolve-MachinePathTokens -Path ([string]$path) -RepositoryRoot $repo
        if($null -ne $resolved){[void]$rootPaths.Add([IO.Path]::GetFullPath($resolved))}
    }
}
$referenceRoots=@($rootPaths|Where-Object {[IO.Directory]::Exists($_)})
$policyPath=Join-Path $taskPath 'effective-validation-retention-policy.json'
$policy|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $policyPath -Encoding utf8
[pscustomobject]@{path=$policyPath;policy=$policy;referenceRoots=$referenceRoots}
