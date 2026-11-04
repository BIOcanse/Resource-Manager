[CmdletBinding()]
param(
    [ValidateSet('New', 'Adopt', 'Complete', 'Keep', 'References', 'Inspect', 'Prune', 'Archive', 'Initialize', 'RetireReferences', 'RepairArchiveDocuments')]
    [string]$Action = 'Inspect',

    [string]$RootId,
    [string]$DestinationRootId,
    [string]$DestinationName,
    [string[]]$DocumentRoots = @(),
    [string]$RecordedMarkersPath,
    [string]$ReferenceRetirementsPath,
    [string]$ArchiveReceiptPath,
    [switch]$Resume,

    # JSON array of { rootId, workspacePaths }; batch Inspect/Prune emit NDJSON.
    [string]$WorkspaceGroups,

    # JSON array of completed-workspace adoption records; exclusive to Adopt.
    [string]$AdoptionsPath,

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
    $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $PolicyPath = Join-Path $repositoryRoot 'build/scripts/workspaces/generated-workspace-policy.json'
}
Import-Module (Join-Path $PSScriptRoot 'GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking

function Get-CliWorkspaceGroups {
    param([object]$Policy,[string]$Kind,[string[]]$Paths)
    $groups=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $locations=@(Get-GeneratedWorkspaceLocations -Policy $Policy -MetadataOnly)+@(Get-GeneratedWorkspacePolicyRoots -Policy $Policy)
    foreach($path in $Paths){
        if(-not [IO.Path]::IsPathRooted($path)){throw 'Workspace selections must be absolute.'}
        $full=[IO.Path]::GetFullPath($path).TrimEnd('\','/')
        $parent=[IO.Path]::GetDirectoryName($full)
        if([IO.File]::Exists((Join-Path $full '.generated-workspace.json'))){
            $location=Resolve-GeneratedWorkspaceRoot -Policy $Policy -WorkspacePath $full -RootId $Kind
        }else{
            $location=@($locations|Where-Object {$_.Path -ieq $parent -and @($_.RootIds) -icontains $Kind})|Select-Object -First 1
            if(-not $location){throw "Workspace is not a direct child of a known or configured '$Kind' location: $full"}
        }
        if(-not $groups.ContainsKey($location.Path)){$groups.Add($location.Path,[pscustomobject]@{RootPath=$location.Path;WorkspacePaths=[Collections.Generic.List[string]]::new()})}
        $groups[$location.Path].WorkspacePaths.Add($full)
    }
    return @($groups.Values|ForEach-Object {[pscustomobject]@{RootPath=$_.RootPath;WorkspacePaths=@($_.WorkspacePaths.ToArray())}})
}

try {
    if($ArchiveReceiptPath -and $Action -ne 'RepairArchiveDocuments'){throw 'ArchiveReceiptPath is exclusive to RepairArchiveDocuments.'}
    $policy = Get-GeneratedWorkspacePolicy -Path $PolicyPath
    if ($Action -eq 'Initialize') {
        if ($WorkspacePath -or $WorkspacePaths.Count -gt 0 -or $WorkspaceGroups -or $AdoptionsPath -or $DestinationRootId -or $DestinationName -or $DocumentRoots.Count -gt 0 -or $RecordedMarkersPath -or $ReferenceRetirementsPath -or $Resume -or $Purpose -or $Outcome -or $References.Count -gt 0 -or $CompletedUtc -or $Apply -or $KeepState -ne 'true') { throw 'Initialize accepts only optional RootId, PolicyPath and ReferenceRoots.' }
        $initialized=$null
        if($RootId){
            $configuredRoot = Get-GeneratedWorkspaceRoot -Policy $policy -RootId $RootId
            $initialized=Initialize-GeneratedWorkspaceRoot -RootPath $configuredRoot.Path -Policy $policy
        }
        $locations=Initialize-GeneratedWorkspaceLocations -Policy $policy
        if($null -ne $initialized){
            $initialized|Add-Member -NotePropertyName locations -NotePropertyValue $locations
            $initialized|ConvertTo-Json -Depth 12
        }else{$locations|ConvertTo-Json -Depth 12}
        return
    }
    $knownLocations=@(Get-GeneratedWorkspaceLocations -Policy $policy -MetadataOnly:($Action -ne 'Inspect'))
    if($Action -eq 'Inspect' -and -not $RootId -and -not $WorkspaceGroups){
        if($WorkspacePath -or $WorkspacePaths.Count -gt 0){throw 'Workspace selections require RootId or WorkspaceGroups.'}
        [pscustomobject]@{statePath=(Resolve-GeneratedWorkspacePolicyPath -Path $policy.statePath);knownLocations=$knownLocations;configuredLocations=@(Get-GeneratedWorkspacePolicyRoots -Policy $policy)}|ConvertTo-Json -Depth 16
        return
    }
    $referenceRootPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($location in $knownLocations+@(Get-GeneratedWorkspacePolicyRoots -Policy $policy)) {
        if (Test-Path -LiteralPath $location.Path -PathType Container) {
            $referenceRootPaths.Add([string]$location.Path)
        }
    }
    foreach ($referenceRoot in @($policy.referenceRoots) + @($ReferenceRoots)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$referenceRoot)) {
            $referenceRootPaths.Add([string]$referenceRoot)
        }
    }
    $allReferenceRoots = [string[]]$referenceRootPaths.ToArray()
    if($Action -eq 'RepairArchiveDocuments'){
        if(-not $RootId -or -not $DestinationRootId -or -not $ArchiveReceiptPath -or $WorkspacePath -or $WorkspacePaths.Count -gt 0 -or $WorkspaceGroups -or $AdoptionsPath -or $DestinationName -or $RecordedMarkersPath -or $ReferenceRetirementsPath -or $Resume -or $Purpose -or $Outcome -or $References.Count -gt 0 -or $CompletedUtc -or $KeepState -ne 'true'){throw 'RepairArchiveDocuments requires RootId, DestinationRootId, ArchiveReceiptPath and optional DocumentRoots/Apply.'}
        $receipt=Get-Item -LiteralPath $ArchiveReceiptPath -Force
        if($receipt.PSIsContainer -or ($receipt.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $receipt.Length -gt 1MB){throw 'ArchiveReceiptPath must be a bounded regular JSON file.'}
        if($DocumentRoots.Count -eq 0){
            Import-Module (Join-Path $PSScriptRoot '..\common\MachineRoots.psm1') -DisableNameChecking
            $DocumentRoots=@((Join-Path (Get-RmMainCheckout) 'docs'))
        }
        $archive=Get-Content $receipt.FullName -Raw|ConvertFrom-Json
        $coldRoot=Resolve-GeneratedWorkspaceRoot -Policy $policy -RootId $DestinationRootId -WorkspacePath ([string]$archive.destination)
        $sourceParent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath([string]$archive.source))
        $sourceLocation=@(Get-GeneratedWorkspaceLocations -Policy $policy -RootId $RootId|Where-Object Path -IEQ $sourceParent)
        if(-not $sourceLocation.Count -and ($null -eq $archive.PSObject.Properties['sourceLocation'] -or @($archive.sourceLocation.RootIds) -inotcontains $RootId)){throw 'The removed source requires an archive receipt bound to its original root kind.'}
        Repair-GeneratedWorkspaceArchiveDocuments -RootPath $sourceParent -DestinationRootPath $coldRoot.Path -ArchiveReceipt $archive -DocumentRoots $DocumentRoots -Policy $policy -ReferenceRoots $allReferenceRoots -Apply:$Apply|ConvertTo-Json -Depth 12
        return
    }
    if($ArchiveReceiptPath){throw 'ArchiveReceiptPath is exclusive to RepairArchiveDocuments.'}
    if($Action -eq 'RetireReferences'){
        if(-not $RootId -or -not $ReferenceRetirementsPath -or $WorkspacePath -or $WorkspacePaths.Count -gt 0 -or $WorkspaceGroups -or $AdoptionsPath -or $DestinationRootId -or $DestinationName -or $DocumentRoots.Count -gt 0 -or $RecordedMarkersPath -or $Resume -or $Purpose -or $Outcome -or $References.Count -gt 0 -or $CompletedUtc -or $KeepState -ne 'true'){throw 'RetireReferences requires RootId, ReferenceRetirementsPath and optional Apply/ReferenceRoots/PolicyPath.'}
        $manifest=Get-Item -LiteralPath $ReferenceRetirementsPath -Force
        if($manifest.PSIsContainer -or ($manifest.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $manifest.Length -gt 1MB){throw 'ReferenceRetirementsPath must be a regular JSON array no larger than 1 MiB.'}
        $raw=Get-Content -LiteralPath $manifest.FullName -Raw
        if(-not $raw.TrimStart().StartsWith('[')){throw 'ReferenceRetirementsPath requires a JSON array.'}
        $requests=@($raw|ConvertFrom-Json)
        if($requests.Count -eq 1 -and $requests[0] -is [array]){$requests=$requests[0]}
        $locations=@(Get-CliWorkspaceGroups -Policy $policy -Kind $RootId -Paths @($requests|ForEach-Object {$_.workspacePath}))
        if($locations.Count -ne 1){throw 'Reference retirements must name exactly one physical location.'}
        Retire-GeneratedWorkspaceReferences -RootPath $locations[0].RootPath -Retirements $requests -Policy $policy -ReferenceRoots $allReferenceRoots -Apply:$Apply|ConvertTo-Json -Depth 16
        return
    }
    if($ReferenceRetirementsPath){throw 'ReferenceRetirementsPath is exclusive to RetireReferences.'}
    if ($Action -eq 'Archive') {
        if (-not $RootId -or -not $WorkspacePath -or -not $DestinationRootId -or $WorkspaceGroups -or $AdoptionsPath -or $WorkspacePaths.Count -gt 0 -or $Purpose -or $Outcome -or $References.Count -gt 0 -or $CompletedUtc -or $KeepState -ne 'true') {
            throw 'Archive requires RootId, WorkspacePath and DestinationRootId; omit batch/adoption selections.'
        }
        $sourceRoot = Resolve-GeneratedWorkspaceRoot -Policy $policy -RootId $RootId -WorkspacePath $WorkspacePath
        $destinationRoot = Get-GeneratedWorkspaceRoot -Policy $policy -RootId $DestinationRootId
        if($Resume){
            $leaf=if($DestinationName){$DestinationName}else{[IO.Path]::GetFileName($WorkspacePath)}
            $matches=@(Get-GeneratedWorkspaceLocations -Policy $policy -RootId $DestinationRootId|Where-Object {Test-Path -LiteralPath (Join-Path $_.Path $leaf) -PathType Container})
            if($matches.Count -ne 1){throw 'Resume requires exactly one known archive destination; the current default does not select history.'}
            $destinationRoot=$matches[0]
        }
        if ($DocumentRoots.Count -eq 0) {
            Import-Module (Join-Path $PSScriptRoot '..\common\MachineRoots.psm1') -DisableNameChecking
            $mainDocs = Join-Path (Get-RmMainCheckout) 'docs'
            if (Test-Path -LiteralPath $mainDocs -PathType Container) { $DocumentRoots = @($mainDocs) }
        }
        $recordedMarkers=@()
        if($RecordedMarkersPath){
            $manifest=Get-Item -LiteralPath $RecordedMarkersPath -Force
            if($manifest.PSIsContainer -or ($manifest.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $manifest.Length -gt 1MB){throw 'RecordedMarkersPath must be a regular JSON array no larger than 1 MiB.'}
            $raw=Get-Content -LiteralPath $manifest.FullName -Raw
            if(-not $raw.TrimStart().StartsWith('[')){throw 'RecordedMarkersPath requires a JSON array.'}
            $recordedMarkers=@($raw|ConvertFrom-Json)
            if($recordedMarkers.Count -eq 1 -and $recordedMarkers[0] -is [array]){$recordedMarkers=$recordedMarkers[0]}
        }
        $result = Move-GeneratedWorkspaceToArchive -RootPath $sourceRoot.Path -WorkspacePath $WorkspacePath -DestinationRootPath $destinationRoot.Path -DestinationName $DestinationName -DocumentRoots $DocumentRoots -RecordedMarkers $recordedMarkers -Policy $policy -ReferenceRoots $allReferenceRoots -Resume:$Resume -Apply:$Apply
        ConvertTo-Json -InputObject $result -Depth 16
        return
    }
    if ($DestinationRootId -or $DestinationName -or $DocumentRoots.Count -gt 0 -or $RecordedMarkersPath -or $Resume) { throw 'Archive destination/document parameters are exclusive to Archive.' }
    if (-not [string]::IsNullOrWhiteSpace($AdoptionsPath)) {
        if ($Action -ne 'Adopt' -or [string]::IsNullOrWhiteSpace($RootId) -or
            -not [string]::IsNullOrWhiteSpace($WorkspaceGroups) -or -not [string]::IsNullOrWhiteSpace($WorkspacePath) -or
            $WorkspacePaths.Count -gt 0 -or -not [string]::IsNullOrWhiteSpace($Purpose) -or
            -not [string]::IsNullOrWhiteSpace($CompletedUtc) -or -not [string]::IsNullOrWhiteSpace($Outcome) -or
            $References.Count -gt 0 -or $Apply -or $KeepState -ne 'true') {
            throw 'AdoptionsPath is exclusive to Adopt; provide RootId and omit single-workspace, completion, Keep, Prune, and batch-inspection arguments.'
        }
        $manifest = Get-Item -LiteralPath $AdoptionsPath -Force
        if ($manifest.PSIsContainer -or ($manifest.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'AdoptionsPath must be a regular, non-reparse JSON file.'
        }
        if ($manifest.Length -gt 8MB) { throw 'AdoptionsPath must be a JSON array no larger than 8 MiB.' }
        $rawAdoptions = Get-Content -LiteralPath $manifest.FullName -Raw
        if ([string]::IsNullOrWhiteSpace($rawAdoptions) -or -not $rawAdoptions.TrimStart().StartsWith('[')) {
            throw 'AdoptionsPath must contain a JSON array.'
        }
        $adoptions = @(ConvertFrom-Json -InputObject $rawAdoptions)
        if ($adoptions.Count -eq 1 -and $adoptions[0] -is [array]) { $adoptions = $adoptions[0] }
        if ($adoptions.Count -lt 1 -or $adoptions.Count -gt 512) { throw 'AdoptionsPath must contain between 1 and 512 adoption records.' }
        $inputTimer = [Diagnostics.Stopwatch]::StartNew()
        $inputMaxMilliseconds = 300000
        if ($null -ne $policy.defaults.PSObject.Properties['maxMilliseconds']) { $inputMaxMilliseconds = [int]$policy.defaults.maxMilliseconds }
        foreach ($entry in $adoptions) {
            if ($inputTimer.ElapsedMilliseconds -gt $inputMaxMilliseconds) { throw 'AdoptionsPath validation exceeded the policy time bound.' }
            if ($null -eq $entry -or $null -eq $entry.PSObject.Properties['WorkspacePath'] -or
                $null -eq $entry.PSObject.Properties['Purpose'] -or $null -eq $entry.PSObject.Properties['CompletedUtc'] -or
                $null -eq $entry.PSObject.Properties['References']) {
                throw 'Every adoption record must include WorkspacePath, Purpose, CompletedUtc, and References.'
            }
        }
        $locations=@(Get-CliWorkspaceGroups -Policy $policy -Kind $RootId -Paths @($adoptions|ForEach-Object {$_.WorkspacePath}))
        if($locations.Count -ne 1){throw 'Batch adoption must name one physical location; split distinct locations into separate batches.'}
        try {
            $result = Adopt-GeneratedWorkspaceBatch -RootPath ([string]$locations[0].RootPath) -Adoptions $adoptions -Policy $policy -ReferenceRoots $allReferenceRoots
        }
        catch {
            if ($null -ne $_.Exception.Data -and $_.Exception.Data.Contains('adoption')) {
                ConvertTo-Json -InputObject ([pscustomobject]@{ error=$_.Exception.Message; adoption=$_.Exception.Data['adoption'] }) -Depth 18 -Compress
                exit 1
            }
            throw
        }
        ConvertTo-Json -InputObject $result -Depth 18
        return
    }
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
            if (@($entry.workspacePaths).Count -eq 0) { throw 'Every batch group requires non-empty exact workspacePaths.' }
            foreach($physical in @(Get-CliWorkspaceGroups -Policy $policy -Kind ([string]$entry.rootId) -Paths @($entry.workspacePaths))){
                $same=@($groups|Where-Object RootPath -IEQ $physical.RootPath)
                if($same.Count){$same[0].WorkspacePaths=@($same[0].WorkspacePaths)+@($physical.WorkspacePaths)}else{$groups.Add($physical)}
            }
        }
        Invoke-GeneratedWorkspaceRetentionBatch -WorkspaceGroups @($groups.ToArray()) -Policy $policy -ReferenceRoots $allReferenceRoots -Apply:($Apply -and $Action -eq 'Prune') |
            ForEach-Object {
                $_|Add-Member -NotePropertyName knownLocations -NotePropertyValue @(Get-GeneratedWorkspaceLocations -Policy $policy -MetadataOnly:($Action -ne 'Inspect')) -Force
                ConvertTo-Json -InputObject $_ -Depth 18 -Compress
            }
        return
    }
    if ([string]::IsNullOrWhiteSpace($RootId)) { throw 'RootId is required without WorkspaceGroups.' }
    if($WorkspacePath){
        $physical=@(Get-CliWorkspaceGroups -Policy $policy -Kind $RootId -Paths @($WorkspacePath))
        $rootPath=[string]$physical[0].RootPath
    }else{
        $existing=@(Get-GeneratedWorkspaceLocations -Policy $policy -RootId $RootId -MetadataOnly)
        if($Action -in @('Inspect','Prune') -and $existing.Count){$rootPath=[string]$existing[0].Path}
        else{
            $configuredRoot = Get-GeneratedWorkspaceRoot -Policy $policy -RootId $RootId
            $rootPath = [string]$configuredRoot.Path
        }
    }
    if($Action -in @('Inspect','Prune') -and -not $WorkspacePath){
        if($WorkspacePaths.Count){
            $physical=@(Get-CliWorkspaceGroups -Policy $policy -Kind $RootId -Paths $WorkspacePaths)
        }else{
            $physical=@(Get-GeneratedWorkspaceLocations -Policy $policy -RootId $RootId -MetadataOnly|ForEach-Object {[pscustomobject]@{RootPath=$_.Path;WorkspacePaths=@()}})
            if(-not $physical.Count -and (Test-Path -LiteralPath $rootPath -PathType Container)){$physical=@([pscustomobject]@{RootPath=$rootPath;WorkspacePaths=@()})}
        }
        $results=@(foreach($location in $physical){
            $receipt=Invoke-GeneratedWorkspaceRetention -RootPath $location.RootPath -Policy $policy -ReferenceRoots $allReferenceRoots -WorkspacePaths $location.WorkspacePaths -Apply:($Action -eq 'Prune' -and $Apply)
            $receipt|Add-Member -NotePropertyName knownLocations -NotePropertyValue @(Get-GeneratedWorkspaceLocations -Policy $policy -MetadataOnly:($Action -ne 'Inspect')) -Force
            $receipt
        })
        if($results.Count -eq 1){$results[0]|ConvertTo-Json -Depth 18}else{ConvertTo-Json -InputObject $results -Depth 18}
        return
    }
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
                $result = Get-GeneratedWorkspaceState -RootPath $rootPath -WorkspacePath $WorkspacePath -Policy $policy
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
    if ($_.Exception.Data.Contains('archive')) {
        ConvertTo-Json -InputObject ([pscustomobject]@{error=$_.Exception.Message;archive=$_.Exception.Data['archive']}) -Depth 16
    }
    if ($_.Exception.Data.Contains('referenceRetirement')) {
        ConvertTo-Json -InputObject ([pscustomobject]@{error=$_.Exception.Message;referenceRetirement=$_.Exception.Data['referenceRetirement']}) -Depth 16
    }
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
