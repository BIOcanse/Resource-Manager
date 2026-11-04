# Machine-local remembered locations. Directory locks are always acquired after
# the state lock; callers hold both until their marker/retention work is done.
function Test-GWDiskFullException {
    param([AllowNull()][Exception]$Exception,[ValidateRange(1,512)][int]$MaxExceptions=512)
    $pending=[Collections.Generic.Stack[Exception]]::new()
    $seen=[Collections.Generic.HashSet[Exception]]::new()
    if($null-ne$Exception){$pending.Push($Exception)}
    while($pending.Count-gt0){
        $current=$pending.Pop()
        if(-not$seen.Add($current)){continue}
        if($seen.Count-gt$MaxExceptions){throw 'Exception graph exceeded the batch bound; refusing further storage operations.'}
        $code=$current.HResult-band 0xffff
        if($code-eq112-or$code-eq39){return $true}
        if($current-is[AggregateException]){foreach($inner in $current.InnerExceptions){if($null-ne$inner){$pending.Push($inner)}}}
        if($null-ne$current.InnerException){$pending.Push($current.InnerException)}
    }
    return $false
}
function Ensure-GWLocationsInitialized {
    param([object]$Policy)
    $path=Get-GWLocationsStatePath -Policy $Policy
    try{$null=[IO.File]::GetAttributes($path);return}
    catch [IO.FileNotFoundException]{}
    catch [IO.DirectoryNotFoundException]{}
    $null=Initialize-GeneratedWorkspaceLocations -Policy $Policy
}

function Assert-GWLocationVolumeAvailable {
    param([Parameter(Mandatory)][string]$Path)
    $volume=[IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))
    try{
        if($volume-match'^[A-Za-z]:[\\/]$'-and-not[IO.DriveInfo]::new($volume).IsReady){throw 'The volume is not ready.'}
        # UNC roots also need a successful share-root query. A missing child
        # alone cannot distinguish a removed directory from an offline owner.
        $attributes=[IO.File]::GetAttributes($volume)
        if(($attributes-band[IO.FileAttributes]::Directory)-eq0){throw 'The volume/share root is not accessible as a directory.'}
    }catch{throw [IO.IOException]::new("Location volume/share is unavailable or uncertain; its ownership remains remembered: $volume",$_.Exception)}
}

function Test-GWLocationDirectory {
    param([string]$Path)
    try{$attributes=[IO.File]::GetAttributes($Path)}
    catch [IO.FileNotFoundException]{Assert-GWLocationVolumeAvailable -Path $Path;return $false}
    catch [IO.DirectoryNotFoundException]{Assert-GWLocationVolumeAvailable -Path $Path;return $false}
    if(($attributes-band[IO.FileAttributes]::Directory)-eq0){throw "Known root is not a directory: $Path"}
    return $true
}

function Get-GWRegisteredWorktrees {
    param([object]$Policy,[string]$RepositoryRoot=(Get-GWRepositoryRoot))
    $repository=ConvertTo-GWFullPath -Path $RepositoryRoot -ParameterName 'worktree repository'
    $savedExitCode=Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    try{
        $rows=@(& git -C $repository -c core.quotepath=false worktree list --porcelain 2>&1)
        if($LASTEXITCODE-ne0){throw "Cannot read registered Git worktrees: $($rows -join ' ')"}
    }finally{
        if($null-ne$savedExitCode){$global:LASTEXITCODE=$savedExitCode.Value}else{Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue}
    }
    $budget=New-GWScanBudget -Limits (Get-GWPolicyLimits -Policy $Policy -RootConfig $null)
    foreach($row in $rows){
        Add-GWScanNode -Budget $budget -RootPath $repository
        if(([string]$row).StartsWith('worktree ')){
            ConvertTo-GWFullPath -Path ([string]$row).Substring(9) -ParameterName 'registered Git worktree'
        }
    }
}

