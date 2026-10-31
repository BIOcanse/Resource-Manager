[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptsRoot = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $scriptsRoot
$app = Join-Path $repo 'src\Core'
$clientApp = Join-Path $repo 'src\UI\Frontend'
if (-not ('ResourceManagerReleaseErrorMode' -as [type])) { Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ResourceManagerReleaseErrorMode {
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
}
'@
}
[void][ResourceManagerReleaseErrorMode]::SetErrorMode(32771)
# The Web SDK snapshots wwwroot items when the project is evaluated. Build the
# frontend before dotnet publish so a clean checkout includes its generated assets.
# The Ledger native build and the frontend preferences generator only run inside a registered
# workspace; the caller's build root is one.
$ledgerWorkRoot = Join-Path $OutputRoot 'ledger'
$callerLedgerWorkRoot = $env:RM_LEDGER_WORK_ROOT
# Generate and build the resident before composing the image. It is a native sibling of the window process.
& (Join-Path $scriptsRoot 'build/Publish-DesktopResident.ps1') -WorkRoot (Join-Path $OutputRoot 'desktop-work') -OutputDirectory (Join-Path $OutputRoot 'desktop-work/publish')
Push-Location $clientApp
try {
    if (-not (Test-Path -LiteralPath 'node_modules' -PathType Container)) {
        & npm install
        if ($LASTEXITCODE -ne 0) { throw "Frontend dependency installation failed: $LASTEXITCODE" }
    }
    # The prebuild step reads the generator's workspace from this variable.
    $env:RM_LEDGER_WORK_ROOT = $ledgerWorkRoot
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "Frontend publication build failed: $LASTEXITCODE" }
}
finally {
    $env:RM_LEDGER_WORK_ROOT = $callerLedgerWorkRoot
    Pop-Location
}
# The backend service and the native UI publish as folders (user decisions 2026-10-24): every assembly
# is mapped from its own file, so the two long-running processes do not keep a private decompressed
# copy of each assembly beside the mapped bundle. The short-lived programs stay single-file.
Push-Location (Join-Path $app 'Native/PerformanceOverlay')
try {
    & cmd.exe /d /c .\build.cmd
    if ($LASTEXITCODE -ne 0) { throw "Frontend native module build failed: $LASTEXITCODE" }
} finally { Pop-Location }
$projects = @(
    @{ Project = (Join-Path $app 'ResourceManager.App.csproj'); Directory = 'backend'; SingleFile = $false },
    @{ Project = (Join-Path $repo 'src\UI\Core\ResourceManager.NativeUi.csproj'); Directory = 'ui'; SingleFile = $false },
    @{ Project = (Join-Path $repo 'src\UI\Launcher\ResourceManager.Launcher.csproj'); Directory = 'launcher'; SingleFile = $true },
    @{ Project = (Join-Path $repo 'src\SessionAgent\ResourceManager.SessionAgent.csproj'); Directory = 'session-agent'; SingleFile = $true },
    @{ Project = (Join-Path $repo 'src\Installer\ResourceManager.Installer.csproj'); Directory = 'installer'; SingleFile = $true }
    @{ Project = (Join-Path $repo 'src\Uninstaller\ResourceManager.Uninstaller.csproj'); Directory = 'uninstaller'; SingleFile = $true }
    @{ Project = (Join-Path $repo 'src\Updater\WinUI\ResourceManager.UpdateManager.csproj'); Directory = 'update-manager'; SingleFile = $true }
)
foreach ($project in $projects) {
    $destination = Join-Path $OutputRoot $project.Directory
    if (Test-Path -LiteralPath $destination) { throw "Publish directory already exists: $destination" }
    # @() keeps the one-switch folder layout an array; splatting a bare string passes its characters.
    $layout = @(if ($project.SingleFile) {
            '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true'
        }
        else { '-p:PublishSingleFile=false' })
    # MSBuild owns incremental native compilation; no deployment or IFEO cleanup runs here.
    & dotnet publish $project.Project -c Release -r win-x64 --self-contained true --disable-build-servers -m:1 `
        -p:BuildInParallel=false -p:UseSharedCompilation=false -p:ResourceManagerZigJobs=1 @layout -p:PublishReadyToRun=true `
        -p:ResourceManagerStateNames=false `
        -p:SkipClientAppBuild=true "-p:ResourceManagerLedgerWorkRoot=$ledgerWorkRoot" `
        -p:DebugType=none -p:DebugSymbols=false "-p:Version=$Version" -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publication failed: $($project.Project), exit $LASTEXITCODE" }
    $stateAssembly = switch ($project.Directory) {
        'backend' { 'ResourceManager.dll' }
        'ui' { 'ResourceManager.NativeUi.dll' }
    }
    if ($stateAssembly) {
        & pwsh -NoProfile -File (Join-Path $scriptsRoot 'validation\Test-StateNamesAbsent.ps1') -AssemblyPath (Join-Path $destination $stateAssembly)
        if ($LASTEXITCODE -ne 0) { throw "StateNames metadata verification failed: $destination, exit $LASTEXITCODE" }
    }
}
$startEntryOutput = Join-Path $OutputRoot 'start-entry'
if (Test-Path -LiteralPath $startEntryOutput) { throw "Publish directory already exists: $startEntryOutput" }
New-Item -ItemType Directory -Path $startEntryOutput | Out-Null
& zig cc -target x86_64-windows-gnu -O2 -s -municode '-Wl,--subsystem,windows' `
    (Join-Path $repo 'src\UI\Launcher\StartEntry.c') -lshell32 -ladvapi32 -o (Join-Path $startEntryOutput 'Start.exe')
if ($LASTEXITCODE -ne 0) { throw "Native start entry publication failed: $LASTEXITCODE" }
& (Join-Path $scriptsRoot 'validation\Test-ResourceManagerThirdPartyNotices.ps1') `
    -BackendPublishRoot (Join-Path $OutputRoot 'backend') -NativeUiPublishRoot (Join-Path $OutputRoot 'ui') `
    -LauncherPublishRoot (Join-Path $OutputRoot 'launcher') -SessionAgentPublishRoot (Join-Path $OutputRoot 'session-agent') | Out-Null
& (Join-Path $scriptsRoot 'validation\Test-WindowsExecutableManifest.ps1') `
    -ExecutablePath (Join-Path $OutputRoot 'ui\ResourceManager.NativeUi.exe') -ExpectedExecutionLevel requireAdministrator | Out-Null
