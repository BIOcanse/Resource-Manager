param([Parameter(Mandatory)][ValidateSet('Build','Tests','Sources','Incomplete','DefaultRestore','Pack','Consumers','Frontend','ProductNative','ProductLegacy','ProductDiskNative','ProductBackend','ProductShell','ProductImage')][string]$Stage,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$TaskRoot,
    [string]$LedgerBuildRoot,
    [string]$ProductArtifactsPath,
    [string]$TestProject,
    [string]$TestCompileManifest,
    [string]$TestRuntimeManifest,
    [string]$TestRuntimeGroupId,
    [string]$NativeContextPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRepo='D:\Code\Resource-Manager-Development'
$taskRoot=[IO.Path]::GetFullPath($TaskRoot)
$taskExplicitTestProject=-not [string]::IsNullOrWhiteSpace($TestProject)
if($taskExplicitTestProject -and $Stage -notin @('Build','Tests')){throw 'TestProject applies only to Build and Tests.'}
$taskProject=if($taskExplicitTestProject){[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($TestProject)){$TestProject}else{Join-Path $taskRepo $TestProject}))}else{Join-Path $taskRoot 'FocusedTests.csproj'}
$taskPartition=$null
$taskPartitionHash=$null
$taskPartitionListHash=$null
if(-not [string]::IsNullOrWhiteSpace($TestCompileManifest)) {
    if(-not $taskExplicitTestProject -or $Stage -notin @('Build','Tests')){throw 'TestCompileManifest requires an explicit TestProject and Build or Tests stage.'}
    $TestCompileManifest=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($TestCompileManifest)){$TestCompileManifest}else{Join-Path $taskRepo $TestCompileManifest}))
    $taskPartitionHash=(Get-FileHash -LiteralPath $TestCompileManifest).Hash
    $taskPartition=Get-Content -LiteralPath $TestCompileManifest -Raw|ConvertFrom-Json
    if($taskPartition.schemaVersion -ne 1 -or [IO.Path]::GetFullPath([string]$taskPartition.projectPath) -ine $taskProject){throw 'Partition schemaVersion/projectPath does not match this explicit test project.'}
    $taskPartitionSources=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($taskSource in @($taskPartition.sourceFiles)) {
        if(-not [IO.Path]::IsPathFullyQualified([string]$taskSource) -or -not [IO.File]::Exists([string]$taskSource) -or
            [IO.Path]::GetExtension([string]$taskSource) -ine '.cs' -or [string]$taskSource -match '[;\r\n]' -or
            -not $taskPartitionSources.Add([IO.Path]::GetFullPath([string]$taskSource))){throw "Invalid, duplicate or missing partition source: $taskSource"}
    }
    if($taskPartitionSources.Count -eq 0){throw 'Partition sourceFiles cannot be empty.'}
    foreach($taskPath in @([string]$taskPartition.sourceListPath,[string]$taskPartition.originalCompileMetadata.path)) {
        if(-not [IO.Path]::IsPathFullyQualified($taskPath) -or -not [IO.File]::Exists($taskPath)){throw "Invalid partition evidence path: $taskPath"}
    }
    $taskPartitionList=@(Get-Content -LiteralPath $taskPartition.sourceListPath)
    if([string]::Join("`n",[string[]]$taskPartitionList) -cne [string]::Join("`n",[string[]]$taskPartition.sourceFiles)){throw 'Partition sourceListPath must exactly match sourceFiles in order.'}
    $taskPartitionListHash=(Get-FileHash -LiteralPath $taskPartition.sourceListPath).Hash
    if((Get-FileHash -LiteralPath $taskPartition.originalCompileMetadata.path).Hash -ine [string]$taskPartition.originalCompileMetadata.sha256){throw 'Original Compile metadata hash changed.'}
    Copy-Item -LiteralPath $TestCompileManifest -Destination (Join-Path $RunDirectory 'test-compile-manifest.json')
    Copy-Item -LiteralPath $taskPartition.sourceListPath -Destination (Join-Path $RunDirectory 'requested-sources.txt')
    Copy-Item -LiteralPath $taskPartition.originalCompileMetadata.path -Destination (Join-Path $RunDirectory 'original-compile-metadata.json')
}
$taskDotnet='C:\Program Files\dotnet\dotnet.exe'
$taskScratch=$env:RM_REVIEW_SCRATCH
if ([string]::IsNullOrWhiteSpace($taskScratch)) { throw 'Use bounded entry.' }
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
$env:MSBUILDDISABLENODEREUSE='1'
$env:MSBUILDNODEENABLENODEREUSE='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='0'
$env:DOTNET_CLI_HOME=Join-Path $taskScratch 'dotnet-home'
[void][IO.Directory]::CreateDirectory($env:DOTNET_CLI_HOME)
if($Stage -eq 'Frontend') {
    & (Join-Path $PSScriptRoot 'Run-FrontendProductInputs.ps1') -TaskRoot $taskRoot -RunDirectory $RunDirectory -ScratchRoot $taskScratch -RepositoryRoot $taskRepo
    exit
}
if($Stage -eq 'ProductNative') {
    & (Join-Path $PSScriptRoot 'Prepare-NativeProductInputs.ps1') -TaskRoot $taskRoot -RunDirectory $RunDirectory -ScratchRoot $taskScratch -RepositoryRoot $taskRepo -Build
    exit
}
if($Stage -eq 'ProductLegacy') {
    & (Join-Path $PSScriptRoot 'Run-LegacyProductInputs.ps1') -TaskRoot $taskRoot -RunDirectory $RunDirectory -ScratchRoot $taskScratch -RepositoryRoot $taskRepo -Build
    exit
}
if($Stage -eq 'ProductDiskNative') {
    & (Join-Path $PSScriptRoot 'Run-DiskCheckpointNative.ps1') -TaskRoot $taskRoot -RunDirectory $RunDirectory -ScratchRoot $taskScratch -RepositoryRoot $taskRepo
    exit
}
if($Stage -in @('ProductBackend','ProductShell','ProductImage')) {
    & (Join-Path $PSScriptRoot 'Run-ProductPublish.ps1') -Stage $Stage -TaskRoot $taskRoot -RunDirectory $RunDirectory -ScratchRoot $taskScratch -RepositoryRoot $taskRepo -LedgerBuildRoot $LedgerBuildRoot -ProductArtifactsPath $ProductArtifactsPath -NativeContextPath $NativeContextPath
    exit
}
$taskDiskInputs=& (Join-Path $PSScriptRoot 'Get-DiskCheckpointNativeInputs.ps1') -TaskRoot $taskRoot
if($taskExplicitTestProject) {
    # Derive the external-evidence names from the actual tests, including
    # custom Fact/Theory probes and constants; retain only presence flags.
    $taskExternalNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $taskTestSources=@(& rg --files tests/App -g '*.cs')
    if($LASTEXITCODE -ne 0){throw 'Could not enumerate test environment contracts.'}
    foreach($taskSource in $taskTestSources) {
        foreach($taskMatch in [regex]::Matches([IO.File]::ReadAllText((Join-Path $taskRepo $taskSource)), '"((?:RM_|RESOURCE_MANAGER_)[A-Z0-9_]+)"')) {
            [void]$taskExternalNames.Add($taskMatch.Groups[1].Value)
        }
    }
    $taskEnvironment=@(foreach($taskName in ($taskExternalNames|Sort-Object)) {
        $taskPresent=$null -ne [Environment]::GetEnvironmentVariable($taskName,'Process')
        [Environment]::SetEnvironmentVariable($taskName,[NullString]::Value,'Process')
        $taskAfterClear=[Environment]::GetEnvironmentVariable($taskName,'Process')
        $taskCleared=$null -eq $taskAfterClear
        if(-not $taskCleared){throw "External evidence environment variable was not deleted: $taskName"}
        [pscustomobject]@{name=$taskName;wasPresent=$taskPresent;cleared=$taskCleared;afterIsNull=($null -eq $taskAfterClear)}
    })
    # This is the runner's existing explicit helper-delivery selection, whose
    # tests use --runtime-check and never open hardware backends.
    $env:RM_CONTROL_HELPER_PACKAGE_DIRECTORY=Join-Path $taskRepo '.local/review-repairs-20261002/component-delivery-current'
    $env:RM_TEST_REPOSITORY_ROOT=$taskRepo
    # The scrub above removes ambient external evidence. Restore only the
    # scratch directory explicitly assigned to this bounded Job, not its
    # potentially overridden value from the test process environment.
    $env:RM_REVIEW_SCRATCH=$taskScratch
    [IO.File]::WriteAllText((Join-Path $RunDirectory 'test-environment.json'),(@{externalEvidence=$taskEnvironment;runnerHelperDeliverySelectionPreserved=$true;testRepositoryRoot=$taskRepo;runnerScratchRoot=$taskScratch}|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
} else {
    $env:RM_CONTROL_HELPER_PACKAGE_DIRECTORY=Join-Path $taskRepo '.local/review-repairs-20261002/component-delivery-current'
    $env:RM_TEST_REPOSITORY_ROOT=$taskRepo
}
function Invoke-Dotnet([string[]]$Arguments,[string]$Name) {
    $taskLog=Join-Path $RunDirectory ($Name+'.log')
    & $taskDotnet @Arguments *> $taskLog
    $taskExit=$LASTEXITCODE
    Get-Content -LiteralPath $taskLog -Tail 12
    if ($taskExit -ne 0) { throw "Command $Name failed: $taskExit" }
}
function Save-TestOutputEvidence([string]$TargetPath,[string]$Label,[string]$ReuseEvidencePath) {
    & (Join-Path $PSScriptRoot 'Save-AppTestOutputEvidence.ps1') -TargetPath $TargetPath -Label $Label -RunDirectory $RunDirectory -ReuseEvidencePath $ReuseEvidencePath
}
function Assert-PartitionInputs {
    if((Get-FileHash -LiteralPath $TestCompileManifest).Hash -cne $taskPartitionHash -or
        (Get-FileHash -LiteralPath $taskPartition.sourceListPath).Hash -cne $taskPartitionListHash -or
        (Get-FileHash -LiteralPath $taskPartition.originalCompileMetadata.path).Hash -ine [string]$taskPartition.originalCompileMetadata.sha256){throw 'Partition inputs changed during this stage.'}
}
$taskContextPath=Join-Path $taskRoot 'build-context.json'
$taskBuildContext=if($Stage -ne 'Build' -or [IO.File]::Exists($taskContextPath)){Get-Content $taskContextPath -Raw|ConvertFrom-Json}else{$null}
if($Stage -eq 'Tests' -and $taskBuildContext.PSObject.Properties.Name -contains 'testProject' -and
    [string]$taskBuildContext.testProject -ine $taskProject){throw 'Tests must select the same TestProject as the accepted build.'}
if($Stage -eq 'Tests' -and $taskBuildContext.PSObject.Properties.Name -contains 'nativeContextPath' -and
    [string]$taskBuildContext.nativeContextPath -ine $NativeContextPath){throw 'Tests must select the same NativeContextPath as the accepted build.'}
if($Stage -eq 'Tests' -and $taskDiskInputs){
    $diskContextPath=Join-Path $taskRoot 'disk-native-context.json'
    if($taskBuildContext.PSObject.Properties.Name -notcontains 'diskNativeContextPath' -or
        $taskBuildContext.diskNativeContextPath -ine $diskContextPath -or
        $taskBuildContext.diskNativeContextSha256 -ine (Get-FileHash -LiteralPath $diskContextPath).Hash -or
        $taskBuildContext.diskNativeRoot -ine $taskDiskInputs.context.artifactRoot){throw 'Tests must use the exact qualified Disk root/context of the accepted Build.'}
}
$taskArtifacts=if($Stage -eq 'Build'){Join-Path $taskScratch 'artifacts'}else{[string]$taskBuildContext.artifacts}
$taskReusedActiveArtifacts=$false
if($Stage -eq 'Build' -and [IO.File]::Exists($taskContextPath)) {
    $taskPrevious=Get-Content $taskContextPath -Raw|ConvertFrom-Json
    $taskPreviousArtifacts=[IO.Path]::GetFullPath([string]$taskPrevious.artifacts)
    $taskPreviousBatch=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($taskPreviousArtifacts))
    $taskPreviousMarker=Join-Path $taskPreviousBatch '.generated-workspace.json'
    $taskSameProject=$taskPrevious.PSObject.Properties.Name -notcontains 'testProject' -or [string]$taskPrevious.testProject -ieq $taskProject
    if($taskSameProject -and [IO.Directory]::Exists($taskPreviousArtifacts) -and [IO.File]::Exists($taskPreviousMarker)) {
        $taskPreviousState=Get-Content $taskPreviousMarker -Raw|ConvertFrom-Json
        if($taskPreviousState.state -eq 'running' -and
            [IO.Path]::GetFullPath([string]$taskPreviousState.workspacePath) -ieq $taskPreviousBatch -and
            $taskPreviousArtifacts -ieq (Join-Path $taskPreviousBatch 'scratch/artifacts')) {
            # Rebuild this active candidate in place. Original receipts/input
            # hashes stay in each run; completed output is never reopened.
            $taskArtifacts=$taskPreviousArtifacts
            $taskReusedActiveArtifacts=$true
        }
    }
}
# A standalone focused closure uses the original project and its real resources,
# but has no previous full-App candidate to reuse. Full-App partitions still do.
$taskFocusedCompile=$taskPartition -and ($taskPartition.PSObject.Properties.Name -contains 'scope') -and
    [string]$taskPartition.scope -ceq 'focused-compile-dependency-closure'
if($Stage -eq 'Build' -and $taskPartition -and -not $taskReusedActiveArtifacts -and -not $taskFocusedCompile){throw 'Partition Build requires the existing running incremental candidate artifacts.'}
if($Stage -eq 'Tests') {
    $taskContextHasPartition=$taskBuildContext.PSObject.Properties.Name -contains 'testCompileManifest' -and -not [string]::IsNullOrEmpty([string]$taskBuildContext.testCompileManifest)
    if($taskContextHasPartition -ne [bool]$taskPartition){throw 'Tests must use the same partition selection as the latest Build.'}
    if($taskPartition) {
        if(-not $taskBuildContext.buildSucceeded -or [string]$taskBuildContext.testCompileManifest -ine $TestCompileManifest -or
            [string]$taskBuildContext.testCompileManifestSha256 -cne $taskPartitionHash -or
            [string]$taskBuildContext.sourceListSha256 -cne $taskPartitionListHash){throw 'Tests require the latest successful Build of this exact partition.'}
        $taskBuildReceipt=Get-Content -LiteralPath (Join-Path $taskBuildContext.runDirectory 'bounded-result.json') -Raw|ConvertFrom-Json
        if(-not $taskBuildReceipt.accepted -or $taskBuildReceipt.stage -cne 'Build' -or
            [string]$taskBuildReceipt.testCompileManifestSha256 -cne $taskPartitionHash){throw 'The latest partition Build has no accepted bounded receipt.'}
        if((Get-FileHash -LiteralPath $taskBuildContext.testAssemblyPath).Hash -cne [string]$taskBuildContext.testAssemblySha256){throw 'The partition test DLL changed after Build.'}
        $taskBuildContextHash=(Get-FileHash -LiteralPath $taskContextPath).Hash
    }
}
$taskProperties=@(('-p:ArtifactsPath='+$taskArtifacts),'-p:BuildInParallel=false','-maxcpucount:1','-nodeReuse:false','-p:UseSharedCompilation=false','-p:NuGetAudit=false',
 '-p:SkipPerformanceOverlayNativeBuild=true','-p:SkipGpuPlacementNativeBuild=true','-p:SkipGpuPlacementPreparationNativeBuild=true','-p:SkipGpuLaunchBrokerNativeBuild=true',
 '-p:SkipAdlxBridgeBuild=true','-p:SkipNativeCoreBuild=true','-p:SkipGpuWindowActionNativeBuild=true','-p:SkipGpuPlacementExternalNativeBuild=true','-p:SkipAdapterNativeBuild=true','-p:SkipClientAppBuild=true',
 '-p:NativeCoreOptimize=ReleaseFast')
if([string]::IsNullOrWhiteSpace($NativeContextPath)) {
    $taskProperties+='-p:NativeCoreInstallRoot=D:\Code\Resource-Manager-Development\.local\review-repairs-third-20261009\native-prefix'
} else {
    $taskNativeContext=Get-Content -LiteralPath $NativeContextPath -Raw|ConvertFrom-Json
    if(-not $taskNativeContext.accepted -or @($taskNativeContext.outputs).Count -eq 0){throw 'Native context must contain accepted outputs.'}
    foreach($taskRow in @($taskNativeContext.outputs)+@($taskNativeContext.sourceInputsManifest)) {
        if((Get-FileHash -LiteralPath $taskRow.path).Hash -ine $taskRow.sha256){throw "Native context input changed: $($taskRow.path)"}
    }
    $taskAllowedNativeProperties=@('NativeCoreOutput','AdapterNativeRoot','AdapterNativeOutput','AdlxBridgeRoot',
        'GpuPlacementShimRoot','GpuLaunchBrokerRoot','GpuWindowActionRoot','GpuPlacementExternalRoot',
        'GpuPlacementPreparationRoot','PerformanceOverlayRoot','GpuMigrationProbeRoot','GpuLaunchTargetProbePath',
        'SkipNativeCoreBuild','SkipAdapterNativeBuild','SkipAdlxBridgeBuild','SkipGpuPlacementNativeBuild',
        'SkipGpuLaunchBrokerNativeBuild','SkipGpuWindowActionNativeBuild','SkipGpuPlacementExternalNativeBuild',
        'SkipGpuPlacementPreparationNativeBuild','SkipPerformanceOverlayNativeBuild')
    foreach($taskProperty in $taskNativeContext.properties.PSObject.Properties) {
        if($taskAllowedNativeProperties -notcontains $taskProperty.Name){throw "Unexpected Native context property: $($taskProperty.Name)"}
        if($taskProperty.Name.StartsWith('Skip',[StringComparison]::Ordinal) -and [string]$taskProperty.Value -cne 'true'){throw 'Native context cannot enable new builds.'}
        if(($taskProperty.Name.EndsWith('Output',[StringComparison]::Ordinal) -or $taskProperty.Name.EndsWith('Path',[StringComparison]::Ordinal)) -and
            @($taskNativeContext.outputs|Where-Object {[string]$_.path -ieq [string]$taskProperty.Value}).Count -ne 1){throw "Native output property is not bound to a verified output: $($taskProperty.Name)"}
        $taskProperties+=('-p:'+$taskProperty.Name+'='+[string]$taskProperty.Value)
    }
    if(@($taskNativeContext.outputs|Where-Object {[string]$_.path -ieq [string]$taskNativeContext.properties.NativeCoreOutput}).Count -ne 1){throw 'NativeCoreOutput is not bound to a verified output.'}
}
if($taskDiskInputs){
    $taskProperties+=@(('-p:DiskCheckpointNativeRoot='+$taskDiskInputs.context.artifactRoot),('-p:DiskCheckpointPowerShell='+[Environment]::ProcessPath))
}
if($taskPartition) {
    $taskCompileEvidence=Join-Path $RunDirectory 'compile-partition'
    $taskProperties+=@(('-p:CustomAfterMicrosoftCommonTargets='+(Join-Path $PSScriptRoot 'AppTestCompilePartition.targets')),
        ('-p:RMAppTestPartitionProject='+$taskProject),('-p:RMAppTestPartitionSources='+(Join-Path $RunDirectory 'requested-sources.txt')),
        ('-p:RMAppTestPartitionEvidence='+$taskCompileEvidence),'-p:BuildProjectReferences=true')
} else {
    $taskProperties+= '-p:CustomAfterMicrosoftCommonTargets=D:\Code\Resource-Manager-Development\scripts\validation\FreedomPointCompilerCapture.targets'
}
$taskProperties+= '-p:CompilerResponseFile=D:\Code\Resource-Manager-Development\scripts\validation\ProductSdk\serial-compiler.rsp'
$taskProperties+= @(('-p:ResourceManagerLedgerWorkRoot='+(Join-Path $taskRoot 'ledger-native')),
    ('-p:ResourceManagerLedgerPowerShell='+[Environment]::ProcessPath))
if(-not [string]::IsNullOrWhiteSpace($LedgerBuildRoot)){$taskProperties+=('-p:ResourceManagerLedgerBuildRoot='+[IO.Path]::GetFullPath($LedgerBuildRoot))}
$taskProperties+=@(('-p:RestoreConfigFile='+(Join-Path $taskRoot 'NuGet.official.config')),('-p:RestorePackagesPath='+(Join-Path $taskArtifacts 'packages')))
$taskLogPrefix=if($taskExplicitTestProject){'full-app'}else{'focused'}
if($Stage -eq 'Build') {
    $taskNewContext=[ordered]@{artifacts=$taskArtifacts;runDirectory=$RunDirectory;testProject=$taskProject;nativeContextPath=$NativeContextPath}
    if($taskDiskInputs){
        $taskNewContext.diskNativeContextPath=Join-Path $taskRoot 'disk-native-context.json'
        $taskNewContext.diskNativeContextSha256=(Get-FileHash -LiteralPath $taskNewContext.diskNativeContextPath).Hash
        $taskNewContext.diskNativeRoot=$taskDiskInputs.context.artifactRoot
    }
    $taskBuildArguments=@('build',$taskProject,'--configuration','Debug','--verbosity','minimal')
    if($taskPartition) {
        $taskNewContext.buildSucceeded=$false
        $taskNewContext.testCompileManifest=$TestCompileManifest
        $taskNewContext.testCompileManifestSha256=$taskPartitionHash
        # Invalidate the old success before overwriting the same assembly output.
        [IO.File]::WriteAllText($taskContextPath,($taskNewContext|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
        $taskDiagnostic=Join-Path $RunDirectory 'app-test-compiler.log'
        # Focused builds may start fresh or change a provider's package graph.
        # Full-App partitions retain their already-restored candidate contract.
        if(-not $taskFocusedCompile){$taskBuildArguments+='--no-restore'}
        $taskBuildArguments+=@(('-bl:'+(Join-Path $RunDirectory 'app-test-build.binlog')),
            ('-flp:LogFile='+$taskDiagnostic+';Verbosity=diagnostic'))
    }
    Invoke-Dotnet ($taskBuildArguments+$taskProperties) ($taskLogPrefix+'-build')
    if($taskPartition) {
        Assert-PartitionInputs
        $taskReferenceBuildProperty=@(Get-Content -LiteralPath (Join-Path $taskCompileEvidence 'properties.txt')|Where-Object {$_ -match '^BuildProjectReferences='})
        if($taskReferenceBuildProperty.Count -ne 1 -or $taskReferenceBuildProperty[0].Split('=',2)[1] -ine 'true'){throw 'The actual App compile must build its production project references.'}
        $taskSelected=@(Get-Content -LiteralPath (Join-Path $taskCompileEvidence 'selected-compile.txt'))
        foreach($taskSource in $taskPartition.sourceFiles){if($taskSelected -inotcontains $taskSource){throw "Requested source absent from actual Compile: $taskSource"}}
        $taskGenerated=@(Get-Content -LiteralPath (Join-Path $taskCompileEvidence 'generated-compile.txt'))
        foreach($taskSource in $taskSelected){if($taskPartition.sourceFiles -inotcontains $taskSource -and $taskGenerated -inotcontains $taskSource){throw "Unexpected source in actual Compile: $taskSource"}}
        $taskTarget=@(Get-Content -LiteralPath (Join-Path $taskCompileEvidence 'target-path.txt'))
        if($taskTarget.Count -ne 1){throw 'Build did not record exactly one test TargetPath.'}
        $taskOutput=@(Save-TestOutputEvidence $taskTarget[0] 'build')
        $taskAssembly=@($taskOutput|Where-Object {$_.exists -and $_.path -ieq $taskTarget[0]})[0]
        $taskSelectedHash=(Get-FileHash -LiteralPath (Join-Path $taskCompileEvidence 'selected-compile.txt')).Hash
        $taskCscInvoked=[bool](Select-String -LiteralPath $taskDiagnostic -Pattern '^\s*(?:Task "Csc"(?: \(TaskId:\d+\))?|任务“Csc”(?: \(任务 ID: \d+\))?)\s*$' -Quiet)
        $taskSamePriorSelection=$taskBuildContext -and $taskBuildContext.PSObject.Properties.Name -contains 'selectedCompileSha256' -and
            [string]$taskBuildContext.selectedCompileSha256 -ceq $taskSelectedHash
        if(-not $taskSamePriorSelection -and -not $taskCscInvoked){throw 'A new Compile partition must invoke Csc.'}
        $taskPriorSelectionChanged=$taskBuildContext -and $taskBuildContext.PSObject.Properties.Name -contains 'selectedCompileSha256' -and -not $taskSamePriorSelection
        if($taskPriorSelectionChanged -and (-not $taskCscInvoked -or [string]$taskBuildContext.testAssemblySha256 -ceq $taskAssembly.sha256)){throw 'A different Compile partition must invoke Csc and produce a different test DLL.'}
        $taskCompileRows=@(Get-ChildItem -LiteralPath $taskCompileEvidence -File|ForEach-Object {[pscustomobject]@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
        $taskNewContext.buildSucceeded=$true
        $taskNewContext.partitionId=[string]$taskPartition.partitionId
        $taskNewContext.sourceListPath=[string]$taskPartition.sourceListPath
        $taskNewContext.sourceListSha256=$taskPartitionListHash
        $taskNewContext.originalCompileMetadata=$taskPartition.originalCompileMetadata
        $taskNewContext.testAssemblyPath=$taskAssembly.path
        $taskNewContext.testAssemblySha256=$taskAssembly.sha256
        $taskNewContext.selectedCompileSha256=$taskSelectedHash
        $taskNewContext.compileEvidence=$taskCompileRows
        $taskNewContext.cscInvoked=$taskCscInvoked
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'partition-build.json'),($taskNewContext|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    } else {
        Invoke-Dotnet (@('build','src/Development/FreedomPointCheck/ResourceManager.FreedomPointCheck.csproj','--configuration','Debug','--verbosity','minimal')+$taskProperties) 'checker-build'
    }
    [IO.File]::WriteAllText($taskContextPath,($taskNewContext|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
} elseif($Stage -eq 'Tests') {
    $taskRuntimeSelection=$null
    if(-not [string]::IsNullOrWhiteSpace($TestRuntimeManifest) -or -not [string]::IsNullOrWhiteSpace($TestRuntimeGroupId)) {
        if(-not $taskPartition -or [string]::IsNullOrWhiteSpace($TestRuntimeManifest) -or [string]::IsNullOrWhiteSpace($TestRuntimeGroupId)){throw 'Runtime grouping requires both runtime parameters and an exact compile partition.'}
        $taskRuntimeSelection=& (Join-Path $PSScriptRoot 'Get-AppTestRuntimeSelection.ps1') -ManifestPath $TestRuntimeManifest -GroupId $TestRuntimeGroupId -CompileManifestPath $TestCompileManifest -BuildContextPath $taskContextPath
        Copy-Item -LiteralPath $TestRuntimeManifest -Destination (Join-Path $RunDirectory 'test-runtime-manifest.json')
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'test-runtime-selection.json'),($taskRuntimeSelection|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    }
    if($taskPartition){[void](Save-TestOutputEvidence $taskBuildContext.testAssemblyPath 'before-tests' (Join-Path $taskBuildContext.runDirectory 'test-output-build.json'))}
    try {
        if($taskRuntimeSelection) {
            # The exact assembly was already built and sealed. Invoke the same
            # VSTest runner directly so an idle MSBuild host does not consume the
            # bounded runtime Job alongside a large payload test.
            $taskTestArguments=@('vstest',$taskBuildContext.testAssemblyPath,
                ('/Settings:'+(Join-Path $taskRepo '.local/backend-information-collection-20261004/serial.runsettings')),
                ('/ResultsDirectory:'+$RunDirectory),('/Logger:trx;LogFileName='+$taskLogPrefix+'.trx'),
                ('/TestCaseFilter:'+$taskRuntimeSelection.filter))
        } else {
            $taskTestArguments=@('test',$taskProject,'--configuration','Debug','--no-build','--no-restore','--settings','.local/backend-information-collection-20261004/serial.runsettings','--results-directory',$RunDirectory,'--logger',('trx;LogFileName='+$taskLogPrefix+'.trx'))+$taskProperties
        }
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'test-command.json'),(@{executable=$taskDotnet;arguments=$taskTestArguments;workingDirectory=$taskRepo}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
        Invoke-Dotnet $taskTestArguments ($taskLogPrefix+'-tests')
    } finally {
        if($taskRuntimeSelection) {
            $taskRuntimeAfter=& (Join-Path $PSScriptRoot 'Get-AppTestRuntimeSelection.ps1') -ManifestPath $TestRuntimeManifest -GroupId $TestRuntimeGroupId -CompileManifestPath $TestCompileManifest -BuildContextPath $taskContextPath
            if($taskRuntimeAfter.runtimeManifestSha256 -cne $taskRuntimeSelection.runtimeManifestSha256 -or
                (Get-FileHash -LiteralPath (Join-Path $RunDirectory 'test-runtime-manifest.json')).Hash -cne $taskRuntimeSelection.runtimeManifestSha256){throw 'Runtime selection changed during Tests.'}
        }
        if($taskPartition) {
            [void](Save-TestOutputEvidence $taskBuildContext.testAssemblyPath 'after-tests' (Join-Path $taskBuildContext.runDirectory 'test-output-build.json'))
            Assert-PartitionInputs
            if((Get-FileHash -LiteralPath $taskContextPath).Hash -cne $taskBuildContextHash -or
                (Get-FileHash -LiteralPath $taskBuildContext.testAssemblyPath).Hash -cne [string]$taskBuildContext.testAssemblySha256){throw 'The latest Build context or test DLL changed during Tests.'}
            [IO.File]::WriteAllText((Join-Path $RunDirectory 'partition-tests-build.json'),($taskBuildContext|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
        }
    }
} elseif($Stage -in @('Pack','Consumers')) {
    & (Join-Path $PSScriptRoot 'Run-PackageStages.ps1') -Stage $Stage -TaskRoot $taskRoot -RunDirectory $RunDirectory -ArtifactsPath $taskArtifacts -ScratchRoot $taskScratch -BuildProperties $taskProperties
} elseif($Stage -eq 'DefaultRestore') {
    $taskDefaultProperties=@($taskProperties|Where-Object {$_ -notlike '-p:RestoreConfigFile=*' -and $_ -notlike '-p:RestorePackagesPath=*' -and $_ -notlike '-p:ArtifactsPath=*'})
    $taskDefaultProperties+= '-p:ArtifactsPath='+ (Join-Path $taskScratch 'default-artifacts')
    Invoke-Dotnet (@('restore','src/Development/FreedomPointCheck/ResourceManager.FreedomPointCheck.csproj','--configfile','NuGet.Config','--verbosity','minimal')+$taskDefaultProperties) 'default-restore'
    [IO.File]::WriteAllText((Join-Path $taskRoot 'default-restore-context.json'),(@{artifacts=(Join-Path $taskScratch 'default-artifacts');runDirectory=$RunDirectory}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
} else {
    $taskContext=Join-Path $RunDirectory 'compiler-context.json'
    & (Join-Path $taskRepo 'scripts/validation/Get-FreedomPointCompilerContext.ps1') -RepositoryRoot $taskRepo -ArtifactsPath $taskArtifacts -OutputPath $taskContext -BuildProperties $taskProperties -Dotnet $taskDotnet
    if($Stage -eq 'Incomplete'){
        $taskInvalid=Get-Content $taskContext -Raw|ConvertFrom-Json
        $taskInvalid.projects[2].Properties.OutputType='Library'
        [IO.File]::WriteAllText($taskContext,($taskInvalid|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
    }
    $taskChecker=Join-Path $taskArtifacts 'bin/ResourceManager.FreedomPointCheck/debug/ResourceManager.FreedomPointCheck.dll'
    $taskOutput=Join-Path $taskRoot 'sources'
    if($Stage -eq 'Sources') {
        Invoke-Dotnet (@('build','src/Development/FreedomPointCheck/ResourceManager.FreedomPointCheck.csproj','--configuration','Debug','--verbosity','minimal')+$taskProperties) 'checker-build'
        $taskCheckerInfo=Get-Item -LiteralPath $taskChecker
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'checker-build-output.json'),(@{stage='Sources';runDirectory=$RunDirectory;binaryPath=$taskCheckerInfo.FullName;length=$taskCheckerInfo.Length;sha256=(Get-FileHash -LiteralPath $taskCheckerInfo.FullName).Hash}|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
        try {
            Invoke-Dotnet @($taskChecker,$taskRepo,$taskContext,$taskOutput,'--refresh-partition-contract') 'sources'
        } finally {
            # A real diagnostic is part of the failed attempt as well. A later
            # successful run must not overwrite its only structured evidence.
            $taskSourceResult=Join-Path $taskOutput 'source-result.json'
            if([IO.File]::Exists($taskSourceResult)) {
                Copy-Item -LiteralPath $taskSourceResult -Destination (Join-Path $RunDirectory 'source-result.json')
            }
        }
    } else {
        $taskIndex=Join-Path $taskOutput 'source-index.json'
        $taskBefore=(Get-FileHash -LiteralPath $taskIndex).Hash
        & $taskDotnet $taskChecker $taskRepo $taskContext $taskOutput *> (Join-Path $RunDirectory 'incomplete.log')
        $taskRejected=$LASTEXITCODE -ne 0
        $taskResult=Get-Content (Join-Path $taskOutput 'source-result.json') -Raw|ConvertFrom-Json
        $taskSame=(Get-FileHash -LiteralPath $taskIndex).Hash -ceq $taskBefore
        Copy-Item -LiteralPath (Join-Path $taskOutput 'source-result.json') -Destination (Join-Path $RunDirectory 'source-result.json')
        if(!$taskRejected -or !$taskSame -or $taskResult.passed -or $taskResult.status -cne 'incomplete'){throw 'Incomplete compiler context did not reject and preserve complete index.'}
        [IO.File]::WriteAllText((Join-Path $RunDirectory 'negative-acceptance.json'),(@{rejected=$taskRejected;completeIndexPreserved=$taskSame;sha256=$taskBefore}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    }
}