function Get-GWGitRegistrationAuthority {
    param([string]$RepositoryRoot=(Get-GWRepositoryRoot))
    $repository=ConvertTo-GWFullPath -Path $RepositoryRoot -ParameterName 'Git registration authority'
    $savedExitCode=Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    try{
        $rows=@(& git -C $repository rev-parse --path-format=absolute --git-common-dir 2>&1)
        if($LASTEXITCODE-ne0-or$rows.Count-ne1){throw "Cannot resolve stable Git registration authority: $($rows -join ' ')"}
        ConvertTo-GWFullPath -Path ([string]$rows[0]) -ParameterName 'Git common directory'
    }finally{
        if($null-ne$savedExitCode){$global:LASTEXITCODE=$savedExitCode.Value}else{Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue}
    }
}
function Get-GWLocationsStatePath {
    param([Parameter(Mandatory)][object]$Policy)
    $raw=[string](Get-GWProperty $Policy 'statePath' '')
    if([string]::IsNullOrWhiteSpace($raw)){throw 'Anonymous retention policies require an explicit statePath.'}
    $resolved=Resolve-GWConfiguredPath -Path $raw
    if(-not $resolved){throw 'The locations statePath has unconfigured machine roots.'}
    if($resolved-match'\{[^}]+\}'){throw 'The locations statePath contains an unresolved machine-root token.'}
    ConvertTo-GWFullPath -Path $resolved -ParameterName 'locations statePath'
}

function Get-GWKindConfig {
    param([object]$Policy,[string[]]$RootIds,[string]$Path,[object[]]$Definitions=@())
    $configs=[Collections.Generic.List[object]]::new()
    $recordedIds=[Collections.Generic.List[string]]::new()
    foreach($id in $RootIds){
        $found=@(@(Get-GWProperty $Policy 'roots' @())|Where-Object {[string](Get-GWProperty $_ 'id' '') -ieq $id})
        if($found.Count-eq0){$found=@($Definitions|Where-Object {[string](Get-GWProperty $_ 'id' '') -ieq $id});$recordedIds.Add($id)}
        if($found.Count-ne1){throw "Location kind must have exactly one current or recorded definition: $id"}
        $configs.Add($found[0])
    }
    if($configs.Count-eq0){throw 'A managed location needs at least one root kind.'}
    $ids=@($RootIds|Sort-Object -Unique)
    $result=[ordered]@{id=$ids[0];RootId=$ids[0];RootIds=$ids;path=$Path;excludedNames=@();archiveOnly=$false;registrationKind='marker';Kinds=@();RecordedKindIds=@($recordedIds.ToArray())}
    $excluded=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($config in $configs){
        foreach($name in @(Get-GWProperty $config 'excludedNames' @())){[void]$excluded.Add([string]$name)}
        if([bool](Get-GWProperty $config 'archiveOnly' $false)){$result.archiveOnly=$true}
        if([string](Get-GWProperty $config 'registrationKind' 'marker') -eq 'git-worktree'){$result.registrationKind='git-worktree'}
        $definition=[ordered]@{id=[string](Get-GWProperty $config 'id' '');excludedNames=@(Get-GWProperty $config 'excludedNames' @());archiveOnly=[bool](Get-GWProperty $config 'archiveOnly' $false);registrationKind=[string](Get-GWProperty $config 'registrationKind' 'marker')}
        $limits=Get-GWPolicyLimits -Policy $Policy -RootConfig $config
        foreach($name in @('maxRecentCompleted','maxBytes','maxNodes','maxMilliseconds')){
            $value=[long]$limits.$name
            $definition[$name]=$value
            if(-not $result.Contains($name)-or$value-lt[long]$result[$name]){$result[$name]=$value}
        }
        $result.Kinds+=@([pscustomobject]$definition)
    }
    $result.excludedNames=@($excluded|Sort-Object)
    [pscustomobject]$result
}

