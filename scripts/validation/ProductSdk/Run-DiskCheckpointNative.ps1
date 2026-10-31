[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot,[Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][string]$ScratchRoot,[Parameter(Mandatory)][string]$RepositoryRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$task=[IO.Path]::GetFullPath($TaskRoot).TrimEnd('\');$run=[IO.Path]::GetFullPath($RunDirectory).TrimEnd('\');$id=[IO.Path]::GetFileName($run)
if($ScratchRoot -ine $env:RM_REVIEW_SCRATCH-or[IO.Path]::GetDirectoryName($run)-ine(Join-Path $task 'runs')-or$id-cnotmatch'^productdisknative-[0-9a-f]{32}$'){throw 'Use the real bounded ProductDiskNative stage.'}
$validator=Join-Path $PSScriptRoot 'Get-DiskCheckpointNativeInputs.ps1';$inputs=& $validator -TaskRoot $task -Producer;$profile=$inputs.profile
if([IO.Path]::GetFullPath($RepositoryRoot)-ine[IO.Path]::GetFullPath($profile.projectRoot)){throw 'Disk ProjectRoot differs from this repository.'}
$artifact=Join-Path $profile.nativeOwner ('native-'+$id);$evidence=Join-Path $run 'disk-native-tool'
if([IO.Directory]::Exists($artifact)-or[IO.File]::Exists($artifact)-or[IO.Directory]::Exists($evidence)-or[IO.File]::Exists($evidence)){throw 'Disk builder output and tool evidence must both be fresh for this stage.'}
$utf8=[Text.UTF8Encoding]::new($false)
function Save([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 20),$utf8)}
function Row([string]$Path){$f=Get-Item -LiteralPath $Path;[pscustomobject]@{path=$f.FullName;length=$f.Length;sha256=(Get-FileHash -LiteralPath $Path).Hash}}
$ownerMarkerPath=Join-Path $profile.nativeOwner '.generated-workspace.json';$ownerAtBuild=Row $ownerMarkerPath
$ownerArchive=Join-Path $run 'native-owner-marker-at-build.json';[IO.File]::Copy($ownerMarkerPath,$ownerArchive,$false);$ownerArchiveRow=Row $ownerArchive
if($ownerAtBuild.sha256 -ine $ownerArchiveRow.sha256 -or $ownerAtBuild.length -ne $ownerArchiveRow.length){throw 'Native owner marker snapshot differs.'}
$contextSources=@($inputs.sourceInputs|Where-Object {$_.path -ine $ownerMarkerPath})+@($ownerArchiveRow)
Import-Module (Join-Path $PSScriptRoot '..\..\dev\MachineRoots.psm1') -DisableNameChecking
$zig=Join-Path (Get-MachineRoot -Name ToolsRoot) 'Zig\zig-0.17.0\zig.exe';$version=@(& $zig version);$versionCode=$LASTEXITCODE
Save (Join-Path $run 'disk-zig-version.json') @{executable=(Row $zig);exitCode=$versionCode;version=($version-join[Environment]::NewLine)}
if($versionCode-ne0-or$version.Count-ne1-or$version[0]-notmatch'^0\.17\.0(?:[-+].*)?$'){throw 'The official builder default Zig0.17 tool is not verified.'}
$arguments=@{DeclarationJson=[string]$profile.declaration;SdkRoot=[string]$profile.sdkRoot;ArtifactRoot=$artifact;EvidenceRoot=$evidence;ProjectRoot=[string]$profile.projectRoot;WorksetRoot=[string]$profile.worksetRoot;Jobs=1;Backend='llvm'}
Save (Join-Path $run 'disk-native-builder-command.json') @{builder=[string]$profile.builderPath;arguments=$arguments;inheritCurrentExecutionJob=$true;defaultZig=(Row $zig)}
$global:LASTEXITCODE=0
& $profile.builderPath @arguments 1> (Join-Path $run 'disk-native-builder.stdout.log') 2> (Join-Path $run 'disk-native-builder.stderr.log')
$ok=$?;$code=$LASTEXITCODE
if(-not$ok-or$code-ne0){throw 'Official Disk builder failed; retain this stage tool result and original receipts.'}
# Capture the small actual output tree. No SDK/source projection or alternate compiler graph.
$queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($artifact);$outputs=[Collections.Generic.List[object]]::new();$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);[long]$bytes=0;$nodes=0
while($queue.Count){$directory=$queue.Dequeue();if(-not$seen.Add($directory)){throw 'Repeated Disk output directory.'};if(++$nodes-gt1024){throw 'Disk output node cap exceeded.'};if(([IO.File]::GetAttributes($directory)-band[IO.FileAttributes]::ReparsePoint)-ne0){throw 'Disk output contains reparse directory.'};foreach($entry in [IO.DirectoryInfo]::new($directory).EnumerateFileSystemInfos()){if(++$nodes-gt1024-or($entry.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw 'Disk output bound/reparse rejection.'};if($entry.Attributes-band[IO.FileAttributes]::Directory){$queue.Enqueue($entry.FullName)}else{$bytes+=$entry.Length;if($bytes-gt1GB-or$outputs.Count-ge256){throw 'Disk output byte/file cap exceeded.'};$outputs.Add((Row $entry.FullName))}}}
$context=[pscustomobject]@{schema='rm/disk-checkpoint-native-context/v1';accepted=$true;runDirectory=$run;artifactRoot=$artifact;manifestPath=(Join-Path $artifact 'native.json');outputs=@($outputs.ToArray()|Sort-Object path);buildResult=(Row (Join-Path $evidence 'result.json'));sourceInputs=$contextSources;ownerMarkerAtBuild=@{source=$ownerAtBuild;archive=$ownerArchiveRow};zigVersion=(Row (Join-Path $run 'disk-zig-version.json'));builderCommand=(Row (Join-Path $run 'disk-native-builder-command.json'))}
# Producer validation deliberately does not read its own unfinished bounded receipt.
$verified=& $validator -TaskRoot $task -Producer -CandidateContext $context
if($verified.context.artifactRoot-ine$artifact){throw 'Disk qualified root differs.'}
& (Join-Path $RepositoryRoot 'scripts/build/Verify-DiskCheckpointNative.ps1') -DeclarationPath $profile.declaration -NativeRoot $artifact -DependencyManifestPath (Join-Path $RepositoryRoot 'artifacts/disk-sdk/dependency-manifest.json') 1> (Join-Path $run 'disk-product-qualification.log')
if(-not $?){throw 'RM Disk declaration/package qualification rejected the actual Native.'}
$contextPath=Join-Path $task 'disk-native-context.json'
if([IO.File]::Exists($contextPath)){[IO.File]::Copy($contextPath,(Join-Path $run 'prior-disk-native-context.json'),$false)}
Save $contextPath $context
Write-Output "Qualified actual Disk Native output: $artifact"