Set-StrictMode -Version Latest

$script:ResourceManagerServiceName = 'ResourceManager.Service'
$script:ResourceManagerLegacyTaskName = '\ResourceManager.UserLogonBootstrap'
$script:ResourceManagerMachineDeploymentContract = 'resource-manager-machine-deployment-v1'
$script:ResourceManagerInstallationContract = 'resource-manager-machine-installation-v1'
$script:ResourceManagerInstallationMarkerFileName = 'resource-manager-machine-installation.json'
$script:ResourceManagerMachineDeploymentMutexName = 'Global\ResourceManager.MachineDeployment.v1'
$script:ResourceManagerDeploymentModulePath = $PSCommandPath

function New-ResourceManagerMachineDeploymentPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceImageRoot,

        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$InstallerScriptPath,

        [Parameter(Mandatory = $true)]
        [string]$FinalImageValidatorPath,

        [Parameter(Mandatory = $true)]
        [string]$ExecutableManifestValidatorPath
    )

    $source = [System.IO.Path]::GetFullPath($SourceImageRoot).TrimEnd('\', '/')
    $install = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
    $installer = [System.IO.Path]::GetFullPath($InstallerScriptPath)
    $imageValidator = [System.IO.Path]::GetFullPath($FinalImageValidatorPath)
    $manifestValidator = [System.IO.Path]::GetFullPath($ExecutableManifestValidatorPath)
    $current = Join-Path $install 'Current'
    $maintenance = Join-Path $install 'Maintenance'
    $backend = Join-Path $current 'ResourceManager\ResourceManager.exe'
    $nativeUi = Join-Path $current 'ResourceManagerNativeUi\ResourceManager.NativeUi.exe'

    [pscustomobject]@{
        Contract = $script:ResourceManagerMachineDeploymentContract
        InstallationContract = $script:ResourceManagerInstallationContract
        ServiceName = $script:ResourceManagerServiceName
        SourceImageRoot = $source
        SourceManifestPath = Join-Path $source 'resource-manager-final-image.manifest.json'
        SourceBackendPath = Join-Path $source 'ResourceManager\ResourceManager.exe'
        SourceNativeUiPath = Join-Path $source 'ResourceManagerNativeUi\ResourceManager.NativeUi.exe'
        InstallRoot = $install
        InstallationMarkerPath = Join-Path $install $script:ResourceManagerInstallationMarkerFileName
        CurrentRoot = $current
        MaintenanceRoot = $maintenance
        BackendPath = $backend
        NativeUiPath = $nativeUi
        InstalledInstallerPath = Join-Path $maintenance 'Install-ResourceManager.ps1'
        InstalledDeploymentModulePath = Join-Path $maintenance 'ResourceManager.MachineDeployment.ps1'
        InstallerScriptPath = $installer
        DeploymentModulePath = [System.IO.Path]::GetFullPath($script:ResourceManagerDeploymentModulePath)
        FinalImageValidatorPath = $imageValidator
        ExecutableManifestValidatorPath = $manifestValidator
        MachineRegistryRoot = 'Registry::HKEY_LOCAL_MACHINE\Software\ResourceManager'
        MachineUninstallKey = 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager'
        MachineAppPathsKey = 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe'
        LegacyUserRegistryRoot = 'Registry::HKEY_CURRENT_USER\Software\ResourceManager'
        LegacyUserUninstallKey = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager'
        LegacyUserAppPathsKey = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe'
        LegacyUserNativeUiAppPathsKey = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.NativeUi.exe'
    }
}