function Get-GeneratedWorkspacePolicyRoots {
    [CmdletBinding()]param([Parameter(Mandatory)][object]$Policy)
    $paths=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($config in @(Get-GWProperty $Policy 'roots' @())){
        $id=[string](Get-GWProperty $config 'id' '')
        if([string]::IsNullOrWhiteSpace($id)-or-not$ids.Add($id)){throw "Missing or duplicate retention kind ID: $id"}
        $path=Resolve-GWConfiguredPath -Path ([string](Get-GWProperty $config 'path' ''))
        if(-not$path){continue}
        $path=ConvertTo-GWFullPath -Path $path -ParameterName 'policy root'
        if(-not$paths.ContainsKey($path)){$paths.Add($path,[Collections.Generic.List[string]]::new())}
        $paths[$path].Add($id)
    }
    foreach($path in @($paths.Keys|Sort-Object)){Get-GWKindConfig -Policy $Policy -RootIds @($paths[$path].ToArray()) -Path $path}
}

function Enter-GWLocationsLock {
    param([object]$Policy)
    $path=Get-GWLocationsStatePath -Policy $Policy
    Assert-GWNoReparsePath -Path $path
    $parent=[IO.Path]::GetDirectoryName($path)
    [void][IO.Directory]::CreateDirectory($parent)
    Assert-GWNoReparsePath -Path $parent -RequireLeaf
    $lockPath=$path+'.lock'
    Assert-GWNoReparsePath -Path $lockPath
    if([IO.Directory]::Exists($lockPath)){throw 'Locations lock is not a regular file.'}
    $deadline=[DateTime]::UtcNow.AddSeconds($script:GeneratedWorkspaceLockWaitSeconds)
    while($true){
        try{return [IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
        catch{if(Test-GWDiskFullException -Exception $_.Exception){throw};if([DateTime]::UtcNow-ge$deadline){throw "Locations state lock unavailable: $lockPath. $($_.Exception.Message)"};Start-Sleep -Milliseconds 250}
    }
}

function Get-GWLocationsLimits {
    param([object]$Policy)
    # State describes all physical locations, so one payload root's deliberately
    # reduced scan budget must never make the shared inventory unreadable.
    $configured=Get-GWProperty $Policy 'stateLimits' $null
    $maxLocations=[int](Get-GWProperty $configured 'maxLocations' 4096)
    $maxMilliseconds=[int](Get-GWProperty $configured 'maxMilliseconds' 300000)
    if($maxLocations-lt1-or$maxLocations-gt4096-or$maxMilliseconds-lt1-or$maxMilliseconds-gt3600000){throw 'Locations stateLimits are outside the existing batch bounds.'}
    [pscustomobject]@{MaxLocations=$maxLocations;MaxMilliseconds=$maxMilliseconds}
}

function Read-GWLocationsData {
    param([object]$Policy)
    $limits=Get-GWLocationsLimits -Policy $Policy
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $path=Get-GWLocationsStatePath -Policy $Policy
    Assert-GWNoReparsePath -Path $path
    try{$attributes=[IO.File]::GetAttributes($path)}
    catch [IO.FileNotFoundException]{return [pscustomobject]@{schemaVersion=1;locations=@()}}
    catch [IO.DirectoryNotFoundException]{return [pscustomobject]@{schemaVersion=1;locations=@()}}
    if(($attributes-band[IO.FileAttributes]::Directory)-ne0){throw 'Locations state is not a regular file.'}
    $stream=$null
    try{
        $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::Read-bor[IO.FileShare]::Delete))
        if($stream.Length-gt 8*$script:GeneratedWorkspaceMarkerMaxBytes){throw 'Locations state exceeds the batch JSON bound.'}
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$true)
        try{$data=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    }finally{if($stream){$stream.Dispose()}}
    if([int](Get-GWProperty $data 'schemaVersion' 0)-ne1){throw 'Unsupported locations state schema.'}
    $rows=@(Get-GWProperty $data 'locations' @())
    if($rows.Count-gt$limits.MaxLocations){throw 'Locations state exceeds its independent location-count bound.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($row in $rows){
        if($timer.ElapsedMilliseconds-gt$limits.MaxMilliseconds){throw 'Locations state read exceeded its independent time bound.'}
        $full=ConvertTo-GWFullPath -Path ([string](Get-GWProperty $row 'Path' '')) -ParameterName 'known location'
        if(-not$seen.Add($full)){throw "Duplicate remembered physical location: $full"}
        $rootIds=@(Get-GWProperty $row 'RootIds' @())
        if($rootIds.Count-gt$limits.MaxLocations){throw 'Location kind set exceeds the batch group bound.'}
        $null=Get-GWKindConfig -Policy $Policy -RootIds $rootIds -Path $full -Definitions @(Get-GWProperty $row 'Kinds' @())
        if($rootIds.Count-eq0){throw 'Known location has no root kinds.'}
    }
    if($timer.ElapsedMilliseconds-gt$limits.MaxMilliseconds){throw 'Locations state read exceeded its independent time bound.'}
    return $data
}

