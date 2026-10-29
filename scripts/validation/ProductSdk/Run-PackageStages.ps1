param(
    [Parameter(Mandatory)][ValidateSet('Pack','Consumers')][string]$Stage,
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$ScratchRoot,
    [Parameter(Mandatory)][string[]]$BuildProperties)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($env:RM_REVIEW_SCRATCH) -or
    [IO.Path]::GetFullPath($env:RM_REVIEW_SCRATCH) -ine [IO.Path]::GetFullPath($ScratchRoot)){throw 'Use the bounded entry.'}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$dotnet='C:\Program Files\dotnet\dotnet.exe'
$packageProfile=Get-Content (Join-Path $TaskRoot 'package-prerequisites.json') -Raw|ConvertFrom-Json
if($packageProfile.schema -cne 'resource-manager/package-prerequisites/v1'){throw 'Unknown package validation profile'}
$utf8=[Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 12),$utf8)}
function Assert-Hash([string]$Path,[string]$Expected) {
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected){throw "Input/payload changed: $Path"}
}
function Assert-Prerequisites {
    Assert-Hash $packageProfile.baselinePackage.path $packageProfile.baselinePackage.sha256
    foreach($row in $packageProfile.nativeSourceInputs){Assert-Hash (Join-Path $repo $row.path) $row.sha256}
    foreach($row in $packageProfile.nativeAssets){Assert-Hash $row.path $row.sha256}
}
function Zip-Hash([string]$Package,[string]$Entry) {
    $zip=[IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $item=$zip.GetEntry($Entry)
        if($null -eq $item){throw "Missing package entry: $Entry"}
        $stream=$item.Open()
        try{return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
    }finally{$zip.Dispose()}
}
function Invoke-Dotnet([string[]]$Arguments,[string]$Name) {
    $log=Join-Path $RunDirectory ($Name+'.log')
    & $dotnet @Arguments *> $log
    $code=$LASTEXITCODE
    Get-Content $log -Tail 10
    if($code -ne 0){throw "Package stage $Name failed: $code"}
}
Assert-Prerequisites
$versions=@{ReadWriteCore=[string]$packageProfile.readWriteVersion;SubscriptionCore=[string]$packageProfile.subscriptionVersion}
$contextPath=Join-Path $TaskRoot 'package-context.json'
if($Stage -eq 'Pack') {
    $buildContextPath=Join-Path $TaskRoot 'build-context.json'
    if(-not [IO.File]::Exists($buildContextPath)){throw 'Pack requires the accepted Build context.'}
    $buildContext=Get-Content -LiteralPath $buildContextPath -Raw|ConvertFrom-Json
    $buildArtifacts=[IO.Path]::GetFullPath([string]$buildContext.artifacts).TrimEnd('\')
    $packArtifacts=[IO.Path]::GetFullPath($ArtifactsPath).TrimEnd('\')
    if($buildArtifacts -ine $packArtifacts){throw 'Pack ArtifactsPath differs from the accepted Build context.'}
    $buildRunDirectory=[IO.Path]::GetFullPath([string]$buildContext.runDirectory)
    $buildReceiptPath=Join-Path $buildRunDirectory 'bounded-result.json'
    if(-not [IO.File]::Exists($buildReceiptPath)){throw 'Pack requires the preceding Build bounded receipt.'}
    $buildReceipt=Get-Content -LiteralPath $buildReceiptPath -Raw|ConvertFrom-Json
    if(-not $buildReceipt.accepted -or $buildReceipt.stage -cne 'Build' -or [int]$buildReceipt.exitCode -ne 0 -or
        -not $buildReceipt.normalDrain -or [IO.Path]::GetFullPath([string]$buildReceipt.runDirectory) -ine $buildRunDirectory){
        throw 'Pack requires an accepted Build receipt bound to the current Build context.'
    }
    $buildBinding=[pscustomobject]@{
        buildContextPath=$buildContextPath;buildContextSha256=(Get-FileHash -LiteralPath $buildContextPath -Algorithm SHA256).Hash
        artifactsPath=$buildArtifacts;runDirectory=$buildRunDirectory;receiptPath=$buildReceiptPath
        receiptSha256=(Get-FileHash -LiteralPath $buildReceiptPath -Algorithm SHA256).Hash
    }
    $feed=Join-Path $ScratchRoot 'packages'
    [void][IO.Directory]::CreateDirectory($feed)
    if(@([IO.Directory]::EnumerateFileSystemEntries($feed)).Count -ne 0){throw 'Package feed must be fresh and empty'}
    $packages=[Collections.Generic.List[object]]::new()
    foreach($name in @('ReadWriteCore','SubscriptionCore')) {
        $arguments=@('pack',('src/'+$name+'/ResourceManager.'+$name+'.csproj'),'--configuration','Debug','--no-build','--no-restore','--output',$feed)+$BuildProperties
        if($name -eq 'ReadWriteCore') {
            foreach($pair in @(@('ReadWriteNativeLibraryPath','ResourceManager.NativeCore.dll'),@('ReadWriteAdlxLibraryPath','ResourceManager.AdlxBridge.dll'),@('ReadWriteDumpAnalysisHelperPath','ResourceManager.DumpAnalysis.exe'))) {
                $asset=@($packageProfile.nativeAssets|Where-Object {[IO.Path]::GetFileName($_.path) -ceq $pair[1]})
                if($asset.Count -ne 1){throw 'Ambiguous Native package prerequisite'}
                $arguments+=('-p:'+$pair[0]+'='+$asset[0].path)
            }
        }
        Invoke-Dotnet $arguments ('pack-'+$name)
        $path=Join-Path $feed ('ResourceManager.'+$name+'.'+$versions[$name]+'.nupkg')
        $managedEntry='lib/net10.0/ResourceManager.'+$name+'.dll'
        $managed=Join-Path $ArtifactsPath ('bin/ResourceManager.'+$name+'/debug/ResourceManager.'+$name+'.dll')
        $managedHash=Zip-Hash $path $managedEntry
        Assert-Hash $managed $managedHash
        $native=[Collections.Generic.List[object]]::new()
        if($name -eq 'ReadWriteCore') {
            foreach($asset in $packageProfile.nativeAssets) {
                $hash=Zip-Hash $path $asset.packageEntry
                if($hash -ine $asset.sha256){throw 'Pack changed a verified Native asset'}
                $native.Add([pscustomobject]@{entry=$asset.packageEntry;sha256=$hash})
            }
        }
        $packages.Add([pscustomobject]@{name=$name;version=$versions[$name];path=$path;sha256=(Get-FileHash $path).Hash;managedEntry=$managedEntry;managedSha256=$managedHash;nativeAssets=@($native)})
    }
    Assert-Hash $buildBinding.buildContextPath $buildBinding.buildContextSha256
    Assert-Hash $buildBinding.receiptPath $buildBinding.receiptSha256
    $result=[pscustomobject]@{runDirectory=$RunDirectory;feed=$feed;freshFeed=$true;buildBinding=$buildBinding;packages=@($packages)}
    Write-Json $contextPath $result
    Write-Json (Join-Path $RunDirectory 'package-pairing.json') $result
}else {
    $context=Get-Content $contextPath -Raw|ConvertFrom-Json
    $buildContextPath=Join-Path $TaskRoot 'build-context.json'
    if(-not $context.buildBinding -or -not [IO.File]::Exists($buildContextPath)){throw 'Consumers requires the Pack binding to an accepted Build.'}
    $buildContext=Get-Content -LiteralPath $buildContextPath -Raw|ConvertFrom-Json
    $buildArtifacts=[IO.Path]::GetFullPath([string]$buildContext.artifacts).TrimEnd('\')
    $buildRunDirectory=[IO.Path]::GetFullPath([string]$buildContext.runDirectory)
    $buildReceiptPath=Join-Path $buildRunDirectory 'bounded-result.json'
    if([string]$context.buildBinding.buildContextPath -ine $buildContextPath -or
        [string]$context.buildBinding.buildContextSha256 -ine (Get-FileHash -LiteralPath $buildContextPath -Algorithm SHA256).Hash -or
        [string]$context.buildBinding.artifactsPath -ine $buildArtifacts -or
        [string]$context.buildBinding.runDirectory -ine $buildRunDirectory -or
        [string]$context.buildBinding.receiptPath -ine $buildReceiptPath -or
        -not [IO.File]::Exists($buildReceiptPath) -or
        [string]$context.buildBinding.receiptSha256 -ine (Get-FileHash -LiteralPath $buildReceiptPath -Algorithm SHA256).Hash) {
        throw 'The accepted Build binding changed between Pack and Consumers.'
    }
    $buildReceipt=Get-Content -LiteralPath $buildReceiptPath -Raw|ConvertFrom-Json
    if(-not $buildReceipt.accepted -or $buildReceipt.stage -cne 'Build' -or [int]$buildReceipt.exitCode -ne 0 -or
        -not $buildReceipt.normalDrain -or [IO.Path]::GetFullPath([string]$buildReceipt.runDirectory) -ine $buildRunDirectory) {
        throw 'Consumers requires the same accepted Build receipt used by Pack.'
    }
    foreach($package in $context.packages){Assert-Hash $package.path $package.sha256}
    $compileManifestPath=Join-Path $RunDirectory 'compile-inputs.json'
    if(-not [IO.File]::Exists($compileManifestPath)){throw 'Consumers requires the pre-stage evaluated Compile manifest.'}
    $compileManifest=Get-Content -LiteralPath $compileManifestPath -Raw|ConvertFrom-Json
    if($compileManifest.schema -cne 'rm/product-sdk-evaluated-compile-inputs/v2' -or
        -not [IO.File]::Exists([string]$compileManifest.captureTargetsPath)){throw 'Invalid evaluated Compile manifest or CoreCompile capture target.'}
    $config=[xml](Get-Content (Join-Path $TaskRoot 'NuGet.official.config') -Raw)
    $mappingRoot=$config.SelectSingleNode('/configuration/packageSourceMapping')
    if($null -eq $mappingRoot) {
        $mappingRoot=$config.CreateElement('packageSourceMapping')
        [void]$config.DocumentElement.AppendChild($mappingRoot)
        # An unmapped input config allows dependencies from every configured source.
        # Keep that coverage; the exact candidate IDs below take precedence over '*'.
        foreach($existingSource in $config.SelectNodes('/configuration/packageSources/add')) {
            $fallback=$config.CreateElement('packageSource');$fallback.SetAttribute('key',$existingSource.GetAttribute('key'))
            $wildcard=$config.CreateElement('package');$wildcard.SetAttribute('pattern','*')
            [void]$fallback.AppendChild($wildcard);[void]$mappingRoot.AppendChild($fallback)
        }
    }
    $source=$config.CreateElement('add');$source.SetAttribute('key','candidate-backend');$source.SetAttribute('value',[string]$context.feed)
    [void]$config.configuration.packageSources.AppendChild($source)
    $mapping=$config.CreateElement('packageSource');$mapping.SetAttribute('key','candidate-backend')
    foreach($name in @('ResourceManager.ReadWriteCore','ResourceManager.SubscriptionCore')){
        $entry=$config.CreateElement('package');$entry.SetAttribute('pattern',$name);[void]$mapping.AppendChild($entry)
    }
    [void]$mappingRoot.AppendChild($mapping)
    $configPath=Join-Path $RunDirectory 'consumer.NuGet.config';$config.Save($configPath)
    # Candidate packages may intentionally keep their version between local builds.
    # A shared global cache can otherwise restore an earlier package with that version.
    $consumerPackages=Join-Path $ScratchRoot 'consumer-packages'
    if([IO.Directory]::Exists($consumerPackages) -and
        @([IO.Directory]::EnumerateFileSystemEntries($consumerPackages)).Count -ne 0){throw 'Consumer package cache must be fresh and empty'}
    [void][IO.Directory]::CreateDirectory($consumerPackages)
    $outputs=[Collections.Generic.List[object]]::new()
    $compileProjects=[Collections.Generic.List[object]]::new()
    $compileInputs=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($name in @('ReadWriteCore','SubscriptionCore')) {
        $output=Join-Path $ScratchRoot ('consumer-'+$name)
        $properties=@($BuildProperties|Where-Object {
            $_ -notlike '-p:ArtifactsPath=*' -and $_ -notlike '-p:RestoreConfigFile=*' -and
            $_ -notlike '-p:RestorePackagesPath=*' -and $_ -notlike '-p:CustomAfterMicrosoftCommonTargets=*'
        })
        $properties+=@(('-p:ArtifactsPath='+$output),('-p:RestoreConfigFile='+$configPath),('-p:RestorePackagesPath='+$consumerPackages))
        $capturePath=Join-Path $RunDirectory ('compile-inputs-actual-'+$name+'.txt')
        $captureHashPath=Join-Path $RunDirectory ('compile-inputs-hashes-'+$name+'.txt')
        $capturePropertiesPath=Join-Path $RunDirectory ('compile-inputs-properties-'+$name+'.txt')
        $properties+=@(
            ('-p:CustomAfterMicrosoftCommonTargets='+[string]$compileManifest.captureTargetsPath),
            ('-p:RMProductSdkCompileInputCapturePath='+$capturePath),
            ('-p:RMProductSdkCompileInputCaptureHashPath='+$captureHashPath),
            ('-p:RMProductSdkCompileInputCapturePropertiesPath='+$capturePropertiesPath)
        )
        Invoke-Dotnet (@('build',('tests/'+$name+'/PackageConsumer/PackageConsumer.csproj'),'--configuration','Debug','--verbosity','minimal')+$properties) ('consumer-build-'+$name)
        if(-not [IO.File]::Exists($capturePath) -or -not [IO.File]::Exists($captureHashPath) -or -not [IO.File]::Exists($capturePropertiesPath)){throw "The actual CoreCompile input capture is missing for $name."}
        $capturedPaths=@(Get-Content -LiteralPath $capturePath|Where-Object {-not [string]::IsNullOrWhiteSpace($_)})
        $capturedHashes=@(Get-Content -LiteralPath $captureHashPath|Where-Object {-not [string]::IsNullOrWhiteSpace($_)})
        if($capturedPaths.Count -eq 0 -or $capturedPaths.Count -gt 8192 -or $capturedHashes.Count -ne $capturedPaths.Count){throw "The actual CoreCompile input set or hash count is invalid for $name."}
        $projectRows=[Collections.Generic.List[object]]::new()
        $expectedProjects=@($compileManifest.projects|Where-Object projectName -CEQ $name)
        if($expectedProjects.Count -ne 1){throw "The evaluated Compile project is ambiguous for $name."}
        $evaluatedItems=@($compileManifest.items|Where-Object projectName -CEQ $name)
        $captureArtifacts=[IO.Path]::GetFullPath($output).TrimEnd('\')+[IO.Path]::DirectorySeparatorChar
        for($captureIndex=0;$captureIndex -lt $capturedPaths.Count;$captureIndex++) {
            $capturedPath=[IO.Path]::GetFullPath([string]$capturedPaths[$captureIndex])
            if(-not [IO.File]::Exists($capturedPath)){throw "CoreCompile captured a missing source for $($name): $capturedPath"}
            $hash=[string]$capturedHashes[$captureIndex]
            if($hash -notmatch '^[0-9A-Fa-f]{64}$'){throw "CoreCompile returned an invalid SHA-256 for $($name): $capturedPath"}
            $metadata=@($evaluatedItems|Where-Object {[string]$_.fullPath -ieq $capturedPath}|ForEach-Object {
                [pscustomobject]@{identity=$_.identity;link=$_.link;definingProjectFullPath=$_.definingProjectFullPath;generated=$_.generated;autoGen=$_.autoGen;designTime=$_.designTime;generatedMetadata=$_.generatedMetadata}
            })
            $generatedUnderArtifacts=$capturedPath.StartsWith($captureArtifacts,[StringComparison]::OrdinalIgnoreCase)
            $row=[pscustomobject]@{
                path=$capturedPath;sha256=$hash;evaluatedBeforeStage=($metadata.Count -gt 0);lateAddedToCompile=($metadata.Count -eq 0)
                generatedUnderArtifacts=$generatedUnderArtifacts;generated=($generatedUnderArtifacts -or @($metadata|Where-Object generated).Count -gt 0)
                evaluatedMetadata=$metadata
            }
            $projectRows.Add($row)
            if(-not $compileInputs.ContainsKey($capturedPath)){$compileInputs.Add($capturedPath,[pscustomobject]@{path=$capturedPath;sha256=$hash})}
        }
        $propertiesUsed=@(Get-Content -LiteralPath $capturePropertiesPath)
        if($propertiesUsed.Count -ne 4){throw "The actual CoreCompile property record is invalid for $name."}
        $propertyValues=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        # WriteLinesToFile receives an MSBuild item list: one property per line.
        foreach($property in $propertiesUsed) {
            $pair=$property.Split('=',2)
            if($pair.Count -ne 2 -or $pair[0] -cnotin @('Configuration','TargetFramework','RuntimeIdentifier','ArtifactsPath') -or $propertyValues.ContainsKey($pair[0])){throw "The actual CoreCompile property record is malformed for $name."}
            $propertyValues.Add($pair[0],$pair[1])
        }
        $expectedProject=$expectedProjects[0]
        if($propertyValues['Configuration'] -cne [string]$expectedProject.configuration -or
            $propertyValues['TargetFramework'] -cne [string]$expectedProject.targetFramework -or
            $propertyValues['RuntimeIdentifier'] -cne [string]$expectedProject.runtimeIdentifier -or
            [IO.Path]::GetFullPath($propertyValues['ArtifactsPath']).TrimEnd('\') -ine [IO.Path]::GetFullPath($output).TrimEnd('\')) {
            throw "The actual CoreCompile properties differ from the pre-stage evaluated project for $name."
        }
        $compileProjects.Add([pscustomobject]@{name=$name;artifactsPath=$output;actualCompileItemCount=$projectRows.Count;properties=$propertiesUsed;items=@($projectRows)})
        $assets=Get-Content -LiteralPath (Join-Path $output 'obj/PackageConsumer/project.assets.json') -Raw|ConvertFrom-Json
        $folders=@($assets.packageFolders.PSObject.Properties.Name)
        if($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('\') -ine
            [IO.Path]::GetFullPath($consumerPackages).TrimEnd('\')){throw 'Consumer restore did not use its exact isolated package cache'}
        $binary=Join-Path $output 'bin/PackageConsumer/debug/PackageConsumer.dll'
        foreach($package in $context.packages) {
            if($name -eq 'ReadWriteCore' -and $package.name -eq 'SubscriptionCore'){continue}
            $packageId=('ResourceManager.'+$package.name).ToLowerInvariant()
            $installedPackage=Join-Path $consumerPackages ($packageId+'/'+$package.version+'/'+$packageId+'.'+$package.version+'.nupkg')
            Assert-Hash $installedPackage $package.sha256
            $dll=Join-Path $output ('bin/PackageConsumer/debug/ResourceManager.'+$package.name+'.dll')
            Assert-Hash $dll $package.managedSha256
            foreach($asset in $package.nativeAssets){
                $assetPath=Join-Path $output ('bin/PackageConsumer/debug/'+$asset.entry)
                Assert-Hash $assetPath $asset.sha256
            }
        }
        Invoke-Dotnet (@($binary)+@($packageProfile.consumerArguments)) ('consumer-run-'+$name)
        $outputs.Add([pscustomobject]@{name=$name;binary=$binary;sha256=(Get-FileHash $binary).Hash;actualPackageBytesMatched=$true;actualPackageArchivesMatched=$true;restorePackagesPath=$consumerPackages;actualCompileItemCount=$projectRows.Count;arguments=@($packageProfile.consumerArguments)})
    }
    $actualCompileManifest=[ordered]@{
        schema='rm/product-sdk-corecompile-inputs/v1';evaluatedManifestPath=$compileManifestPath
        evaluatedManifestSha256=(Get-FileHash -LiteralPath $compileManifestPath -Algorithm SHA256).Hash
        captureTargetsPath=[string]$compileManifest.captureTargetsPath;projects=@($compileProjects)
        compileItemCount=@($compileProjects|Measure-Object -Property actualCompileItemCount -Sum).Sum
        uniqueInputCount=$compileInputs.Count;inputs=@($compileInputs.Values|Sort-Object path)
    }
    $actualCompileManifestPath=Join-Path $RunDirectory 'compile-inputs-actual.json'
    Write-Json $actualCompileManifestPath $actualCompileManifest
    Assert-Hash $context.buildBinding.buildContextPath $context.buildBinding.buildContextSha256
    Assert-Hash $context.buildBinding.receiptPath $context.buildBinding.receiptSha256
    Write-Json (Join-Path $RunDirectory 'consumer-acceptance.json') ([pscustomobject]@{
        accepted=$true;profile='recorded actual public package consumers';packageContext=$contextPath
        buildBinding=$context.buildBinding;consumers=@($outputs);compileInputManifest=$actualCompileManifestPath
        compileInputManifestSha256=(Get-FileHash -LiteralPath $actualCompileManifestPath -Algorithm SHA256).Hash
        hardwareLive=$false
    })
}
Assert-Prerequisites
