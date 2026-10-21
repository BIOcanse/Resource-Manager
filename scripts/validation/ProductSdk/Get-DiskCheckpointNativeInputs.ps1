[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot,[switch]$Producer,[object]$CandidateContext)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$task=[IO.Path]::GetFullPath($TaskRoot).TrimEnd('\')
$profilePath=Join-Path $task 'disk-inputs.json';$contextPath=Join-Path $task 'disk-native-context.json'
if(-not [IO.File]::Exists($profilePath)){
    if($Producer -or [IO.File]::Exists($contextPath)){throw 'Disk qualification requires disk-inputs.json.'}
    return
}
function Require([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Full([string]$Path){Require ([IO.Path]::IsPathFullyQualified($Path)) "Absolute Disk input required: $Path";[IO.Path]::GetFullPath($Path).TrimEnd('\')}
function Under([string]$Path,[string]$Root){$p=Full $Path;$r=Full $Root;$p.StartsWith($r+'\',[StringComparison]::OrdinalIgnoreCase)}
$checked=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function No-Reparse([string]$Path){
    $at=Full $Path
    for($i=0;$i-lt64;$i++){
        if(-not $checked.Add($at)){return}
        if([IO.File]::Exists($at)-or[IO.Directory]::Exists($at)){Require (((Get-Item -LiteralPath $at -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)-eq0) "Reparse Disk path: $at"}
        $parent=[IO.Directory]::GetParent($at);if($null-eq$parent){return};$at=$parent.FullName
    };throw 'Disk ancestor bound exceeded.'
}
function File-Row([string]$Path){$p=Full $Path;No-Reparse $p;$f=Get-Item -LiteralPath $p;Require (-not$f.PSIsContainer-and$f.Length-le1GB) 'Disk file bound exceeded.';[pscustomobject]@{path=$p;length=$f.Length;sha256=(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}}
function Check-Row($Expected){$now=File-Row ([string]$Expected.path);Require ($Expected.sha256-match'^[0-9a-fA-F]{64}$'-and$now.sha256-ieq$Expected.sha256) "Disk input hash differs: $($Expected.path)";if($Expected.PSObject.Properties['length']){Require ($now.length-eq$Expected.length) 'Disk input length differs.'};$now}
function Json([string]$Path){Require ((Get-Item -LiteralPath $Path).Length-le16MB) 'Disk JSON exceeds16MiB.';Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json}
$profile=Json $profilePath;Require ($profile.schema-ceq'rm/disk-checkpoint-inputs/v1') 'Unknown Disk qualification profile.'
foreach($name in @('declaration','sdkRoot','bindingRoot','toolsRoot','builderPath','nativeOwner','worksetRoot','projectRoot')){[void](Full ([string]$profile.$name))}
$repo='D:\Code\Resource-Manager-Development';$workset='F:\code\Resource-Manager-Development\generated'
Require ((Full $profile.projectRoot)-ieq$repo-and(Full $profile.worksetRoot)-ieq$workset-and[IO.Path]::GetDirectoryName((Full $profile.nativeOwner))-ieq$workset) 'Disk project/workset/owner differs.'
$ownerMarker=Join-Path $profile.nativeOwner '.generated-workspace.json';$owner=Json $ownerMarker
Require ($owner.rootId-ceq'rm-disk-native-artifacts'-and(Full $owner.rootPath)-ieq$workset-and(Full $owner.workspacePath)-ieq(Full $profile.nativeOwner)-and$owner.state-cin@('running','completed')-and$owner.references-icontains$task) 'Disk artifact owner is not registered for this task.'
if($Producer){Require ($owner.state-ceq'running') 'Do not reopen completed Disk Native owner.'}
Require (@($profile.formalInputs).Count-gt0-and@($profile.formalInputs).Count-le4096) 'Disk formal input count bound.'
$rows=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Row($Row){$key=Full ([string]$Row.path);if($rows.ContainsKey($key)){Require ($rows[$key].sha256-ieq$Row.sha256-and$rows[$key].length-eq$Row.length) 'Conflicting Disk input rows.'}else{$rows.Add($key,$Row)}}
Add-Row (File-Row $profilePath);Add-Row (File-Row $ownerMarker);Add-Row (File-Row 'D:\Code\CommonAssets\Tools\Zig\zig-0.16.0\zig.exe')
foreach($inputRow in $profile.formalInputs){Require ($null-ne$inputRow.PSObject.Properties['length']) 'Formal Disk input needs a length.';Add-Row (Check-Row $inputRow)}
foreach($path in @($profile.declaration,$profile.builderPath,$profile.managedPackage.path,(Join-Path $profile.sdkRoot 'manifest.json'))){Require ($rows.ContainsKey((Full $path))) "Disk formal input missing: $path"}
$managed=Check-Row $profile.managedPackage;Require ($rows[(Full $managed.path)].sha256-ieq$managed.sha256) 'Disk managed package differs from formal inputs.'
$baseRows=@($rows.Values|Sort-Object path)
if($Producer-and$null-eq$CandidateContext){return [pscustomobject]@{profile=$profile;context=$null;sourceInputs=$baseRows;inputPaths=[string[]]@($baseRows|ForEach-Object path)}}
if(-not $Producer){Require ([IO.File]::Exists($contextPath)) 'Run a qualified ProductDiskNative stage before managed Disk consumption; no generated-directory fallback.'}
$context=if($Producer){$CandidateContext}else{Json $contextPath}
Require ($context.schema-ceq'rm/disk-checkpoint-native-context/v1'-and$context.accepted) 'Unaccepted Disk Native context.'
$run=Full $context.runDirectory;$id=[IO.Path]::GetFileName($run)
Require ([IO.Path]::GetDirectoryName($run)-ieq(Join-Path $task 'runs')-and$id-cmatch'^productdisknative-[0-9a-f]{32}$') 'Disk producer is not one real stage in this task.'
$artifact=Full $context.artifactRoot
Require ($artifact-ieq(Join-Path $profile.nativeOwner ('native-'+$id))-and(Full $context.manifestPath)-ieq(Join-Path $artifact 'native.json')) 'Disk artifact/manifest is not this stage fresh output.'
Require (@($context.outputs).Count-ge3-and@($context.outputs).Count-le256-and@($context.sourceInputs).Count-le4096) 'Disk context row bound.'
$outputs=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($output in $context.outputs){Require (Under $output.path $artifact) 'Disk output escaped artifact root.';$actual=Check-Row $output;Require (-not$outputs.ContainsKey($actual.path)) 'Duplicate Disk output.';$outputs.Add($actual.path,$actual)}
foreach($name in @('native.json','declaration.zig','Disk.ReadWrite.Native.dll')){Require ($outputs.ContainsKey((Join-Path $artifact $name))) "Missing actual Disk output: $name"}
foreach($pair in @(@{row=$context.zigVersion;leaf='disk-zig-version.json'},@{row=$context.builderCommand;leaf='disk-native-builder-command.json'})){Require ((Full $pair.row.path)-ieq(Join-Path $run $pair.leaf)) 'Disk producer evidence belongs to another run.';Add-Row (Check-Row $pair.row)}
$zigEvidence=Json $context.zigVersion.path;$command=Json $context.builderCommand.path
Require ($zigEvidence.exitCode-eq0-and$zigEvidence.version-match'^0\.16\.0(?:[-+].*)?$'-and$zigEvidence.executable.sha256-ieq$rows[(Full 'D:\Code\CommonAssets\Tools\Zig\zig-0.16.0\zig.exe')].sha256-and(Full $command.builder)-ieq(Full $profile.builderPath)-and$command.arguments.Jobs-eq1-and$command.arguments.Backend-ceq'llvm'-and(Full $command.arguments.ArtifactRoot)-ieq$artifact-and(Full $command.arguments.EvidenceRoot)-ieq(Join-Path $run 'disk-native-tool')-and(Full $command.arguments.DeclarationJson)-ieq(Full $profile.declaration)-and(Full $command.arguments.SdkRoot)-ieq(Full $profile.sdkRoot)-and(Full $command.arguments.ProjectRoot)-ieq$repo-and(Full $command.arguments.WorksetRoot)-ieq$workset) 'Disk actual compiler/builder command identity differs.'
$resultPath=Join-Path $run 'disk-native-tool/result.json';Require ((Full $context.buildResult.path)-ieq$resultPath) 'Disk build result belongs to another run.';$resultRow=Check-Row $context.buildResult;$result=Json $resultPath;$manifest=Json $context.manifestPath
Require ($result.accepted-and$result.exit_code-eq0-and@($result.changed_sources).Count-eq0-and$result.compile_policy.jobs-eq1-and$result.compile_policy.backend-ceq'llvm'-and$result.compile_policy.optimization-ceq'ReleaseSafe'-and(Full $result.project_root)-ieq$repo-and(Full $result.workset_root)-ieq$workset) 'Official Disk builder did not accept original compile policy and sources.'
Require ($manifest.schema-ceq'disk.dotnet.native.v1'-and$manifest.sdk_version-ceq'0.1.3'-and$manifest.builder_version-ceq'0.1.3'-and$manifest.compile_policy.jobs-eq1-and$manifest.compile_policy.backend-ceq'llvm'-and$manifest.compile_policy.optimization-ceq'ReleaseSafe'-and$manifest.identity-match'^[0-9A-Fa-f]{64}$'-and$manifest.identity-ceq$result.identity-and$manifest.library-ceq'Disk.ReadWrite.Native.dll') 'Disk native manifest/identity/policy differs.'
Require ($outputs[(Join-Path $artifact $manifest.library)].sha256-ieq$manifest.library_sha256-and$manifest.sdk_manifest_sha256-ieq$rows[(Full (Join-Path $profile.sdkRoot 'manifest.json'))].sha256-and$result.sdk_manifest_sha256-ieq$manifest.sdk_manifest_sha256) 'Disk library/SDK hash not paired.'
$generated=[IO.File]::ReadAllText((Join-Path $artifact 'declaration.zig'));$identity=[regex]::Match($generated,'pub const identity\s*=\s*"([0-9A-Fa-f]{64})";')
Require ($identity.Success-and$identity.Groups[1].Value-ceq$manifest.identity) 'Generated Disk declaration has a different compiled identity.'
# Lifecycle metadata may change after closure. Bind the original stage seal to an immutable
# exact snapshot, and check live owner identity/references/state rather than freezing its SHA.
Require ((Full $context.ownerMarkerAtBuild.source.path)-ieq(Full $ownerMarker)-and(Full $context.ownerMarkerAtBuild.archive.path)-ieq(Join-Path $run 'native-owner-marker-at-build.json')) 'Disk owner marker snapshot belongs to another owner/run.'
$ownerArchiveRow=Check-Row $context.ownerMarkerAtBuild.archive
Require ($ownerArchiveRow.sha256-ieq$context.ownerMarkerAtBuild.source.sha256-and$ownerArchiveRow.length-eq$context.ownerMarkerAtBuild.source.length) 'Disk owner marker archive differs from its original stage bytes.'
$archivedOwner=Json $ownerArchiveRow.path
foreach($name in @('schemaVersion','workspaceId','rootId','rootPath','workspacePath')){Require ([string]$archivedOwner.$name-ceq[string]$owner.$name) 'Disk live owner identity changed after qualification.'}
if($Producer){Require ($rows[(Full $ownerMarker)].sha256-ieq$ownerArchiveRow.sha256) 'Disk owner marker changed during production.'}
$sealed=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($source in $context.sourceInputs){$actual=Check-Row $source;Require (-not$sealed.ContainsKey($actual.path)) 'Duplicate context source.';$sealed.Add($actual.path,$actual)}
foreach($required in @($baseRows|Where-Object {$_.path -ine (Full $ownerMarker)})+@($ownerArchiveRow)){Require ($sealed.ContainsKey($required.path)-and$sealed[$required.path].sha256-ieq$required.sha256-and$sealed[$required.path].length-eq$required.length) 'Context omitted or changed a formal/profile/archived owner input.'}
Require (@($result.sources).Count-gt0-and@($result.sources).Count-le4096) 'Official Disk builder source closure empty/oversized.'
foreach($source in $result.sources){$actual=Check-Row $source;Require ($sealed.ContainsKey($actual.path)-and$sealed[$actual.path].sha256-ieq$actual.sha256) 'Builder source not covered by actual context inputs.'}
if(-not $Producer){
    $boundedPath=Join-Path $run 'bounded-result.json';$bounded=Json $boundedPath
    Require ($bounded.stage-ceq'ProductDiskNative'-and$bounded.accepted-and$bounded.normalDrain-and$bounded.sourceInputsUnchangedExceptDeclaredPartitionRefresh-and$bounded.exitCode-eq0-and(Full $bounded.runDirectory)-ieq$run) 'Original ProductDiskNative bounded result is not accepted/stable.'
    $jobPath=Join-Path $run 'job-manifest.json';$job=Json $jobPath;$receiptPath=Join-Path $run 'job/receipts.jsonl';Require ((Get-Item -LiteralPath $receiptPath).Length-le16MB) 'Disk Job receipt bound.'
    $events=[Collections.Generic.List[object]]::new();foreach($line in [IO.File]::ReadLines($receiptPath)){Require ($events.Count-lt65536) 'Disk Job event bound.';if($line.Trim()){$events.Add(($line|ConvertFrom-Json))}}
    $a=@($events|Where-Object kind -ceq 'admission');$f=@($events|Where-Object kind -ceq 'final-job');$e=@($events|Where-Object kind -ceq 'exit');$c=@($events|Where-Object kind -ceq 'owned-job-closed')
    Require ($a.Count-eq1-and$f.Count-eq1-and$e.Count-eq1-and$c.Count-eq1-and$events[-1].kind-ceq'owned-job-closed'-and$a[0].data.manifest-ieq$jobPath-and$job.allowedReceiptRoot-ieq$run-and$job.runDirectory-ieq(Join-Path $run 'job')-and$f[0].data.empty-and$f[0].data.reason-ceq'normal'-and$e[0].data.ExitCode-eq0-and$e[0].data.reason-ceq'normal'-and$c[0].data.reason-ceq'normal'-and$c[0].data.assigned-and$c[0].data.killOnCloseConfigured) 'Original Disk Job was not normal empty and closed.'
    Require ([DateTimeOffset]$f[0].at-le[DateTimeOffset]$e[0].at-and[DateTimeOffset]$e[0].at-le[DateTimeOffset]$c[0].at-and$job.cpuRate-eq2500-and$job.activeProcessLimit-eq32-and$job.jobMemoryLimitBytes-gt0-and$job.jobMemoryLimitBytes-le1610612736-and$job.physicalReserveBytes-eq1073741824-and$job.commitReserveBytes-eq4294967296-and$job.environmentVariables.DOTNET_GCHeapHardLimit-ceq'30000000'-and$job.environmentVariables.RM_REVIEW_SCRATCH-ieq$bounded.workset) 'Disk Job ordering/envelope differs.'
    foreach($name in @('runDirectory','jobMemoryLimitBytes','cpuRate','physicalReserveBytes','commitReserveBytes')){Require ([string]$a[0].data.plan.$name-ceq[string]$job.$name) 'Disk admission differs from original manifest.'}
    Require (([string[]]$a[0].data.plan.arguments-join[Environment]::NewLine)-ceq([string[]]$job.arguments-join[Environment]::NewLine)) 'Disk actual admission arguments differ.'
    foreach($pair in @(@{flag='-Stage';value='ProductDiskNative'},@{flag='-TaskRoot';value=$task},@{flag='-RunDirectory';value=$run},@{flag='-File';value=(Join-Path $PSScriptRoot 'Run-Stage.ps1')})){$values=[Collections.Generic.List[string]]::new();for($i=0;$i-lt$job.arguments.Count;$i++){if($job.arguments[$i]-ceq$pair.flag){Require ($i+1-lt$job.arguments.Count) 'Disk Job argument missing value.';$values.Add($job.arguments[$i+1])}};Require ($values.Count-eq1-and$values[0]-ieq$pair.value) 'Disk Job stage/task command differs.'}
    $beforePath=Join-Path $run 'source-inputs.json';$afterPath=Join-Path $run 'source-inputs-after.json';$before=@(Json $beforePath);$after=@(Json $afterPath)
    Require ($before.Count-le20000-and$before.Count-eq$after.Count-and@(Compare-Object $before $after -Property path,sha256).Count-eq0) 'Original Disk before/after seals differ.'
    $map=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($row in $before){$path=if([IO.Path]::IsPathFullyQualified($row.path)){$row.path}else{Join-Path $repo $row.path};$key=Full $path;if($map.ContainsKey($key)){Require ($map[$key]-ieq$row.sha256) 'Conflicting duplicate original source seal.'}else{$map.Add($key,$row.sha256)}}
    foreach($required in $baseRows){
        $expected=if($required.path -ieq (Full $ownerMarker)){$context.ownerMarkerAtBuild.source}else{$required}
        Require ($map.ContainsKey($expected.path)-and$map[$expected.path]-ieq$expected.sha256) 'Original stage seal omitted actual Disk formal/profile/owner input.'
    }
    foreach($path in @($contextPath,$boundedPath,$jobPath,$receiptPath,$beforePath,$afterPath)){Add-Row (File-Row $path)}
}
foreach($row in $context.sourceInputs){Add-Row (Check-Row $row)};foreach($row in $context.outputs){Add-Row (Check-Row $row)};Add-Row $resultRow
[pscustomobject]@{profile=$profile;context=$context;sourceInputs=$baseRows;inputPaths=[string[]]@($rows.Keys|Sort-Object)}