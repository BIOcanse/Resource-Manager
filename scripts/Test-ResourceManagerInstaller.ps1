[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ResourceManager.DirectoryRegistration.ps1')
. (Join-Path $PSScriptRoot 'ResourceManager.LegacyStartup.ps1')
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

# Mock only Task Scheduler access. No real startup task is created or changed.
$script:tasks = @()
$script:removed = @()
function Get-ScheduledTask { param($TaskPath, $ErrorAction) return $script:tasks }
function Unregister-ScheduledTask {
    param($TaskName, $TaskPath, $Confirm, $ErrorAction)
    $script:removed += "$TaskPath$TaskName"
}
Remove-ResourceManagerLegacyStartupTask
Assert ($script:removed.Count -eq 0) 'Absent task should be a no-op.'
$action = [pscustomobject]@{ Execute = 'C:\Old Package\Bin\ResourceManagerNativeUi\ResourceManager.NativeUi.exe'; Arguments = '--background-startup' }
$script:tasks = @([pscustomobject]@{ TaskName = 'ResourceManager.UserLogonBootstrap'; Actions = @($action) })
Remove-ResourceManagerLegacyStartupTask
Assert ($script:removed.Count -eq 1 -and $script:removed[0] -ceq '\ResourceManager.UserLogonBootstrap') 'Wrong cleanup target.'
foreach ($actions in @(
        ,@([pscustomobject]@{ Execute = 'C:\Other.exe'; Arguments = '--background-startup' }),
        ,@([pscustomobject]@{ Execute = $action.Execute; Arguments = '--another-action' }),
        ,@($action, $action))) {
    $script:tasks[0].Actions = $actions
    $rejected = $false
    try { Remove-ResourceManagerLegacyStartupTask } catch { $rejected = $true }
    Assert $rejected 'Unexpected task action must remain untouched.'
}
Assert ($script:removed.Count -eq 1) 'Unexpected task was removed.'
$script:tasks = @([pscustomobject]@{ TaskName = 'OtherSoftware'; Actions = @($action) })
Remove-ResourceManagerLegacyStartupTask
Assert ($script:removed.Count -eq 1) 'Unrelated task was changed.'

# Real registry operations, isolated below a unique HKCU test key, never HKLM.
$testKey = 'Software\ResourceManagerInstallerTests\' + [Guid]::NewGuid().ToString('N')
$registry = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($testKey)
try {
    $plan = New-ResourceManagerDirectoryRegistration -PackageRoot 'C:\Fixture Package' `
        -RegistrationScriptPath 'C:\Fixture Package\scripts\Register-ResourceManager.ps1' -Unregister
    $sentinel = $registry.CreateSubKey('Unrelated')
    $sentinel.SetValue('Keep', 'user data')
    $sentinel.Dispose()
    Invoke-ResourceManagerDirectoryRegistration -Registration $plan -RegistryRoot $registry
    $uninstall = $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager')
    Assert ($uninstall.GetValue('DisplayName') -ceq 'Resource Manager') 'Missing Installed Apps display name.'
    Assert ($uninstall.GetValue('UninstallString').Contains('-Unregister')) 'Missing uninstall command.'
    Assert ($uninstall.GetValue('InstallLocation') -ceq 'C:\Fixture Package') 'Wrong install location.'
    $uninstall.Dispose()
    Assert ($null -eq $registry.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')) 'Installer must not enable startup.'
    Invoke-ResourceManagerDirectoryRegistration -Registration $plan -RegistryRoot $registry
    Invoke-ResourceManagerDirectoryRegistration -Registration $plan -RegistryRoot $registry -Unregister
    foreach ($entry in $plan.Entries) { Assert ($null -eq $registry.OpenSubKey($entry.Key)) 'Unregister left an owned entry.' }
    $sentinel = $registry.OpenSubKey('Unrelated')
    Assert ($sentinel.GetValue('Keep') -ceq 'user data') 'Unrelated data changed.'
    $sentinel.Dispose()
} finally {
    $registry.Dispose()
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($testKey)
}
'Installer checks passed: legacy cleanup, unrelated task preservation, Installed Apps registration, no startup registration, scoped unregister.'