function Test-ResourceManagerAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-ResourceManagerDeploymentPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Plan,

        [switch]$AllowNonProgramFilesRoot
    )

    if ([string]$Plan.Contract -cne $script:ResourceManagerMachineDeploymentContract) {
        throw 'Machine deployment plan contract is invalid.'
    }

    foreach ($requiredFile in @(
            $Plan.SourceManifestPath,
            $Plan.SourceBackendPath,
            $Plan.SourceNativeUiPath,
            $Plan.InstallerScriptPath,
            $Plan.DeploymentModulePath,
            $Plan.FinalImageValidatorPath,
            $Plan.ExecutableManifestValidatorPath)) {
        if (-not [System.IO.File]::Exists([string]$requiredFile)) {
            throw "Required deployment input does not exist: $requiredFile"
        }
    }

    $installRoot = [System.IO.Path]::GetFullPath([string]$Plan.InstallRoot).TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($installRoot) -or
        $installRoot.Equals(
            [System.IO.Path]::GetPathRoot($installRoot).TrimEnd('\', '/'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Machine install root is unsafe: $installRoot"
    }

    $sourceRoot = [System.IO.Path]::GetFullPath([string]$Plan.SourceImageRoot).TrimEnd('\', '/')
    $installBoundary = $installRoot + [System.IO.Path]::DirectorySeparatorChar
    $sourceBoundary = $sourceRoot + [System.IO.Path]::DirectorySeparatorChar
    if ($sourceRoot.Equals($installRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        $sourceRoot.StartsWith($installBoundary, [System.StringComparison]::OrdinalIgnoreCase) -or
        $installRoot.StartsWith($sourceBoundary, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source image and machine install root must be disjoint.'
    }

    if (-not $AllowNonProgramFilesRoot) {
        $programFiles = [System.IO.Path]::GetFullPath(
            [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)).TrimEnd('\', '/')
        $programFilesBoundary = $programFiles + [System.IO.Path]::DirectorySeparatorChar
        if (-not $installRoot.StartsWith(
                $programFilesBoundary,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Machine install root must be below Program Files: $installRoot"
        }
    }
}

function Write-ResourceManagerInstallationMarker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Plan,
        [Parameter(Mandatory = $true)][string]$MarkerRoot
    )

    $root = [System.IO.Path]::GetFullPath($MarkerRoot).TrimEnd('\', '/')
    if (-not [System.IO.Directory]::Exists($root)) {
        throw "Installation marker root does not exist: $root"
    }
    $markerPath = Join-Path $root $script:ResourceManagerInstallationMarkerFileName
    $marker = [ordered]@{
        Contract = $script:ResourceManagerInstallationContract
        InstallRoot = [System.IO.Path]::GetFullPath([string]$Plan.InstallRoot).TrimEnd('\', '/')
        ServiceName = $script:ResourceManagerServiceName
        BackendRelativePath = 'Current\ResourceManager\ResourceManager.exe'
        NativeUiRelativePath = 'Current\ResourceManagerNativeUi\ResourceManager.NativeUi.exe'
        RegistrationVersion = 1
    }
    [System.IO.File]::WriteAllText(
        $markerPath,
        ($marker | ConvertTo-Json -Depth 2),
        [System.Text.UTF8Encoding]::new($false))
    return $markerPath
}

function Get-ResourceManagerFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Assert-ResourceManagerInstallationMarker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Plan,
        [Parameter(Mandatory = $true)][string]$MarkerRoot,
        [switch]$AllowStagingRoot
    )

    $root = [System.IO.Path]::GetFullPath($MarkerRoot).TrimEnd('\', '/')
    $install = [System.IO.Path]::GetFullPath([string]$Plan.InstallRoot).TrimEnd('\', '/')
    if (-not $AllowStagingRoot -and
        -not $root.Equals($install, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Installation marker root does not match the canonical install root: $root"
    }
    if (-not [System.IO.Directory]::Exists($root)) {
        throw "Installation marker root does not exist: $root"
    }
    $rootInfo = [System.IO.DirectoryInfo]::new($root)
    if (($rootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installation root cannot be a reparse point: $root"
    }

    $markerPath = Join-Path $root $script:ResourceManagerInstallationMarkerFileName
    if (-not [System.IO.File]::Exists($markerPath)) {
        throw "Resource Manager installation marker is missing: $markerPath"
    }
    $markerInfo = [System.IO.FileInfo]::new($markerPath)
    if (($markerInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installation marker cannot be a reparse point: $markerPath"
    }
    try {
        $marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
    }
    catch {
        throw "Resource Manager installation marker is invalid JSON: $markerPath"
    }

    if ([string]$marker.Contract -cne $script:ResourceManagerInstallationContract -or
        [string]$marker.ServiceName -cne $script:ResourceManagerServiceName -or
        [int]$marker.RegistrationVersion -ne 1 -or
        [string]$marker.BackendRelativePath -cne 'Current\ResourceManager\ResourceManager.exe' -or
        [string]$marker.NativeUiRelativePath -cne 'Current\ResourceManagerNativeUi\ResourceManager.NativeUi.exe') {
        throw 'Resource Manager installation marker identity is invalid.'
    }
    $markedInstall = [System.IO.Path]::GetFullPath([string]$marker.InstallRoot).TrimEnd('\', '/')
    if (-not $markedInstall.Equals($install, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Resource Manager installation marker is bound to a different install root.'
    }

    $backendPath = [System.IO.Path]::GetFullPath((Join-Path $root ([string]$marker.BackendRelativePath)))
    $nativeUiPath = [System.IO.Path]::GetFullPath((Join-Path $root ([string]$marker.NativeUiRelativePath)))
    if (-not [System.IO.File]::Exists($backendPath) -or
        -not [System.IO.File]::Exists($nativeUiPath)) {
        throw 'Resource Manager installation marker does not have the required product layout.'
    }

    [pscustomobject]@{
        Contract = [string]$marker.Contract
        InstallRoot = $markedInstall
        ServiceName = [string]$marker.ServiceName
        BackendPath = $backendPath
        NativeUiPath = $nativeUiPath
        MarkerPath = $markerPath
        MarkerSha256 = Get-ResourceManagerFileSha256 -Path $markerPath
    }
}

function Assert-ResourceManagerMachineOwnershipFacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$Plan,
        [Parameter(Mandatory = $true)][object]$MarkerEvidence,
        [Parameter(Mandatory = $true)][object]$Registration,
        [AllowNull()][string]$RegisteredServiceBinaryPath
    )

    if ([string]$Plan.Contract -cne $script:ResourceManagerMachineDeploymentContract -or
        [string]$Plan.ServiceName -cne $script:ResourceManagerServiceName) {
        throw 'Machine uninstall plan identity is invalid.'
    }
    $expectedInstall = [System.IO.Path]::GetFullPath([string]$Plan.InstallRoot).TrimEnd('\', '/')
    $registeredInstall = [System.IO.Path]::GetFullPath([string]$Registration.InstallRoot).TrimEnd('\', '/')
    $registeredBackend = [System.IO.Path]::GetFullPath([string]$Registration.BackendPath)
    $registeredNativeUi = [System.IO.Path]::GetFullPath([string]$Registration.NativeUiPath)
    if (-not $registeredInstall.Equals($expectedInstall, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $registeredBackend.Equals([System.IO.Path]::GetFullPath([string]$Plan.BackendPath), [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $registeredNativeUi.Equals([System.IO.Path]::GetFullPath([string]$Plan.NativeUiPath), [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]$Registration.ServiceName -cne $script:ResourceManagerServiceName -or
        [string]$Registration.InstallationContract -cne $script:ResourceManagerInstallationContract -or
        [string]$Registration.InstallationMarkerSha256 -cne [string]$MarkerEvidence.MarkerSha256) {
        throw 'Machine registration does not own the requested Resource Manager installation.'
    }
    if (-not [string]::IsNullOrWhiteSpace($RegisteredServiceBinaryPath)) {
        $serviceBinary = [System.IO.Path]::GetFullPath($RegisteredServiceBinaryPath)
        if (-not $serviceBinary.Equals(
                [System.IO.Path]::GetFullPath([string]$Plan.BackendPath),
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'SCM service binary is not owned by the requested Resource Manager installation.'
        }
    }
    return $MarkerEvidence
}

function Assert-ResourceManagerMachineUninstallOwnership {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Plan)

    $markerEvidence = Assert-ResourceManagerInstallationMarker `
        -Plan $Plan `
        -MarkerRoot $Plan.InstallRoot
    if (-not (Test-Path -LiteralPath $Plan.MachineRegistryRoot)) {
        throw 'Resource Manager machine registration is missing; refusing destructive uninstall.'
    }
    $registration = Get-ItemProperty -LiteralPath $Plan.MachineRegistryRoot -ErrorAction Stop

    $serviceBinaryPath = $null
    if (Test-ResourceManagerServiceExists -ServiceName $Plan.ServiceName) {
        $instance = Get-CimInstance -ClassName Win32_Service -Filter (
            "Name='$($Plan.ServiceName.Replace("'", "''"))'") -ErrorAction Stop
        if ($null -eq $instance -or [string]::IsNullOrWhiteSpace([string]$instance.PathName)) {
            throw 'Resource Manager SCM service path is unavailable.'
        }
        $serviceBinaryPath = ([string]$instance.PathName).Trim()
        if ($serviceBinaryPath.Length -ge 2 -and
            $serviceBinaryPath[0] -eq '"' -and
            $serviceBinaryPath[$serviceBinaryPath.Length - 1] -eq '"') {
            $serviceBinaryPath = $serviceBinaryPath.Substring(1, $serviceBinaryPath.Length - 2)
        }
    }

    Assert-ResourceManagerMachineOwnershipFacts `
        -Plan $Plan `
        -MarkerEvidence $markerEvidence `
        -Registration $registration `
        -RegisteredServiceBinaryPath $serviceBinaryPath
}

function Assert-ResourceManagerMachineInstallTargetOwnership {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Plan)

    if ([System.IO.Directory]::Exists([string]$Plan.InstallRoot)) {
        $null = Assert-ResourceManagerMachineUninstallOwnership -Plan $Plan
        return
    }

    Assert-ResourceManagerNoConflictingMachineDeploymentFacts `
        -MachineRegistryExists (Test-Path -LiteralPath $Plan.MachineRegistryRoot) `
        -MachineAppPathsExists (Test-Path -LiteralPath $Plan.MachineAppPathsKey) `
        -MachineUninstallRegistrationExists (Test-Path -LiteralPath $Plan.MachineUninstallKey) `
        -ServiceExists (Test-ResourceManagerServiceExists -ServiceName $Plan.ServiceName)
}

function Assert-ResourceManagerNoConflictingMachineDeploymentFacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][bool]$MachineRegistryExists,
        [Parameter(Mandatory = $true)][bool]$MachineAppPathsExists,
        [Parameter(Mandatory = $true)][bool]$MachineUninstallRegistrationExists,
        [Parameter(Mandatory = $true)][bool]$ServiceExists
    )

    if ($MachineRegistryExists -or
        $MachineAppPathsExists -or
        $MachineUninstallRegistrationExists -or
        $ServiceExists) {
        throw 'Existing Resource Manager machine ownership facts conflict with the requested new install root.'
    }
}

function Get-ResourceManagerMachineInstallRollbackMode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][bool]$MachineMutationStarted,
        [Parameter(Mandatory = $true)][bool]$HadExistingInstallation,
        [Parameter(Mandatory = $true)][bool]$HadPreviousBackup,
        [Parameter(Mandatory = $true)][bool]$PromotedNewInstallation
    )

    if (-not $MachineMutationStarted) {
        return 'NoMachineMutation'
    }
    if ($HadPreviousBackup) {
        return 'RestorePreviousBackup'
    }
    if ($HadExistingInstallation -and -not $PromotedNewInstallation) {
        return 'RestoreExistingInPlace'
    }
    return 'RemoveNewInstallation'
}

function Get-ResourceManagerTreeFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
    $pending = [System.Collections.Generic.Queue[string]]::new()
    $pending.Enqueue([System.IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        $directoryInfo = [System.IO.DirectoryInfo]::new($directory)
        if (($directoryInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Deployment tree contains a directory reparse point: $directory"
        }

        foreach ($filePath in [System.IO.Directory]::EnumerateFiles($directory)) {
            $file = [System.IO.FileInfo]::new($filePath)
            if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Deployment tree contains a file reparse point: $filePath"
            }
            $files.Add($file)
        }
        foreach ($child in [System.IO.Directory]::EnumerateDirectories($directory)) {
            $pending.Enqueue($child)
        }
    }
    return $files.ToArray()
}

function Copy-ResourceManagerDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $pending = [System.Collections.Generic.Queue[object]]::new()
    $pending.Enqueue([pscustomobject]@{
        Source = [System.IO.Path]::GetFullPath($Source)
        Destination = [System.IO.Path]::GetFullPath($Destination)
    })
    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        $sourceInfo = [System.IO.DirectoryInfo]::new([string]$item.Source)
        if (($sourceInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Deployment input contains a directory reparse point: $($sourceInfo.FullName)"
        }
        [void][System.IO.Directory]::CreateDirectory([string]$item.Destination)

        foreach ($filePath in [System.IO.Directory]::EnumerateFiles([string]$item.Source)) {
            $file = [System.IO.FileInfo]::new($filePath)
            if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Deployment input contains a file reparse point: $filePath"
            }
            $destinationPath = Join-Path ([string]$item.Destination) $file.Name
            [System.IO.File]::Copy($file.FullName, $destinationPath, $false)
            [System.IO.File]::SetLastWriteTimeUtc($destinationPath, $file.LastWriteTimeUtc)
        }

        foreach ($childPath in [System.IO.Directory]::EnumerateDirectories([string]$item.Source)) {
            $child = [System.IO.DirectoryInfo]::new($childPath)
            $pending.Enqueue([pscustomobject]@{
                Source = $child.FullName
                Destination = Join-Path ([string]$item.Destination) $child.Name
            })
        }
    }
}

function Remove-ResourceManagerOwnedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedParent,
        [Parameter(Mandatory = $true)][string]$ExpectedLeafPrefix
    )

    if (-not [System.IO.Directory]::Exists($Path)) {
        return
    }
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parent = [System.IO.Path]::GetDirectoryName($resolved)
    $leaf = [System.IO.Path]::GetFileName($resolved)
    $expected = [System.IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\', '/')
    if (-not $parent.Equals($expected, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($ExpectedLeafPrefix, [System.StringComparison]::Ordinal)) {
        throw "Refusing to remove an unowned deployment directory: $resolved"
    }
    [System.IO.Directory]::Delete($resolved, $true)
}

function Invoke-ResourceManagerNativeCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int[]]$AllowedExitCodes = @(0)
    )

    $output = & $FilePath @Arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($AllowedExitCodes -notcontains $exitCode) {
        throw "$FilePath failed with exit code $exitCode. $($output.Trim())"
    }
    return $output
}

function Enter-ResourceManagerMachineDeploymentLock {
    [CmdletBinding()]
    param([TimeSpan]$Timeout = [TimeSpan]::FromMinutes(2))

    $mutex = [System.Threading.Mutex]::new(
        $false,
        $script:ResourceManagerMachineDeploymentMutexName)
    try {
        $acquired = $false
        try {
            $acquired = $mutex.WaitOne($Timeout)
        }
        catch [System.Threading.AbandonedMutexException] {
            $acquired = $true
        }
        if (-not $acquired) {
            throw 'Another Resource Manager machine deployment operation is still running.'
        }
        return $mutex
    }
    catch {
        $mutex.Dispose()
        throw
    }
}

function Exit-ResourceManagerMachineDeploymentLock {
    param([Parameter(Mandatory = $true)][System.Threading.Mutex]$Mutex)

    try {
        $Mutex.ReleaseMutex()
    }
    finally {
        $Mutex.Dispose()
    }
}

function Test-ResourceManagerServiceExists {
    param([Parameter(Mandatory = $true)][string]$ServiceName)
    return $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
}

function Stop-ResourceManagerMachineService {
    param([Parameter(Mandatory = $true)][string]$ServiceName)

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        return
    }
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30))
}

