[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$SkipPublish,
    [switch]$Quiet,
    [switch]$PlanOnly,
    [switch]$NoSelfElevate,
    [string]$InstallRoot,
    [string]$SourceImageRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSScriptRoot
$repositoryModule = Join-Path $scriptDirectory 'scripts\ResourceManager.MachineDeployment.ps1'
$modulePath = if (Test-Path -LiteralPath $repositoryModule) {
    $repositoryModule
}
else {
    Join-Path $scriptDirectory 'ResourceManager.MachineDeployment.ps1'
}
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Machine deployment module was not found: $modulePath"
}
. $modulePath

$publishScript = Join-Path $scriptDirectory 'scripts\release\Publish-ResourceManagerLocal.ps1'
$defaultSourceImage = Join-Path $scriptDirectory 'artifacts\local-publish\ResourceManagerFinal'
$finalImageValidator = Join-Path $scriptDirectory 'scripts\validation\Test-ResourceManagerFinalImage.ps1'
$manifestValidator = Join-Path $scriptDirectory 'scripts\validation\Test-WindowsExecutableManifest.ps1'
if (-not (Test-Path -LiteralPath $finalImageValidator)) {
    $finalImageValidator = Join-Path $scriptDirectory 'Test-ResourceManagerFinalImage.ps1'
}
if (-not (Test-Path -LiteralPath $manifestValidator)) {
    $manifestValidator = Join-Path $scriptDirectory 'Test-WindowsExecutableManifest.ps1'
}

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        'Resource Manager'
}
if ([string]::IsNullOrWhiteSpace($SourceImageRoot)) {
    $SourceImageRoot = $defaultSourceImage
}
$InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
$SourceImageRoot = [System.IO.Path]::GetFullPath($SourceImageRoot).TrimEnd('\', '/')

function Write-Step([string]$Message) {
    if (-not $Quiet) {
        Write-Host "[Resource Manager] $Message"
    }
}

function ConvertTo-QuotedPowerShellArgument([string]$Value) {
    if ($Value.Contains('"')) {
        throw 'PowerShell relaunch arguments cannot contain a quote character.'
    }
    return '"' + $Value + '"'
}

function Invoke-ElevatedSelf {
    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', (ConvertTo-QuotedPowerShellArgument $PSCommandPath),
            '-NoSelfElevate',
            '-InstallRoot', (ConvertTo-QuotedPowerShellArgument $InstallRoot),
            '-SourceImageRoot', (ConvertTo-QuotedPowerShellArgument $SourceImageRoot))) {
        $arguments.Add($argument)
    }
    if ($Uninstall) { $arguments.Add('-Uninstall') }
    if ($SkipPublish) { $arguments.Add('-SkipPublish') }
    if ($Quiet) { $arguments.Add('-Quiet') }

    $process = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList ($arguments -join ' ') `
        -WorkingDirectory $scriptDirectory `
        -Verb RunAs `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

if (-not $PlanOnly -and
    -not $NoSelfElevate -and
    -not (Test-ResourceManagerAdministrator)) {
    Invoke-ElevatedSelf
}

if (-not $Uninstall -and -not $PlanOnly -and -not $SkipPublish) {
    if (-not (Test-Path -LiteralPath $publishScript)) {
        throw "Unified publish script was not found: $publishScript"
    }
    Write-Step 'Building the sealed win-x64 final image.'
    & $publishScript -Target All -Quiet:$Quiet
    if (-not $?) {
        throw 'Final image publish failed.'
    }
}

$plan = New-ResourceManagerMachineDeploymentPlan `
    -SourceImageRoot $SourceImageRoot `
    -InstallRoot $InstallRoot `
    -InstallerScriptPath $PSCommandPath `
    -FinalImageValidatorPath $finalImageValidator `
    -ExecutableManifestValidatorPath $manifestValidator

if ($PlanOnly) {
    Assert-ResourceManagerDeploymentPlan -Plan $plan -AllowNonProgramFilesRoot
    $plan | ConvertTo-Json -Depth 4
    exit 0
}

if ($Uninstall) {
    Write-Step "Removing $($plan.ServiceName) and its machine installation."
    Invoke-ResourceManagerMachineUninstall -Plan $plan
    Write-Step 'Machine uninstall completed.'
    exit 0
}

Write-Step "Installing the sealed final image under $($plan.InstallRoot)."
$result = Invoke-ResourceManagerMachineInstall -Plan $plan
$result | ConvertTo-Json -Depth 3
Write-Step "SCM service $($plan.ServiceName) is running."
Write-Step "Native UI entry: $($plan.NativeUiPath)"
