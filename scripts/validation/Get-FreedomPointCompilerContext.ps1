param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$ArtifactsPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string[]]$BuildProperties = @(),
    [string[]]$ProjectRelativePaths = @('src/FreedomPointContracts/ResourceManager.FreedomPointContracts.csproj','src/ReadWriteCore/ResourceManager.ReadWriteCore.csproj','src/SubscriptionCore/ResourceManager.SubscriptionCore.csproj','src/Core/ResourceManager.App.csproj'),
    [string]$Dotnet = 'dotnet'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskProjects=@()
foreach($taskRelative in $ProjectRelativePaths) {
    $taskArguments=@('msbuild',(Join-Path $RepositoryRoot $taskRelative),
        '-target:FreedomPointCompilerInputs',
        '-getProperty:DefineConstants,Configuration,TargetFramework,AssemblyName,LangVersion,OutputType,Nullable,AllowUnsafeBlocks,CompilerGeneratedFilesOutputPath,IntermediateOutputPath,ResourceManagerLedgerOutputRoot',
        '-getItem:ReferencePath,Compile,ProjectReference','-nologo','-verbosity:quiet','-nodeReuse:false','-p:UseSharedCompilation=false')+$BuildProperties
    $taskContext=(& $Dotnet @taskArguments) -join [Environment]::NewLine
    if($LASTEXITCODE -ne 0){throw "Compiler context failed for $taskRelative"}
    $taskProject=$taskContext|ConvertFrom-Json
    $taskProject|Add-Member -NotePropertyName projectRelativePath -NotePropertyValue $taskRelative
    $taskProjects+=$taskProject
}
[IO.File]::WriteAllText($OutputPath,(@{artifactsPath=[IO.Path]::GetFullPath($ArtifactsPath);projects=$taskProjects}|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
