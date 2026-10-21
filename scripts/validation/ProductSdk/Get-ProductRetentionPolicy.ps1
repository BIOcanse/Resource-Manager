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
$policy=Get-Content -LiteralPath (Join-Path $taskPath 'retention-policy.json') -Raw|ConvertFrom-Json
if([string]::IsNullOrWhiteSpace($ProjectPolicyPath)) {$ProjectPolicyPath=Join-Path $repo 'scripts/maintenance/generated-workspace-policy.json'}
$projectPolicy=Get-Content -LiteralPath $ProjectPolicyPath -Raw|ConvertFrom-Json
$policy.enabled=[bool]$projectPolicy.enabled
$policy.defaults=$projectPolicy.defaults
$policy.referenceRoots=@(@($projectPolicy.referenceRoots)+@($policy.referenceRoots)|Sort-Object -Unique)
$rootPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$roots=[Collections.Generic.List[object]]::new()
# Current project root rules win over stale copies in historical task profiles.
foreach($root in @($projectPolicy.roots)+@($policy.roots)) {
    $path=[IO.Path]::GetFullPath(([string]$root.path).Replace('{RepositoryRoot}',$repo))
    if($rootPaths.Add($path)) {$roots.Add($root)}
}
$policy.roots=@($roots.ToArray())
foreach($path in @($policy.referenceRoots)) {
    if(-not [string]::IsNullOrWhiteSpace([string]$path)) {
        [void]$rootPaths.Add([IO.Path]::GetFullPath(([string]$path).Replace('{RepositoryRoot}',$repo)))
    }
}
$referenceRoots=@($rootPaths|Where-Object {[IO.Directory]::Exists($_)})
$policyPath=Join-Path $taskPath 'effective-validation-retention-policy.json'
$policy|ConvertTo-Json -Depth 16|Set-Content -LiteralPath $policyPath -Encoding utf8
[pscustomobject]@{path=$policyPath;policy=$policy;referenceRoots=$referenceRoots}
