Set-StrictMode -Version Latest

function New-ResourceManagerDirectoryRegistration {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)][string]$RegistrationScriptPath,
        [switch]$Unregister
    )

    $root = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
    $scriptPath = [IO.Path]::GetFullPath($RegistrationScriptPath)
    if ($root.Contains('"') -or $scriptPath.Contains('"')) {
        throw 'Registration paths must not contain quote characters.'
    }
    $backend = Join-Path $root 'Bin\ResourceManager\ResourceManager.exe'
    $ui = Join-Path $root 'Bin\ResourceManagerNativeUi\ResourceManager.NativeUi.exe'
    $launcher = Join-Path $root 'Bin\ResourceManagerLauncher\ResourceManager.Launcher.exe'
    if (-not $Unregister) {
        foreach ($path in @($backend, $ui, $launcher, $scriptPath,
                (Join-Path $root 'Bin\ResourceManager\wwwroot\index.html'))) {
            if (-not [IO.File]::Exists($path)) { throw "Required package file is missing: $path" }
        }
        if (-not [IO.Directory]::Exists((Join-Path $root 'Config'))) {
            throw 'The prepared package must contain its Config directory.'
        }
    }

    $hostPath = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $remove = '"' + $hostPath + '" -NoProfile -ExecutionPolicy Bypass -File "' + $scriptPath +
        '" -PackageRoot "' + $root + '" -Unregister'
    $version = if ([IO.File]::Exists($ui)) { [Diagnostics.FileVersionInfo]::GetVersionInfo($ui).ProductVersion } else { '' }
    if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.0.0' }
    $entries = @(
        [pscustomobject]@{
            Key = 'Software\ResourceManager'
            Values = [ordered]@{
                BackendPath = $backend; NativeUiPath = $ui
                LauncherPath = $launcher
                ServiceName = 'ResourceManager.Service'; AppUserModelId = 'ResourceManager.Desktop'
            }
        },
        [pscustomobject]@{
            Key = 'Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe'
            Values = [ordered]@{ '' = $launcher; Path = [IO.Path]::GetDirectoryName($launcher) }
        },
        [pscustomobject]@{
            Key = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager'
            Values = [ordered]@{
                DisplayName = 'Resource Manager'; DisplayVersion = $version; Publisher = 'BIOcanse'
                InstallLocation = $root; DisplayIcon = $ui; UninstallString = $remove
                QuietUninstallString = $remove; NoModify = 1; NoRepair = 1
            }
        }
    )
    [pscustomobject]@{ InstallRoot = $root; Entries = $entries }
}

function Invoke-ResourceManagerDirectoryRegistration {
    param(
        [Parameter(Mandatory = $true)][object]$Registration,
        [Parameter(Mandatory = $true)][Microsoft.Win32.RegistryKey]$RegistryRoot,
        [switch]$Unregister
    )

    $contract = 'resource-manager-directory-registration-v1'
    # Preflight all three keys before writing any, including existing machine installs.
    foreach ($entry in $Registration.Entries) {
        $key = $RegistryRoot.OpenSubKey($entry.Key)
        if ($null -eq $key) { continue }
        try {
            if ($key.GetValue('InstallationContract') -cne $contract -or
                -not [string]::Equals([string]$key.GetValue('InstallRoot'),
                    $Registration.InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Registration belongs to another installer or directory: $($entry.Key)"
            }
            if ($key.SubKeyCount -ne 0) { throw "Registration has unexpected child keys: $($entry.Key)" }
        }
        finally { $key.Dispose() }
    }

    foreach ($entry in $Registration.Entries) {
        if ($Unregister) {
            $RegistryRoot.DeleteSubKey($entry.Key, $false)
            continue
        }
        $key = $RegistryRoot.CreateSubKey($entry.Key)
        try {
            $key.SetValue('InstallationContract', $contract, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('InstallRoot', $Registration.InstallRoot, [Microsoft.Win32.RegistryValueKind]::String)
            foreach ($value in $entry.Values.GetEnumerator()) {
                $kind = if ($value.Value -is [int]) { [Microsoft.Win32.RegistryValueKind]::DWord }
                    else { [Microsoft.Win32.RegistryValueKind]::String }
                $key.SetValue($value.Key, $value.Value, $kind)
            }
        }
        finally { $key.Dispose() }
    }
}