function Set-ResourceManagerMachineServiceRegistration {
    param([Parameter(Mandatory = $true)][object]$Plan)

    $sc = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'sc.exe'
    $quotedBinary = '"' + [string]$Plan.BackendPath + '"'
    if (Test-ResourceManagerServiceExists -ServiceName $Plan.ServiceName) {
        $null = Invoke-ResourceManagerNativeCommand -FilePath $sc -Arguments @(
            'config', $Plan.ServiceName,
            'binPath=', $quotedBinary,
            'start=', 'delayed-auto',
            'obj=', 'LocalSystem',
            'DisplayName=', 'Resource Manager Service')
    }
    else {
        $null = Invoke-ResourceManagerNativeCommand -FilePath $sc -Arguments @(
            'create', $Plan.ServiceName,
            'binPath=', $quotedBinary,
            'start=', 'delayed-auto',
            'obj=', 'LocalSystem',
            'DisplayName=', 'Resource Manager Service')
    }
    $null = Invoke-ResourceManagerNativeCommand -FilePath $sc -Arguments @(
        'description', $Plan.ServiceName,
        'Resource Manager machine backend and interactive-session coordinator.')
    $null = Invoke-ResourceManagerNativeCommand -FilePath $sc -Arguments @(
        'failure', $Plan.ServiceName,
        'reset=', '86400',
        'actions=', 'restart/5000/restart/15000/none/0')
    $null = Invoke-ResourceManagerNativeCommand -FilePath $sc -Arguments @(
        'failureflag', $Plan.ServiceName, '1')
}

