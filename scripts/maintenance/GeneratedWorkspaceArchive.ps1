# Loaded in GeneratedWorkspaceRetention's module scope to reuse its path, lock,
# marker, bounded snapshot and immediate source-deletion checks.
function ConvertTo-GWArchiveReference {
    param([string]$Path, [string]$Source, [string]$Destination, [AllowNull()][object]$RecordedPaths=$null)
    if ($null -ne $RecordedPaths -and $RecordedPaths.ContainsKey($Path)) { return $RecordedPaths[$Path] }
    if (Test-GWPathWithin -Path $Path -Root $Source -AllowRoot) {
        return $Destination + ([IO.Path]::GetFullPath($Path)).Substring($Source.Length)
    }
    return $Path
}

function Retire-GeneratedWorkspaceReferences {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RootPath,[Parameter(Mandatory)][object[]]$Retirements,
        [object]$Policy=(Get-GeneratedWorkspacePolicy),[string[]]$ReferenceRoots=@(),[switch]$Apply)
    if(-not (Test-GWPolicyEnabled -Policy $Policy)){throw 'Reference retirement requires enabled project retention.'}
    $root=ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $config=Get-GWPolicyRoot -Policy $Policy -RootPath $root
    $budget=New-GWScanBudget -Limits (Get-GWPolicyLimits -Policy $Policy -RootConfig $config)
    if($Retirements.Count -eq 0){throw 'Reference retirement requires explicit completed owners and evidence.'}
    $locks=Enter-GWRootLocks -RootPaths @(Get-GWReferenceRootPaths -RootPath $root -ReferenceRoots $ReferenceRoots)
    $plans=[Collections.Generic.List[object]]::new();$updated=[Collections.Generic.List[string]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try{
        foreach($request in $Retirements){
            Add-GWScanNode -Budget $budget -RootPath $root
            $owner=(Assert-GWManagedWorkspacePath -RootPath $root -WorkspacePath ([string](Get-GWProperty $request 'workspacePath' '')) -RootConfig $config).WorkspacePath
            if(-not $seen.Add($owner)){throw 'Duplicate reference-retirement owner.'}
            $read=Read-GWStateRecord -RootPath $root -WorkspacePath $owner
            if(-not $read.Valid -or $read.State.state -cne 'completed' -or $read.State.rootId -ine [string]$config.id){throw "Reference retirement only changes completed owners registered by this root: $owner"}
            $reason=[string](Get-GWProperty $request 'reason' '')
            $evidence=@(Get-GWProperty $request 'evidence' @())
            $retired=@(ConvertTo-GWReferences -References @(Get-GWProperty $request 'retiredReferences' @()))
            if(-not $reason.Trim() -or $retired.Count -eq 0 -or $evidence.Count -eq 0){throw 'Reference retirement requires a reason, exact outgoing references and hash-bound evidence.'}
            foreach($proof in $evidence){
                Add-GWScanNode -Budget $budget -RootPath $root
                $file=ConvertTo-GWFullPath -Path ([string](Get-GWProperty $proof 'path' '')) -ParameterName 'reference evidence'
                Assert-GWNoReparsePath -Path $file -RequireLeaf
                $info=[IO.FileInfo]::new($file)
                if(-not $info.Exists -or $info.Length -gt $script:GeneratedWorkspaceMarkerMaxBytes){throw "Reference evidence must be a bounded regular file: $file"}
                $hash=[string](Get-GWProperty $proof 'sha256' '')
                if($hash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $hash){throw "Reference evidence hash mismatch: $file"}
            }
            foreach($reference in $retired){if(@($read.State.references|Where-Object {$_ -ieq $reference}).Count -ne 1){throw "An exact retired reference is no longer present: $reference"}}
            $after=Copy-GWStateRecord -State $read.State
            $after.references=@($read.State.references|Where-Object {$_ -notin $retired})
            $null=New-GWStateRecordPayload -State $after
            $plans.Add([pscustomobject]@{state=$after;retiredReferences=$retired;reason=$reason;evidence=$evidence})
        }
        if($Apply){foreach($plan in $plans){Add-GWScanNode -Budget $budget -RootPath $root;Write-GWStateRecord -State $plan.state;$updated.Add([string]$plan.state.workspacePath)}}
        # Deliberately no implicit completed-pool collection here. The caller first
        # persists this evidence/receipt and then explicitly selects the target root's Prune.
        [pscustomobject]@{applied=[bool]$Apply;updatedOwners=@($updated.ToArray());retirements=@($plans|ForEach-Object{[pscustomobject]@{workspacePath=$_.state.workspacePath;retiredReferences=$_.retiredReferences;reason=$_.reason;evidence=$_.evidence}});retentionInvoked=$false}
    }catch{
        $_.Exception.Data['referenceRetirement']=[pscustomobject]@{updatedOwners=@($updated.ToArray());plannedOwners=@($plans|ForEach-Object{$_.state.workspacePath})}
        throw
    }finally{Exit-GWRootLocks -Streams $locks}
}

