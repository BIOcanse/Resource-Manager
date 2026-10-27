#requires -Version 7.0
<#
.SYNOPSIS
Prepare the real NativeUi package guard and user directories in a registered test workspace.
.DESCRIPTION
Run through JobQueue from a normal elevated PowerShell executor. This helper does
not launch NativeUi, install a product, change a service, or weaken a production
guard. A restricted worker records requires-admin without attempting another
RunAs launch. The caller retains ownership of the generated workspace lifecycle.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$WorkRoot,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$WindowExe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
$receipt = $null
$canWriteReceipt = $false
$details = [ordered]@{ schemaVersion = 1; status = 'failed-setup'; preparedUtc = $null }

function Assert-OutsideInstallation([string]$Path) {
    foreach ($protected in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:SystemRoot)) {
        if (-not $protected) { continue }
        $prefix = [IO.Path]::GetFullPath($protected).TrimEnd('\', '/')
        if ($Path -ieq $prefix -or $Path.StartsWith($prefix + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Installed or system paths are not fixture inputs: $Path"
        }
    }
}

function Assert-PlainWorkPath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ine $Root -and -not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture input or output escaped WorkRoot.'
    }
    $cursor = $full
    $count = 0
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (++$count -gt 513) { throw 'Fixture ancestor count exceeds its bound.' }
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Fixture path contains a reparse point: $cursor"
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Write-Receipt {
    $json = $details | ConvertTo-Json -Depth 5
    if ($canWriteReceipt) { [IO.File]::WriteAllText($receipt, $json, [Text.UTF8Encoding]::new($false)) }
    Write-Output $json
}

try {
    if (-not [IO.Path]::IsPathFullyQualified($WorkRoot) -or -not [IO.Path]::IsPathFullyQualified($WindowExe)) {
        throw 'WorkRoot and WindowExe must be absolute development paths.'
    }
    $taskRoot = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\', '/')
    $windowPath = [IO.Path]::GetFullPath($WindowExe)
    Assert-OutsideInstallation $taskRoot
    Assert-OutsideInstallation $windowPath
    Assert-PlainWorkPath $taskRoot $taskRoot
    $marker = Join-Path $taskRoot '.generated-workspace.json'
    Assert-PlainWorkPath $marker $taskRoot
    if (-not [IO.File]::Exists($marker)) { throw 'WorkRoot must be a registered generated workspace.' }
    $state = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($state.state -cne 'running' -or [IO.Path]::GetFullPath($state.workspacePath).TrimEnd('\', '/') -ine $taskRoot) {
        throw 'WorkRoot registration must be running and name this exact directory.'
    }

    $package = Join-Path $taskRoot 'native-ui-pipe-package'
    $receipt = Join-Path $taskRoot 'native-ui-pipe-package-setup.json'
    foreach ($path in @($package, $receipt, $windowPath)) { Assert-PlainWorkPath $path $taskRoot }
    $canWriteReceipt = $true
    $details['workRoot'] = $taskRoot
    $details['packageRoot'] = $package
    $details['windowExe'] = $windowPath
    $details['receiptPath'] = $receipt
    if ([IO.Path]::GetFileName($windowPath) -ine 'ResourceManager.NativeUi.exe' -or -not [IO.File]::Exists($windowPath)) {
        throw 'WindowExe must name the built development ResourceManager.NativeUi.exe inside WorkRoot.'
    }
    $shared = Join-Path ([IO.Path]::GetDirectoryName($windowPath)) 'ResourceManager.Shared.dll'
    Assert-PlainWorkPath $shared $taskRoot
    if (-not [IO.File]::Exists($shared)) { throw 'The window build must supply ResourceManager.Shared.dll beside WindowExe.' }
    $details['sharedAssembly'] = $shared

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $administrator = $identity.IsSystem -or [Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
    if (-not $administrator) {
        $details['status'] = 'requires-admin'
        $details['error'] = 'Run this helper through JobQueue from the normal administrator executor. No RunAs process was attempted in the restricted worker.'
        Write-Receipt
        exit 2
    }

    $assembly = [Reflection.Assembly]::LoadFrom($shared)
    if ([IO.Path]::GetFullPath($assembly.Location) -ine $shared) { throw 'A different ResourceManager.Shared assembly is already loaded.' }
    $guard = [ResourceManager.Shared.Packages.PackageUseGuard]::PathForPackage($package)
    foreach ($path in @($guard, (Join-Path $package 'Config/WebView2'), (Join-Path $package 'Config/Diagnostics/NativeUiStartup'))) {
        Assert-PlainWorkPath $path $taskRoot
    }
    [IO.Directory]::CreateDirectory($package) | Out-Null
    [ResourceManager.Shared.Packages.PackageUseGuard]::Prepare($package)
    [ResourceManager.Shared.Packages.PackageUserDirectoryAccess]::Prepare($package)
    [ResourceManager.Shared.Packages.PackageUseGuard]::Verify($package)
    $details['status'] = 'prepared'
    $details['preparedUtc'] = [DateTime]::UtcNow.ToString('o')
    $details['guardPath'] = $guard
    $details['preparedUserDirectories'] = @('Config/WebView2', 'Config/Diagnostics/NativeUiStartup')
    Write-Receipt
    exit 0
} catch {
    $details['status'] = 'failed-setup'
    $details['error'] = $_.Exception.Message
    $details['hresult'] = $_.Exception.HResult
    Write-Receipt
    exit 1
}
