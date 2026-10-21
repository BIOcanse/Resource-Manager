param(
    [string]$RepositoryRoot,
    [string]$OutputDirectory,
    [string]$RetentionRoot,
    [string[]]$References = @(),
    [string[]]$ReferenceRoots = @(),
    [switch]$CreatePartition,
    [switch]$RefreshPartitionContract
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$taskStatusFile = $null
$taskExitCode = 1
$taskManaged = $false
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Join-Path $PSScriptRoot '..\..' }
try {
    $taskRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $RetentionRoot = Join-Path $taskRoot '.local'
        $OutputDirectory = Join-Path $RetentionRoot ('freedom-point-source-check-' + [guid]::NewGuid().ToString('N'))
    }
    $taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not [string]::IsNullOrWhiteSpace($RetentionRoot)) {
        Import-Module (Join-Path $PSScriptRoot '..\maintenance\GeneratedWorkspaceRetention.psm1') -Force -DisableNameChecking
        $taskRetentionPolicy = Get-GeneratedWorkspacePolicy
        $taskRetentionPolicy.roots = @($taskRetentionPolicy.roots) + @([pscustomobject]@{ id = 'source-check-root'; path = [IO.Path]::GetFullPath($RetentionRoot); excludedNames = @() })
        if (-not $PSBoundParameters.ContainsKey('ReferenceRoots')) {
            $ReferenceRoots = @($taskRetentionPolicy.roots | ForEach-Object {
                (Get-GeneratedWorkspaceRoot -Policy $taskRetentionPolicy -RootId $_.id).Path
            } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -Unique)
        }
        New-Item -ItemType Directory -Path $RetentionRoot -Force | Out-Null
        $null = New-GeneratedWorkspace -RootPath $RetentionRoot -WorkspacePath $taskOutput -Purpose 'freedom-point-source-check' -References $References -ReferenceRoots $ReferenceRoots -Policy $taskRetentionPolicy
        $taskManaged = $true
    } else {
        # An explicit output path belongs to the caller's batch lifecycle.
        New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    }
    $taskStatusFile = Join-Path $taskOutput 'source-result.json'
    [IO.File]::WriteAllText($taskStatusFile, '{"passed":false,"status":"checking"}', [Text.UTF8Encoding]::new($false))
    $taskProject = Join-Path $taskRoot 'src\Development\FreedomPointCheck\ResourceManager.FreedomPointCheck.csproj'
    if ($CreatePartition -and $RefreshPartitionContract) { throw 'Choose one explicit partition operation.' }
    $taskArtifacts = Join-Path $taskOutput 'artifacts'
    $taskBuildProperties = @('-p:BuildInParallel=false','-maxcpucount:1',
        '-p:SkipPerformanceOverlayNativeBuild=true','-p:SkipGpuPlacementNativeBuild=true',
        '-p:SkipGpuPlacementPreparationNativeBuild=true','-p:SkipGpuLaunchBrokerNativeBuild=true',
        '-p:SkipAdlxBridgeBuild=true','-p:SkipNativeCoreBuild=true',
        '-p:SkipGpuWindowActionNativeBuild=true','-p:SkipGpuPlacementExternalNativeBuild=true',
        '-p:SkipClientAppBuild=true',('-p:ArtifactsPath='+$taskArtifacts),
        ('-p:CustomAfterMicrosoftCommonTargets='+(Join-Path $PSScriptRoot 'FreedomPointCompilerCapture.targets')))
    & dotnet build $taskProject @taskBuildProperties --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        [IO.File]::WriteAllText($taskStatusFile, '{"passed":false,"error":"consumer_build_failed"}', [Text.UTF8Encoding]::new($false))
        $taskExitCode = $LASTEXITCODE
        exit $taskExitCode
    }
    $taskContextFile = Join-Path $taskOutput 'compiler-context.json'
    & (Join-Path $PSScriptRoot 'Get-FreedomPointCompilerContext.ps1') -RepositoryRoot $taskRoot -ArtifactsPath $taskArtifacts -OutputPath $taskContextFile -BuildProperties $taskBuildProperties
    $taskTool = Join-Path $taskArtifacts 'bin\ResourceManager.FreedomPointCheck\debug\ResourceManager.FreedomPointCheck.dll'
    $taskArguments = @($taskTool, $taskRoot, $taskContextFile, $taskOutput)
    if ($CreatePartition) { $taskArguments += '--create-partition' }
    if ($RefreshPartitionContract) { $taskArguments += '--refresh-partition-contract' }
    & dotnet @taskArguments
    $taskExitCode = $LASTEXITCODE
    exit $taskExitCode
} catch {
    if ($null -ne $taskStatusFile -and (Test-Path -LiteralPath $taskStatusFile)) {
        [IO.File]::WriteAllText($taskStatusFile, '{"passed":false,"error":"source_check_failed"}', [Text.UTF8Encoding]::new($false))
    }
    Write-Error $_
    exit 1
} finally {
    if ($taskManaged) {
        $taskOutcome = if ($taskExitCode -eq 0) { 'success' } else { 'failed' }
        $null = Complete-GeneratedWorkspace -RootPath $RetentionRoot -WorkspacePath $taskOutput -Outcome $taskOutcome -References $References -ReferenceRoots $ReferenceRoots -Policy $taskRetentionPolicy
    }
}