function Remove-ResourceManagerMachineServiceRegistration {
    param([Parameter(Mandatory = $true)][string]$ServiceName)

    if (-not (Test-ResourceManagerServiceExists -ServiceName $ServiceName)) {
        return
    }
    Stop-ResourceManagerMachineService -ServiceName $ServiceName
    $sc = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'sc.exe'
    $null = Invoke-ResourceManagerNativeCommand -FilePath $sc -Arguments @('delete', $ServiceName)
    $deadline = [DateTimeOffset]::Now.AddSeconds(20)
    while ((Test-ResourceManagerServiceExists -ServiceName $ServiceName) -and
           [DateTimeOffset]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-ResourceManagerServiceExists -ServiceName $ServiceName) {
        throw "Service deletion did not complete: $ServiceName"
    }
}

function Start-ResourceManagerMachineService {
    param([Parameter(Mandatory = $true)][object]$Plan)

    Start-Service -Name $Plan.ServiceName -ErrorAction Stop
    $service = Get-Service -Name $Plan.ServiceName -ErrorAction Stop
    $service.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))

    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    while ([DateTimeOffset]::Now -lt $deadline) {
        $instance = Get-CimInstance -ClassName Win32_Service -Filter (
            "Name='$($Plan.ServiceName.Replace("'", "''"))'") -ErrorAction Stop
        if ([uint32]$instance.ProcessId -ne 0) {
            $process = Get-Process -Id ([int]$instance.ProcessId) -ErrorAction Stop
            try {
                $actualPath = $process.MainModule.FileName
                if (-not [string]::IsNullOrWhiteSpace($actualPath) -and
                    [System.IO.Path]::GetFullPath($actualPath).Equals(
                        [System.IO.Path]::GetFullPath([string]$Plan.BackendPath),
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                    try {
                        $response = Invoke-WebRequest -UseBasicParsing `
                            -Uri 'http://127.0.0.1:9321/api/runtime/identity?challenge=0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF' `
                            -TimeoutSec 2
                        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                            return
                        }
                    }
                    catch {
                        $statusCode = $null
                        try {
                            $statusCode = [int]$_.Exception.Response.StatusCode
                        }
                        catch {
                        }
                        if ($statusCode -eq 401) {
                            return
                        }
                    }
                }
            }
            finally {
                $process.Dispose()
            }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'SCM service did not expose the expected backend identity before timeout.'
}

function Set-ResourceManagerInstallAcl {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $icacls = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'icacls.exe'
    $null = Invoke-ResourceManagerNativeCommand -FilePath $icacls -Arguments @(
        $InstallRoot,
        '/inheritance:r',
        '/grant:r',
        '*S-1-5-18:(OI)(CI)F',
        '*S-1-5-32-544:(OI)(CI)F',
        '*S-1-5-32-545:(OI)(CI)RX',
        '/T', '/C', '/Q')

    $writeRights = [System.Security.AccessControl.FileSystemRights]::Write -bor
        [System.Security.AccessControl.FileSystemRights]::Modify -bor
        [System.Security.AccessControl.FileSystemRights]::FullControl -bor
        [System.Security.AccessControl.FileSystemRights]::Delete -bor
        [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [System.Security.AccessControl.FileSystemRights]::TakeOwnership
    $unsafeSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    $paths = [System.Collections.Generic.List[string]]::new()
    $paths.Add([System.IO.Path]::GetFullPath($InstallRoot))
    foreach ($file in Get-ResourceManagerTreeFiles -Root $InstallRoot) {
        $paths.Add($file.FullName)
    }
    $pending = [System.Collections.Generic.Queue[string]]::new()
    $pending.Enqueue([System.IO.Path]::GetFullPath($InstallRoot))
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($child in [System.IO.Directory]::EnumerateDirectories($directory)) {
            $paths.Add($child)
            $pending.Enqueue($child)
        }
    }

    foreach ($path in $paths) {
        $acl = Get-Acl -LiteralPath $path
        foreach ($rule in $acl.Access) {
            $sid = $null
            try {
                $sid = $rule.IdentityReference.Translate(
                    [System.Security.Principal.SecurityIdentifier]).Value
            }
            catch [System.Security.Principal.IdentityNotMappedException] {
            }
            if ($unsafeSids -contains $sid -and
                $rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow -and
                ($rule.FileSystemRights -band $writeRights) -ne 0) {
                throw "Machine install payload remains writable by $sid at $path"
            }
        }
    }
}

function Remove-ResourceManagerLegacyStartup {
    param([Parameter(Mandatory = $true)][object]$Plan)

    $schtasks = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'schtasks.exe'
    & $schtasks /Query /TN $script:ResourceManagerLegacyTaskName *> $null
    $queryExitCode = $LASTEXITCODE
    if ($queryExitCode -eq 0) {
        $null = Invoke-ResourceManagerNativeCommand -FilePath $schtasks -Arguments @(
            '/Delete', '/TN', $script:ResourceManagerLegacyTaskName, '/F')
    }
    elseif ($queryExitCode -ne 1) {
        throw "Legacy startup task query failed with exit code $queryExitCode."
    }

    foreach ($path in @(
            $Plan.LegacyUserRegistryRoot,
            $Plan.LegacyUserUninstallKey,
            $Plan.LegacyUserAppPathsKey,
            $Plan.LegacyUserNativeUiAppPathsKey)) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-ResourceManagerDirectorySizeKb {
    param([Parameter(Mandatory = $true)][string]$Root)

    $bytes = 0L
    foreach ($file in Get-ResourceManagerTreeFiles -Root $Root) {
        if ($bytes -gt [long]::MaxValue - $file.Length) {
            throw 'Installed payload size exceeds Int64 capacity.'
        }
        $bytes += $file.Length
    }
    return [int][Math]::Max(1, [Math]::Ceiling($bytes / 1KB))
}

function Set-ResourceManagerMachineRegistry {
    param([Parameter(Mandatory = $true)][object]$Plan)

    $markerEvidence = Assert-ResourceManagerInstallationMarker `
        -Plan $Plan `
        -MarkerRoot $Plan.InstallRoot
    $version = (Get-Item -LiteralPath $Plan.NativeUiPath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        $version = '1.0.0'
    }
    $uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$($Plan.InstalledInstallerPath)`" -Uninstall -InstallRoot `"$($Plan.InstallRoot)`""

    New-Item -Path $Plan.MachineRegistryRoot -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name InstallRoot -Value $Plan.InstallRoot -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name BackendPath -Value $Plan.BackendPath -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name NativeUiPath -Value $Plan.NativeUiPath -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name ServiceName -Value $Plan.ServiceName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name AppUserModelId -Value 'ResourceManager.Desktop' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name RegistrationVersion -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name InstallationContract -Value $script:ResourceManagerInstallationContract -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineRegistryRoot -Name InstallationMarkerSha256 -Value $markerEvidence.MarkerSha256 -PropertyType String -Force | Out-Null

    New-Item -Path $Plan.MachineAppPathsKey -Force | Out-Null
    Set-Item -Path $Plan.MachineAppPathsKey -Value $Plan.NativeUiPath
    New-ItemProperty -Path $Plan.MachineAppPathsKey -Name Path -Value (Split-Path -Parent $Plan.NativeUiPath) -PropertyType String -Force | Out-Null

    New-Item -Path $Plan.MachineUninstallKey -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name DisplayName -Value 'Resource Manager' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name DisplayVersion -Value $version -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name Publisher -Value 'Resource Manager' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name InstallLocation -Value $Plan.InstallRoot -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name DisplayIcon -Value $Plan.NativeUiPath -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name UninstallString -Value $uninstall -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name QuietUninstallString -Value "$uninstall -Quiet" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $Plan.MachineUninstallKey -Name EstimatedSize -Value (Get-ResourceManagerDirectorySizeKb -Root $Plan.InstallRoot) -PropertyType DWord -Force | Out-Null
}

function Invoke-ResourceManagerMachineInstall {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Plan)

    $deploymentLock = Enter-ResourceManagerMachineDeploymentLock
    try {
        Invoke-ResourceManagerMachineInstallCore -Plan $Plan
    }
    finally {
        Exit-ResourceManagerMachineDeploymentLock -Mutex $deploymentLock
    }
}

function Invoke-ResourceManagerMachineInstallCore {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Plan)

    Assert-ResourceManagerDeploymentPlan -Plan $Plan
    if (-not (Test-ResourceManagerAdministrator)) {
        throw 'Machine installation requires an elevated administrator token.'
    }
    $null = Assert-ResourceManagerMachineInstallTargetOwnership -Plan $Plan

    $null = & $Plan.FinalImageValidatorPath -RootDirectory $Plan.SourceImageRoot
    $null = & $Plan.ExecutableManifestValidatorPath `
        -ExecutablePath $Plan.SourceNativeUiPath `
        -ExpectedExecutionLevel asInvoker

    $install = [System.IO.Path]::GetFullPath([string]$Plan.InstallRoot).TrimEnd('\', '/')
    $parent = [System.IO.Path]::GetDirectoryName($install)
    $leaf = [System.IO.Path]::GetFileName($install)
    $nonce = [Guid]::NewGuid().ToString('N')
    $staging = Join-Path $parent "$leaf.staging.$nonce"
    $backup = Join-Path $parent "$leaf.previous.$nonce"
    $hadExistingInstallation = [System.IO.Directory]::Exists($install)
    $hadPrevious = $false
    $promoted = $false
    $machineMutationStarted = $false
    $backupCleanupPending = $false
    $previousBackupPath = $null
    try {
        [void][System.IO.Directory]::CreateDirectory($staging)
        Copy-ResourceManagerDirectoryTree `
            -Source $Plan.SourceImageRoot `
            -Destination (Join-Path $staging 'Current')
        [void][System.IO.Directory]::CreateDirectory((Join-Path $staging 'Maintenance'))
        [System.IO.File]::Copy(
            $Plan.InstallerScriptPath,
            (Join-Path $staging 'Maintenance\Install-ResourceManager.ps1'),
            $false)
        [System.IO.File]::Copy(
            $Plan.DeploymentModulePath,
            (Join-Path $staging 'Maintenance\ResourceManager.MachineDeployment.ps1'),
            $false)
        $null = Write-ResourceManagerInstallationMarker -Plan $Plan -MarkerRoot $staging

        $null = & $Plan.FinalImageValidatorPath -RootDirectory (Join-Path $staging 'Current')
        $null = & $Plan.ExecutableManifestValidatorPath `
            -ExecutablePath (Join-Path $staging 'Current\ResourceManagerNativeUi\ResourceManager.NativeUi.exe') `
            -ExpectedExecutionLevel asInvoker
        $null = Assert-ResourceManagerInstallationMarker `
            -Plan $Plan `
            -MarkerRoot $staging `
            -AllowStagingRoot
        Set-ResourceManagerInstallAcl -InstallRoot $staging

        $machineMutationStarted = $true
        Stop-ResourceManagerMachineService -ServiceName $Plan.ServiceName
        if ([System.IO.Directory]::Exists($install)) {
            [System.IO.Directory]::Move($install, $backup)
            $hadPrevious = $true
        }
        [System.IO.Directory]::Move($staging, $install)
        $promoted = $true

        Set-ResourceManagerMachineServiceRegistration -Plan $Plan
        Start-ResourceManagerMachineService -Plan $Plan
        Set-ResourceManagerMachineRegistry -Plan $Plan
        Remove-ResourceManagerLegacyStartup -Plan $Plan

        if ($hadPrevious) {
            try {
                Remove-ResourceManagerOwnedDirectory `
                    -Path $backup `
                    -ExpectedParent $parent `
                    -ExpectedLeafPrefix "$leaf.previous."
            }
            catch {
                $backupCleanupPending = $true
                $previousBackupPath = $backup
            }
            $hadPrevious = $false
        }

        [pscustomobject]@{
            Contract = $script:ResourceManagerMachineDeploymentContract
            InstallRoot = $Plan.InstallRoot
            ServiceName = $Plan.ServiceName
            BackendPath = $Plan.BackendPath
            NativeUiPath = $Plan.NativeUiPath
            BackupCleanupPending = $backupCleanupPending
            PreviousBackupPath = $previousBackupPath
            Installed = $true
        }
    }
    catch {
        $installError = $_
        $rollbackMode = Get-ResourceManagerMachineInstallRollbackMode `
            -MachineMutationStarted $machineMutationStarted `
            -HadExistingInstallation $hadExistingInstallation `
            -HadPreviousBackup $hadPrevious `
            -PromotedNewInstallation $promoted
        if ($rollbackMode -ceq 'NoMachineMutation') {
            throw $installError
        }
        try {
            Stop-ResourceManagerMachineService -ServiceName $Plan.ServiceName
            if ($promoted -and [System.IO.Directory]::Exists($install)) {
                Remove-ResourceManagerOwnedDirectory `
                    -Path $install `
                    -ExpectedParent $parent `
                    -ExpectedLeafPrefix $leaf
                $promoted = $false
            }
            switch ($rollbackMode) {
                'RestorePreviousBackup' {
                    if (-not [System.IO.Directory]::Exists($backup)) {
                        throw 'Previous Resource Manager installation backup is missing during rollback.'
                    }
                    [System.IO.Directory]::Move($backup, $install)
                    $hadPrevious = $false
                    Set-ResourceManagerMachineServiceRegistration -Plan $Plan
                    Start-ResourceManagerMachineService -Plan $Plan
                    Set-ResourceManagerMachineRegistry -Plan $Plan
                }
                'RestoreExistingInPlace' {
                    if (-not [System.IO.Directory]::Exists($install)) {
                        throw 'Existing Resource Manager installation disappeared before rollback.'
                    }
                    Set-ResourceManagerMachineServiceRegistration -Plan $Plan
                    Start-ResourceManagerMachineService -Plan $Plan
                    Set-ResourceManagerMachineRegistry -Plan $Plan
                }
                'RemoveNewInstallation' {
                    Remove-ResourceManagerMachineServiceRegistration -ServiceName $Plan.ServiceName
                    foreach ($path in @(
                            $Plan.MachineAppPathsKey,
                            $Plan.MachineUninstallKey,
                            $Plan.MachineRegistryRoot)) {
                        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                default {
                    throw "Unsupported machine installation rollback mode: $rollbackMode"
                }
            }
        }
        catch {
            throw [System.AggregateException]::new(
                'Machine installation and rollback both failed.',
                @($installError.Exception, $_.Exception))
        }
        throw $installError
    }
    finally {
        if ([System.IO.Directory]::Exists($staging)) {
            Remove-ResourceManagerOwnedDirectory `
                -Path $staging `
                -ExpectedParent $parent `
                -ExpectedLeafPrefix "$leaf.staging."
        }
    }
}

function Invoke-ResourceManagerGpuInterceptionCleanup {
    param([Parameter(Mandatory = $true)][object]$Plan)

    $broker = Join-Path $Plan.CurrentRoot 'ResourceManager\ResourceManager.GpuLaunchBroker.exe'
    if (-not [System.IO.File]::Exists($broker)) {
        return
    }
    $null = Invoke-ResourceManagerNativeCommand `
        -FilePath $broker `
        -Arguments @('--remove-all-resource-manager-ifeo')
}

function Invoke-ResourceManagerMachineUninstall {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Plan)

    $deploymentLock = Enter-ResourceManagerMachineDeploymentLock
    try {
        Invoke-ResourceManagerMachineUninstallCore -Plan $Plan
    }
    finally {
        Exit-ResourceManagerMachineDeploymentLock -Mutex $deploymentLock
    }
}

