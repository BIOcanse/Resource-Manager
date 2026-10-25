param([Parameter(Mandatory)][ValidateSet('Build','Tests','Sources','Incomplete','DefaultRestore','Pack','Consumers','Frontend','ProductNative','ProductLegacy','ProductDiskNative','ProductBackend','ProductShell','ProductImage')][string]$Stage,
    [long]$JobMemoryLimitBytes=1610612736,
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$WorkspaceRoot,
    [string]$LedgerBuildRoot,
    [string]$ProductArtifactsPath,
    [string]$TestProject,
    [string]$TestCompileManifest,
    [string]$TestRuntimeManifest,
    [string]$TestRuntimeGroupId,
    [string]$NativeContextPath,
    [ValidateSet('DOTNET_gcServer=0')][string]$CompilerEnvironment)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRepo='D:\Code\Resource-Manager-Development'
$taskRoot=[IO.Path]::GetFullPath($TaskRoot)
$taskWorkspace=[IO.Path]::GetFullPath($WorkspaceRoot)
if(-not [IO.File]::Exists((Join-Path $taskRoot 'NuGet.official.config'))) {
    throw 'Prepare TaskRoot/NuGet.official.config with explicit package sources before allocating a validation workset.'
}
if($CompilerEnvironment -and $Stage -notin @('Build','ProductBackend','ProductShell','ProductImage')){throw 'CompilerEnvironment applies only to Build and product composition stages.'}
$taskExplicitTestProject=-not [string]::IsNullOrWhiteSpace($TestProject)
if($taskExplicitTestProject -and $Stage -notin @('Build','Tests')){throw 'TestProject applies only to Build and Tests.'}
$taskPartitionHash=$null
$taskPartitionReferences=@()
$taskRuntimeReferences=@()
$taskRuntime=$null
$taskRuntimeSelectedCompilePath=$null
$taskRuntimeSelection=$null
if(-not [string]::IsNullOrWhiteSpace($TestCompileManifest)) {
    if(-not $taskExplicitTestProject -or $Stage -notin @('Build','Tests')){throw 'TestCompileManifest requires an explicit TestProject and Build or Tests stage.'}
    $TestCompileManifest=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($TestCompileManifest)){$TestCompileManifest}else{Join-Path $taskRepo $TestCompileManifest}))
    if(-not [IO.File]::Exists($TestCompileManifest)){throw "Test compile manifest does not exist: $TestCompileManifest"}
    $taskPartitionHash=(Get-FileHash -LiteralPath $TestCompileManifest).Hash
    $taskPartition=Get-Content -LiteralPath $TestCompileManifest -Raw|ConvertFrom-Json
    if($taskPartition.schemaVersion -ne 1 -or $taskPartition.PSObject.Properties.Name -notcontains 'sourceFiles' -or @($taskPartition.sourceFiles).Count -eq 0){throw 'Test compile manifest requires schemaVersion 1 and nonempty sourceFiles.'}
    foreach($taskPartitionSource in @($taskPartition.sourceFiles)) {
        if(-not [IO.Path]::IsPathFullyQualified([string]$taskPartitionSource) -or -not [IO.File]::Exists([string]$taskPartitionSource)){throw "Partition source must be an existing absolute file: $taskPartitionSource"}
    }
    $taskPartitionProject=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($TestProject)){$TestProject}else{Join-Path $taskRepo $TestProject}))
    if([IO.Path]::GetFullPath([string]$taskPartition.projectPath) -ine $taskPartitionProject){throw 'Partition projectPath must match the explicit TestProject.'}
    foreach($taskPartitionPath in @([string]$taskPartition.sourceListPath,[string]$taskPartition.originalCompileMetadata.path)) {
        if(-not [IO.Path]::IsPathFullyQualified($taskPartitionPath) -or -not [IO.File]::Exists($taskPartitionPath)){throw "Partition evidence path must be an existing absolute file: $taskPartitionPath"}
    }
    $taskPartitionList=@(Get-Content -LiteralPath $taskPartition.sourceListPath)
    if([string]::Join("`n",[string[]]$taskPartitionList) -cne [string]::Join("`n",[string[]]$taskPartition.sourceFiles)){throw 'Partition sourceListPath must exactly match sourceFiles in order.'}
    if((Get-FileHash -LiteralPath $taskPartition.originalCompileMetadata.path).Hash -ine [string]$taskPartition.originalCompileMetadata.sha256){throw 'Original Compile metadata hash changed.'}
    $taskPartitionReferences=@($TestCompileManifest,[string]$taskPartition.sourceListPath,[string]$taskPartition.originalCompileMetadata.path)
}
if(-not [string]::IsNullOrWhiteSpace($TestRuntimeManifest) -or -not [string]::IsNullOrWhiteSpace($TestRuntimeGroupId)) {
    if($Stage -cne 'Tests' -or -not $taskPartitionHash -or [string]::IsNullOrWhiteSpace($TestRuntimeManifest) -or [string]::IsNullOrWhiteSpace($TestRuntimeGroupId)){throw 'Runtime grouping requires Tests, an exact compile manifest, and both runtime parameters.'}
    $TestRuntimeManifest=[IO.Path]::GetFullPath($TestRuntimeManifest)
    $taskRuntimeSelection=& (Join-Path $PSScriptRoot 'Get-AppTestRuntimeSelection.ps1') -ManifestPath $TestRuntimeManifest -GroupId $TestRuntimeGroupId -CompileManifestPath $TestCompileManifest -BuildContextPath (Join-Path $taskRoot 'build-context.json')
    $taskRuntime=Get-Content -LiteralPath $TestRuntimeManifest -Raw|ConvertFrom-Json
    $taskRuntimeReferences=@($TestRuntimeManifest,$taskRuntime.planPath,$taskRuntime.assemblyArchive,(Join-Path $taskRuntime.buildRunDirectory 'partition-build.json'))+@($taskRuntime.tools|ForEach-Object path)
    $taskRuntimeSelectedCompilePath=Join-Path ([string]$taskRuntime.buildRunDirectory) 'compile-partition/selected-compile.txt'
    $taskRuntimeReferences+= $taskRuntimeSelectedCompilePath
}
if($Stage -ceq 'Tests' -and $taskPartitionHash -and -not $taskRuntime) {
    $taskExistingBuildContext=Get-Content -LiteralPath (Join-Path $taskRoot 'build-context.json') -Raw|ConvertFrom-Json
    $taskRuntimeSelectedCompilePath=Join-Path ([string]$taskExistingBuildContext.runDirectory) 'compile-partition/selected-compile.txt'
    $taskRuntimeReferences+=@((Join-Path ([string]$taskExistingBuildContext.runDirectory) 'partition-build.json'),$taskRuntimeSelectedCompilePath)
}
$taskTestProject=if($taskExplicitTestProject){[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($TestProject)){$TestProject}else{Join-Path $taskRepo $TestProject}))}else{Join-Path $taskRoot 'FocusedTests.csproj'}
$taskUsesTestProject=$Stage -notin @('Frontend','ProductNative','ProductLegacy','ProductDiskNative','ProductBackend','ProductShell','ProductImage')
if($taskUsesTestProject -and $Stage -notin @('Build','Tests') -and [IO.File]::Exists((Join-Path $taskRoot 'build-context.json'))) {
    $taskPriorBuild=Get-Content -LiteralPath (Join-Path $taskRoot 'build-context.json') -Raw|ConvertFrom-Json
    if($taskPriorBuild.PSObject.Properties.Name -contains 'testProject'){$taskTestProject=[string]$taskPriorBuild.testProject}
}
if($taskUsesTestProject -and -not [IO.File]::Exists($taskTestProject)){throw "Test project does not exist: $taskTestProject"}
$taskNativeInputs=@()
$taskQualifiedNativeConsumer=$false
if(-not [string]::IsNullOrWhiteSpace($NativeContextPath)) {
    $NativeContextPath=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($NativeContextPath)){$NativeContextPath}else{Join-Path $taskRepo $NativeContextPath}))
    $taskNativeContext=Get-Content -LiteralPath $NativeContextPath -Raw|ConvertFrom-Json
    if(-not $taskNativeContext.accepted -or @($taskNativeContext.outputs).Count -eq 0){throw 'Native context must contain accepted outputs.'}
    $taskNativeRows=@($taskNativeContext.outputs)+@($taskNativeContext.sourceInputsManifest)
    foreach($taskNativeRow in $taskNativeRows) {
        if((Get-FileHash -LiteralPath $taskNativeRow.path).Hash -ine $taskNativeRow.sha256){throw "Native context input changed: $($taskNativeRow.path)"}
    }
    $taskNativeSource=Get-Content -LiteralPath $taskNativeContext.sourceInputsManifest.path -Raw|ConvertFrom-Json
    if(@($taskNativeSource.source).Count -eq 0){throw 'Native source input manifest is empty.'}
    foreach($taskNativeGroup in @('source','projected','baseline','tools')) {
        if($taskNativeSource.PSObject.Properties.Name -notcontains $taskNativeGroup){continue}
        foreach($taskNativeRow in @($taskNativeSource.$taskNativeGroup)) {
            if((Get-FileHash -LiteralPath $taskNativeRow.path).Hash -ine $taskNativeRow.sha256){throw "Native source or tool changed: $($taskNativeRow.path)"}
            $taskNativeRows+=$taskNativeRow
        }
    }
    $taskNativeInputs=@($NativeContextPath)+@($taskNativeRows|ForEach-Object path)
    if($taskNativeContext.PSObject.Properties.Name -contains 'dependencyQualification') {
        $taskQualificationRow=$taskNativeContext.dependencyQualification
        if((Get-FileHash -LiteralPath $taskQualificationRow.path).Hash -ine $taskQualificationRow.sha256){throw 'Native dependency qualification changed.'}
        $taskQualification=Get-Content -LiteralPath $taskQualificationRow.path -Raw|ConvertFrom-Json
        if($taskQualification.schema -cne 'rm/native-consumer-dependency-qualification/v1' -or -not $taskQualification.accepted -or
            @($taskQualification.closure.source).Count -eq 0){throw 'Invalid Native consumer dependency qualification.'}
        foreach($taskDependency in @($taskQualification.closure.source)) {
            $taskDependencyRows=@($taskNativeSource.source|Where-Object {[string]$_.path -ieq [string]$taskDependency.path})
            if($taskDependencyRows.Count -ne 1 -or $taskDependencyRows[0].sha256 -ine $taskDependency.sha256){throw 'Qualified Native dependency is absent from the sealed source inputs.'}
        }
        $taskNativeInputs+=$taskQualificationRow.path
        $taskQualifiedNativeConsumer=$true
    }
}
$taskDiskInputs=& (Join-Path $PSScriptRoot 'Get-DiskCheckpointNativeInputs.ps1') -TaskRoot $taskRoot -Producer:($Stage -ceq 'ProductDiskNative')
$taskDiskFiles=@();$taskDiskReferences=@()
if($taskDiskInputs){
    $taskDiskFiles=@($taskDiskInputs.inputPaths)
    foreach($path in @((Join-Path $taskRepo 'artifacts/disk-sdk/dependency-manifest.json'),(Join-Path $taskRepo ('artifacts/disk-sdk/packages/'+[IO.Path]::GetFileName($taskDiskInputs.profile.managedPackage.path))))){if([IO.File]::Exists($path)){$taskDiskFiles+=$path}}
    $taskDiskReferences=$taskDiskFiles+@([string]$taskDiskInputs.profile.nativeOwner)
}
$taskPackageReferences=@()
if($Stage -ceq 'Consumers') {
    $taskPackageContextPath=Join-Path $taskRoot 'package-context.json'
    $taskPackageContextHash=(Get-FileHash -LiteralPath $taskPackageContextPath -Algorithm SHA256).Hash
    $taskPackageContext=Get-Content -LiteralPath $taskPackageContextPath -Raw|ConvertFrom-Json
    if(-not $taskPackageContext.freshFeed -or @($taskPackageContext.packages).Count -ne 2){throw 'Consumers requires the preceding fresh two-package context.'}
    $taskPackageFeed=[string]$taskPackageContext.feed
    if(-not [IO.Path]::IsPathFullyQualified($taskPackageFeed) -or -not [IO.Directory]::Exists($taskPackageFeed)){throw 'Consumers package feed must be an existing absolute directory.'}
    $taskPackageFeed=[IO.Path]::GetFullPath($taskPackageFeed)
    $taskPackageNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $taskPackagePaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskPackageReferences=@($taskPackageContextPath,$taskPackageFeed)
    foreach($taskPackage in @($taskPackageContext.packages)) {
        $taskPackagePath=[string]$taskPackage.path
        if(@('ReadWriteCore','SubscriptionCore') -cnotcontains [string]$taskPackage.name -or -not $taskPackageNames.Add([string]$taskPackage.name)){throw 'Consumers requires distinct ReadWriteCore and SubscriptionCore package identities.'}
        if(-not [IO.Path]::IsPathFullyQualified($taskPackagePath) -or -not [IO.File]::Exists($taskPackagePath)){throw 'Consumers package must be an existing absolute file.'}
        $taskPackagePath=[IO.Path]::GetFullPath($taskPackagePath)
        if([IO.Path]::GetDirectoryName($taskPackagePath) -ine $taskPackageFeed -or -not $taskPackagePaths.Add($taskPackagePath)){throw 'Consumers packages must be distinct files in the exact declared feed.'}
        if([string]$taskPackage.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or (Get-FileHash -LiteralPath $taskPackagePath -Algorithm SHA256).Hash -ine [string]$taskPackage.sha256){throw 'Consumers package SHA differs from its actual Pack context.'}
        $taskPackageReferences+=$taskPackagePath
    }
    if((Get-FileHash -LiteralPath $taskPackageContextPath -Algorithm SHA256).Hash -cne $taskPackageContextHash){throw 'Consumers package context changed while validating Prepare references.'}
}
$taskRetention=& (Join-Path $PSScriptRoot 'Get-ProductRetentionPolicy.ps1') -TaskRoot $taskRoot
$taskPolicy=$taskRetention.path
$taskHold=Join-Path $taskRepo '.local/subscription-core-stage-20261007/execution-hold.json'
if(!(Get-Content $taskHold -Raw|ConvertFrom-Json).highLoadAllowed){throw 'Execution hold remains active.'}
$taskId=$Stage.ToLowerInvariant()+'-'+[guid]::NewGuid().ToString('N')
if($Stage -in @('ProductNative','ProductLegacy')) {
    # Original MinGW build.cmd includes sibling headers through unnormalised
    # .. paths. A compact registered batch ID keeps those paths below MAX_PATH.
    $taskId=$(if($Stage -eq 'ProductNative'){'pn-'}else{'pl-'})+[guid]::NewGuid().ToString('N').Substring(0,16)
}
$taskRun=Join-Path $taskRoot ('runs/'+$taskId)
$taskExpectedBatch=Join-Path $taskWorkspace ('.validation/batches/'+$taskId)
if(Test-Path -LiteralPath $taskExpectedBatch){throw 'Fresh validation BatchId already exists.'}
[void][IO.Directory]::CreateDirectory($taskRun)
$taskWorkset=& (Join-Path $taskRepo 'scripts/validation/Manage-ValidationWorkset.ps1') -Mode Prepare -WorkspaceRoot $taskWorkspace -BatchId $taskId -ConsumerId 'rm-product-sdk' -MaxWorksetGiB 20 -ExpectedGrowthGiB 2 -RetentionPolicyPath $taskPolicy -References (@($taskRoot)+$taskNativeInputs+$taskDiskReferences+$taskPartitionReferences+$taskRuntimeReferences+$taskPackageReferences) -ReferenceRoots $taskRetention.referenceRoots|ConvertFrom-Json
if(!$taskWorkset.IsolatedScratchRoot){throw 'Workset preparation failed.'}
$taskCode=1
$taskNormal=$false
$taskSame=$false
$taskJobDeclared=$false
$taskWorksetRecorded=$false
$taskFailure=$null
try {
    if([IO.Path]::GetFullPath([string]$taskWorkset.WorkspaceRoot) -ine $taskWorkspace -or
        [string]$taskWorkset.BatchId -cne $taskId -or
        [IO.Path]::GetFullPath([string]$taskWorkset.IsolatedScratchRoot) -ine (Join-Path $taskExpectedBatch 'scratch')){throw 'Prepared workset does not belong to this fresh invocation.'}
[IO.File]::WriteAllText((Join-Path $taskRun 'workset.json'),($taskWorkset|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$taskWorksetRecorded=$true
$taskInputs=@(& rg --files src/Core src/ReadWriteCore src/SubscriptionCore src/Shared sdk/csharp/ResourceManager.Adapter.Abstractions sdk/csharp/ResourceManager.Adapter.SharedMemory tests/App tests/ReadWriteCore/PackageConsumer tests/SubscriptionCore/PackageConsumer scripts/validation scripts/build)
if($LASTEXITCODE -ne 0){throw 'Could not enumerate backend inputs.'}
$taskInputs=@($taskInputs|Where-Object {$_ -notmatch '(^|[\\/])(bin|obj|node_modules)([\\/]|$)' -and $_ -notlike 'src\UI\Frontend\*' -and $_ -match '\.(cs|csproj|props|targets|json|ps1|mjs|rsp|runsettings)$'})
if($taskExplicitTestProject) {
    # Seal the complete managed project graph and copied/embedded resources,
    # including xaml/resx, instead of applying the focused C# extension filter.
    $taskAppInputs=@(& rg --files src/UI/Core src/UI/Launcher src/Installer src/Updater src/Shared src/Core src/ReadWriteCore src/SubscriptionCore tests/App src/UI/Frontend/src src/UI/Frontend/tests)
    if($LASTEXITCODE -ne 0){throw 'Could not enumerate full App inputs.'}
    $taskInputs+=@($taskAppInputs|Where-Object {
        $_ -notmatch '(^|[\\/])(bin|obj|node_modules|zig-out|\.zig-cache)([\\/]|$)' -and
        $_ -notmatch '^src[\\/]Core[\\/]Native[\\/]'
    })
    $taskInputs+=@('global.json','LICENSE','NOTICE','src/UI/Frontend/package.json','src/UI/Frontend/package-lock.json')|Where-Object {[IO.File]::Exists((Join-Path $taskRepo $_))}
}
if($Stage -in @('Frontend','ProductNative','ProductLegacy','ProductDiskNative','ProductBackend','ProductShell','ProductImage')) {
    # Product composition includes the shell, frontend and legacy Native inputs.
    # Enumerate source trees, never their generated caches or compiled outputs.
    $taskProductInputs=@(& rg --files src/UI src/Updater src/Core/Native src/Core/wwwroot sdk/zig scripts/release tests/Fixtures)
    if($LASTEXITCODE -ne 0){throw 'Could not enumerate product inputs.'}
    if($taskQualifiedNativeConsumer -and $Stage -in @('ProductBackend','ProductShell','ProductImage')) {
        # These stages consume the qualified prebuilt Native outputs. Their
        # complete SDK dependency set is already sealed through taskNativeInputs;
        # producer stages retain the full SDK tree when actually compiling it.
        $taskProductInputs=@($taskProductInputs|Where-Object {$_ -notmatch '^sdk[\\/]zig[\\/]'})
    }
    $taskInputs+=@($taskProductInputs|Where-Object {
        $_ -notmatch '(^|[\\/])(bin|obj|node_modules|zig-out|\.zig-cache)([\\/]|$)' -and
        $_ -match '\.(cs|csproj|props|targets|json|ps1|mjs|js|ts|tsx|css|html|cmd|c|cpp|h|hpp|inc|def|rc|manifest|zig|zon|txt|md|svg|png|ico)$'
    })
    $taskInputs+=@('global.json','LICENSE','NOTICE')|Where-Object {[IO.File]::Exists((Join-Path $taskRepo $_))}
}
$taskInputs+=@('NuGet.Config','Directory.Build.props',(Join-Path $taskRoot 'NuGet.official.config'))+$taskNativeInputs+$taskDiskFiles+$taskRuntimeReferences
if($Stage -in @('ProductBackend','ProductShell','ProductImage')) {
    $taskProductNativeContextPath=if([string]::IsNullOrWhiteSpace($NativeContextPath)){Join-Path $taskRoot 'product-native-context.json'}else{$NativeContextPath}
    $taskInputs+=@($taskProductNativeContextPath,(Join-Path $taskRoot 'frontend-product-context.json'))
    if($Stage -eq 'ProductImage'){$taskInputs+=Join-Path $taskRoot 'product-publish-context.json'}
}
if($taskUsesTestProject){$taskInputs+=$taskTestProject}
if($taskPartitionHash){$taskInputs+=@($TestCompileManifest,$taskPartition.sourceListPath,$taskPartition.originalCompileMetadata.path); $taskInputs+=@($taskPartition.sourceFiles)}
if([IO.File]::Exists((Join-Path $taskRepo 'Directory.Build.targets'))){$taskInputs+='Directory.Build.targets'}
if([IO.File]::Exists((Join-Path $taskRoot 'package-prerequisites.json'))){
    $taskInputs+=Join-Path $taskRoot 'package-prerequisites.json'
    $taskPrerequisites=Get-Content (Join-Path $taskRoot 'package-prerequisites.json') -Raw|ConvertFrom-Json
    $taskInputs+=@($taskPrerequisites.nativeSourceInputs|ForEach-Object path)
    $taskInputs+=@($taskPrerequisites.nativeAssets|ForEach-Object path)
}
$taskInputs+=@('artifacts/sdk/manifest.0.1.0-dev.2.json','artifacts/sdk/packages/ResourceManager.Ledger.0.1.0-dev.2.nupkg','artifacts/sdk/packages/ResourceManager.Ledger.Build.0.1.0-dev.2.nupkg','artifacts/sdk/packages/ResourceManager.Ledger.Configuration.0.1.0-dev.2.nupkg','artifacts/sdk/native/0.1.0-dev.2/ResourceManager.Ledger.NativeSources.0.1.0-dev.2.zip','artifacts/sdk/tools/0.1.0-dev.2/.config/dotnet-tools.json')
$taskInputs+=@(Get-ChildItem (Join-Path $taskRepo 'sdk/freedom-point/packages') -File -Filter '*0.1.0-dev.45.nupkg'|ForEach-Object FullName)
if($Stage -in @('Sources','Pack','Consumers')) {
    $taskCompileBuildContext=Get-Content -LiteralPath (Join-Path $taskRoot 'build-context.json') -Raw|ConvertFrom-Json
    $taskCompileArtifacts=[IO.Path]::GetFullPath([string]$taskCompileBuildContext.artifacts)
    $taskCompilePowerShell='C:\Users\kings\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe'
    $taskCompileProperties=@(
        ('-p:ArtifactsPath='+$taskCompileArtifacts),'-p:Configuration=Debug','-p:BuildInParallel=false','-maxcpucount:1','-nodeReuse:false',
        '-p:UseSharedCompilation=false','-p:NuGetAudit=false','-p:SkipPerformanceOverlayNativeBuild=true','-p:SkipGpuPlacementNativeBuild=true',
        '-p:SkipGpuPlacementPreparationNativeBuild=true','-p:SkipGpuLaunchBrokerNativeBuild=true','-p:SkipAdlxBridgeBuild=true',
        '-p:SkipNativeCoreBuild=true','-p:SkipGpuWindowActionNativeBuild=true','-p:SkipGpuPlacementExternalNativeBuild=true',
        '-p:SkipAdapterNativeBuild=true','-p:SkipClientAppBuild=true','-p:NativeCoreOptimize=ReleaseFast',
        ('-p:CustomAfterMicrosoftCommonTargets='+(Join-Path $taskRepo 'scripts/validation/FreedomPointCompilerCapture.targets')),
        ('-p:CompilerResponseFile='+(Join-Path $taskRepo 'scripts/validation/ProductSdk/serial-compiler.rsp')),
        ('-p:ResourceManagerLedgerWorkRoot='+(Join-Path $taskRoot 'ledger-native')),
        ('-p:ResourceManagerLedgerPowerShell='+$taskCompilePowerShell),
        ('-p:DiskCheckpointPowerShell='+$taskCompilePowerShell),
        ('-p:RestoreConfigFile='+(Join-Path $taskRoot 'NuGet.official.config')),
        ('-p:RestorePackagesPath='+(Join-Path $taskCompileArtifacts 'packages'))
    )
    if([string]::IsNullOrWhiteSpace($NativeContextPath)) {
        $taskCompileProperties+='-p:NativeCoreInstallRoot=D:\Code\Resource-Manager-Development\.local\review-repairs-third-20261009\native-prefix'
    } else {
        $taskCompileNativeContext=Get-Content -LiteralPath $NativeContextPath -Raw|ConvertFrom-Json
        $taskCompileAllowedNativeProperties=@('NativeCoreOutput','AdapterNativeRoot','AdapterNativeOutput','AdlxBridgeRoot',
            'GpuPlacementShimRoot','GpuLaunchBrokerRoot','GpuWindowActionRoot','GpuPlacementExternalRoot',
            'GpuPlacementPreparationRoot','PerformanceOverlayRoot','GpuMigrationProbeRoot','GpuLaunchTargetProbePath',
            'SkipNativeCoreBuild','SkipAdapterNativeBuild','SkipAdlxBridgeBuild','SkipGpuPlacementNativeBuild',
            'SkipGpuLaunchBrokerNativeBuild','SkipGpuWindowActionNativeBuild','SkipGpuPlacementExternalNativeBuild',
            'SkipGpuPlacementPreparationNativeBuild','SkipPerformanceOverlayNativeBuild')
        foreach($taskProperty in $taskCompileNativeContext.properties.PSObject.Properties) {
            if($taskCompileAllowedNativeProperties -notcontains $taskProperty.Name){throw "Unexpected Native context property: $($taskProperty.Name)"}
            if($taskProperty.Name.StartsWith('Skip',[StringComparison]::Ordinal) -and [string]$taskProperty.Value -cne 'true'){throw 'Native context cannot enable new builds.'}
            if(($taskProperty.Name.EndsWith('Output',[StringComparison]::Ordinal) -or $taskProperty.Name.EndsWith('Path',[StringComparison]::Ordinal)) -and
                @($taskCompileNativeContext.outputs|Where-Object {[string]$_.path -ieq [string]$taskProperty.Value}).Count -ne 1){throw "Native output property is not bound to a verified output: $($taskProperty.Name)"}
            $taskCompileProperties+=('-p:'+$taskProperty.Name+'='+[string]$taskProperty.Value)
        }
        if(@($taskCompileNativeContext.outputs|Where-Object {[string]$_.path -ieq [string]$taskCompileNativeContext.properties.NativeCoreOutput}).Count -ne 1){throw 'NativeCoreOutput is not bound to a verified output.'}
    }
    if($taskDiskInputs) {
        $taskCompileProperties+=('-p:DiskCheckpointNativeRoot='+$taskDiskInputs.context.artifactRoot)
    }
    if(-not [string]::IsNullOrWhiteSpace($LedgerBuildRoot)) {
        $taskCompileProperties+=('-p:ResourceManagerLedgerBuildRoot='+[IO.Path]::GetFullPath($LedgerBuildRoot))
    }
    $taskCompileManifestPath=Join-Path $taskRun 'compile-inputs.json'
    $taskCompileArguments=@{
        Stage=$Stage;RepositoryRoot=$taskRepo;TaskRoot=$taskRoot;ScratchRoot=$taskWorkset.IsolatedScratchRoot
        ArtifactsPath=$taskCompileArtifacts;OutputPath=$taskCompileManifestPath;BuildProperties=$taskCompileProperties
    }
    $taskCompileSummary=& (Join-Path $PSScriptRoot 'Get-EvaluatedCompileInputs.ps1') @taskCompileArguments|ConvertFrom-Json
    $taskCompileManifest=Get-Content -LiteralPath $taskCompileManifestPath -Raw|ConvertFrom-Json
    $taskInputs+=@($taskCompileManifestPath,[string]$taskCompileSummary.captureTargetsPath)
    $taskInputs+=@($taskCompileManifest.inputs|Where-Object {-not $_.generated}|ForEach-Object path)
}
if($taskPartitionHash) {
    . (Join-Path $PSScriptRoot 'Get-AppTestCompileInputScope.ps1')
    $taskSelectedCompilePath=$null
    $taskSelectedCompileSha256=$null
    if($Stage -ceq 'Tests') {
        $taskSelectedCompileBuildRun=if($taskRuntime){[string]$taskRuntime.buildRunDirectory}else{[string]$taskExistingBuildContext.runDirectory}
        $taskSelectedCompile=Get-AppTestSelectedCompileEvidence -BuildRunDirectory $taskSelectedCompileBuildRun `
            -ExpectedPartitionId ([string]$taskPartition.partitionId) -ExpectedManifestPath $TestCompileManifest `
            -ExpectedManifestSha256 $taskPartitionHash
        $taskSelectedCompilePath=[string]$taskSelectedCompile.path
        $taskSelectedCompileSha256=[string]$taskSelectedCompile.sha256
        if([IO.Path]::GetFullPath($taskSelectedCompilePath) -ine [IO.Path]::GetFullPath($taskRuntimeSelectedCompilePath)) {
            throw 'Test stage selected Compile evidence path differs from the Build output.'
        }
        $taskInputs+=$taskSelectedCompilePath
    }
    $taskInputs=@(Get-AppTestScopedInputRows -Rows $taskInputs -RepositoryRoot $taskRepo -Stage $Stage `
        -ManifestPath $TestCompileManifest -OriginalCompileMetadataPath $taskPartition.originalCompileMetadata.path `
        -SelectedCompilePath $taskSelectedCompilePath -SelectedCompileSha256 $taskSelectedCompileSha256 `
        -IncludeRuntimeSelection:([bool]$taskRuntimeSelection))
}
$taskInputs=@($taskInputs|ForEach-Object { if([IO.Path]::IsPathRooted($_)){[IO.Path]::GetRelativePath($taskRepo,$_)}else{$_} })
function Read-Inputs {
    foreach($taskPath in ($taskInputs|Sort-Object -Unique)) {
        $taskInputPath=if([IO.Path]::IsPathRooted($taskPath)){$taskPath}else{Join-Path $taskRepo $taskPath}
        [pscustomobject]@{path=$taskPath;sha256=(Get-FileHash -LiteralPath $taskInputPath).Hash}
    }
}
$taskBefore=@(Read-Inputs)
$taskPartitionBefore=Get-Content -LiteralPath (Join-Path $taskRepo 'src/Core/Configuration/FreedomPoints/backend.groups.json') -Raw|ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $taskRun 'source-inputs.json'),($taskBefore|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $taskRun 'selected-test-project.json'),(@{stage=$Stage;testProject=$taskTestProject;explicitTestProject=$taskExplicitTestProject;testCompileManifest=$TestCompileManifest;testCompileManifestSha256=$taskPartitionHash;nativeContextPath=$NativeContextPath}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $taskPreflight=& (Join-Path $PSScriptRoot 'Capture-Preflight.ps1') -TaskRoot $taskRoot -Scope Product -JobMemoryLimitBytes $JobMemoryLimitBytes
    $taskPlan=[ordered]@{
        executionHoldPath=$taskHold;faultReviewPath=$taskPreflight.faultReviewPath;headroomPath=$taskPreflight.headroomPath
        allowedReceiptRoot=$taskRun;runDirectory=(Join-Path $taskRun 'job')
        executable='C:\Users\kings\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe'
        arguments=@('-NoLogo','-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'Run-Stage.ps1'),'-Stage',$Stage,'-RunDirectory',$taskRun,'-TaskRoot',$taskRoot)
        workingDirectory=$taskRepo;jobMemoryLimitBytes=$JobMemoryLimitBytes;activeProcessLimit=32;cpuRate=2500
        commitReserveBytes=$taskPreflight.commitReserveBytes;physicalReserveBytes=$taskPreflight.physicalReserveBytes
        timeoutSeconds=900;pollMilliseconds=100;maxOutputBytes=4194304
        environmentVariables=@{RM_REVIEW_SCRATCH=$taskWorkset.IsolatedScratchRoot;TEMP=$taskWorkset.IsolatedScratchRoot;TMP=$taskWorkset.IsolatedScratchRoot;DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0';DOTNET_GCHeapHardLimit='30000000';DOTNET_GCConserveMemory='7';DOTNET_PROCESSOR_COUNT='2';NODE_OPTIONS='--max-old-space-size=768'}
    }
    if($CompilerEnvironment){$taskPlan.environmentVariables.CscEnvironment=$CompilerEnvironment}
    if(-not [string]::IsNullOrWhiteSpace($LedgerBuildRoot)){$taskPlan.arguments+=@('-LedgerBuildRoot',[IO.Path]::GetFullPath($LedgerBuildRoot))}
    if(-not [string]::IsNullOrWhiteSpace($ProductArtifactsPath)){$taskPlan.arguments+=@('-ProductArtifactsPath',[IO.Path]::GetFullPath($ProductArtifactsPath))}
    if($taskExplicitTestProject){$taskPlan.arguments+=@('-TestProject',$taskTestProject)}
    if($taskPartitionHash){$taskPlan.arguments+=@('-TestCompileManifest',$TestCompileManifest)}
    if($taskRuntimeSelection){$taskPlan.arguments+=@('-TestRuntimeManifest',$TestRuntimeManifest,'-TestRuntimeGroupId',$TestRuntimeGroupId)}
    if(-not [string]::IsNullOrWhiteSpace($NativeContextPath)){$taskPlan.arguments+=@('-NativeContextPath',$NativeContextPath)}
    $taskManifest=Join-Path $taskRun 'job-manifest.json'
    # A partial declaration is also ambiguous; never treat it as unstarted.
    $taskJobDeclared=$true
    [IO.File]::WriteAllText($taskManifest,($taskPlan|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    & (Join-Path $taskRepo '.local/subscription-core-device-owners-20261007/execution-job-fixed/bin/ExecutionJob/release/ExecutionJob.exe') $taskManifest *> (Join-Path $taskRun 'controller.log')
    $taskCode=$LASTEXITCODE
    Get-Content (Join-Path $taskRun 'controller.log') -Tail 8
    $taskEvents=@(Get-Content (Join-Path $taskPlan.runDirectory 'receipts.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
    $taskExit=@($taskEvents|Where-Object kind -eq 'exit')
    $taskDrain=@($taskEvents|Where-Object kind -eq 'final-job')
    $taskNormal=$taskExit.Count -eq 1 -and $taskExit[0].data.reason -eq 'normal' -and $taskDrain.Count -eq 1 -and $taskDrain[0].data.empty -and $taskDrain[0].data.reason -eq 'normal'
    $taskAfter=@(Read-Inputs)
    [IO.File]::WriteAllText((Join-Path $taskRun 'source-inputs-after.json'),($taskAfter|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    $taskDifferences=@(Compare-Object $taskBefore $taskAfter -Property path,sha256)
    $taskSame=$taskDifferences.Count -eq 0
    if($Stage -eq 'Sources' -and !$taskSame) {
        $taskGroupsPath='src\Core\Configuration\FreedomPoints\backend.groups.json'
        $taskOther=@($taskDifferences|Where-Object {$_.path.Replace('/','\') -cne $taskGroupsPath})
        $taskPartitionAfter=Get-Content (Join-Path $taskRepo $taskGroupsPath) -Raw|ConvertFrom-Json
        $taskSame=$taskOther.Count -eq 0 -and ($taskPartitionBefore.groups|ConvertTo-Json -Depth 16 -Compress) -ceq ($taskPartitionAfter.groups|ConvertTo-Json -Depth 16 -Compress)
    }
    if($taskCode -ne 0 -or !$taskNormal -or !$taskSame){throw "Bounded stage failed: $taskRun"}
} catch {
    $taskFailure=$_
    throw
} finally {
    $taskAccepted=$taskCode -eq 0 -and $taskNormal -and $taskSame
    $taskReceipt=@{stage=$Stage;testProject=$taskTestProject;testCompileManifest=$TestCompileManifest;testCompileManifestSha256=$taskPartitionHash;nativeContextPath=$NativeContextPath;exitCode=$taskCode;normalDrain=$taskNormal;sourceInputsUnchangedExceptDeclaredPartitionRefresh=$taskSame;runDirectory=$taskRun;workset=$taskWorkset.IsolatedScratchRoot;accepted=$taskAccepted}
    if($taskRuntimeSelection){$taskReceipt.testRuntimeManifest=$TestRuntimeManifest;$taskReceipt.testRuntimeManifestSha256=$taskRuntimeSelection.runtimeManifestSha256;$taskReceipt.testRuntimeGroupId=$TestRuntimeGroupId}
    try {
        [IO.File]::WriteAllText((Join-Path $taskRun 'bounded-result.json'),($taskReceipt|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
        if($taskWorksetRecorded -and -not $taskAccepted -and -not $taskJobDeclared) {
            # Persisted workset evidence and this call's control flow establish a
            # fresh, never-declared leaf. Missing files alone prove nothing.
            if((Test-Path -LiteralPath (Join-Path $taskRun 'job-manifest.json')) -or
                (Test-Path -LiteralPath (Join-Path $taskRun 'controller.log')) -or
                (Test-Path -LiteralPath (Join-Path $taskRun 'job'))){throw 'Cannot conclude a possibly declared or started Job as unstarted.'}
            $taskBatchState=Get-Content -LiteralPath (Join-Path $taskExpectedBatch '.generated-workspace.json') -Raw|ConvertFrom-Json
            if($taskBatchState.state -cne 'running' -or $taskBatchState.keep -or
                [IO.Path]::GetFullPath([string]$taskBatchState.rootPath) -ine (Split-Path -Parent $taskExpectedBatch) -or
                [IO.Path]::GetFullPath([string]$taskBatchState.workspacePath) -ine $taskExpectedBatch -or
                [IO.Path]::GetFullPath([string]$taskWorkset.IsolatedScratchRoot) -ine (Join-Path $taskExpectedBatch 'scratch')){throw 'Unstarted batch ownership changed.'}
            $global:LASTEXITCODE=0
            $taskCompletion=& (Join-Path $taskRepo 'scripts/validation/Manage-ValidationWorkset.ps1') -Mode Complete -WorkspaceRoot $taskWorkspace -BatchId $taskId -Outcome failed -References @() -ReferenceRoots $taskRetention.referenceRoots -RetentionPolicyPath $taskPolicy
            if($LASTEXITCODE -ne 0 -or -not $taskCompletion){throw 'Official unstarted batch completion failed.'}
        }
    } catch {
        if($null -ne $taskFailure) {
            throw [AggregateException]::new('Validation failed and its result persistence or unstarted completion also failed.',[Exception[]]@($taskFailure.Exception,$_.Exception))
        }
        throw
    }
    # A successful/reusable Build or any declared Job needs normal final closure.
    $taskReceipt|ConvertTo-Json -Compress
}
