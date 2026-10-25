param(
    [Parameter(Mandatory)][ValidateSet('ProductBackend','ProductShell','ProductImage')][string]$Stage,
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ScratchRoot,
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$LedgerBuildRoot,
    [string]$ProductArtifactsPath,
    [string]$NativeContextPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if([IO.Path]::GetFullPath($ScratchRoot) -ine [IO.Path]::GetFullPath($env:RM_REVIEW_SCRATCH)){throw 'Use bounded entry.'}
$repo=[IO.Path]::GetFullPath($RepositoryRoot)
$dotnet='C:\Program Files\dotnet\dotnet.exe'
function Save-Json($Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
}
function Read-AcceptedContext([string]$Name) {
    $context=Get-Content (Join-Path $TaskRoot $Name) -Raw|ConvertFrom-Json
    if(-not $context.accepted){throw "Unaccepted prerequisite: $Name"}
    $receipt=Get-Content (Join-Path $context.runDirectory 'bounded-result.json') -Raw|ConvertFrom-Json
    if(-not $receipt.accepted -or -not $receipt.normalDrain){throw "Unbounded prerequisite: $Name"}
    return $context
}
function Assert-Hash($Item) {
    if((Get-FileHash -LiteralPath $Item.path).Hash -ine $Item.sha256){throw "Input changed: $($Item.path)"}
}
function Get-TreeManifest([string]$Root,[switch]$ExcludeNodeModules) {
    $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue([IO.Path]::GetFullPath($Root))
    $files=[Collections.Generic.List[object]]::new();[long]$bytes=0;[int]$nodes=0
    while($queue.Count) {
        $directory=$queue.Dequeue()
        if(++$nodes -gt 100000){throw 'Candidate tree exceeds node bound.'}
        if(([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Candidate contains reparse point.'}
        foreach($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes=[IO.File]::GetAttributes($path)
            if(($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Candidate contains reparse point: $path"}
            if(($attributes -band [IO.FileAttributes]::Directory) -ne 0){
                if($ExcludeNodeModules -and [IO.Path]::GetFileName($path) -ceq 'node_modules'){continue}
                $queue.Enqueue($path);continue
            }
            if(++$nodes -gt 100000){throw 'Candidate tree exceeds node bound.'}
            $info=[IO.FileInfo]::new($path);$bytes+=$info.Length
            if($bytes -gt 8GB){throw 'Candidate exceeds byte bound.'}
            $files.Add([pscustomobject]@{path=$path;relativePath=[IO.Path]::GetRelativePath($Root,$path).Replace('\','/');length=$info.Length;sha256=(Get-FileHash -LiteralPath $path).Hash})
        }
    }
    return @($files|Sort-Object relativePath)
}
function Invoke-Publish([string]$Project,[string]$Name,[string[]]$Extra) {
    $output=Join-Path $candidate $Name
    if([IO.Directory]::Exists($output)){throw "Publish output already exists: $output"}
    $arguments=@('publish',(Join-Path $repo $Project),'-c','Release','-r','win-x64','--self-contained','true','--nologo','--verbosity','minimal')+$common+$Extra+('-p:PublishDir='+$output)
    Save-Json @{executable=$dotnet;arguments=$arguments;workingDirectory=$repo} (Join-Path $RunDirectory ($Name+'-command.json'))
    & $dotnet @arguments *> (Join-Path $RunDirectory ($Name+'-publish.log'))
    $code=$LASTEXITCODE
    Save-Json @{exitCode=$code;outputDirectory=$output;command=(Join-Path $RunDirectory ($Name+'-command.json'))} (Join-Path $RunDirectory ($Name+'-publish-result.json'))
    Get-Content (Join-Path $RunDirectory ($Name+'-publish.log')) -Tail 12|ForEach-Object {Write-Host $_}
    if($code -ne 0){throw "Publish $Name failed: $code"}
    $manifest=@(Get-TreeManifest $output)
    Save-Json $manifest (Join-Path $RunDirectory ($Name+'-output.json'))
    if(@($manifest|Where-Object {$_.relativePath -match '(^|/)(Config|UserData|Dependencies|Misc)/|\.pdb$'}).Count){throw "Runtime data/debug symbols entered $Name candidate."}
    return $output
}
function Capture-ResolvedInputs([string]$EvidenceRoot) {
    $items=[Collections.Generic.List[object]]::new()
    foreach($file in [IO.Directory]::EnumerateFiles($EvidenceRoot,'*.txt')) {
        if($file -notmatch '\.(bundle|publish|compile|embedded|reference|pre-r2r)-inputs\.txt$'){continue}
        foreach($line in [IO.File]::ReadAllLines($file)) {
            if([string]::IsNullOrWhiteSpace($line)){continue}
            $parts=$line.Split('|',2);$path=$parts[0]
            if(-not [IO.File]::Exists($path)){throw "Resolved compiler/publish input disappeared: $path"}
            $items.Add([pscustomobject]@{list=[IO.Path]::GetFileName($file);path=$path;relativePath=$(if($parts.Length -gt 1){$parts[1]}else{''});length=([IO.FileInfo]::new($path)).Length;sha256=(Get-FileHash -LiteralPath $path).Hash})
        }
    }
    Save-Json $items.ToArray() (Join-Path $RunDirectory 'resolved-inputs.json')
    return $items.ToArray()
}
Set-Location $repo
$diskInputs=& (Join-Path $PSScriptRoot 'Get-DiskCheckpointNativeInputs.ps1') -TaskRoot $TaskRoot
$nativeContextPath=if([string]::IsNullOrWhiteSpace($NativeContextPath)){Join-Path $TaskRoot 'product-native-context.json'}else{[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($NativeContextPath)){$NativeContextPath}else{Join-Path $repo $NativeContextPath}))}
& (Join-Path $PSScriptRoot 'Assert-ProductNativeContext.ps1') -ContextPath $nativeContextPath | Out-Null
$native=Get-Content -LiteralPath $nativeContextPath -Raw|ConvertFrom-Json
$frontend=Read-AcceptedContext 'frontend-product-context.json'
if(-not $frontend.contractsPassed -or -not $frontend.repositoryAssetsIdentical){throw 'Frontend source/asset contract is not ready for normal publish.'}
foreach($proof in @(@{path=$frontend.sourceInputManifest;sha256=$frontend.sourceInputManifestSha256},@{path=$frontend.outputManifest;sha256=$frontend.outputManifestSha256})){Assert-Hash $proof}
$frontendSource=Get-Content $frontend.sourceInputManifest -Raw|ConvertFrom-Json
$frontendOutput=Get-Content $frontend.outputManifest -Raw|ConvertFrom-Json
foreach($pair in @(@{expected=$frontendSource;current=@(Get-TreeManifest (Join-Path $repo 'src/UI/Frontend') -ExcludeNodeModules)},@{expected=$frontendOutput;current=@(Get-TreeManifest (Join-Path $repo 'src/Core/wwwroot'))})) {
    $expected=@($pair.expected.files|ForEach-Object {[pscustomobject]@{relativePath=$_.path;length=$_.bytes;sha256=$_.sha256}})
    if(@(Compare-Object $expected $pair.current -Property relativePath,length,sha256).Count){throw 'Frontend source/assets changed since accepted rebuild.'}
}
if($Stage -eq 'ProductBackend') {
    $artifacts=Join-Path $ScratchRoot 'product-artifacts'
    if(-not [string]::IsNullOrWhiteSpace($ProductArtifactsPath)) {
        $artifacts=[IO.Path]::GetFullPath($ProductArtifactsPath)
        $batch=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($artifacts))
        $state=Get-Content (Join-Path $batch '.generated-workspace.json') -Raw|ConvertFrom-Json
        if($state.state -ne 'running' -or [IO.Path]::GetFullPath($state.workspacePath) -ine $batch -or
            $artifacts -ine (Join-Path $batch 'scratch/product-artifacts') -or $state.references -notcontains [IO.Path]::GetFullPath($TaskRoot)) {
            throw "Only this running task's exact product artifact directory can be reused."
        }
        # Always run normal restore/build/publish. Reuse only intermediates and
        # package cache; the current attempt gets a fresh candidate directory.
    }
    $candidate=Join-Path $ScratchRoot 'candidate'
} else {
    $product=Get-Content (Join-Path $TaskRoot 'product-publish-context.json') -Raw|ConvertFrom-Json
    $prior=Get-Content (Join-Path $product.backendRun 'bounded-result.json') -Raw|ConvertFrom-Json
    if(-not $prior.accepted){throw 'Backend publish prerequisite failed.'}
    $artifacts=[string]$product.artifacts;$candidate=[string]$product.candidate
    $batch=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($artifacts))
    $state=Get-Content (Join-Path $batch '.generated-workspace.json') -Raw|ConvertFrom-Json
    if($state.state -ne 'running'){throw 'Do not reopen completed candidate artifacts.'}
}
$evidence=Join-Path $RunDirectory 'publish-inputs';[void][IO.Directory]::CreateDirectory($evidence)
$common=@('-p:PublishReadyToRun=true','-p:PublishReadyToRunComposite=false','-p:PublishReadyToRunCrossgen2ExtraArgs=--parallelism:1','-p:DebugType=none','-p:DebugSymbols=false','-p:CopyOutputSymbolsToPublishDirectory=false',
    ('-p:ArtifactsPath='+$artifacts),('-p:RestorePackagesPath='+(Join-Path $artifacts 'packages')),('-p:RestoreConfigFile='+(Join-Path $TaskRoot 'NuGet.official.config')),
    '-p:BuildInParallel=false','-maxcpucount:1','-nodeReuse:false','-p:UseSharedCompilation=false','-p:NuGetAudit=false',
    ('-p:CustomAfterMicrosoftCommonTargets='+(Join-Path $PSScriptRoot 'ProductPublishCapture.targets')),('-p:RMProductEvidenceRoot='+$evidence),
    ('-p:CompilerResponseFile='+(Join-Path $PSScriptRoot 'serial-compiler.rsp')))
if($diskInputs){
    $common+=@(('-p:DiskCheckpointNativeRoot='+$diskInputs.context.artifactRoot),('-p:DiskCheckpointPowerShell='+[Environment]::ProcessPath))
}
if($Stage -eq 'ProductBackend') {
    if([string]::IsNullOrWhiteSpace($LedgerBuildRoot)){throw 'Explicit accepted Ledger build is required.'}
    $adapter=[string]$native.properties.AdapterNativeOutput
    $adapterProof=@($native.outputs|Where-Object {$_.path -ieq $adapter})
    if($adapterProof.Count -ne 1){throw 'Exact rebuilt legacy Adapter output proof is required.'}
    $probeOutput=Join-Path $ScratchRoot 'legacy-probe'
    $probeProperties=@($common|Where-Object {$_ -notlike '-p:PublishReadyToRun=*'})
    $probeArguments=@('build',(Join-Path $repo 'tests/Fixtures/LegacyAdapterInitializationProbe/LegacyAdapterInitializationProbe.csproj'),'-c','Release','-r','win-x64','--output',$probeOutput,'--nologo','--verbosity','minimal')+$probeProperties+@('-p:PublishSingleFile=false','-p:PublishReadyToRun=false','-p:SelfContained=false','-p:SkipAdapterNativeBuild=true',('-p:AdapterNativeOutput='+$adapter))
    Save-Json @{executable=$dotnet;arguments=$probeArguments} (Join-Path $RunDirectory 'legacy-probe-build-command.json')
    & $dotnet @probeArguments *> (Join-Path $RunDirectory 'legacy-probe-build.log')
    if($LASTEXITCODE -ne 0){throw 'Legacy managed probe build failed.'}
    $probeArguments=@((Join-Path $probeOutput 'LegacyAdapterInitializationProbe.dll'),$adapter,[string]$adapterProof[0].sha256)
    Save-Json @{executable=$dotnet;arguments=$probeArguments} (Join-Path $RunDirectory 'legacy-probe-run-command.json')
    & $dotnet @probeArguments 1> (Join-Path $RunDirectory 'legacy-probe-result.json') 2> (Join-Path $RunDirectory 'legacy-probe-error.log')
    if($LASTEXITCODE -ne 0){throw 'Legacy Adapter initialization/layout probe failed.'}
    $probe=Get-Content (Join-Path $RunDirectory 'legacy-probe-result.json') -Raw|ConvertFrom-Json
    if($probe.status -ne 'passed' -or @($probe.sessions).Count -ne 4 -or @($probe.sessions|Where-Object {-not $_.initialized -or -not $_.disposed}).Count){throw 'Legacy Adapter probe result incomplete.'}
    # The backend publishes as a folder like the release script; the shell programs stay single-file.
    $extra=@('-p:SkipClientAppBuild=true','-p:PublishSingleFile=false',
        ('-p:ResourceManagerLedgerWorkRoot='+(Join-Path $ScratchRoot 'ledger-work')),('-p:ResourceManagerLedgerBuildRoot='+[IO.Path]::GetFullPath($LedgerBuildRoot)),
        ('-p:ResourceManagerLedgerPowerShell='+[Environment]::ProcessPath))
    foreach($property in $native.properties.psobject.Properties){$extra+=('-p:'+$property.Name+'='+[string]$property.Value)}
    $backend=Invoke-Publish 'src/Core/ResourceManager.App.csproj' 'backend' $extra
    $resolved=@(Capture-ResolvedInputs $evidence)
    $requiredManaged=@('ResourceManager.dll','ResourceManager.ReadWriteCore.dll','ResourceManager.SubscriptionCore.dll','FreedomPoint.Core.dll','ResourceManager.Ledger.dll')
    if($diskInputs){$requiredManaged+='Disk.ReadWrite.dll'}
    foreach($name in $requiredManaged) {
        if(-not @($resolved|Where-Object {$_.list -eq 'ResourceManager.App.publish-inputs.txt' -and [IO.Path]::GetFileName($_.path) -ceq $name}).Count){throw "Actual backend publish omitted $name"}
    }
    foreach($name in @('Dia2Lib.dll','TraceReloggerLib.dll')) {
        foreach($phase in @('pre-r2r','publish')) {
            if(@($resolved|Where-Object {$_.list -eq ('ResourceManager.App.'+$phase+'-inputs.txt') -and [IO.Path]::GetFileName($_.path) -ceq $name}).Count -ne 1){throw "TraceEvent interop input must occur once in $phase : $name"}
        }
    }
    foreach($name in @('LedgerOwnerShape.g.cs','LedgerOwnerBuild.g.cs')) {
        $expected=Join-Path $LedgerBuildRoot $name
        if(-not @($resolved|Where-Object {$_.list -eq 'ResourceManager.App.compile-inputs.txt' -and $_.path -ieq $expected}).Count){throw "Actual Core compilation omitted $name"}
    }
    foreach($relative in @('Configuration/HostManager/default.json','Configuration/FreedomPoints/backend.json','Configuration/Cpu/baseline-defaults.json','Configuration/SoftwareProfiles/official.json')) {
        $expected=Join-Path $repo ('src/Core/'+$relative)
        if(-not @($resolved|Where-Object {$_.list -eq 'ResourceManager.App.embedded-inputs.txt' -and $_.path -ieq $expected}).Count){throw "Actual Core compilation omitted embedded default: $relative"}
    }
    $ledgerDll=Join-Path $backend 'ResourceManager.Ledger.Native.dll'
    if((Get-FileHash $ledgerDll).Hash -ine (Get-FileHash (Join-Path $LedgerBuildRoot 'ResourceManager.Ledger.Native.dll')).Hash){throw 'Published Ledger Native mismatch.'}
    $properties=Get-Content (Join-Path $evidence 'ResourceManager.App.properties.txt')
    $assetsPath=($properties|Where-Object {$_ -like 'ProjectAssetsFile=*'}).Substring('ProjectAssetsFile='.Length)
    $assets=Get-Content $assetsPath -Raw|ConvertFrom-Json
    $requiredDependencies=@('FreedomPoint.Core/0.1.0-dev.45','ResourceManager.Ledger/0.1.0-dev.2')
    if($diskInputs){$requiredDependencies+='Disk.ReadWrite/0.1.0'}
    foreach($dependency in $requiredDependencies) {
        if(-not $assets.libraries.PSObject.Properties[$dependency]){throw "Missing actual dependency: $dependency"}
    }
    Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $RunDirectory 'backend-project.assets.json')
    $packagePairs=[Collections.Generic.List[object]]::new()
    $formalPairs=@(@{package='sdk/freedom-point/packages/FreedomPoint.Core.0.1.0-dev.45.nupkg';assembly='FreedomPoint.Core.dll'},@{package='artifacts/sdk/packages/ResourceManager.Ledger.0.1.0-dev.2.nupkg';assembly='ResourceManager.Ledger.dll'})
    if($diskInputs){
        $diskPackage='artifacts/disk-sdk/packages/'+[IO.Path]::GetFileName($diskInputs.profile.managedPackage.path)
        $mirror=Get-Item -LiteralPath (Join-Path $repo $diskPackage)
        if($mirror.Length -ne $diskInputs.profile.managedPackage.length -or (Get-FileHash -LiteralPath $mirror.FullName).Hash -ine $diskInputs.profile.managedPackage.sha256){throw 'Formal Disk package mirror differs from the source profile.'}
        $formalPairs+=@{package=$diskPackage;assembly='Disk.ReadWrite.dll'}
    }
    foreach($pair in $formalPairs) {
        $packagePath=Join-Path $repo $pair.package
        $archive=[IO.Compression.ZipFile]::OpenRead($packagePath)
        try {
            $entries=@($archive.Entries|Where-Object {$_.FullName -like 'lib/*' -and $_.Name -ceq $pair.assembly})
            if($pair.assembly -ceq 'Disk.ReadWrite.dll' -and ($entries.Count -ne 1 -or $entries[0].FullName -cne 'lib/net10.0/Disk.ReadWrite.dll')){throw 'Disk formal reference must come from the exact net10.0 package assembly.'}
            if($entries.Count -ne 1){throw 'Ambiguous formal package assembly.'}
            $stream=$entries[0].Open()
            try {$expectedHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
            # ReadyToRun rewrites runtime bundle inputs. Match the actual
            # original compiler reference to the formal package before R2R.
            $references=@($resolved|Where-Object {$_.list -eq 'ResourceManager.App.reference-inputs.txt' -and [IO.Path]::GetFileName($_.path) -ceq $pair.assembly})
            if($references.Count -ne 1 -or $references[0].sha256 -ine $expectedHash){throw "Actual formal package reference mismatch: $($pair.assembly)"}
            $pairedRecord=@{package=$packagePath;packageSha256=(Get-FileHash $packagePath).Hash;entry=$entries[0].FullName;reference=$references[0];sha256=$expectedHash}
            if($pair.assembly -ceq 'Disk.ReadWrite.dll'){$pairedRecord.formalSource=$diskInputs.profile.managedPackage}
            $packagePairs.Add($pairedRecord)
        } finally {$archive.Dispose()}
    }
    Save-Json $packagePairs.ToArray() (Join-Path $RunDirectory 'formal-managed-package-pairs.json')
    if($diskInputs){
        foreach($name in @('Disk.ReadWrite.Native.dll','native.json')){
            $original=@($diskInputs.context.outputs|Where-Object {[IO.Path]::GetFileName($_.path) -ceq $name})
            $published=Join-Path $backend ('Native/DiskCheckpoint/'+$name)
            if($original.Count -ne 1 -or (Get-Item -LiteralPath $published).Length -ne $original[0].length -or (Get-FileHash -LiteralPath $published).Hash -ine $original[0].sha256){throw "Actual Disk product payload mismatch: $name"}
        }
    }
    $candidateWeb=@(Get-TreeManifest (Join-Path $backend 'wwwroot'))
    $repositoryWeb=@(Get-TreeManifest (Join-Path $repo 'src/Core/wwwroot'))
    $originals=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach($item in $repositoryWeb){$originals.Add($item.relativePath,$item)}
    $seenOriginals=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $compressed=[Collections.Generic.List[object]]::new()
    foreach($item in $candidateWeb) {
        if($originals.ContainsKey($item.relativePath)) {
            $original=$originals[$item.relativePath]
            if($item.length -ne $original.length -or $item.sha256 -cne $original.sha256){throw "Published frontend original differs: $($item.relativePath)"}
            [void]$seenOriginals.Add($item.relativePath)
            continue
        }
        $extension=[IO.Path]::GetExtension($item.relativePath)
        $basePath=$item.relativePath.Substring(0,$item.relativePath.Length-$extension.Length)
        if($extension -notin @('.gz','.br') -or -not $originals.ContainsKey($basePath)){throw "Unexpected published frontend file: $($item.relativePath)"}
        $original=$originals[$basePath]
        $input=[IO.File]::OpenRead($item.path)
        $decoder=$null;$hasher=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $decoder=if($extension -ceq '.gz'){[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress)}else{[IO.Compression.BrotliStream]::new($input,[IO.Compression.CompressionMode]::Decompress)}
            $buffer=[byte[]]::new(65536);[long]$decodedBytes=0
            while(($count=$decoder.Read($buffer,0,$buffer.Length)) -gt 0) {
                $decodedBytes+=$count
                if($decodedBytes -gt $original.length){throw "Compressed frontend exceeds original length: $($item.relativePath)"}
                $hasher.AppendData($buffer,0,$count)
            }
            $decodedHash=[Convert]::ToHexString($hasher.GetHashAndReset())
            if($decodedBytes -ne $original.length -or $decodedHash -cne $original.sha256){throw "Compressed frontend differs from original: $($item.relativePath)"}
            $compressed.Add(@{path=$item.relativePath;basePath=$basePath;decodedLength=$decodedBytes;decodedSha256=$decodedHash})
        } finally {if($decoder){$decoder.Dispose()};$input.Dispose();$hasher.Dispose()}
    }
    if($seenOriginals.Count -ne $originals.Count){throw 'Published frontend originals are incomplete.'}
    Save-Json @{originalFiles=$seenOriginals.Count;verifiedCompressedCopies=$compressed.ToArray()} (Join-Path $RunDirectory 'published-frontend-acceptance.json')
    $backendFiles=@(Get-TreeManifest $backend)
    foreach($item in $native.outputs|Where-Object {[IO.Path]::GetExtension($_.path) -in @('.dll','.exe')}) {
        $copies=@($backendFiles|Where-Object {[IO.Path]::GetFileName($_.path) -ieq [IO.Path]::GetFileName($item.path)})
        if($copies.Count -ne 1 -or $copies[0].sha256 -ine $item.sha256){throw "Published Native input mismatch: $($item.path)"}
    }
    Save-Json @(Get-TreeManifest (Join-Path $artifacts 'freedom-point-generated')) (Join-Path $RunDirectory 'freedom-point-generated-output.json')
    Save-Json @{accepted=$true;backendRun=$RunDirectory;artifacts=$artifacts;candidate=$candidate;backendDirectory=$backend;nativeContext=$nativeContextPath;frontendContext=(Join-Path $TaskRoot 'frontend-product-context.json')} (Join-Path $TaskRoot 'product-publish-context.json')
} elseif($Stage -eq 'ProductShell') {
    $nativeUi=Invoke-Publish 'src/UI/Core/ResourceManager.NativeUi.csproj' 'native-ui' @('-p:PublishSingleFile=false')
    $launcher=Invoke-Publish 'src/UI/Launcher/ResourceManager.Launcher.csproj' 'launcher' @('-p:PublishSingleFile=true')
    [void](Capture-ResolvedInputs $evidence)
    $product|Add-Member -NotePropertyName shellRun -NotePropertyValue $RunDirectory -Force
    $product|Add-Member -NotePropertyName nativeUiDirectory -NotePropertyValue $nativeUi -Force
    $product|Add-Member -NotePropertyName launcherDirectory -NotePropertyValue $launcher -Force
    Save-Json $product (Join-Path $TaskRoot 'product-publish-context.json')
} else {
    $shellReceipt=Get-Content (Join-Path $product.shellRun 'bounded-result.json') -Raw|ConvertFrom-Json
    if(-not $shellReceipt.accepted){throw 'Shell publish prerequisite failed.'}
    foreach($entry in @(@{run=$product.backendRun;name='backend'},@{run=$product.shellRun;name='native-ui'},@{run=$product.shellRun;name='launcher'})) {
        $before=Get-Content (Join-Path $entry.run ($entry.name+'-output.json')) -Raw|ConvertFrom-Json
        $current=@(Get-TreeManifest (Join-Path $candidate $entry.name))
        if(@(Compare-Object $before $current -Property relativePath,length,sha256).Count){throw 'Published candidate changed before composition.'}
    }
    $image=Join-Path $ScratchRoot 'final-image'
    & (Join-Path $repo 'scripts/release/New-ResourceManagerFinalImage.ps1') -BackendDirectory $product.backendDirectory -NativeUiDirectory $product.nativeUiDirectory -LauncherDirectory $product.launcherDirectory -OutputDirectory $image *> (Join-Path $RunDirectory 'final-image-build.log')
    & (Join-Path $repo 'scripts/validation/Test-ResourceManagerFinalImage.ps1') -RootDirectory $image *> (Join-Path $RunDirectory 'final-image-validation.log')
    & (Join-Path $repo 'scripts/validation/Test-WindowsExecutableManifest.ps1') -ExecutablePath (Join-Path $image 'ResourceManagerNativeUi/ResourceManager.NativeUi.exe') -ExpectedExecutionLevel asInvoker *> (Join-Path $RunDirectory 'native-ui-manifest.log')
    Save-Json @(Get-TreeManifest $image) (Join-Path $RunDirectory 'final-image-output.json')
    Save-Json @{accepted=$true;runDirectory=$RunDirectory;imageDirectory=$image;backendRun=$product.backendRun;shellRun=$product.shellRun;installedProductModified=$false;formalRelease=$false} (Join-Path $TaskRoot 'product-image-context.json')
}
