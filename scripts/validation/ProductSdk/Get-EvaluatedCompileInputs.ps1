[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Sources','Pack','Consumers')][string]$Stage,
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$TaskRoot,
    [Parameter(Mandatory)][string]$ScratchRoot,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string[]]$BuildProperties,
    [string]$Dotnet='C:\Program Files\dotnet\dotnet.exe'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

$repo=[IO.Path]::GetFullPath($RepositoryRoot)
$task=[IO.Path]::GetFullPath($TaskRoot)
$scratch=[IO.Path]::GetFullPath($ScratchRoot)
$artifacts=[IO.Path]::GetFullPath($ArtifactsPath)
$output=[IO.Path]::GetFullPath($OutputPath)
if(-not [IO.File]::Exists($Dotnet)){throw "dotnet is missing: $Dotnet"}
if(-not [IO.Directory]::Exists($repo) -or -not [IO.Directory]::Exists($scratch)){throw 'RepositoryRoot and ScratchRoot must be existing directories.'}

$projectPaths=switch($Stage) {
    'Sources' { @('src/ReadWriteCore/ResourceManager.ReadWriteCore.csproj','src/SubscriptionCore/ResourceManager.SubscriptionCore.csproj','src/Core/ResourceManager.App.csproj') }
    'Pack' { @('src/ReadWriteCore/ResourceManager.ReadWriteCore.csproj','src/SubscriptionCore/ResourceManager.SubscriptionCore.csproj') }
    'Consumers' { @('tests/ReadWriteCore/PackageConsumer/PackageConsumer.csproj','tests/SubscriptionCore/PackageConsumer/PackageConsumer.csproj') }
}
if($projectPaths.Count -eq 0 -or $projectPaths.Count -gt 8){throw 'Compile project count is outside the supported bound.'}

# Import the same compiler-capture targets used by Run-Stage, then add a target
# that records the final Compile items at the actual CoreCompile boundary.
$existingCapture=Join-Path $repo 'scripts/validation/FreedomPointCompilerCapture.targets'
$captureTargets=$output+'.targets'
$captureImport=[Security.SecurityElement]::Escape($existingCapture)
$captureTargetSource=@"
<Project>
  <Import Project="$captureImport" />
  <Target Name="CaptureProductSdkCompileInputs" BeforeTargets="CoreCompile"
          Condition="'`$(RMProductSdkCompileInputCapturePath)' != '' and '`$(RMProductSdkCompileInputCaptureHashPath)' != ''">
    <GetFileHash Files="@(Compile)" Algorithm="SHA256" HashEncoding="hex">
      <Output TaskParameter="Items" ItemName="_RMProductSdkCompileInputWithHash" />
    </GetFileHash>
    <WriteLinesToFile File="`$(RMProductSdkCompileInputCapturePath)"
                      Lines="@(_RMProductSdkCompileInputWithHash->'%(FullPath)')" Overwrite="true" Encoding="UTF-8" />
    <WriteLinesToFile File="`$(RMProductSdkCompileInputCaptureHashPath)"
                      Lines="@(_RMProductSdkCompileInputWithHash->'%(FileHash)')" Overwrite="true" Encoding="UTF-8" />
    <WriteLinesToFile File="`$(RMProductSdkCompileInputCapturePropertiesPath)"
                      Lines="Configuration=`$(Configuration);TargetFramework=`$(TargetFramework);RuntimeIdentifier=`$(RuntimeIdentifier);ArtifactsPath=`$(ArtifactsPath)"
                      Overwrite="true" Encoding="UTF-8" />
  </Target>
</Project>
"@
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
[IO.File]::WriteAllText($captureTargets,$captureTargetSource,[Text.UTF8Encoding]::new($false))