. (Join-Path $scriptsRoot 'ResourceManager.DesktopLayout.ps1')
$desktopLayout = Get-ResourceManagerDesktopLayout
& (Join-Path $scriptsRoot 'validation\Test-WindowsExecutableManifest.ps1') `
    -ExecutablePath (Join-Path $OutputRoot ('desktop-work/publish/bin/' + $desktopLayout.ResidentExecutableName)) -ExpectedExecutionLevel requireAdministrator | Out-Null
& (Join-Path $scriptsRoot 'validation\Test-WindowsExecutableManifest.ps1') `
    -ExecutablePath (Join-Path $OutputRoot 'session-agent\ResourceManager.SessionAgent.exe') -ExpectedExecutionLevel asInvoker | Out-Null
& (Join-Path $scriptsRoot 'validation\Test-WindowsExecutableManifest.ps1') `
    -ExecutablePath (Join-Path $OutputRoot 'installer\Install.exe') -ExpectedExecutionLevel asInvoker | Out-Null
& (Join-Path $scriptsRoot 'validation\Test-WindowsExecutableManifest.ps1') `
    -ExecutablePath (Join-Path $OutputRoot 'update-manager\ResourceManager.UpdateManager.exe') -ExpectedExecutionLevel asInvoker | Out-Null
& (Join-Path $PSScriptRoot 'New-ResourceManagerFinalImage.ps1') `
    -BackendDirectory (Join-Path $OutputRoot 'backend') -NativeUiDirectory (Join-Path $OutputRoot 'ui') `
    -DesktopDirectory (Join-Path $OutputRoot 'desktop-work/publish/bin') `
    -LauncherDirectory (Join-Path $OutputRoot 'launcher') -SessionAgentDirectory (Join-Path $OutputRoot 'session-agent') -OutputDirectory (Join-Path $OutputRoot 'image')