function Invoke-ResourceManagerMachineUninstallCore {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object]$Plan)

    if (-not (Test-ResourceManagerAdministrator)) {
        throw 'Machine uninstallation requires an elevated administrator token.'
    }
    $install = [System.IO.Path]::GetFullPath([string]$Plan.InstallRoot).TrimEnd('\', '/')
    $programFiles = [System.IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)).TrimEnd('\', '/')
    if (-not $install.StartsWith(
            $programFiles + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to uninstall outside Program Files: $install"
    }
    $null = Assert-ResourceManagerMachineUninstallOwnership -Plan $Plan

    Stop-ResourceManagerMachineService -ServiceName $Plan.ServiceName
    Invoke-ResourceManagerGpuInterceptionCleanup -Plan $Plan
    Remove-ResourceManagerMachineServiceRegistration -ServiceName $Plan.ServiceName
    foreach ($path in @(
            $Plan.MachineAppPathsKey,
            $Plan.MachineUninstallKey,
            $Plan.MachineRegistryRoot)) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-ResourceManagerLegacyStartup -Plan $Plan

    if (-not [System.IO.Directory]::Exists($install)) {
        return
    }
    $currentScript = [System.IO.Path]::GetFullPath($MyInvocation.PSCommandPath)
    $installBoundary = $install + [System.IO.Path]::DirectorySeparatorChar
    if (-not $currentScript.StartsWith(
            $installBoundary,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $parent = [System.IO.Path]::GetDirectoryName($install)
        Remove-ResourceManagerOwnedDirectory `
            -Path $install `
            -ExpectedParent $parent `
            -ExpectedLeafPrefix ([System.IO.Path]::GetFileName($install))
        return
    }

    $command = @"
Start-Sleep -Seconds 2
`$target = [System.IO.Path]::GetFullPath('$($install.Replace("'", "''"))')
`$programFiles = [System.IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)).TrimEnd('\', '/')
if (-not `$target.StartsWith(`$programFiles + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) { exit 87 }
for (`$attempt = 0; `$attempt -lt 40; `$attempt++) {
    try { [System.IO.Directory]::Delete(`$target, `$true); exit 0 } catch { Start-Sleep -Milliseconds 250 }
}
exit 1
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) -WindowStyle Hidden
}