function Set-StageProperties([string[]]$Properties,[string]$ProjectRelative,[string]$ProjectArtifacts) {
    $replaced=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($name in @('Configuration','ArtifactsPath','RestoreConfigFile','RestorePackagesPath','CustomAfterMicrosoftCommonTargets')){[void]$replaced.Add($name)}
    $result=[Collections.Generic.List[string]]::new()
    foreach($entry in $Properties) {
        if($entry -match '^-p:([^=]+)=') {
            $name=$Matches[1]
            if($replaced.Contains($name)){continue}
        }
        $result.Add($entry)
    }
    $result.Add('-p:Configuration=Debug')
    $result.Add('-p:ArtifactsPath='+$ProjectArtifacts)
    $result.Add('-p:CustomAfterMicrosoftCommonTargets='+$captureTargets)
    if($Stage -eq 'Consumers') {
        $result.Add('-p:RestoreConfigFile='+(Join-Path ([IO.Path]::GetDirectoryName($output)) 'consumer.NuGet.config'))
        $result.Add('-p:RestorePackagesPath='+(Join-Path $scratch 'consumer-packages'))
    }
    return ,@($result)
}

$targets='GenerateGlobalUsings,GenerateAssemblyInfo,GenerateTargetFrameworkMonikerAttribute'
$projectResults=[Collections.Generic.List[object]]::new()
$allItems=[Collections.Generic.List[object]]::new()
$inputByPath=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($relative in $projectPaths) {
    $project=[IO.Path]::GetFullPath((Join-Path $repo $relative))
    if(-not [IO.File]::Exists($project)){throw "Compile project is missing: $project"}
    $projectArtifacts=$artifacts
    $name=''
    if($Stage -eq 'Consumers') {
        $name=if($relative -like 'tests/ReadWriteCore/*'){'ReadWriteCore'}else{'SubscriptionCore'}
        $projectArtifacts=[IO.Path]::GetFullPath((Join-Path $scratch ('consumer-'+$name)))
    }
    $properties=@(Set-StageProperties -Properties $BuildProperties -ProjectRelative $relative -ProjectArtifacts $projectArtifacts)
    $arguments=@('msbuild',$project,('-target:'+$targets),
        '-getProperty:Configuration,TargetFramework,TargetFrameworks,RuntimeIdentifier,RuntimeIdentifiers,ArtifactsPath,DefineConstants,CompilerGeneratedFilesOutputPath',
        '-getItem:Compile','-nologo','-verbosity:quiet','-maxcpucount:1','-nodeReuse:false')+$properties
    $lines=@(& $Dotnet @arguments 2>&1)
    $exit=$LASTEXITCODE
    if($exit -ne 0){throw "MSBuild Compile evaluation failed for ${relative} ($exit): $([string]::Join(' ',@($lines|Select-Object -Last 8)))"}
    $text=[string]::Join([Environment]::NewLine,[string[]]$lines)
    $jsonStart=$text.IndexOf('{')
    if($jsonStart -lt 0){throw "MSBuild Compile evaluation returned no JSON for $relative."}
    $evaluation=$text.Substring($jsonStart)|ConvertFrom-Json
    $compile=@($evaluation.Items.Compile)
    if($compile.Count -eq 0 -or $compile.Count -gt 8192){throw "Evaluated Compile item count is invalid for $relative."}
    $effective=$evaluation.Properties
    $artifactRoot=$projectArtifacts.TrimEnd('\')+[IO.Path]::DirectorySeparatorChar
    $projectItems=[Collections.Generic.List[object]]::new()
    foreach($item in $compile) {
        $fullPath=[string]$item.FullPath
        if([string]::IsNullOrWhiteSpace($fullPath) -or -not [IO.Path]::IsPathFullyQualified($fullPath)){throw "Compile item has no absolute FullPath in ${relative}: $($item.Identity)"}
        $fullPath=[IO.Path]::GetFullPath($fullPath)
        $exists=[IO.File]::Exists($fullPath)
        $generatedUnderArtifacts=$fullPath.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)
        $linkProperty=$item.PSObject.Properties['Link']
        $definingProjectProperty=$item.PSObject.Properties['DefiningProjectFullPath']
        $autoGenProperty=$item.PSObject.Properties['AutoGen']
        $designTimeProperty=$item.PSObject.Properties['DesignTime']
        $generatedProperty=$item.PSObject.Properties['Generated']
        $autoGen=$null -ne $autoGenProperty -and [string]$autoGenProperty.Value -ieq 'true'
        $designTime=$null -ne $designTimeProperty -and [string]$designTimeProperty.Value -ieq 'true'
        $generatedMetadata=$null -ne $generatedProperty -and [string]$generatedProperty.Value -ieq 'true'
        $generated=$generatedUnderArtifacts -or $autoGen -or $designTime -or $generatedMetadata
        $row=[pscustomobject]@{
            projectPath=$project
            projectName=$name
            identity=[string]$item.Identity
            fullPath=$fullPath
            link=if($null -eq $linkProperty){''}else{[string]$linkProperty.Value}
            definingProjectFullPath=if($null -eq $definingProjectProperty){''}else{[string]$definingProjectProperty.Value}
            autoGen=$autoGen
            designTime=$designTime
            generatedMetadata=$generatedMetadata
            existsBeforeStage=$exists
            generatedUnderArtifacts=$generatedUnderArtifacts
            generated=$generated
        }
        $projectItems.Add($row)
        $allItems.Add($row)
        if($exists -and -not $inputByPath.ContainsKey($fullPath)) {
            $inputByPath.Add($fullPath,[pscustomobject]@{path=$fullPath;sha256=(Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash;generated=$generated})
        }
    }
    $projectResults.Add([pscustomobject]@{
        projectPath=$project
        projectName=$name
        artifactsPath=[string]$effective.ArtifactsPath
        configuration=[string]$effective.Configuration
        targetFramework=[string]$effective.TargetFramework
        targetFrameworks=[string]$effective.TargetFrameworks
        runtimeIdentifier=[string]$effective.RuntimeIdentifier
        runtimeIdentifiers=[string]$effective.RuntimeIdentifiers
        defineConstants=[string]$effective.DefineConstants
        compilerGeneratedFilesOutputPath=[string]$effective.CompilerGeneratedFilesOutputPath
        compileItemCount=$projectItems.Count
        existingCompileItemCount=@($projectItems|Where-Object existsBeforeStage).Count
        pendingCompileItemCount=@($projectItems|Where-Object {-not $_.existsBeforeStage}).Count
        compileItems=@($projectItems)
    })
}
if($allItems.Count -gt 16384){throw 'Aggregate Compile item count exceeds the supported bound.'}
$inputs=@($inputByPath.Values|Sort-Object path)
$result=[ordered]@{
    schema='rm/product-sdk-evaluated-compile-inputs/v2'
    stage=$Stage
    evaluationTargets=@($targets.Split(','))
    buildProperties=@($BuildProperties)
    captureTargetsPath=$captureTargets
    actualCaptureFilePattern=(Join-Path ([IO.Path]::GetDirectoryName($output)) 'compile-inputs-actual-<consumer>.txt')
    projects=@($projectResults)
    items=@($allItems)
    inputs=$inputs
    compileItemCount=$allItems.Count
    uniqueExistingInputCount=$inputs.Count
    generatedCompileItemCount=@($allItems|Where-Object generated).Count
    pendingCompileItemCount=@($allItems|Where-Object {-not $_.existsBeforeStage}).Count
}
$json=$result|ConvertTo-Json -Depth 14
if([Text.Encoding]::UTF8.GetByteCount($json) -gt 16777216){throw 'Compile input metadata exceeds the 16 MiB bound.'}
[IO.File]::WriteAllText($output,$json,[Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    path=$output
    sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    captureTargetsPath=$captureTargets
    compileItemCount=$allItems.Count
    uniqueExistingInputCount=$inputs.Count
    generatedCompileItemCount=@($allItems|Where-Object generated).Count
    pendingCompileItemCount=@($allItems|Where-Object {-not $_.existsBeforeStage}).Count
}|ConvertTo-Json -Compress
