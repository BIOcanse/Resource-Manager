[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$app = Join-Path (Split-Path -Parent $PSScriptRoot) 'Resource Manager\Resource Manager-APP'
if (-not ('ResourceManagerReleaseErrorMode' -as [type])) { Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ResourceManagerReleaseErrorMode {
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
}
'@
}
[void][ResourceManagerReleaseErrorMode]::SetErrorMode(32771)
$projects = @(
    @{ Project = 'ResourceManager.App.csproj'; Directory = 'backend' },
    @{ Project = 'NativeUi\ResourceManager.NativeUi.csproj'; Directory = 'ui' },
    @{ Project = 'Launcher\ResourceManager.Launcher.csproj'; Directory = 'launcher' }
)
foreach ($project in $projects) {
    $destination = Join-Path $OutputRoot $project.Directory
    if (Test-Path -LiteralPath $destination) { throw "Publish directory already exists: $destination" }
    # MSBuild owns incremental native compilation; no deployment or IFEO cleanup runs here.
    & dotnet publish (Join-Path $app $project.Project) -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true `
        -p:DebugType=none -p:DebugSymbols=false "-p:Version=$Version" -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publication failed: $($project.Project), exit $LASTEXITCODE" }
}
& (Join-Path $PSScriptRoot 'Test-ResourceManagerThirdPartyNotices.ps1') `
    -BackendPublishRoot (Join-Path $OutputRoot 'backend') -NativeUiPublishRoot (Join-Path $OutputRoot 'ui') `
    -LauncherPublishRoot (Join-Path $OutputRoot 'launcher') | Out-Null
& (Join-Path $PSScriptRoot 'Test-WindowsExecutableManifest.ps1') `
    -ExecutablePath (Join-Path $OutputRoot 'ui\ResourceManager.NativeUi.exe') -ExpectedExecutionLevel asInvoker | Out-Null
& (Join-Path $PSScriptRoot 'New-ResourceManagerFinalImage.ps1') `
    -BackendDirectory (Join-Path $OutputRoot 'backend') -NativeUiDirectory (Join-Path $OutputRoot 'ui') `
    -LauncherDirectory (Join-Path $OutputRoot 'launcher') -OutputDirectory (Join-Path $OutputRoot 'image')
