[CmdletBinding()]
param(
    [ValidateSet('New', 'Adopt', 'Complete', 'Keep', 'References', 'Inspect', 'Prune')]
    [string]$Action = 'Inspect',

    [string]$RootId,

    # JSON array of { rootId, workspacePaths }; batch Inspect/Prune emit NDJSON.
    [string]$WorkspaceGroups,

    [string]$WorkspacePath,
    [string[]]$WorkspacePaths = @(),
    [string]$Purpose,
    [ValidateSet('success', 'failed')]
    [string]$Outcome,
    [string[]]$References = @(),
    [string[]]$ReferenceRoots = @(),
    [string]$CompletedUtc,
    [ValidateSet('true', 'false')]
    [string]$KeepState = 'true',
    [string]$PolicyPath,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $PSScriptRoot 'generated-workspace-policy.json'
}
Import-Module (Join-Path $PSScriptRoot 'GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking

try {
    $policy = Get-GeneratedWorkspacePolicy -Path $PolicyPath
    $referenceRootPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($rootEntry in @($policy.roots)) {
        $configuredReferenceRoot = Get-GeneratedWorkspaceRoot -Policy $policy -RootId ([string]$rootEntry.id)
        if (Test-Path -LiteralPath $configuredReferenceRoot.Path -PathType Container) {
            $referenceRootPaths.Add([string]$configuredReferenceRoot.Path)
        }
    }
    foreach ($referenceRoot in @($policy.referenceRoots) + @($ReferenceRoots)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$referenceRoot)) {
            $referenceRootPaths.Add([string]$referenceRoot)
        }
    }
    $allReferenceRoots = [string[]]$referenceRootPaths.ToArray()
    if (-not [string]::IsNullOrWhiteSpace($WorkspaceGroups)) {
        if ($Action -notin @('Inspect', 'Prune') -or -not [string]::IsNullOrWhiteSpace($RootId) -or
            -not [string]::IsNullOrWhiteSpace($WorkspacePath) -or $WorkspacePaths.Count -gt 0) {
            throw 'WorkspaceGroups is exclusive to batch Inspect/Prune; omit RootId and single-workspace arguments.'
        }
        $manifest = Get-Item -LiteralPath $WorkspaceGroups -Force
        if ($manifest.Length -gt 1048576 -or $manifest.PSIsContainer) { throw 'WorkspaceGroups must be a JSON file no larger than 1 MiB.' }
        $groups = [System.Collections.Generic.List[object]]::new()
        $entries = @(Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json)
        # ConvertFrom-Json emits an array as a single item on Windows PowerShell.
        if ($entries.Count -eq 1 -and $entries[0] -is [array]) { $entries = $entries[0] }
        if ($entries.Count -lt 1 -or $entries.Count -gt 4096) { throw 'WorkspaceGroups must contain between 1 and 4096 roots.' }
        $inputTimer = [Diagnostics.Stopwatch]::StartNew()
        $inputMaxMilliseconds = 300000
        if ($null -ne $policy.defaults.PSObject.Properties['maxMilliseconds']) { $inputMaxMilliseconds = [int]$policy.defaults.maxMilliseconds }
        $inputPathCount = [long]0
        foreach ($entry in $entries) {
            if ($inputTimer.ElapsedMilliseconds -gt $inputMaxMilliseconds) { throw 'Batch manifest validation exceeded the policy time bound.' }
            $inputPathCount += @($entry.workspacePaths).Count
            if ($inputPathCount -gt 750000) { throw 'Batch input exceeds the 750000 exact-workspace limit.' }
            $configured = Get-GeneratedWorkspaceRoot -Policy $policy -RootId ([string]$entry.rootId)
            if (@($entry.workspacePaths).Count -eq 0) { throw 'Every batch group requires non-empty exact workspacePaths.' }
            $groups.Add([pscustomobject]@{ RootPath = [string]$configured.Path; WorkspacePaths = @($entry.workspacePaths) })
        }
        Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @($groups.ToArray()) -Policy $policy -ReferenceRoots $allReferenceRoots -Apply:($Apply -and $Action -eq 'Prune') |
            ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 16 -Compress }
        return
    }
    if ([string]::IsNullOrWhiteSpace($RootId)) { throw 'RootId is required without WorkspaceGroups.' }
    $configuredRoot = Get-GeneratedWorkspaceRoot -Policy $policy -RootId $RootId
    $rootPath = [string]$configuredRoot.Path
    switch ($Action) {
        'New' {
            if ([string]::IsNullOrWhiteSpace($WorkspacePath) -or [string]::IsNullOrWhiteSpace($Purpose)) { throw 'New requires -WorkspacePath and -Purpose.' }
            $result = New-GeneratedWorkspace -RootPath $rootPath -WorkspacePath $WorkspacePath -Purpose $Purpose -References $References -Policy $policy -ReferenceRoots $allReferenceRoots
        }
        'Adopt' {
            if ([string]::IsNullOrWhiteSpace($WorkspacePath) -or [string]::IsNullOrWhiteSpace($Purpose)) { throw 'Adopt requires -WorkspacePath and -Purpose.' }
            $result = Adopt-GeneratedWorkspace -RootPath $rootPath -WorkspacePath $WorkspacePath -Purpose $Purpose -References $References -CompletedUtc $CompletedUtc -Policy $policy -ReferenceRoots $allReferenceRoots
        }
        'Complete' {
            if ([string]::IsNullOrWhiteSpace($WorkspacePath) -or [string]::IsNullOrWhiteSpace($Outcome)) { throw 'Complete requires -WorkspacePath and -Outcome.' }
            $result = Complete-GeneratedWorkspace -RootPath $rootPath -WorkspacePath $WorkspacePath -Outcome $Outcome -References $References -Policy $policy -ReferenceRoots $allReferenceRoots
        }
        'Keep' {
            if ([string]::IsNullOrWhiteSpace($WorkspacePath)) { throw 'Keep requires -WorkspacePath.' }
            $result = Set-GeneratedWorkspaceKeep -RootPath $rootPath -WorkspacePath $WorkspacePath -Keep ($KeepState -eq 'true') -Policy $policy -ReferenceRoots $allReferenceRoots
        }
        'References' {
            if ([string]::IsNullOrWhiteSpace($WorkspacePath)) { throw 'References requires -WorkspacePath.' }
            $result = Set-GeneratedWorkspaceReferences -RootPath $rootPath -WorkspacePath $WorkspacePath -References $References -Policy $policy -ReferenceRoots $allReferenceRoots
        }
        'Inspect' {
            if (-not [string]::IsNullOrWhiteSpace($WorkspacePath)) {
                $result = Get-GeneratedWorkspaceState -RootPath $rootPath -WorkspacePath $WorkspacePath
            }
            else {
                $result = Invoke-GeneratedWorkspaceRetention -RootPath $rootPath -Policy $policy -ReferenceRoots $allReferenceRoots -WorkspacePaths $WorkspacePaths
            }
        }
        'Prune' {
            $result = Invoke-GeneratedWorkspaceRetention -RootPath $rootPath -Policy $policy -ReferenceRoots $allReferenceRoots -WorkspacePaths $WorkspacePaths -Apply:$Apply
        }
    }
    ConvertTo-Json -InputObject $result -Depth 16
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