function Get-GWArchiveDocumentPlan {
    param([string[]]$DocumentRoots, [string]$Source, [string]$Destination, [object]$Limits, [AllowNull()][object]$RecordedPaths=$null)
    $budget = New-GWScanBudget -Limits ([pscustomobject]@{MaxNodes=$Limits.maxDocumentNodes;MaxMilliseconds=$Limits.maxDocumentMilliseconds})
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($root in $DocumentRoots) {
        $full = ConvertTo-GWFullPath -Path $root -ParameterName 'DocumentRoot'
        Assert-GWNoReparsePath -Path $full -RequireLeaf
        if ((Test-GWPathWithin -Path $full -Root $Source -AllowRoot) -or (Test-GWPathWithin -Path $full -Root $Destination -AllowRoot)) { throw 'Document roots must be outside the relocated workspace.' }
        $pending.Push($full)
    }
    $plans = [Collections.Generic.List[object]]::new()
    # Build one matcher for this operation. Repeated full-text scans for every
    # historical-marker/path variant multiply work in large docs trees.
    $replacements=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pathPairs=[Collections.Generic.List[object]]::new()
    if($null -ne $RecordedPaths){foreach($old in $RecordedPaths.Keys){$pathPairs.Add(@($old,$RecordedPaths[$old]))}}
    $pathPairs.Add(@($Source,$Destination))
    foreach($pathPair in $pathPairs){
        foreach($pair in @(@($pathPair[0],$pathPair[1]),@($pathPair[0].Replace('\','/'),$pathPair[1].Replace('\','/')),@($pathPair[0].Replace('\','\\'),$pathPair[1].Replace('\','\\')))){
            $old=[string]$pair[0];$new=[string]$pair[1]
            if($replacements.ContainsKey($old) -and $replacements[$old] -cne $new){throw 'Conflicting archive document path mapping.'}
            $replacements[$old]=$new
        }
    }
    # Exact marker mappings must win over their containing workspace prefix.
    $alternatives=@($replacements.Keys|Sort-Object -Property Length -Descending|ForEach-Object{[regex]::Escape($_)})
    $matcher=[regex]::new(('(?:'+($alternatives -join '|')+')(?![A-Za-z0-9_.-])'),([Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant -bor [Text.RegularExpressions.RegexOptions]::Compiled))
    $replaceMatch=[Text.RegularExpressions.MatchEvaluator]{param($match) $replacements[$match.Value]}
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        if (-not $seen.Add($current)) { continue }
        Add-GWScanNode -Budget $budget -RootPath $current
        foreach ($entry in ([IO.DirectoryInfo]::new($current)).EnumerateFileSystemInfos()) {
            Add-GWScanNode -Budget $budget -RootPath $current
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Document tree contains a reparse point: $($entry.FullName)" }
            if ($entry -is [IO.DirectoryInfo]) { $pending.Push($entry.FullName); continue }
            if ($entry.Extension -ine '.md') { continue }
            if ($entry.Length -gt [long]$Limits.maxDocumentBytes) { throw "Document exceeds archive rewrite bound: $($entry.FullName)" }
            $bytes = [IO.File]::ReadAllBytes($entry.FullName)
            $original = [Text.UTF8Encoding]::new($false,$true).GetString($bytes)
            $text = $matcher.Replace($original,$replaceMatch)
            if ($text -cne $original) {
                $plans.Add([pscustomobject]@{Path=$entry.FullName;Before=$bytes;After=[Text.UTF8Encoding]::new($false).GetBytes($text)})
            }
        }
    }
    return @($plans.ToArray())
}

function Write-GWArchiveDocument {
    param([object]$Plan)
    Assert-GWNoReparsePath -Path $Plan.Path -RequireLeaf
    $current = [IO.File]::ReadAllBytes($Plan.Path)
    if ([Convert]::ToBase64String($current) -cne [Convert]::ToBase64String($Plan.Before)) { throw "Document changed before archive reference update: $($Plan.Path)" }
    $temporary = $Plan.Path + '.' + [Guid]::NewGuid().ToString('N') + '.archive-update'
    try {
        [IO.File]::WriteAllBytes($temporary, [byte[]]$Plan.After)
        [IO.File]::Replace($temporary, $Plan.Path, [NullString]::Value)
    }
    finally {
        # Only this exact, newly created temporary file; never a tree cleanup.
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Repair-GeneratedWorkspaceArchiveDocuments {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RootPath,[Parameter(Mandatory)][string]$DestinationRootPath,
        [Parameter(Mandatory)][object]$ArchiveReceipt,[string[]]$DocumentRoots=@(),
        [object]$Policy=(Get-GeneratedWorkspacePolicy),[string[]]$ReferenceRoots=@(),[switch]$Apply)
    if(-not (Test-GWPolicyEnabled -Policy $Policy)){throw 'Archive document repair requires enabled retention.'}
    if(-not [bool](Get-GWProperty $ArchiveReceipt 'complete' $false) -or -not [bool](Get-GWProperty $ArchiveReceipt 'applied' $false)){throw 'Document repair requires a successful applied archive receipt.'}
    $root=ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $cold=ConvertTo-GWFullPath -Path $DestinationRootPath -ParameterName 'DestinationRootPath'
    $sourceConfig=Get-GWPolicyRoot -Policy $Policy -RootPath $root
    $coldConfig=Get-GWPolicyRoot -Policy $Policy -RootPath $cold
    if(-not [bool](Get-GWProperty $coldConfig 'archiveOnly' $false)){throw 'Document repair destination must be archiveOnly.'}
    $source=(Assert-GWManagedWorkspacePath -RootPath $root -WorkspacePath ([string]$ArchiveReceipt.source) -RootConfig $sourceConfig).WorkspacePath
    $destination=(Assert-GWManagedWorkspacePath -RootPath $cold -WorkspacePath ([string]$ArchiveReceipt.destination) -RootConfig $coldConfig).WorkspacePath
    $locks=Enter-GWRootLocks -RootPaths @(Get-GWReferenceRootPaths -RootPath $root -ReferenceRoots (@($cold)+$ReferenceRoots))
    try{
        if(Test-Path -LiteralPath $source){throw 'Document repair requires an already removed archive source.'}
        $read=Read-GWStateRecord -RootPath $cold -WorkspacePath $destination
        if(-not $read.Valid -or $read.State.state -cne 'completed' -or $read.State.rootId -ine [string]$coldConfig.id -or $read.State.workspaceId -cne [string]$ArchiveReceipt.workspaceId){throw 'Archive receipt destination identity does not match a completed cold workspace.'}
        $recorded=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($mapping in @(Get-GWProperty $ArchiveReceipt 'recordedMarkers' @())){
            $old=ConvertTo-GWFullPath -Path ([string]$mapping.source) -ParameterName 'recorded source'
            $new=ConvertTo-GWFullPath -Path ([string]$mapping.destination) -ParameterName 'recorded destination'
            if(-not (Test-GWPathWithin -Path $old -Root $source) -or -not (Test-GWPathWithin -Path $new -Root $destination)){throw 'Recorded archive mapping escapes its workspace.'}
            Assert-GWNoReparsePath -Path $new -RequireLeaf
            $info=[IO.FileInfo]::new($new)
            if(-not $info.Exists -or $info.Length -gt $script:GeneratedWorkspaceMarkerMaxBytes -or (Get-FileHash $new -Algorithm SHA256).Hash -ine [string]$mapping.sha256){throw 'Recorded archive evidence hash mismatch.'}
            $recorded.Add($old,$new)
        }
        $plans=@(Get-GWArchiveDocumentPlan -DocumentRoots $DocumentRoots -Source $source -Destination $destination -Limits $Policy.archiveLimits -RecordedPaths $recorded)
        $updated=[Collections.Generic.List[string]]::new()
        if($Apply){foreach($plan in $plans){Write-GWArchiveDocument -Plan $plan;$updated.Add($plan.Path)}}
        [pscustomobject]@{source=$source;destination=$destination;workspaceId=$read.State.workspaceId;applied=[bool]$Apply;plannedDocuments=@($plans|ForEach-Object{$_.Path});updatedDocuments=@($updated.ToArray());payloadChanged=$false}
    }finally{Exit-GWRootLocks -Streams $locks}
}

function Move-GeneratedWorkspaceToArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RootPath,
        [Parameter(Mandatory)][string]$WorkspacePath,
        [Parameter(Mandatory)][string]$DestinationRootPath,
        [string]$DestinationName,
        [string[]]$DocumentRoots = @(),
        [object[]]$RecordedMarkers = @(),
        [switch]$Resume,
        [object]$Policy = (Get-GeneratedWorkspacePolicy),
        [string[]]$ReferenceRoots = @(),
        [switch]$Apply
    )
    if (-not (Test-GWPolicyEnabled -Policy $Policy)) { throw 'Archive requires the enabled project retention policy.' }
    $root = ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $destinationRoot = ConvertTo-GWFullPath -Path $DestinationRootPath -ParameterName 'DestinationRootPath'
    $sourceConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $root
    $destinationConfig = Get-GWPolicyRoot -Policy $Policy -RootPath $destinationRoot
    if (-not [bool](Get-GWProperty $destinationConfig 'archiveOnly' $false)) { throw 'Archive destination must be explicitly configured archiveOnly.' }
    $source = (Assert-GWManagedWorkspacePath -RootPath $root -WorkspacePath $WorkspacePath -RootConfig $sourceConfig).WorkspacePath
    if (-not $DestinationName) { $DestinationName = [IO.Path]::GetFileName($source) }
    $destination = (Assert-GWManagedWorkspacePath -RootPath $destinationRoot -WorkspacePath (Join-Path $destinationRoot $DestinationName) -RootConfig $destinationConfig).WorkspacePath
    if ((Test-GWPathWithin -Path $destination -Root $source -AllowRoot) -or (Test-GWPathWithin -Path $source -Root $destination -AllowRoot)) { throw 'Archive source and destination must be disjoint.' }
    $archiveLimits = Get-GWProperty $Policy 'archiveLimits' $null
    foreach ($name in @('maxNodes','maxMilliseconds','maxDocumentBytes','maxDocumentNodes','maxDocumentMilliseconds')) {
        if ([long](Get-GWProperty $archiveLimits $name 0) -le 0) { throw "Policy archiveLimits.$name must be configured and positive." }
    }
    $limits = Get-GWPolicyLimits -Policy $Policy -RootConfig $sourceConfig
    $transferLimits = [pscustomobject]@{ MaxNodes=[int]$archiveLimits.maxNodes; MaxMilliseconds=[int]$archiveLimits.maxMilliseconds }
    $roots = @(Get-GWReferenceRootPaths -RootPath $root -ReferenceRoots (@($destinationRoot) + $ReferenceRoots))
    $locks = Enter-GWRootLocks -RootPaths $roots
    $copyCreated = $false
    $resumeSnapshot = $null
    $verifiedResume = $false
    $reusedFiles = 0
    $referencesCommitted = $false
    $updatedMarkers = [Collections.Generic.List[string]]::new()
    $updatedDocuments = [Collections.Generic.List[string]]::new()
    $hashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $recordedPaths = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $recordedHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $retiredNestedReferences = [Collections.Generic.List[string]]::new()
    $phase = 'preflight'
    $destinationState = $null
    $removed = $null
    try {
        Assert-GWNoReparsePath -Path $source -RequireLeaf
        Assert-GWNoReparsePath -Path $destinationRoot -RequireLeaf
        Assert-GWNoReparsePath -Path $destination
        $read = Read-GWStateRecord -RootPath $root -WorkspacePath $source
        if (-not $read.Valid -or $read.State.state -cne 'completed' -or $read.State.rootId -ine [string]$sourceConfig.id) { throw 'Archive requires a valid completed workspace owned by its configured source root.' }
        $originalState = $read.State
        $budget = New-GWScanBudget -Limits $transferLimits
        if(Test-Path -LiteralPath $destination){
            if(-not $Resume){throw "Archive refuses an existing destination without Resume: $destination"}
            $staging=Read-GWStateRecord -RootPath $destinationRoot -WorkspacePath $destination
            $verifiedResume=$staging.Valid -and $staging.State.state -eq 'completed' -and $staging.State.keep -and
                $staging.State.purpose -ceq ($originalState.purpose+' [verified archive; Keep until source cleanup succeeds]')
            if(-not $staging.Valid -or $staging.State.rootId -ine [string]$destinationConfig.id -or $staging.State.workspaceId -cne $originalState.workspaceId -or
                (-not $verifiedResume -and $staging.State.state -ne 'running' -and -not ($staging.State.state -eq 'completed' -and $staging.State.outcome -eq 'failed' -and $staging.State.purpose -ceq ('Failed archive copy; original source retained: '+$source)))){throw 'Resume only accepts an uncommitted or protected verified archive with the exact source identity.'}
            $resumeSnapshot=Get-GWTreeSnapshot -WorkspacePath $destination -Budget $budget -CaptureDeletionSnapshot
            if(-not $resumeSnapshot.Complete -or $resumeSnapshot.ReparseCount -gt 0){throw 'Archive staging copy is incomplete or contains a reparse point.'}
        }elseif($Resume){throw 'Resume requires an existing archive staging copy.'}
        $snapshot = Get-GWTreeSnapshot -WorkspacePath $source -Budget $budget -CaptureDeletionSnapshot
        if (-not $snapshot.Complete -or $snapshot.ReparseCount -gt 0) { throw "Archive source is incomplete or has a reparse point: $($snapshot.Error)" }
        foreach ($recorded in $RecordedMarkers) {
            Add-GWScanNode -Budget $budget -RootPath $source
            $recordedPath=ConvertTo-GWFullPath -Path ([string](Get-GWProperty $recorded 'path' '')) -ParameterName 'recorded marker'
            $expectedHash=[string](Get-GWProperty $recorded 'sha256' '')
            if (-not (Test-GWPathWithin -Path $recordedPath -Root $source) -or $recordedPath -ieq (Join-Path $source $script:GeneratedWorkspaceMarkerName) -or
                [IO.Path]::GetFileName($recordedPath) -ine $script:GeneratedWorkspaceMarkerName -or $expectedHash -notmatch '^[a-fA-F0-9]{64}$') { throw 'RecordedMarkers must explicitly hash-bind bounded copied lifecycle evidence inside the source.' }
            $renamed=Join-Path ([IO.Path]::GetDirectoryName($recordedPath)) '.generated-workspace.evidence.json'
            $recordedTarget=$destination+$renamed.Substring($source.Length)
            $recordedReadPath=$recordedPath
            if(-not $snapshot.FileEntries.ContainsKey($recordedPath)){
                if(-not $verifiedResume){throw 'Recorded marker is missing from source.'}
                $recordedReadPath=$recordedTarget
            }
            Assert-GWNoReparsePath -Path $recordedReadPath -RequireLeaf
            if(([IO.FileInfo]::new($recordedReadPath)).Length -gt $script:GeneratedWorkspaceMarkerMaxBytes){throw 'Recorded marker exceeds its bound.'}
            $handle=[ResourceManager.GeneratedWorkspaces.NativeFileGuard]::OpenReadDelete($recordedReadPath,0)
            try {
                $actualHash=[BitConverter]::ToString([Convert]::FromBase64String([ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Sha256($handle))).Replace('-','')
                if($actualHash -ine $expectedHash){throw "Recorded marker hash changed: $recordedPath"}
                $input=[IO.FileStream]::new($handle,[IO.FileAccess]::Read)
                try { $input.Position=0; $reader=[IO.StreamReader]::new($input,[Text.Encoding]::UTF8); try{$state=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()} }finally{$input.Dispose()}
            }finally{$handle.Dispose()}
            $recordedOwner=ConvertTo-GWFullPath -Path ([string](Get-GWProperty $state 'workspacePath' '')) -ParameterName 'recorded workspace'
            $recordedRoot=ConvertTo-GWFullPath -Path ([string](Get-GWProperty $state 'rootPath' '')) -ParameterName 'recorded root'
            $null=Assert-GWManagedWorkspacePath -RootPath $recordedRoot -WorkspacePath $recordedOwner -RootConfig $null
            if ([int](Get-GWProperty $state 'schemaVersion' 0) -ne 1 -or [string](Get-GWProperty $state 'state' '') -notin @('running','completed')) { throw "Invalid recorded evidence schema/state: $recordedPath" }
            if ($recordedOwner -ieq [IO.Path]::GetDirectoryName($recordedPath) -or (Test-GWPathWithin -Path $recordedOwner -Root $source -AllowRoot)) {
                # Explicitly retire an ended legacy child registration into the
                # kept archive parent's single lifecycle; never relabel running work.
                if (-not [bool](Get-GWProperty $recorded 'retireCompletedOwner' $false) -or -not $originalState.keep -or
                    [string]$state.state -cne 'completed' -or $recordedOwner -ine [IO.Path]::GetDirectoryName($recordedPath) -or
                    -not (Test-GWPathWithin -Path $recordedRoot -Root $source -AllowRoot)) { throw "A live nested owner cannot be declared recorded evidence: $recordedPath" }
                if(-not $verifiedResume){$live=Read-GWStateRecord -RootPath $recordedRoot -WorkspacePath $recordedOwner;if(-not $live.Valid){throw "Invalid completed nested registration: $recordedPath"}}
                foreach($reference in @(ConvertTo-GWReferences -References @((Get-GWProperty $state 'references' @())))){
                    Add-GWScanNode -Budget $budget -RootPath $source
                    $retiredNestedReferences.Add($reference)
                }
            }
            if($snapshot.FileEntries.ContainsKey($renamed)){throw "Recorded evidence name collision: $renamed"}
            $recordedPaths.Add($recordedPath,($destination+$renamed.Substring($source.Length)))
            $recordedHashes.Add($recordedPath,$expectedHash)
        }
        foreach ($file in $snapshot.Files) {
            if ([IO.Path]::GetFileName($file) -ieq $script:GeneratedWorkspaceMarkerName -and $file -ine (Join-Path $source $script:GeneratedWorkspaceMarkerName) -and -not $recordedPaths.ContainsKey($file)) {
                throw "Archive does not relocate nested lifecycle registrations: $file"
            }
        }
        if($null -ne $resumeSnapshot -and -not $verifiedResume){
            $allowed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach($file in $snapshot.Files){if($file -ieq (Join-Path $source $script:GeneratedWorkspaceMarkerName)){continue};[void]$allowed.Add((ConvertTo-GWArchiveReference -Path $file -Source $source -Destination $destination -RecordedPaths $recordedPaths))}
            foreach($file in $resumeSnapshot.Files){if($file -ine (Join-Path $destination $script:GeneratedWorkspaceMarkerName) -and -not $allowed.Contains($file)){throw "Unexpected file in archive staging copy: $file"}}
            $allowedDirectories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach($directory in $snapshot.Directories){[void]$allowedDirectories.Add($destination+$directory.Substring($source.Length))}
            foreach($directory in $resumeSnapshot.Directories){if(-not $allowedDirectories.Contains($directory)){throw "Unexpected directory in archive staging copy: $directory"}}
        }
        $occupancy = Test-GWFilesUnoccupied -FilePaths @($snapshot.Files) -RootPath $source -Limits $limits
        if (-not $occupancy.Unoccupied) { throw "Archive source is occupied or inaccessible: $($occupancy.Path) ($($occupancy.Reason))" }

        $markerPlans = [Collections.Generic.List[object]]::new()
        foreach ($referenceRoot in $roots) {
            $catalog = Get-GWRootCatalog -RootPath $referenceRoot -Policy $Policy -MetadataOnly
            if (-not $catalog.Complete -or -not $catalog.GraphComplete) { throw ('Archive reference catalogue is incomplete: ' + ($catalog.Issues | ConvertTo-Json -Compress)) }
            foreach ($item in $catalog.Items) {
                Add-GWScanNode -Budget $budget -RootPath $referenceRoot
                if (-not $item.Registered -or $item.Path -ieq $source) { continue }
                $replacementReferences = @($item.References | ForEach-Object { ConvertTo-GWArchiveReference -Path $_ -Source $source -Destination $destination -RecordedPaths $recordedPaths })
                if (($replacementReferences -join "`n") -ceq ($item.References -join "`n")) { continue }
                if ($item.State -eq 'running') { throw "Archive evidence is still consumed by a running workspace: $($item.Path)" }
                $after = Copy-GWStateRecord -State $item.StateRecord
                $after.references = $replacementReferences
                $null = New-GWStateRecordPayload -State $after
                $markerPlans.Add([pscustomobject]@{Before=$item.StateRecord;After=$after})
            }
        }
        $documents = @(Get-GWArchiveDocumentPlan -DocumentRoots $DocumentRoots -Source $source -Destination $destination -Limits $archiveLimits -RecordedPaths $recordedPaths)
        $destinationCatalog = Get-GWRootCatalog -RootPath $destinationRoot -Policy $Policy
        if (-not $destinationCatalog.Complete -or -not $destinationCatalog.GraphComplete -or -not $destinationCatalog.AllSizesKnown) { throw 'Archive destination capacity cannot be completely accounted.' }
        $destinationLimits = Get-GWPolicyLimits -Policy $Policy -RootConfig $destinationConfig
        $stagedBytes=if($null -ne $resumeSnapshot -and -not $verifiedResume){[long]$resumeSnapshot.Bytes}elseif($verifiedResume){[long]$snapshot.Bytes}else{0L}
        if ($destinationCatalog.TotalBytes - $stagedBytes + $snapshot.Bytes -gt $destinationLimits.MaxBytes) { throw 'Archive copy would exceed the configured cold-root budget.' }
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($destinationRoot))
        if ([Math]::Max(0L,$snapshot.Bytes-$stagedBytes) -gt $drive.AvailableFreeSpace) { throw 'Insufficient free space for verified archive copy.' }
        if (-not $Apply) {
            return [pscustomobject]@{source=$source;destination=$destination;applied=$false;eligible=$true;bytes=$snapshot.Bytes;recordedMarkers=$recordedPaths.Count;markerUpdates=@($markerPlans | ForEach-Object {$_.After.workspacePath});documentUpdates=@($documents | ForEach-Object {$_.Path})}
        }

        $phase = 'copy'
        [void][IO.Directory]::CreateDirectory($destination)
        $copyCreated = $true
        $destinationState = Copy-GWStateRecord -State $originalState
        $destinationState.rootId = [string]$destinationConfig.id
        $destinationState.rootPath = $destinationRoot
        $destinationState.workspacePath = $destination
        $destinationState.references = @(ConvertTo-GWReferences -References @((@($originalState.references)+@($retiredNestedReferences.ToArray())) | ForEach-Object { ConvertTo-GWArchiveReference -Path $_ -Source $source -Destination $destination -RecordedPaths $recordedPaths }))
        $destinationState.state = if($verifiedResume){'completed'}else{'running'}
        $destinationState.completedUtc = if($verifiedResume){$originalState.completedUtc}else{$null}
        $destinationState.outcome = if($verifiedResume){$originalState.outcome}else{$null}
        if($verifiedResume){$destinationState.purpose=$originalState.purpose+' [verified archive; Keep until source cleanup succeeds]'}
        $destinationState.keep = $true
        Write-GWStateRecord -State $destinationState
        foreach ($directory in $snapshot.Directories) {
            Add-GWScanNode -Budget $budget -RootPath $source
            $target = $destination + $directory.Substring($source.Length)
            Assert-GWNoReparsePath -Path $target
            [void][IO.Directory]::CreateDirectory($target)
        }
        $marker = Join-Path $source $script:GeneratedWorkspaceMarkerName
        foreach ($file in $snapshot.Files) {
            Add-GWScanNode -Budget $budget -RootPath $source
            if ($file -ieq $marker) { continue }
            Assert-GWNoReparsePath -Path $file -RequireLeaf
            $target = ConvertTo-GWArchiveReference -Path $file -Source $source -Destination $destination -RecordedPaths $recordedPaths
            Assert-GWNoReparsePath -Path $target
            $handle = [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::OpenReadDelete($file,0)
            try {
                $expected = $snapshot.FileEntries[$file]
                if ([ResourceManager.GeneratedWorkspaces.NativeFileGuard]::Length($handle) -ne $expected.Item1 -or [ResourceManager.GeneratedWorkspaces.NativeFileGuard]::LastWriteTicks($handle) -ne $expected.Item2) { throw "Source changed before archive copy: $file" }
                $input = [IO.FileStream]::new($handle,[IO.FileAccess]::Read)
                try {
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $hash = [Convert]::ToBase64String($sha.ComputeHash($input)) } finally { $sha.Dispose() }
                    if($recordedHashes.ContainsKey($file) -and [BitConverter]::ToString([Convert]::FromBase64String($hash)).Replace('-','') -ine $recordedHashes[$file]) { throw "Recorded marker changed before copy: $file" }
                    $input.Position = 0
                    $reuse=$false
                    if($Resume -and [IO.File]::Exists($target)){
                        $old=[IO.File]::Open($target,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
                        try{$sha=[Security.Cryptography.SHA256]::Create();try{$reuse=$old.Length -eq $expected.Item1 -and [Convert]::ToBase64String($sha.ComputeHash($old)) -ceq $hash}finally{$sha.Dispose()}}finally{$old.Dispose()}
                    }
                    if($reuse){$reusedFiles++}else{
                        if($verifiedResume){throw "A protected verified archive differs from remaining source: $file"}
                        $mode=if($Resume){[IO.FileMode]::Create}else{[IO.FileMode]::CreateNew}
                        $output = [IO.File]::Open($target,$mode,[IO.FileAccess]::Write,[IO.FileShare]::None)
                        try { $input.CopyTo($output); $output.Flush([bool](Get-GWProperty $archiveLimits 'flushToDiskPerFile' $true)) } finally { $output.Dispose() }
                    }
                } finally { $input.Dispose() }
            } finally { $handle.Dispose() }
            $copied = [IO.File]::OpenRead($target)
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { if ($copied.Length -ne $expected.Item1 -or [Convert]::ToBase64String($sha.ComputeHash($copied)) -cne $hash) { throw "Archive length/hash mismatch: $target" } } finally { $sha.Dispose() }
            } finally { $copied.Dispose() }
            $hashes.Add($file,$hash)
        }
        $phase = 'references'
        # A completed, temporarily kept verified copy must survive any reference
        # write or partial source cleanup failure. Restore original Keep only at success.
        $destinationState.state = 'completed'
        $destinationState.completedUtc = $originalState.completedUtc
        $destinationState.outcome = $originalState.outcome
        $destinationState.purpose = $originalState.purpose + ' [verified archive; Keep until source cleanup succeeds]'
        Write-GWStateRecord -State $destinationState
        foreach ($plan in $markerPlans) {
            Add-GWScanNode -Budget $budget -RootPath $source
            $current = Read-GWStateRecord -RootPath $plan.Before.rootPath -WorkspacePath $plan.Before.workspacePath
            if (-not $current.Valid -or (ConvertTo-Json ($current.State | Select-Object * -ExcludeProperty updatedUtc) -Depth 12 -Compress) -cne (ConvertTo-Json ($plan.Before | Select-Object * -ExcludeProperty updatedUtc) -Depth 12 -Compress)) { throw "Lifecycle reference changed before archive update: $($plan.Before.workspacePath)" }
            Write-GWStateRecord -State $plan.After
            $updatedMarkers.Add([string]$plan.After.workspacePath)
        }
        foreach ($document in $documents) {
            Add-GWScanNode -Budget $budget -RootPath $source
            Write-GWArchiveDocument -Plan $document
            $updatedDocuments.Add([string]$document.Path)
        }
        $referencesCommitted = $true
        $phase = 'source-cleanup'
        $item = New-GWCatalogItem -RootPath $root -RootId ([string]$sourceConfig.id) -WorkspacePath $source
        $item.StateRecord = $originalState
        $item.Snapshot = $snapshot
        $verified = [pscustomobject]@{Path=$destination;RootPath=$destinationRoot;Hashes=$hashes;WorkspaceId=$originalState.workspaceId;RecordedPaths=$recordedPaths}
        $removed = Remove-GWWorkspaceSnapshot -Item $item -Limits $transferLimits -VerifiedArchive $verified
        if (-not $removed.Complete) { throw "Archive source cleanup incomplete ($($removed.RemovedBytes) bytes removed): $($removed.Error)" }
        $phase = 'complete'
        $destinationState.purpose = $originalState.purpose
        $destinationState.keep = $originalState.keep
        Write-GWStateRecord -State $destinationState
        return [pscustomobject]@{source=$source;destination=$destination;applied=$true;complete=$true;resumed=[bool]$Resume;resumedVerifiedCopy=$verifiedResume;reusedFiles=$reusedFiles;verifiedFiles=$hashes.Count;copiedBytes=$(if($verifiedResume){$resumeSnapshot.Bytes}else{$snapshot.Bytes});sourceRemovedBytes=$removed.RemovedBytes;updatedMarkers=@($updatedMarkers.ToArray());updatedDocuments=@($updatedDocuments.ToArray());recordedMarkers=@($recordedPaths.Keys|ForEach-Object{[pscustomobject]@{source=$_;destination=$recordedPaths[$_];sha256=$recordedHashes[$_]}});keep=$destinationState.keep;workspaceId=$destinationState.workspaceId}
    }
    catch {
        if ($copyCreated -and -not $verifiedResume -and $null -ne $destinationState -and $phase -eq 'copy') {
            # No references have moved to this unverified failed copy. Give it
            # a terminal, reclaimable lifecycle instead of leaking a running task.
            $destinationState.state='completed'; $destinationState.outcome='failed'; $destinationState.completedUtc=[DateTime]::UtcNow.ToString('o'); $destinationState.keep=$false
            $destinationState.purpose='Failed archive copy; original source retained: '+$source
            try { Write-GWStateRecord -State $destinationState } catch { }
        }
        $failure=[InvalidOperationException]::new("Archive failed during ${phase}: $($_.Exception.Message)",$_.Exception)
        $failure.Data['archive']=[pscustomobject]@{source=$source;destination=$destination;phase=$phase;sourceExists=[IO.Directory]::Exists($source);copyCreated=$copyCreated;referencesCommitted=$referencesCommitted;updatedMarkers=@($updatedMarkers.ToArray());updatedDocuments=@($updatedDocuments.ToArray());verifiedFiles=$hashes.Count;sourceCleanup=$removed}
        throw $failure
    }
    finally { Exit-GWRootLocks -Streams $locks }
}
