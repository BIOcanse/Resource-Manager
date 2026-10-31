Set-StrictMode -Version Latest
$script:ReleaseBuildRepository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$script:ReleaseBuildPurpose='ResourceManager release regenerable build intermediates'
Import-Module (Join-Path $script:ReleaseBuildRepository 'scripts/maintenance/GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking
function Assert-ReleasePathWithoutReparse {
    param([Parameter(Mandatory)][string]$Path)
    $current=[IO.Path]::GetFullPath($Path)
    $depth=0
    while(-not [string]::IsNullOrWhiteSpace($current)) {
        if(++$depth -gt 128){throw 'Release output path exceeds its component bound.'}
        try {
            $attributes=[IO.File]::GetAttributes($current)
            if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Release output path contains a reparse point: $current"}
        } catch [IO.FileNotFoundException] {
        } catch [IO.DirectoryNotFoundException] {
        }
        $current=[IO.Path]::GetDirectoryName($current)
    }
}
function Assert-ReleaseBuildReceiptPath {
    param([Parameter(Mandatory)][string]$Path)
    Assert-ReleasePathWithoutReparse $Path
    if([IO.Directory]::Exists($Path)){throw 'Release build result path is a directory.'}
    if([IO.File]::Exists($Path)) {
        if(([IO.FileInfo]::new($Path)).Length -gt 16384){throw 'Existing release build result is not a bounded owned receipt.'}
        $old=Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json
        if($old.schema -cne 'rm/release-build-lifecycle/v1'){throw 'Existing release build result is not owned by this producer.'}
    }
 }
function Get-ReleaseOutputPaths {
    param([Parameter(Mandatory)][string]$OutputRoot, [Parameter(Mandatory)][string]$Version)
    $root=[IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
    if($root -ieq [IO.Path]::GetPathRoot($root).TrimEnd('\')){throw 'Release OutputRoot cannot be a volume root.'}
    Assert-ReleasePathWithoutReparse $root
    if([IO.File]::Exists($root)){throw 'Release OutputRoot must be a directory.'}
    $stage=Join-Path $root "ResourceManager-$Version-win-x64"
    $build=Join-Path $root "build-$Version"
    $zip="$stage.zip"
    foreach($path in @($stage,$build,$zip,"$zip.sha256")) {
        if([IO.Path]::GetDirectoryName($path) -ine $root){throw 'Release output escapes its direct-child boundary.'}
        Assert-ReleasePathWithoutReparse $path
    }
    $receipt=Join-Path $root '.resource-manager-release-build-last-result.json'
    Assert-ReleaseBuildReceiptPath $receipt
    [pscustomobject]@{OutputRoot=$root;Version=$Version;Stage=$stage;BuildRoot=$build;ZipPath=$zip;ReceiptPath=$receipt}
}
function Assert-ReleaseOutputTargets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version
    )
    $paths=Get-ReleaseOutputPaths -OutputRoot $OutputRoot -Version $Version
    foreach($path in @($paths.Stage,$paths.BuildRoot,$paths.ZipPath,"$($paths.ZipPath).sha256")) {
        if([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)){throw "Output already exists; nothing was removed: $path"}
    }
    $paths
}
# The retention policy and reference roots for this producer's build directory under OutputRoot.
function Get-ReleaseBuildPolicyContext {
    param([Parameter(Mandatory)][object]$Paths, [string]$ProjectPolicyPath)
    if([string]::IsNullOrWhiteSpace($ProjectPolicyPath)) {$ProjectPolicyPath=Join-Path $script:ReleaseBuildRepository 'scripts/maintenance/generated-workspace-policy.json'}
    $policy=Get-GeneratedWorkspacePolicy -Path $ProjectPolicyPath
    # The sole deletion target is this producer's build directory. Formal
    # stages/packages are neither registered nor selected for retention.
    $root=[pscustomobject]@{id='release-build-temporaries';path=$Paths.OutputRoot;excludedNames=@();maxRecentCompleted=0;maxBytes=8589934592}
    # A root under an unconfigured {WorkRoot} resolves to $null: it is not this OutputRoot and protects nothing here.
    $otherRoots=@($policy.roots|Where-Object {$resolved=Resolve-GeneratedWorkspacePolicyPath -Path ([string]$_.path); $null -eq $resolved -or $resolved.TrimEnd('\') -ine $Paths.OutputRoot})
    $policy.roots=@($root)+$otherRoots
    $policy.enabled=$true
    $referenceRoots=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($candidate in @($policy.roots.path)+@($policy.referenceRoots)) {
        if(-not [string]::IsNullOrWhiteSpace([string]$candidate)) {
            $path=Resolve-GeneratedWorkspacePolicyPath -Path ([string]$candidate)
            if($null -ne $path -and [IO.Directory]::Exists($path)){[void]$referenceRoots.Add($path)}
        }
    }
    [void][IO.Directory]::CreateDirectory($Paths.OutputRoot)
    Assert-ReleasePathWithoutReparse $Paths.OutputRoot
    [void]$referenceRoots.Add($Paths.OutputRoot)
    # Lifecycle registration cannot select unrelated OutputRoot siblings. The
    # enabled retention always receives an exact WorkspacePaths selection.
    $registration=($policy|ConvertTo-Json -Depth 16)|ConvertFrom-Json
    $registration.enabled=$false
    [pscustomobject]@{Paths=$Paths;Policy=$policy;RegistrationPolicy=$registration;ReferenceRoots=@($referenceRoots)}
}
function New-ReleaseBuildWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
        [string]$ProjectPolicyPath
    )
    $paths=Assert-ReleaseOutputTargets -OutputRoot $OutputRoot -Version $Version
    $context=Get-ReleaseBuildPolicyContext -Paths $paths -ProjectPolicyPath $ProjectPolicyPath
    $created=New-GeneratedWorkspace -RootPath $paths.OutputRoot -WorkspacePath $paths.BuildRoot -Purpose $script:ReleaseBuildPurpose -References @() -Policy $context.RegistrationPolicy -ReferenceRoots $context.ReferenceRoots
    $context|Add-Member -NotePropertyName Workspace -NotePropertyValue $created.workspace -PassThru
}
# Only for a caller that knows the previous producer of this version is gone: a JobQueue restart follows an attempt
# the queue stopped or killed. New-ReleaseBuildWorkspace still refuses a running build directory, because by itself
# it cannot tell a dead producer from a live one. The leftover build directory is closed as failed and retired
# through the retention entry (occupancy and path checks included); a formal stage or package is never touched.
function Close-AbandonedReleaseBuildWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?$')][string]$Version,
        [Parameter(Mandatory)][string]$Reason,
        [string]$ProjectPolicyPath
    )
    $paths=Get-ReleaseOutputPaths -OutputRoot $OutputRoot -Version $Version
    $marker=Join-Path $paths.BuildRoot '.generated-workspace.json'
    if(-not [IO.File]::Exists($marker)){return $null}
    $old=Get-Content -LiteralPath $marker -Raw|ConvertFrom-Json
    if($old.state -cne 'running' -or $old.rootId -cne 'release-build-temporaries' -or $old.purpose -cne $script:ReleaseBuildPurpose){return $null}
    $context=Get-ReleaseBuildPolicyContext -Paths $paths -ProjectPolicyPath $ProjectPolicyPath
    Complete-ReleaseBuildWorkspace -Context $context -Outcome failed -FailureMessage $Reason
}
function Complete-ReleaseBuildWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][ValidateSet('success','failed')][string]$Outcome,
        [string]$FailureMessage
    )
    $paths=$Context.Paths
    # Publication and installer preflights are synchronous. Occupancy and path
    # checks in the official maintenance entry still protect any remaining user.
    $closed=Complete-GeneratedWorkspace -RootPath $paths.OutputRoot -WorkspacePath $paths.BuildRoot -Outcome $Outcome -References @() -Policy $Context.RegistrationPolicy -ReferenceRoots $Context.ReferenceRoots
    $retention=Invoke-GeneratedWorkspaceRetention -RootPath $paths.OutputRoot -WorkspacePaths @($paths.BuildRoot) -Policy $Context.Policy -ReferenceRoots $Context.ReferenceRoots -Apply
    $retired=-not [IO.Directory]::Exists($paths.BuildRoot)
    $message=if([string]::IsNullOrWhiteSpace($FailureMessage)){''}else{$FailureMessage.Substring(0,[Math]::Min(2048,$FailureMessage.Length))}
    $receipt=[pscustomobject]@{schema='rm/release-build-lifecycle/v1';version=$paths.Version;outcome=$Outcome;completedUtc=$closed.workspace.completedUtc;temporaryWorkspace=$paths.BuildRoot;temporaryWorkspaceRetired=$retired;failure=$message}
    # One bounded receipt is replaced per invocation; no full-round archive or
    # accumulating per-version failure copies accompany the formal package.
    Assert-ReleaseBuildReceiptPath $paths.ReceiptPath
    $payload=$receipt|ConvertTo-Json -Depth 4
    if([Text.Encoding]::UTF8.GetByteCount($payload) -gt 16384){throw 'Release build conclusion exceeds its 16KiB receipt bound.'}
    [IO.File]::WriteAllText($paths.ReceiptPath,$payload,[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{WorkspaceRetired=$retired;Receipt=$receipt;Retention=$retention}
}
Export-ModuleMember -Function Assert-ReleaseOutputTargets,New-ReleaseBuildWorkspace,Complete-ReleaseBuildWorkspace,Close-AbandonedReleaseBuildWorkspace
