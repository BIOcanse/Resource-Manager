[CmdletBinding()]
param(
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$PlanOnly,
    [switch]$NoSelfElevate,
    [string]$CallerSid
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$serviceName = 'ResourceManager.Service'
$contract = 'resource-manager-directory-registration-v1'
$packageRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\', '/')
$rootFiles = @('Install.cmd', 'Start.cmd', 'Restart.cmd', 'LICENSE', 'NOTICE',
    'README.md', 'README.zh-CN.md', 'release-manifest.json')

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Quote-Argument([string]$Value) {
    Assert (-not $Value.Contains('"')) 'Installer paths must not contain quotes.'
    return '"' + $Value + '"'
}
function Assert-UnderRoot([string]$Root, [string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    Assert ($full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) `
        "Upgrade path escapes the installed directory: $full"
}
function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }
}
function Read-RegisteredRoot {
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $paths = @('Software\ResourceManager',
            'Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager',
            'Software\Microsoft\Windows\CurrentVersion\App Paths\ResourceManager.exe')
        $keys = @($paths | ForEach-Object { $machine.OpenSubKey($_) })
        try {
            if ($null -eq $keys[0]) {
                Assert (@($keys | Where-Object { $null -ne $_ }).Count -eq 0) `
                    'Partial machine registration belongs to an unknown installation.'
                return $null
            }
            $root = [string]$keys[0].GetValue('InstallRoot')
            Assert ([IO.Path]::IsPathRooted($root)) 'Registered install root is not absolute.'
            $root = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
            Assert ($root -ne [IO.Path]::GetPathRoot($root).TrimEnd('\', '/')) `
                'A drive root cannot be an installed product root.'
            foreach ($key in $keys) {
                Assert ($null -ne $key) 'Partial machine registration is not an upgrade target.'
                Assert ($key.GetValue('InstallationContract') -ceq $contract -and $key.SubKeyCount -eq 0) `
                    'Machine registration belongs to another installer.'
                Assert ([string]::Equals([string]$key.GetValue('InstallRoot'), $root,
                        [StringComparison]::OrdinalIgnoreCase)) `
                    'Machine registration entries disagree on the install directory.'
            }
            $backend = Join-Path $root 'Bin\ResourceManager\ResourceManager.exe'
            Assert ([string]::Equals([string]$keys[0].GetValue('BackendPath'), $backend,
                    [StringComparison]::OrdinalIgnoreCase)) 'Registered backend points elsewhere.'
            return $root
        }
        finally { foreach ($key in $keys) { if ($null -ne $key) { $key.Dispose() } } }
    }
    finally { $machine.Dispose() }
}
function Read-OwnedService([string]$Root) {
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($null -eq $service) { return $null }
    $expected = '"' + (Join-Path $Root 'Bin\ResourceManager\ResourceManager.exe') + '"'
    Assert ($service.StartName -ceq 'LocalSystem' -and $service.PathName -ceq $expected) `
        'The existing service has another account or executable.'
    return $service
}
function Wait-OwnedService([string]$Root, [string]$State) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 45) {
        $service = Read-OwnedService $Root
        Assert ($null -ne $service) 'The owned service disappeared during upgrade.'
        if ($service.State -ceq $State) { return $service }
        Start-Sleep -Milliseconds 250
    }
    throw "The service did not reach $State within 45 seconds."
}
function Invoke-Registration([string]$Root) {
    $script = Join-Path $Root 'scripts\Register-ResourceManager.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Argument $script) +
        ' -PackageRoot ' + (Quote-Argument $Root) + ' -NoSelfElevate'
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    try { Assert ($process.ExitCode -eq 0) "Directory registration failed: $($process.ExitCode)" }
    finally { $process.Dispose() }
}
function Invoke-ServiceAction([string]$Root, [bool]$Restart) {
    $launcher = Join-Path $Root 'Bin\ResourceManagerLauncher\ResourceManager.Launcher.exe'
    $arguments = if ($Restart) { '--service-action --restart' } else { '--service-action' }
    $process = Start-Process -FilePath $launcher -ArgumentList $arguments `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($launcher)) -WindowStyle Hidden -Wait -PassThru
    try { Assert ($process.ExitCode -eq 0) "Service action failed: $($process.ExitCode)" }
    finally { $process.Dispose() }
}
function Wait-BackendReady {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 60) {
        try {
            $response = Invoke-WebRequest -Uri 'http://127.0.0.1:9321/api/public/v1' `
                -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'The updated backend did not become ready within 60 seconds.'
}

function Invoke-OwnedUpgrade(
    [string]$InstallRoot,
    [string]$StageRoot,
    [string]$BackupRoot,
    [object]$PriorService,
    [string]$Version
) {
    $wasRunning = $null -ne $PriorService -and $PriorService.State -ceq 'Running'
    $oldStartMode = if ($null -eq $PriorService) { $null } else { [string]$PriorService.StartMode }
    $stopped = $false
    $movedBin = $false
    $movedScripts = $false
    $movedDocs = $false
    $copiedDocs = $false
    $rootFilesTouched = $false
    try {
        if ($wasRunning) {
            Stop-Service -Name $serviceName -ErrorAction Stop
            $stopped = $true
            $null = Wait-OwnedService $InstallRoot 'Stopped'
        }
        New-Item -ItemType Directory -Path $BackupRoot | Out-Null
        Move-Item -LiteralPath (Join-Path $InstallRoot 'Bin') -Destination (Join-Path $BackupRoot 'Bin')
        $movedBin = $true
        Move-Item -LiteralPath (Join-Path $InstallRoot 'scripts') -Destination (Join-Path $BackupRoot 'scripts')
        $movedScripts = $true
        $oldDocs = Join-Path $InstallRoot 'docs'
        if (Test-Path -LiteralPath $oldDocs) {
            Assert-UnderRoot $InstallRoot $oldDocs
            Assert-UnderRoot $InstallRoot (Join-Path $BackupRoot 'docs')
            Assert (((Get-Item -LiteralPath $oldDocs).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
                'Installed docs is a reparse point.'
            Move-Item -LiteralPath $oldDocs -Destination (Join-Path $BackupRoot 'docs')
            $movedDocs = $true
        }
        Copy-Item -LiteralPath (Join-Path $StageRoot 'Bin') -Destination (Join-Path $InstallRoot 'Bin') -Recurse
        Copy-Item -LiteralPath (Join-Path $StageRoot 'scripts') -Destination (Join-Path $InstallRoot 'scripts') -Recurse
        $stageDocs = Join-Path $StageRoot 'docs'
        if (Test-Path -LiteralPath $stageDocs -PathType Container) {
            $copiedDocs = $true
            Copy-Item -LiteralPath $stageDocs -Destination $oldDocs -Recurse
        }
        foreach ($relative in @('ResourceManager\ResourceManager.exe',
                'ResourceManagerNativeUi\ResourceManager.NativeUi.exe',
                'ResourceManagerLauncher\ResourceManager.Launcher.exe')) {
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $StageRoot 'Bin') $relative)).Hash
            $installedHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $InstallRoot 'Bin') $relative)).Hash
            Assert ($sourceHash -ceq $installedHash) "Installed binary differs: $relative"
        }
        Invoke-Registration $InstallRoot
        if ($wasRunning) {
            Invoke-ServiceAction $InstallRoot $false
            $newService = Wait-OwnedService $InstallRoot 'Running'
            Assert ($newService.StartMode -ceq $oldStartMode) 'Service startup preference changed.'
            Wait-BackendReady
        }
        New-Item -ItemType Directory -Path (Join-Path $BackupRoot 'RootFiles') | Out-Null
        foreach ($name in $rootFiles) {
            $installedFile = Join-Path $InstallRoot $name
            if (Test-Path -LiteralPath $installedFile -PathType Leaf) {
                Copy-Item -LiteralPath $installedFile -Destination (Join-Path $BackupRoot 'RootFiles')
            }
            $fresh = Join-Path $StageRoot $name
            if (Test-Path -LiteralPath $fresh -PathType Leaf) {
                $rootFilesTouched = $true
                Copy-Item -LiteralPath $fresh -Destination $installedFile -Force
            }
        }
        [pscustomobject]@{
            action = 'upgraded'; installRoot = $InstallRoot; version = $Version
            backupRoot = $BackupRoot; serviceRestarted = $wasRunning
        } | ConvertTo-Json
    }
    catch {
        $failure = $_.Exception.Message
        if (-not $stopped -and -not $movedBin -and -not $movedScripts -and -not $rootFilesTouched) {
            if ($wasRunning) {
                $currentService = Read-OwnedService $InstallRoot
                if ($null -ne $currentService -and $currentService.State -ceq 'Stopped') {
                    Invoke-ServiceAction $InstallRoot $false
                }
            }
            throw "Upgrade stopped before file replacement: $failure"
        }
        try {
            $currentService = Read-OwnedService $InstallRoot
            if ($null -ne $currentService -and $currentService.State -ceq 'Running') {
                Stop-Service -Name $serviceName -ErrorAction Stop
                $null = Wait-OwnedService $InstallRoot 'Stopped'
            }
            if ($movedBin) {
                $newBin = Join-Path $InstallRoot 'Bin'
                if (Test-Path -LiteralPath $newBin) {
                    Assert-UnderRoot $InstallRoot (Join-Path $BackupRoot 'failed-Bin')
                    Move-Item -LiteralPath $newBin -Destination (Join-Path $BackupRoot 'failed-Bin')
                }
                Move-Item -LiteralPath (Join-Path $BackupRoot 'Bin') -Destination $newBin
            }
            if ($movedScripts) {
                $newScripts = Join-Path $InstallRoot 'scripts'
                if (Test-Path -LiteralPath $newScripts) {
                    Assert-UnderRoot $InstallRoot (Join-Path $BackupRoot 'failed-scripts')
                    Move-Item -LiteralPath $newScripts -Destination (Join-Path $BackupRoot 'failed-scripts')
                }
                Move-Item -LiteralPath (Join-Path $BackupRoot 'scripts') -Destination $newScripts
            }
            if ($copiedDocs -and (Test-Path -LiteralPath (Join-Path $InstallRoot 'docs'))) {
                Assert-UnderRoot $InstallRoot (Join-Path $BackupRoot 'failed-docs')
                Move-Item -LiteralPath (Join-Path $InstallRoot 'docs') -Destination (Join-Path $BackupRoot 'failed-docs')
            }
            if ($movedDocs) {
                Move-Item -LiteralPath (Join-Path $BackupRoot 'docs') -Destination (Join-Path $InstallRoot 'docs')
            }
            if ($rootFilesTouched) {
                $failedFiles = Join-Path $BackupRoot 'failed-root-files'
                Assert-UnderRoot $InstallRoot $failedFiles
                New-Item -ItemType Directory -Path $failedFiles | Out-Null
                foreach ($name in $rootFiles) {
                    $oldFile = Join-Path (Join-Path $BackupRoot 'RootFiles') $name
                    $installedFile = Join-Path $InstallRoot $name
                    if (Test-Path -LiteralPath $oldFile -PathType Leaf) {
                        Copy-Item -LiteralPath $oldFile -Destination $installedFile -Force
                    }
                    elseif (Test-Path -LiteralPath $installedFile -PathType Leaf) {
                        Move-Item -LiteralPath $installedFile -Destination (Join-Path $failedFiles $name)
                    }
                }
            }
            if ($movedBin -and $movedScripts) { Invoke-Registration $InstallRoot }
            if ($wasRunning -and $stopped) {
                Invoke-ServiceAction $InstallRoot $false
                $null = Wait-OwnedService $InstallRoot 'Running'
            }
        }
        catch {
            throw "Upgrade failed: $failure. Rollback also failed: $($_.Exception.Message). Backup: $BackupRoot"
        }
        throw "Upgrade failed: $failure. Old files and service restored; backup: $BackupRoot"
    }
}

try {
    Assert ([IO.Directory]::Exists($packageRoot)) 'Release package directory is missing.'
    Assert (((Get-Item -LiteralPath $packageRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        'Release package root is a reparse point.'
    $manifestPath = Join-Path $packageRoot 'release-manifest.json'
    Assert ([IO.File]::Exists($manifestPath)) 'Release manifest is missing.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert (-not [string]::IsNullOrWhiteSpace([string]$manifest.sourceCommit) -and
        -not [string]::IsNullOrWhiteSpace([string]$manifest.version)) `
        'Release provenance is missing.'
    $registeredRoot = Read-RegisteredRoot
    $isInstalledRoot = $null -ne $registeredRoot -and
        [string]::Equals($registeredRoot, $packageRoot, [StringComparison]::OrdinalIgnoreCase)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in @($manifest.files)) {
        $relative = [string]$record.path
        Assert (-not [IO.Path]::IsPathRooted($relative) -and $relative -notmatch '(^|/)\.\.(/|$)' -and
            $relative -notmatch '\\' -and $seen.Add($relative)) "Unsafe or duplicate member: $relative"
        $file = Join-Path $packageRoot $relative.Replace('/', '\')
        Assert ([IO.File]::Exists($file)) "Missing release member: $relative"
        $item = Get-Item -LiteralPath $file
        Assert ($item.Length -eq [long]$record.length -and
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ceq [string]$record.sha256) `
            "Release member changed: $relative"
    }
    if (-not $isInstalledRoot) {
        $queue = [Collections.Generic.Queue[string]]::new()
        $queue.Enqueue($packageRoot)
        $actualFiles = 0
        while ($queue.Count -gt 0) {
            $directory = $queue.Dequeue()
            foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
                Assert (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
                    "Reparse point in release package: $($item.FullName)"
                if ($item.PSIsContainer) { $queue.Enqueue($item.FullName); continue }
                $relative = $item.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
                Assert ($relative -ceq 'release-manifest.json' -or $seen.Contains($relative)) `
                    "Unexpected release member: $relative"
                $actualFiles++
            }
        }
        Assert ($actualFiles -eq $seen.Count + 1) 'Release package member count differs from its manifest.'
        foreach ($name in @('UserData', 'Dependencies', 'Misc')) {
            Assert (-not (Test-Path -LiteralPath (Join-Path $packageRoot $name))) `
                "Release package contains runtime state: $name"
        }
        Assert (@(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Config') -Force).Count -eq 0) `
            'Release Config must be empty.'
    }
    $targetRoot = if ($null -eq $registeredRoot) { $packageRoot } else { $registeredRoot }
    if ($null -ne $registeredRoot) {
        Assert ([IO.Directory]::Exists($registeredRoot)) 'Registered install directory is absent.'
        Assert (((Get-Item -LiteralPath $registeredRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
            'Registered install directory is a reparse point.'
        $service = Read-OwnedService $registeredRoot
        Assert ($null -eq $service -or $service.State -in @('Running', 'Stopped')) `
            'The owned service is changing state; wait before upgrading.'
    }
    else {
        Assert ($null -eq (Get-CimInstance Win32_Service -Filter "Name='$serviceName'")) `
            'An unowned service blocks a new installation.'
        $service = $null
    }
    . (Join-Path $packageRoot 'scripts\ResourceManager.LegacyStartup.ps1')
    $userRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try { $null = Assert-ResourceManagerLegacyUserRegistration -RegistryRoot $userRegistry }
    finally { $userRegistry.Dispose() }
    $legacyTask = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop |
        Where-Object { $_.TaskName -ceq 'ResourceManager.UserLogonBootstrap' })
    Assert ($legacyTask.Count -le 1) 'Duplicate legacy startup tasks were found.'
    if ($legacyTask.Count -eq 1) {
        Assert (Test-ResourceManagerLegacyStartupAction -Actions @($legacyTask[0].Actions)) `
            'Legacy startup task has an unexpected action.'
    }
    if ($PlanOnly) {
        $action = if ($null -eq $registeredRoot) { 'register' }
            elseif ([string]::Equals($registeredRoot, $packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
                'refresh-in-place'
            } else { 'upgrade-owned-directory' }
        [pscustomobject]@{
            action = $action; packageRoot = $packageRoot; installRoot = $targetRoot
            serviceState = if ($null -eq $service) { 'Absent' } else { $service.State }
            sourceCommit = $manifest.sourceCommit; version = $manifest.version
        } | ConvertTo-Json
        return
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $currentSid = $identity.User.Value }
    finally { $identity.Dispose() }
    if (-not (Test-Administrator)) {
        if ($NoSelfElevate) { throw 'Administrator permission is required to register this release.' }
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote-Argument $PSCommandPath) +
            ' -PackageRoot ' + (Quote-Argument $packageRoot) + ' -NoSelfElevate -CallerSid ' +
            (Quote-Argument $currentSid)
        $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        try { exit $process.ExitCode }
        finally { $process.Dispose() }
    }
    if (-not [string]::IsNullOrWhiteSpace($CallerSid)) {
        Assert ($currentSid -ceq $CallerSid) 'Elevation used another Windows user profile.'
    }

    if ($null -eq $registeredRoot) {
        Invoke-Registration $packageRoot
        [pscustomobject]@{ action = 'registered'; installRoot = $packageRoot; version = $manifest.version } |
            ConvertTo-Json
        return
    }
    if ([string]::Equals($registeredRoot, $packageRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Invoke-Registration $registeredRoot
        if ($null -ne $service -and $service.State -ceq 'Running') {
            Invoke-ServiceAction $registeredRoot $true
            $null = Wait-OwnedService $registeredRoot 'Running'
            Wait-BackendReady
        }
        [pscustomobject]@{ action = 'refreshed'; installRoot = $registeredRoot
            version = $manifest.version } | ConvertTo-Json
        return
    }

    $backupRoot = Join-Path $registeredRoot ('upgrade-backup-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
    Assert (-not (Test-Path -LiteralPath $backupRoot)) 'Upgrade backup already exists.'
    Assert (-not $packageRoot.StartsWith($registeredRoot + '\', [StringComparison]::OrdinalIgnoreCase)) `
        'Stage an update outside the installed directory.'
    foreach ($name in @('Bin', 'scripts')) {
        $old = Join-Path $registeredRoot $name
        Assert-UnderRoot $registeredRoot $old
        Assert-UnderRoot $registeredRoot (Join-Path $backupRoot $name)
        Assert ([IO.Directory]::Exists($old) -and [IO.Directory]::Exists((Join-Path $packageRoot $name))) `
            "Missing installed or packaged $name directory."
        Assert (((Get-Item -LiteralPath $old).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
            "Installed $name is a reparse point."
    }
    Assert (@(Get-CimInstance Win32_Process -Filter "Name='ResourceManager.NativeUi.exe'").Count -eq 0) `
        'Close the Native UI before upgrading.'
    Invoke-OwnedUpgrade $registeredRoot $packageRoot $backupRoot $service $manifest.version
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