function Write-GWLocationsData {
    param([object]$Policy,[object]$Data)
    $limits=Get-GWLocationsLimits -Policy $Policy
    $timer=[Diagnostics.Stopwatch]::StartNew()
    if(@($Data.locations).Count-gt$limits.MaxLocations){throw 'Locations state exceeds its independent location-count bound.'}
    $path=Get-GWLocationsStatePath -Policy $Policy
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Data -Depth 12)+[Environment]::NewLine)
    if($bytes.Length-gt 8*$script:GeneratedWorkspaceMarkerMaxBytes){throw 'Locations state exceeds the batch JSON bound.'}
    Assert-GWNoReparsePath -Path $path
    $temporary=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $stream=$null;$diskFull=$false
    try{
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $stream.Write($bytes,0,$bytes.Length);$stream.Flush($true);$stream.Dispose();$stream=$null
        if($timer.ElapsedMilliseconds-gt$limits.MaxMilliseconds){throw 'Locations state write exceeded its independent time bound before commit.'}
        if([IO.File]::Exists($path)){[IO.File]::Replace($temporary,$path,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$path)}
    }catch{$diskFull=Test-GWDiskFullException -Exception $_.Exception;throw}finally{
        if($stream){$stream.Dispose()}
        if(-not$diskFull-and[IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
    }
}

function Get-GWLocationObservation {
    param([object]$Policy,[object]$Config)
    $valid=0;$unknown=0;$unregistered=0;$gitCount=0;$complete=$true;$reason=$null;$root=$Config.Path
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $budget=New-GWScanBudget -Limits (Get-GWPolicyLimits -Policy $Policy -RootConfig $Config)
    try{
        if([string](Get-GWProperty $Config 'registrationKind' 'marker') -eq 'git-worktree'){
            $repository=[string](Get-GWProperty $Config 'RepositoryRoot' (Get-GWRepositoryRoot))
            $gitCount=@(Get-GWRegisteredWorktrees -Policy $Policy -RepositoryRoot $repository|Where-Object {[IO.Path]::GetDirectoryName($_)-ieq$root}).Count
        }
        Assert-GWNoReparsePath -Path $root
        try{$attrs=[IO.File]::GetAttributes($root)}
        catch [IO.FileNotFoundException]{Assert-GWLocationVolumeAvailable -Path $root;return [pscustomobject]@{RegisteredWorkspaceCount=0;RegisteredWorktreeCount=$gitCount;UnknownMarkerCount=0;UnregisteredDirectoryCount=0;ScanComplete=$true;RootIds=@();ObservationReason='Location directory is absent on an available volume/share.'}}
        catch [IO.DirectoryNotFoundException]{Assert-GWLocationVolumeAvailable -Path $root;return [pscustomobject]@{RegisteredWorkspaceCount=0;RegisteredWorktreeCount=$gitCount;UnknownMarkerCount=0;UnregisteredDirectoryCount=0;ScanComplete=$true;RootIds=@();ObservationReason='Location directory is absent on an available volume/share.'}}
        if(($attrs-band[IO.FileAttributes]::Directory)-eq0){throw 'Known root is not a directory.'}
        foreach($entry in [IO.DirectoryInfo]::new($root).EnumerateFileSystemInfos()){
            Add-GWScanNode -Budget $budget -RootPath $root
            if(($entry.Attributes-band[IO.FileAttributes]::Directory)-eq0){continue}
            if(($entry.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){$unknown++;continue}
            # Even excluded children can contain an owner. Never call a root
            # empty by skipping excluded marker-bearing directories.
            $read=Read-GWStateRecord -RootPath $root -WorkspacePath $entry.FullName
            if($read.Valid){
                $id=[string]$read.State.rootId
                if(@($Config.RootIds)-inotcontains$id){$unknown++;continue}
                $valid++;[void]$ids.Add($id)
            }elseif($read.Exists){$unknown++}else{
                $marker=Join-Path $entry.FullName $script:GeneratedWorkspaceMarkerName
                try{$null=[IO.File]::GetAttributes($marker);$unknown++}
                catch [IO.FileNotFoundException]{$unregistered++}
                catch [IO.DirectoryNotFoundException]{$unregistered++}
                catch{$unknown++}
            }
        }
    }catch{$complete=$false;$reason=$_.Exception.Message}
    [pscustomobject]@{RegisteredWorkspaceCount=$valid;RegisteredWorktreeCount=$gitCount;UnknownMarkerCount=$unknown;UnregisteredDirectoryCount=$unregistered;ScanComplete=$complete;RootIds=@($ids);ObservationReason=$reason}
}

function Get-GeneratedWorkspaceLocations {
    [CmdletBinding()]param([Parameter(Mandatory)][object]$Policy,[string]$RootId,[switch]$MetadataOnly)
    Ensure-GWLocationsInitialized -Policy $Policy
    $data=Read-GWLocationsData -Policy $Policy
    $defaults=@(Get-GeneratedWorkspacePolicyRoots -Policy $Policy)
    foreach($row in @($data.locations)){
        $config=Get-GWLocationConfig -Policy $Policy -RootPath $row.Path -LocationsData $data -PolicyRoots $defaults
        if($RootId-and@($config.RootIds)-inotcontains$RootId){continue}
        if($MetadataOnly){
            $observation=[pscustomobject]@{RegisteredWorkspaceCount=$null;RegisteredWorktreeCount=$null;UnknownMarkerCount=$null;UnregisteredDirectoryCount=$null;ScanComplete=$null;ObservationReason='Metadata only; registration census was not requested.'}
        }else{$observation=Get-GWLocationObservation -Policy $Policy -Config $config}
        foreach($property in $observation.PSObject.Properties){if($property.Name-ne'RootIds'){$config|Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force}}
        $config|Add-Member -NotePropertyName Reason -NotePropertyValue ([string](Get-GWProperty $row 'Reason' 'registered')) -Force
        $config
    }
}

function Merge-GWLocationData {
    # Caller owns state lock then root locks. Register before marker writes so
    # a failed marker write can never leave an owner outside the remembered set.
    param([object]$Policy,[object]$Data,[string]$RootPath,[string[]]$RootIds,[string]$Reason='registration',[string]$RepositoryRoot)
    $root=ConvertTo-GWFullPath -Path $RootPath -ParameterName 'location'
    $existing=@($data.locations|Where-Object {$_.Path-ieq$root})
    $definitions=@()
    if($existing.Count){$definitions=@(Get-GWProperty $existing[0] 'Kinds' @());$RootIds=@(@($existing[0].RootIds)+@($RootIds)|Sort-Object -Unique)}
    $config=Get-GWKindConfig -Policy $Policy -RootIds $RootIds -Path $root -Definitions $definitions
    if($config.registrationKind-eq'git-worktree'){
        if(-not$RepositoryRoot){$RepositoryRoot=[string](Get-GWProperty $(if($existing.Count){$existing[0]}else{$null}) 'RepositoryRoot' (Get-GWRepositoryRoot))}
        $RepositoryRoot=Get-GWGitRegistrationAuthority -RepositoryRoot $RepositoryRoot
    }
    if($existing.Count){
        $before=ConvertTo-Json -InputObject $existing[0] -Depth 12 -Compress
        $existing[0].RootIds=@($config.RootIds)
        $existing[0]|Add-Member -NotePropertyName Kinds -NotePropertyValue @($config.Kinds) -Force
        if($RepositoryRoot){$existing[0]|Add-Member -NotePropertyName RepositoryRoot -NotePropertyValue $RepositoryRoot -Force}
        if($before -ceq (ConvertTo-Json -InputObject $existing[0] -Depth 12 -Compress)){return $false}
    }else{$data.locations=@($data.locations)+@([pscustomobject]@{Path=$root;RootIds=@($config.RootIds);Kinds=@($config.Kinds);Reason=$Reason;RepositoryRoot=$(if($RepositoryRoot){$RepositoryRoot}else{Get-GWRepositoryRoot})})}
    return $true
}

function Register-GWLocation {
    param([object]$Policy,[string]$RootPath,[string[]]$RootIds,[string]$Reason='registration',[string]$RepositoryRoot)
    $data=Read-GWLocationsData -Policy $Policy
    if(Merge-GWLocationData -Policy $Policy -Data $data -RootPath $RootPath -RootIds $RootIds -Reason $Reason -RepositoryRoot $RepositoryRoot){Write-GWLocationsData -Policy $Policy -Data $data}
}

function Initialize-GeneratedWorkspaceLocations {
    [CmdletBinding()]param([Parameter(Mandatory)][object]$Policy,[string]$RepositoryRoot=(Get-GWRepositoryRoot))
    $stateLock=Enter-GWLocationsLock -Policy $Policy
    $rootLocks=$null
    try{
        $defaults=@(Get-GeneratedWorkspacePolicyRoots -Policy $Policy)
        $existing=@($defaults|Where-Object {[IO.Directory]::Exists($_.Path)}|ForEach-Object {$_.Path})
        if($existing.Count){$rootLocks=Enter-GWDirectoryLocks -RootPaths $existing -Policy $Policy}
        # Build the entire import before one atomic write. A failed first scan
        # must not leave a partial file that suppresses the next first-boot scan.
        $data=Read-GWLocationsData -Policy $Policy
        foreach($config in $defaults){
            $authority=$RepositoryRoot
            if($config.registrationKind-eq'git-worktree'){$authority=Get-GWGitRegistrationAuthority -RepositoryRoot $RepositoryRoot}
            $config|Add-Member -NotePropertyName RepositoryRoot -NotePropertyValue $authority -Force
            $observation=Get-GWLocationObservation -Policy $Policy -Config $config
            if($observation.RegisteredWorkspaceCount-gt0-or$observation.RegisteredWorktreeCount-gt0){$null=Merge-GWLocationData -Policy $Policy -Data $data -RootPath $config.Path -RootIds @($config.RootIds) -Reason 'imported-existing-registrations' -RepositoryRoot $authority}
            if(-not$observation.ScanComplete){throw "Location initialization could not completely inspect $($config.Path): $($observation.ObservationReason)"}
        }
        Write-GWLocationsData -Policy $Policy -Data $data
    }finally{Exit-GWDirectoryLocks -Streams $rootLocks;$stateLock.Dispose()}
    Get-GeneratedWorkspaceLocations -Policy $Policy
}

function Get-GWLocationConfig {
    param([object]$Policy,[string]$RootPath,[switch]$ConfiguredOnly,[object]$LocationsData,[AllowEmptyCollection()][object[]]$PolicyRoots)
    $root=ConvertTo-GWFullPath -Path $RootPath -ParameterName 'RootPath'
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $definitions=@();$repository=Get-GWRepositoryRoot
    if(-not$ConfiguredOnly){
        if(-not$PSBoundParameters.ContainsKey('LocationsData')){$LocationsData=Read-GWLocationsData -Policy $Policy}
        foreach($row in @($LocationsData.locations)){if($row.Path-ieq$root){foreach($id in @($row.RootIds)){[void]$ids.Add([string]$id)};$definitions=@(Get-GWProperty $row 'Kinds' @());$repository=[string](Get-GWProperty $row 'RepositoryRoot' $repository)}}
    }
    if(-not$PSBoundParameters.ContainsKey('PolicyRoots')){$PolicyRoots=@(Get-GeneratedWorkspacePolicyRoots -Policy $Policy)}
    foreach($config in @($PolicyRoots)){if($config.Path-ieq$root){foreach($id in @($config.RootIds)){[void]$ids.Add([string]$id)}}}
    if($ids.Count-eq0){throw "RootPath is not a remembered or configured managed location: $root"}
    $result=Get-GWKindConfig -Policy $Policy -RootIds @($ids) -Path $root -Definitions $definitions
    $result|Add-Member -NotePropertyName RepositoryRoot -NotePropertyValue $repository -Force
    $result
}

function Get-GWRegistrationRootId {
    param([object]$Policy,[string]$RootPath)
    $configured=@(Get-GeneratedWorkspacePolicyRoots -Policy $Policy|Where-Object {$_.Path-ieq$RootPath})
    if($configured.Count){return [string]$configured[0].RootId}
    return [string](Get-GWLocationConfig -Policy $Policy -RootPath $RootPath).RootId
}

function Resolve-GeneratedWorkspaceRoot {
    [CmdletBinding()]param([Parameter(Mandatory)][object]$Policy,[Parameter(Mandatory)][string]$WorkspacePath,[string]$RootId)
    Ensure-GWLocationsInitialized -Policy $Policy
    $workspace=ConvertTo-GWFullPath -Path $WorkspacePath -ParameterName 'WorkspacePath'
    $parent=[IO.Path]::GetDirectoryName($workspace)
    $read=Read-GWStateRecord -RootPath $parent -WorkspacePath $workspace
    if(-not$read.Valid){throw "Existing operations require a valid marker: $($read.Reason)"}
    $markerId=[string]$read.State.rootId
    $rows=@((Read-GWLocationsData -Policy $Policy).locations|Where-Object {$_.Path-ieq$parent-and@($_.RootIds)-icontains$markerId})
    if($rows.Count-ne1){throw 'Workspace marker location/kind is not in the remembered locations list.'}
    Assert-GWNoReparsePath -Path $workspace -RequireLeaf
    $config=Get-GWLocationConfig -Policy $Policy -RootPath $parent
    if($RootId-and@($config.RootIds)-inotcontains$RootId){throw 'Requested root kind is not in the location kind set.'}
    if($config.registrationKind-eq'git-worktree'){
        $members=@(Get-GWRegisteredWorktrees -Policy $Policy -RepositoryRoot $config.RepositoryRoot)
        if($members-icontains$workspace){throw 'Registered Git source worktrees cannot use a generated-workspace lifecycle.'}
    }
    $config
}

function Enter-GWRootLocks {
    param([Parameter(Mandatory)][string[]]$RootPaths,[Parameter(Mandatory)][object]$Policy)
    $stateLock=Enter-GWLocationsLock -Policy $Policy
    $directoryLocks=$null
    try{
        $paths=@(Get-GWReferenceRootPaths -RootPath $RootPaths[0] -ReferenceRoots $RootPaths -Policy $Policy)
        $directoryLocks=Enter-GWDirectoryLocks -RootPaths $paths -Policy $Policy
        [pscustomobject]@{StateStream=$stateLock;Streams=$directoryLocks;RootPaths=$paths}
    }catch{Exit-GWDirectoryLocks -Streams $directoryLocks;$stateLock.Dispose();throw}
}

function Get-GWRootLockPath {
    param([Parameter(Mandatory)][object]$Policy,[Parameter(Mandatory)][string]$RootPath)
    $root=(ConvertTo-GWFullPath -Path $RootPath -ParameterName 'lock root').ToUpperInvariant()
    $state=Get-GWLocationsStatePath -Policy $Policy
    $directory=Join-Path ([IO.Path]::GetDirectoryName($state)) '.generated-workspace-root-locks'
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    Join-Path $directory ($hash+'.lock')
}

function Exit-GWRootLocks {
    param([AllowNull()][object]$Streams)
    if($null-eq$Streams){return}
    Exit-GWDirectoryLocks -Streams $Streams.Streams
    $Streams.StateStream.Dispose()
}

function Remove-GWEmptyLocation {
    param([object]$Policy,[string]$RootPath)
    $config=Get-GWLocationConfig -Policy $Policy -RootPath $RootPath
    $observation=Get-GWLocationObservation -Policy $Policy -Config $config
    if(-not$observation.ScanComplete-or$observation.RegisteredWorkspaceCount-gt0-or$observation.RegisteredWorktreeCount-gt0-or$observation.UnknownMarkerCount-gt0-or$observation.UnregisteredDirectoryCount-gt0){return $false}
    $data=Read-GWLocationsData -Policy $Policy
    $rows=@($data.locations|Where-Object {$_.Path-ine$RootPath})
    if($rows.Count-eq@($data.locations).Count){return $false}
    $data.locations=$rows;Write-GWLocationsData -Policy $Policy -Data $data
    return $true
}

function Register-GeneratedWorkspaceWorktreeLocation {
    [CmdletBinding()]param([Parameter(Mandatory)][object]$Policy,[Parameter(Mandatory)][string]$RootPath,[string]$RootId,[string]$RepositoryRoot=(Get-GWRepositoryRoot))
    Ensure-GWLocationsInitialized -Policy $Policy
    $root=ConvertTo-GWFullPath -Path $RootPath -ParameterName 'Git worktree parent'
    $config=$null
    try{$config=Get-GWLocationConfig -Policy $Policy -RootPath $root}catch{
        if(-not$RootId){throw}
    }
    if($RootId){
        # Explicit source parents retain the existing kind identity. Only a
        # current Git-kind definition can authorize a previously unknown path.
        $requested=Get-GWKindConfig -Policy $Policy -RootIds @($RootId) -Path $root
        if($requested.registrationKind-ne'git-worktree'){throw 'Source location RootId must be a current Git-worktree kind.'}
        if($null-ne$config){
            if(@($config.Kinds|Where-Object {$_.registrationKind-ne'git-worktree'}).Count){throw 'Cannot merge a source parent with a generated-output location kind.'}
            $config=Get-GWKindConfig -Policy $Policy -RootIds @(@($config.RootIds)+@($RootId)|Sort-Object -Unique) -Path $root -Definitions @($config.Kinds)
        }else{$config=$requested}
    }
    if($null-eq$config){throw 'Source parent needs a configured Git kind or an explicit RootId.'}
    if($config.registrationKind-ne'git-worktree'){throw 'Location is not configured for Git worktree registrations.'}
    $RepositoryRoot=Get-GWGitRegistrationAuthority -RepositoryRoot $RepositoryRoot
    $locks=Enter-GWRootLocks -Policy $Policy -RootPaths @($config.Path)
    try{
        $members=@(Get-GWRegisteredWorktrees -Policy $Policy -RepositoryRoot $RepositoryRoot|Where-Object {[IO.Path]::GetDirectoryName($_)-ieq$config.Path})
        if($members.Count-eq0){throw 'No real registered Git worktree belongs to this location.'}
        Register-GWLocation -Policy $Policy -RootPath $config.Path -RootIds @($config.RootIds) -Reason 'registered-git-worktrees' -RepositoryRoot $RepositoryRoot
    }finally{Exit-GWRootLocks -Streams $locks}
    Get-GeneratedWorkspaceLocations -Policy $Policy|Where-Object {$_.Path-ieq$config.Path}
}
