Set-StrictMode -Version Latest

function Test-ResourceManagerLegacyStartupAction {
    param([Parameter(Mandatory = $true)][object[]]$Actions)
    if ($Actions.Count -ne 1) { return $false }
    $action = $Actions[0]
    return [IO.Path]::IsPathRooted([string]$action.Execute) -and
        [IO.Path]::GetFileName([string]$action.Execute) -ieq 'ResourceManager.NativeUi.exe' -and
        ([string]$action.Arguments).Trim() -ceq '--background-startup'
}

function Remove-ResourceManagerLegacyStartupTask {
    # Upgrade cleanup only. This never registers or enables startup.
    $task = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop |
        Where-Object { $_.TaskName -ceq 'ResourceManager.UserLogonBootstrap' })
    if ($task.Count -eq 0) { return }
    if ($task.Count -ne 1 -or -not (Test-ResourceManagerLegacyStartupAction -Actions @($task[0].Actions))) {
        throw 'The legacy startup task has an unexpected action; it was not removed.'
    }
    Unregister-ScheduledTask -TaskName $task[0].TaskName -TaskPath '\' -Confirm:$false -ErrorAction Stop
}

function Assert-ResourceManagerLegacyUserRegistration {
    param([Parameter(Mandatory = $true)][Microsoft.Win32.RegistryKey]$RegistryRoot)

    $paths = @(
        'Software\ResourceManager',
        'Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager',
        'Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe',
        'Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.NativeUi.exe'
    )
    $keys = [Collections.Generic.List[Microsoft.Win32.RegistryKey]]::new()
    foreach ($path in $paths) { $keys.Add($RegistryRoot.OpenSubKey($path)) }
    try {
        if ($null -eq $keys[0]) {
            if (@($keys | Where-Object { $null -ne $_ }).Count -ne 0) {
                throw 'Legacy user registration has no owning Resource Manager key.'
            }
            return @()
        }

        $owner = $keys[0]
        $root = [string]$owner.GetValue('InstallRoot')
        if (-not [IO.Path]::IsPathRooted($root)) { throw 'Legacy user install root is not absolute.' }
        $root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
        if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\', '/')) {
            throw 'Legacy user install root is a drive root.'
        }
        $backend = Join-Path $root 'Bin\ResourceManager\ResourceManager.exe'
        $ui = Join-Path $root 'Bin\ResourceManagerNativeUi\ResourceManager.NativeUi.exe'
        foreach ($entry in @(
                @('BackendPath', $backend), @('NativeUiPath', $ui), @('MainPath', $ui),
                @('AppUserModelId', 'ResourceManager.Desktop'))) {
            if (-not [string]::Equals([string]$owner.GetValue($entry[0]), [string]$entry[1],
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Legacy user registration has an unexpected $($entry[0])."
            }
        }
        if ([int]$owner.GetValue('GreenInstall') -ne 1) {
            throw 'Legacy user registration marker is missing.'
        }
        for ($index = 0; $index -lt $keys.Count; $index++) {
            $key = $keys[$index]
            if ($null -eq $key) { continue }
            $allowedNames = switch ($index) {
                0 { @('InstallRoot', 'BackendPath', 'NativeUiPath', 'MainPath', 'AppUserModelId', 'GreenInstall') }
                1 { @('DisplayName', 'DisplayVersion', 'Publisher', 'InstallLocation', 'DisplayIcon',
                    'UninstallString', 'QuietUninstallString', 'NoModify', 'NoRepair', 'EstimatedSize') }
                default { @('', 'Path') }
            }
            if ($key.SubKeyCount -ne 0 -or @($key.GetValueNames() | Where-Object { $_ -cnotin $allowedNames }).Count -ne 0) {
                throw "Legacy user registration has unexpected values or child keys: $($paths[$index])"
            }
        }
        if ($null -ne $keys[1] -and (
                -not [string]::Equals([string]$keys[1].GetValue('InstallLocation'), $root,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([string]$keys[1].GetValue('DisplayIcon'), $ui,
                    [StringComparison]::OrdinalIgnoreCase))) {
            throw 'Legacy user uninstall registration points to another installation.'
        }
        foreach ($index in 2, 3) {
            if ($null -eq $keys[$index]) { continue }
            if (-not [string]::Equals([string]$keys[$index].GetValue(''), $ui,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([string]$keys[$index].GetValue('Path'), [IO.Path]::GetDirectoryName($ui),
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Legacy user App Paths registration points to another installation: $($paths[$index])"
            }
        }
        $existing = [Collections.Generic.List[string]]::new()
        for ($index = 0; $index -lt $paths.Count; $index++) {
            if ($null -ne $keys[$index]) { $existing.Add($paths[$index]) }
        }
        return $existing.ToArray()
    }
    finally {
        foreach ($key in $keys) { if ($null -ne $key) { $key.Dispose() } }
    }
}

function Remove-ResourceManagerLegacyUserRegistration {
    param([Parameter(Mandatory = $true)][Microsoft.Win32.RegistryKey]$RegistryRoot)

    $paths = @(Assert-ResourceManagerLegacyUserRegistration -RegistryRoot $RegistryRoot)
    foreach ($path in $paths) { $RegistryRoot.DeleteSubKey($path, $false) }
}
