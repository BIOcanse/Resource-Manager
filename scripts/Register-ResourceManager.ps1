[CmdletBinding()]
param(
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Resource Manager'),
    [switch]$Unregister,
    [switch]$PlanOnly,
    [switch]$NoSelfElevate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    . (Join-Path $PSScriptRoot 'ResourceManager.DirectoryRegistration.ps1')
    . (Join-Path $PSScriptRoot 'ResourceManager.LegacyStartup.ps1')
    $registration = New-ResourceManagerDirectoryRegistration -PackageRoot $PackageRoot `
        -RegistrationScriptPath $PSCommandPath -Unregister:$Unregister
    if ($PlanOnly) {
        $registration | ConvertTo-Json -Depth 6
        exit 0
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $admin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }
    if (-not $admin) {
        if ($NoSelfElevate) { throw 'HKLM directory registration requires an administrator.' }
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'),
            '-PackageRoot', ('"' + $registration.InstallRoot + '"'), '-NoSelfElevate')
        if ($Unregister) { $arguments += '-Unregister' }
        $process = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $arguments `
            -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        exit $process.ExitCode
    }

    $userRoot = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        if (-not $Unregister) {
            $null = Assert-ResourceManagerLegacyUserRegistration -RegistryRoot $userRoot
        }
        $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            Invoke-ResourceManagerDirectoryRegistration -Registration $registration -RegistryRoot $root `
                -Unregister:$Unregister
        }
        finally { $root.Dispose() }
        if (-not $Unregister) {
            Remove-ResourceManagerLegacyUserRegistration -RegistryRoot $userRoot
        }
        Remove-ResourceManagerLegacyStartupTask
    }
    finally { $userRoot.Dispose() }
    [pscustomobject]@{
        Action = $(if ($Unregister) { 'Unregistered' } else { 'Registered' })
        InstallRoot = $registration.InstallRoot
        Registry = 'HKLM (64-bit)'
    } | ConvertTo-Json
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
