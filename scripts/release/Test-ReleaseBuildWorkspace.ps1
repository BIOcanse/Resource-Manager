[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('release-build-fixture-'+$PSVersionTable.PSEdition)
$output=Join-Path $fixture 'output'
[void][IO.Directory]::CreateDirectory($output)
$policyPath=Join-Path $fixture 'policy.json'
$referenceRoot=Join-Path $fixture 'reference-root'
$explicitReferenceRoot=Join-Path $fixture 'explicit-reference-root'
foreach($path in @($referenceRoot,$explicitReferenceRoot)){[void][IO.Directory]::CreateDirectory($path)}
$policy=[pscustomobject]@{schemaVersion=1;enabled=$true;referenceRoots=@($explicitReferenceRoot);defaults=@{maxRecentCompleted=0;maxBytes=8388608;maxNodes=10000;maxMilliseconds=15000};roots=@(@{id='release-fixture-root';path=$output;excludedNames=@()},@{id='release-fixture-reference-root';path=$referenceRoot;excludedNames=@()})}
$policy|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $policyPath -Encoding utf8
Import-Module (Join-Path $repo 'scripts/release/ReleaseBuildWorkspace.psm1') -Force -DisableNameChecking
$passed=[Collections.Generic.List[string]]::new()
function Fingerprint([string[]]$Paths) {
    @($Paths|ForEach-Object {[pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash;length=([IO.FileInfo]::new($_)).Length}})|ConvertTo-Json -Depth 4 -Compress
}
# Formal sibling names intentionally belong to another version. They are
# unregistered user-visible deliverables, never temporary workspace owners.
$stage=Join-Path $output 'ResourceManager-9.8.7-win-x64'
[void][IO.Directory]::CreateDirectory($stage)
$stageFile=Join-Path $stage 'formal-input.txt'
[IO.File]::WriteAllText($stageFile,'formal stage must be byte-identical')
$zip="$stage.zip";$checksum="$zip.sha256"
[IO.File]::WriteAllText($zip,'formal ZIP fixture bytes')
[IO.File]::WriteAllText($checksum,'formal checksum fixture bytes')
$formal=@($stageFile,$zip,$checksum)
$before=Fingerprint $formal
$context=New-ReleaseBuildWorkspace -OutputRoot $output -Version 1.2.3 -ProjectPolicyPath $policyPath
if($context.Policy.enabled -ne $true -or $context.Policy.roots[0].maxRecentCompleted -ne 0){throw 'Temporary release retention is not enabled with zero completed retention'}
if($referenceRoot -notin $context.ReferenceRoots -or $explicitReferenceRoot -notin $context.ReferenceRoots -or $output -notin $context.ReferenceRoots){throw 'Release lifecycle omitted a configured reference root'}
$passed.Add('All project policy roots and explicit reference roots participate in retention protection')

Import-Module (Join-Path $repo 'scripts/maintenance/GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking
$unrelated=Join-Path $output 'unrelated-completed-output'
$null=New-GeneratedWorkspace -RootPath $output -WorkspacePath $unrelated -Purpose 'synthetic unrelated completed sibling' -Policy $context.RegistrationPolicy -ReferenceRoots $context.ReferenceRoots
$unrelatedFile=Join-Path $unrelated 'keep.txt'
[IO.File]::WriteAllText($unrelatedFile,'not selected by this build lifecycle')
$null=Complete-GeneratedWorkspace -RootPath $output -WorkspacePath $unrelated -Outcome success -References @() -Policy $context.RegistrationPolicy -ReferenceRoots $context.ReferenceRoots
$unrelatedBefore=Fingerprint @($unrelatedFile,(Join-Path $unrelated '.generated-workspace.json'))
[IO.File]::WriteAllText((Join-Path $context.Paths.BuildRoot 'temporary.bin'),'regenerable compiler output')
$success=Complete-ReleaseBuildWorkspace -Context $context -Outcome success
if(-not $success.WorkspaceRetired -or [IO.Directory]::Exists($context.Paths.BuildRoot) -or (Fingerprint $formal) -cne $before){throw 'Successful release build cleanup changed formal deliverables or left its temporary workspace'}
$passed.Add('Successful lifecycle removes build intermediates and preserves formal stage/ZIP/checksum bytes')
if([IO.File]::Exists((Join-Path $stage '.generated-workspace.json'))){throw 'Formal release stage was registered as a generated task'}
$passed.Add('Formal stage remains unregistered and outside the exact deletion selection')
if((Fingerprint @($unrelatedFile,(Join-Path $unrelated '.generated-workspace.json'))) -cne $unrelatedBefore){throw 'Exact release cleanup selected another registered sibling'}
$passed.Add('Exact build selection leaves even an unrelated registered completed sibling unchanged')

$context=New-ReleaseBuildWorkspace -OutputRoot $output -Version 1.2.4 -ProjectPolicyPath $policyPath
[IO.File]::WriteAllText((Join-Path $context.Paths.BuildRoot 'failure-repro-cache.bin'),'regenerable failed compiler output')
$failure=Complete-ReleaseBuildWorkspace -Context $context -Outcome failed -FailureMessage ('failure detail '*500)
$receipt=Get-Content -LiteralPath $context.Paths.ReceiptPath -Raw|ConvertFrom-Json
if(-not $failure.WorkspaceRetired -or $receipt.outcome -cne 'failed' -or $receipt.failure.Length -ne 2048 -or ([IO.FileInfo]::new($context.Paths.ReceiptPath)).Length -gt 16384 -or (Fingerprint $formal) -cne $before){throw 'Failed release build lifecycle did not keep only a bounded lightweight conclusion'}
$passed.Add('Failed lifecycle removes regenerable output and replaces one bounded lightweight conclusion')
$rejected=$false
try {$null=New-ReleaseBuildWorkspace -OutputRoot $output -Version 9.8.7 -ProjectPolicyPath $policyPath} catch {$rejected=$true}
if(-not $rejected -or (Fingerprint $formal) -cne $before){throw 'Existing formal output was overwritten or accepted'}
$passed.Add('New rejects existing formal output without removing anything')
$context=New-ReleaseBuildWorkspace -OutputRoot $output -Version 1.2.5 -ProjectPolicyPath $policyPath
$markerBefore=(Get-FileHash -LiteralPath (Join-Path $context.Paths.BuildRoot '.generated-workspace.json')).Hash
$rejected=$false
try {$null=New-ReleaseBuildWorkspace -OutputRoot $output -Version 1.2.5 -ProjectPolicyPath $policyPath} catch {$rejected=$true}
if(-not $rejected -or (Get-FileHash -LiteralPath (Join-Path $context.Paths.BuildRoot '.generated-workspace.json')).Hash -cne $markerBefore){throw 'New changed an existing running build workspace'}
$null=Complete-ReleaseBuildWorkspace -Context $context -Outcome failed -FailureMessage 'synthetic duplicate-output check'
$passed.Add('New rejects an existing build and preserves its running lifecycle marker')
$context=New-ReleaseBuildWorkspace -OutputRoot $output -Version 1.2.8 -ProjectPolicyPath $policyPath
$occupied=Join-Path $context.Paths.BuildRoot 'held.bin'
[IO.File]::WriteAllText($occupied,'synthetic file held by another owner')
$hold=[IO.File]::Open($occupied,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
try {
    $protected=Complete-ReleaseBuildWorkspace -Context $context -Outcome failed -FailureMessage 'synthetic occupied-file protection'
    if($protected.WorkspaceRetired -or -not [IO.File]::Exists($occupied)){throw 'Cleanup deleted an occupied temporary workspace'}
} finally {$hold.Dispose()}
$null=Invoke-GeneratedWorkspaceRetention -RootPath $output -WorkspacePaths @($context.Paths.BuildRoot) -Policy $context.Policy -ReferenceRoots $context.ReferenceRoots -Apply
if([IO.Directory]::Exists($context.Paths.BuildRoot)){throw 'Released synthetic occupancy still blocked exact official cleanup'}
$passed.Add('An occupied build is completed but preserved and reported; official retry cleans it after the handle closes')

$rejected=$false
try {$null=New-ReleaseBuildWorkspace -OutputRoot ([IO.Path]::GetPathRoot($output)) -Version 1.2.6 -ProjectPolicyPath $policyPath} catch {$rejected=$true}
if(-not $rejected){throw 'Release temporary lifecycle accepted a volume-root management boundary'}
$passed.Add('Volume-root OutputRoot is rejected before any lifecycle mutation')
$link=Join-Path $fixture 'reparse-output'
$target=Join-Path $fixture 'reparse-target'
[void][IO.Directory]::CreateDirectory($target)
$null=New-Item -ItemType Junction -Path $link -Target $target
try {
    $rejected=$false
    try {$null=New-ReleaseBuildWorkspace -OutputRoot $link -Version 1.2.7 -ProjectPolicyPath $policyPath} catch {$rejected=$true}
    if(-not $rejected -or [IO.Directory]::Exists((Join-Path $target 'build-1.2.7'))){throw 'Release lifecycle traversed a reparse output root'}
    $passed.Add('Actual junction OutputRoot is rejected before writing into its target')
} finally {
    # Unlink only this verified synthetic junction; no recursive filesystem
    # deletion is used. Build trees are deleted only by the official mechanism.
    $linkFull=[IO.Path]::GetFullPath($link)
    if(-not $linkFull.StartsWith($fixture+'\',[StringComparison]::OrdinalIgnoreCase) -or
        ([IO.File]::GetAttributes($linkFull) -band [IO.FileAttributes]::ReparsePoint) -eq 0){throw 'Synthetic link cleanup boundary changed'}
    [IO.Directory]::Delete($linkFull,$false)
}
$null=Invoke-GeneratedWorkspaceRetention -RootPath $output -WorkspacePaths @($unrelated) -Policy $context.Policy -ReferenceRoots $context.ReferenceRoots -Apply
$result=[pscustomobject]@{passed=$passed.Count;cases=@($passed);powerShellVersion=[string]$PSVersionTable.PSVersion;actualPublisherBuildExecuted=$false;actualProjectWorksetsTouched=$false}
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $fixture 'result.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $fixture 'result.json')
