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
$repo='D:\Code\Resource-Manager-Development'
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
    $result=[pscustomobject]@{runDirectory=$RunDirectory;feed=$feed;freshFeed=$true;packages=@($packages)}
    Write-Json $contextPath $result
    Write-Json (Join-Path $RunDirectory 'package-pairing.json') $result
}else {
    $context=Get-Content $contextPath -Raw|ConvertFrom-Json
    foreach($package in $context.packages){Assert-Hash $package.path $package.sha256}
    $config=[xml](Get-Content (Join-Path $TaskRoot 'NuGet.official.config') -Raw)
    $source=$config.CreateElement('add');$source.SetAttribute('key','candidate-backend');$source.SetAttribute('value',[string]$context.feed)
    [void]$config.configuration.packageSources.AppendChild($source)
    $mapping=$config.CreateElement('packageSource');$mapping.SetAttribute('key','candidate-backend')
    foreach($name in @('ResourceManager.ReadWriteCore','ResourceManager.SubscriptionCore')){
        $entry=$config.CreateElement('package');$entry.SetAttribute('pattern',$name);[void]$mapping.AppendChild($entry)
    }
    [void]$config.configuration.packageSourceMapping.AppendChild($mapping)
    $configPath=Join-Path $RunDirectory 'consumer.NuGet.config';$config.Save($configPath)
    # Candidate packages may intentionally keep their version between local builds.
    # A shared global cache can otherwise restore an earlier package with that version.
    $consumerPackages=Join-Path $ScratchRoot 'consumer-packages'
    if([IO.Directory]::Exists($consumerPackages) -and
        @([IO.Directory]::EnumerateFileSystemEntries($consumerPackages)).Count -ne 0){throw 'Consumer package cache must be fresh and empty'}
    [void][IO.Directory]::CreateDirectory($consumerPackages)
    $outputs=[Collections.Generic.List[object]]::new()
    foreach($name in @('ReadWriteCore','SubscriptionCore')) {
        $output=Join-Path $ScratchRoot ('consumer-'+$name)
        $properties=@($BuildProperties|Where-Object {$_ -notlike '-p:ArtifactsPath=*' -and $_ -notlike '-p:RestoreConfigFile=*' -and $_ -notlike '-p:RestorePackagesPath=*'})
        $properties+=@(('-p:ArtifactsPath='+$output),('-p:RestoreConfigFile='+$configPath),('-p:RestorePackagesPath='+$consumerPackages))
        Invoke-Dotnet (@('build',('tests/'+$name+'/PackageConsumer/PackageConsumer.csproj'),'--configuration','Debug','--verbosity','minimal')+$properties) ('consumer-build-'+$name)
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
        $outputs.Add([pscustomobject]@{name=$name;binary=$binary;sha256=(Get-FileHash $binary).Hash;actualPackageBytesMatched=$true;actualPackageArchivesMatched=$true;restorePackagesPath=$consumerPackages;arguments=@($packageProfile.consumerArguments)})
    }
    Write-Json (Join-Path $RunDirectory 'consumer-acceptance.json') ([pscustomobject]@{accepted=$true;profile='recorded actual public package consumers';packageContext=$contextPath;consumers=@($outputs);hardwareLive=$false})
}
Assert-Prerequisites
