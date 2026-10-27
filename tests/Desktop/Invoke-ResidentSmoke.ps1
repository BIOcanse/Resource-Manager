#requires -Version 7.0
<#
.SYNOPSIS
Real phase-A tray/NativeUi acceptance, isolated from the installed product.
.DESCRIPTION
Run through the repository JobQueue. OutputRoot must already be a registered,
running generated workspace; this script neither registers nor completes it.
Only spare-monitor, passive windows are shown. All images use HWND PrintWindow.
The backend is deliberately unavailable, so this does not certify WebView,
backend readiness, localization quality, or release cold-start performance.
Prepare the shared isolated NativeUi package once with
Initialize-NativeUiFixture.ps1; this script only verifies that preparation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ResidentExe,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$WindowExe,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$OutputRoot,
    [ValidateRange(1024, 65535)][int]$ApiPort = 19321,
    [string]$FixtureExe,
    [string]$PreparedPackageRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$owned = [System.Collections.Generic.List[object]]::new()
$checks = [System.Collections.Generic.List[object]]::new()
$limitations = [System.Collections.Generic.List[string]]::new()
$samples = [System.Collections.Generic.List[object]]::new()
$snapshots = [System.Collections.Generic.List[object]]::new()
$failure = $null
$runRoot = $null
$foreground = [IntPtr]::Zero
$runStarted = [DateTime]::UtcNow
$status = 'failed'
$setupComplete = $false

function Assert-Smoke([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Add-Check([string]$Name, [object]$Evidence) {
    $checks.Add([ordered]@{ name = $Name; status = 'passed'; evidence = $Evidence })
}
function Resolve-TestExecutable([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    Assert-Smoke ([IO.Path]::GetExtension($resolved) -eq '.exe') "Expected executable: $resolved"
    foreach ($programRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($programRoot) {
            Assert-Smoke (-not $resolved.StartsWith($programRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Installed executables cannot be used for this test.'
        }
    }
    return $resolved
}
function Assert-IsolatedPlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    Assert-Smoke ($full.StartsWith($OutputRoot + '\', [StringComparison]::OrdinalIgnoreCase)) 'Prepared package must remain inside OutputRoot.'
    $cursor = $full; $count = 0
    while (-not [string]::IsNullOrEmpty($cursor)) {
        Assert-Smoke (++$count -le 513) 'Prepared package ancestor count exceeds its bound.'
        if (Test-Path -LiteralPath $cursor) {
            Assert-Smoke (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Prepared package path contains a reparse point: $cursor"
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Get-RuleKey([Security.AccessControl.FileSystemAccessRule]$Rule) {
    return '{0}|{1}|{2}|{3}|{4}|{5}' -f $Rule.IdentityReference.Value, [int]$Rule.FileSystemRights,
        [int]$Rule.InheritanceFlags, [int]$Rule.PropagationFlags, [int]$Rule.AccessControlType, $Rule.IsInherited
}
function Verify-PreparedNativeUiPackage {
    if (-not $PreparedPackageRoot) { $script:PreparedPackageRoot = Join-Path $OutputRoot 'native-ui-pipe-package' }
    $script:PreparedPackageRoot = [IO.Path]::GetFullPath($PreparedPackageRoot).TrimEnd('\')
    Assert-IsolatedPlainPath $PreparedPackageRoot
    Assert-Smoke (Test-Path -LiteralPath $PreparedPackageRoot -PathType Container) 'NativeUi fixture is missing. Run Initialize-NativeUiFixture.ps1 once before smoke acceptance.'
    Assert-Smoke (-not (Test-Path -LiteralPath (Join-Path $PreparedPackageRoot 'no-backend.exe'))) 'The deliberately unavailable backend path must not exist in the prepared fixture.'
    $sharedDll = Join-Path ([IO.Path]::GetDirectoryName($WindowExe)) 'ResourceManager.Shared.dll'
    Assert-Smoke (Test-Path -LiteralPath $sharedDll -PathType Leaf) 'The WindowExe build is missing its ResourceManager.Shared.dll.'
    $assembly = [Reflection.Assembly]::LoadFrom($sharedDll)
    Assert-Smoke ([string]::Equals([IO.Path]::GetFullPath($assembly.Location), [IO.Path]::GetFullPath($sharedDll), [StringComparison]::OrdinalIgnoreCase)) 'A different build of ResourceManager.Shared.dll is already loaded; run smoke in a fresh pwsh process.'
    $guard = $assembly.GetType('ResourceManager.Shared.Packages.PackageUseGuard', $true)
    $guard.GetMethod('Verify', [type[]]@([string])).Invoke($null, [object[]]@($PreparedPackageRoot)) | Out-Null

    # PackageUserDirectoryAccess currently exposes provisioning only, no Verify.
    # Match its declared ACL using in-memory rules; do not call Prepare/Set-Acl.
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $expected = [System.Collections.Generic.List[string]]::new()
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), [Security.AccessControl.FileSystemRights]::FullControl, $inherit, [Security.AccessControl.PropagationFlags]::None, $allow)
        $expected.Add((Get-RuleKey $rule))
    }
    $creator = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-3-0'), [Security.AccessControl.FileSystemRights]::FullControl, $inherit, [Security.AccessControl.PropagationFlags]::InheritOnly, $allow)
    $expected.Add((Get-RuleKey $creator))
    $userRights = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::CreateDirectories
    $users = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), $userRights, [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None, $allow)
    $expected.Add((Get-RuleKey $users))
    foreach ($relative in @('Config/WebView2', 'Config/Diagnostics/NativeUiStartup')) {
        $directory = Join-Path $PreparedPackageRoot $relative
        Assert-IsolatedPlainPath $directory
        Assert-Smoke (Test-Path -LiteralPath $directory -PathType Container) "NativeUi user-cache fixture is missing: $relative"
        $acl = Get-Acl -LiteralPath $directory
        Assert-Smoke $acl.AreAccessRulesProtected "NativeUi user-cache ACL must be protected: $relative"
        $actual = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object { Get-RuleKey $_ })
        $differences = @(Compare-Object -ReferenceObject @($expected.ToArray() | Sort-Object) -DifferenceObject @($actual | Sort-Object))
        Assert-Smoke ($differences.Count -eq 0) "NativeUi user-cache ACL does not match PackageUserDirectoryAccess: $relative"
    }
    Add-Check 'preflight-prepared-native-ui-package' ([ordered]@{ packageRoot = $PreparedPackageRoot; sharedDll = $sharedDll; guardVerified = $true; userDirectoryAclsVerified = $true; provisioningInvoked = $false })
}
function Register-Owned([System.Diagnostics.Process]$Process, [string]$Role) {
    $entry = [pscustomobject]@{ process = $Process; id = $Process.Id; started = $Process.StartTime.ToUniversalTime(); role = $Role }
    $owned.Add($entry)
    return $entry
}
function Test-OwnedAlive([object]$Entry) {
    try {
        $candidate = [Diagnostics.Process]::GetProcessById($Entry.id)
        try { return $candidate.StartTime.ToUniversalTime() -eq $Entry.started -and -not $candidate.HasExited }
        finally { $candidate.Dispose() }
    } catch [ArgumentException] { return $false }
}
function Start-Resident([string]$Label, [string]$ChildExe, [string]$ChildArguments = '', [switch]$UseFixturePackage) {
    $pipe = 'ResourceManager.Desktop.Test.' + [Guid]::NewGuid().ToString('N')
    $snapshot = Join-Path $runRoot ($Label + '-snapshot.json')
    $package = $PreparedPackageRoot
    if ($UseFixturePackage) {
        $package = Join-Path $runRoot ($Label + '-package')
        [IO.Directory]::CreateDirectory($package) | Out-Null
    }
    $start = [Diagnostics.ProcessStartInfo]::new($ResidentExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $runRoot
    $start.Environment['RESOURCE_MANAGER_PACKAGE_ROOT'] = $package
    $arguments = @('--background-startup', '--instance-pipe', $pipe,
        '--backend-service', $pipe, '--backend-port', $ApiPort.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--backend-path', (Join-Path $package 'no-backend.exe'), '--package-root', $package,
        '--window-executable', $ChildExe, '--child-arguments', $ChildArguments, '--diagnostics', $snapshot)
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $entry = Register-Owned $process ('resident-' + $Label)
    $entry | Add-Member -NotePropertyName childExe -NotePropertyValue $ChildExe
    return [pscustomobject]@{ entry = $entry; pipe = $pipe; snapshot = $snapshot; label = $Label; childExe = $ChildExe; packageRoot = $package }
}
function Start-ExitClient([object]$Round) {
    Assert-Smoke ($Round.pipe.StartsWith('ResourceManager.Desktop.Test.', [StringComparison]::Ordinal)) 'Refusing the production instance pipe.'
    $start = [Diagnostics.ProcessStartInfo]::new($ResidentExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $runRoot
    $start.Environment['RESOURCE_MANAGER_PACKAGE_ROOT'] = $Round.packageRoot
    foreach ($argument in @('--instance-pipe', $Round.pipe, '--exit-existing')) { $start.ArgumentList.Add($argument) }
    return Register-Owned ([Diagnostics.Process]::Start($start)) ('client-' + $Round.label)
}
function Read-Snapshot([object]$Round) {
    if (-not (Test-Path -LiteralPath $Round.snapshot -PathType Leaf)) { return $null }
    # Allow the host's atomic replacement while retaining a complete old/new image.
    try {
        $stream = [IO.FileStream]::new($Round.snapshot, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try { $reader = [IO.StreamReader]::new($stream); $value = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $stream.Dispose() }
    }
    catch [IO.IOException] { return $null }
    catch [ArgumentException] { return $null }
    if ($null -eq $value) { return $null }
    Assert-Smoke ([int]$value.pid -eq $Round.entry.id) 'Diagnostic PID does not belong to the launched resident.'
    return $value
}
function Wait-Snapshot([object]$Round, [scriptblock]$Predicate, [string]$Description, [int]$TimeoutSeconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Assert-Smoke (Test-OwnedAlive $Round.entry) "Resident exited while waiting for $Description."
        $value = Read-Snapshot $Round
        if ($null -ne $value -and (& $Predicate $value)) { return $value }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Description."
}
function Save-Snapshot([string]$Name, [object]$Value) {
    $path = Join-Path $runRoot ($Name + '.json')
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
    $snapshots.Add([ordered]@{ name = $Name; path = $path })
}
function Send-InstanceCommand([object]$Round, [ValidateSet('show-passive-secondary', 'exit')][string]$Command) {
    Assert-Smoke ($Round.pipe.StartsWith('ResourceManager.Desktop.Test.', [StringComparison]::Ordinal)) 'Refusing the production instance pipe.'
    $client = [IO.Pipes.NamedPipeClientStream]::new('.', $Round.pipe, [IO.Pipes.PipeDirection]::Out)
    try {
        $client.Connect(3000)
        $line = [Text.Encoding]::UTF8.GetBytes($Command + "`n")
        $client.Write($line, 0, $line.Length)
        $client.Flush()
    } finally { $client.Dispose() }
}
function Remember-Child([object]$Round, [object]$Snapshot) {
    $childId = [int]$Snapshot.windowPid
    Assert-Smoke ($childId -gt 0) 'Missing window process PID.'
    $children = @([ResidentSmokeNative]::Children([uint32]$Round.entry.id))
    Assert-Smoke (@($children | Where-Object { $_.Pid -eq $childId }).Count -eq 1) 'Window process is not a direct child of this resident.'
    $child = [Diagnostics.Process]::GetProcessById($childId)
    $entry = Register-Owned $child ('window-' + $Round.label)
    Assert-Smoke ([string]::Equals($child.MainModule.FileName, $Round.childExe, [StringComparison]::OrdinalIgnoreCase)) 'Window child executable does not match the requested development binary.'
    return $entry
}
function Wait-Exited([object]$Entry) {
    Assert-Smoke ($Entry.process.WaitForExit(15000)) "Owned $($Entry.role) did not exit."
    Assert-Smoke ($Entry.process.ExitCode -eq 0) "Owned $($Entry.role) exited with code $($Entry.process.ExitCode)."
}
function Assert-ForegroundUnchanged {
    Assert-Smoke ([ResidentSmokeNative]::GetForegroundWindow() -eq $foreground) 'Foreground changed; stop visible automation to avoid desktop interference.'
}
function Get-Tray([object]$Round) {
    $snapshot = Wait-Snapshot $Round { param($s) [long]$s.trayWindow -ne 0 } 'tray registration'
    $tray = [IntPtr]::new([long]$snapshot.trayWindow)
    Assert-Smoke ([ResidentSmokeNative]::Owner($tray) -eq $Round.entry.id) 'Tray window ownership mismatch.'
    Assert-Smoke ([ResidentSmokeNative]::ClassName($tray) -eq 'ResourceManager.Desktop.Tray') 'Unexpected tray window class.'
    Assert-Smoke (-not [ResidentSmokeNative]::IsWindowVisible($tray)) 'Tray host window must be hidden.'
    Add-Check ($Round.label + '-tray-icon') ([ResidentSmokeNative]::TrayRect($tray))
    return $tray
}
function Open-DiagnosticMenu([object]$Round, [IntPtr]$Tray, [object]$Monitor, [string]$CaptureName) {
    Assert-ForegroundUnchanged
    $x = $Monitor.Work.Left + 32; $y = $Monitor.Work.Top + 32
    Assert-Smoke ($x -ge -32768 -and $x -le 32767 -and $y -ge -32768 -and $y -le 32767) 'Secondary monitor coordinates exceed the diagnostic message range.'
    $packed = [long](([uint32]($x -band 0xffff)) -bor (([uint32]($y -band 0xffff)) -shl 16))
    [ResidentSmokeNative]::Post($Tray, 0x800a, 0, $packed)
    $deadline = [DateTime]::UtcNow.AddSeconds(5); $menu = [IntPtr]::Zero
    while ($menu -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        $menu = [ResidentSmokeNative]::Menu([uint32]$Round.entry.id)
        if ($menu -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 100 }
    }
    Assert-Smoke ($menu -ne [IntPtr]::Zero) 'The diagnostic tray menu did not appear.'
    Assert-ForegroundUnchanged
    Assert-Smoke ($Monitor.Bounds.Contains([ResidentSmokeNative]::Bounds($menu))) 'Tray menu was not contained on the secondary monitor.'
    [ResidentSmokeNative]::CaptureWindow($menu, [uint32]$Round.entry.id, (Join-Path $runRoot $CaptureName))
    return $menu
}

try {
    Assert-Smoke $IsWindows 'This smoke test requires Windows.'
    Assert-Smoke ($ApiPort -notin @(9321, 9333)) 'Ports 9321 and 9333 belong to the installed product and are forbidden.'
    $ResidentExe = Resolve-TestExecutable $ResidentExe
    $WindowExe = Resolve-TestExecutable $WindowExe
    if ($FixtureExe) { $FixtureExe = Resolve-TestExecutable $FixtureExe }
    $OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).ProviderPath.TrimEnd('\')
    $markerPath = Join-Path $OutputRoot '.generated-workspace.json'
    Assert-Smoke (Test-Path -LiteralPath $markerPath -PathType Leaf) 'OutputRoot must be registered by Manage-GeneratedWorkspace before execution.'
    $marker = [IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
    Assert-Smoke ($marker.state -eq 'running' -and [string]::Equals([IO.Path]::GetFullPath($marker.workspacePath).TrimEnd('\'), $OutputRoot, [StringComparison]::OrdinalIgnoreCase)) 'OutputRoot must be its own running generated workspace.'
    Assert-Smoke (((Get-Item -LiteralPath $OutputRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'OutputRoot cannot be a reparse point.'
    $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    Assert-Smoke (@($listeners | Where-Object Port -eq $ApiPort).Count -eq 0) 'The isolated test port is already in use.'
    $runRoot = Join-Path $OutputRoot ('smoke-' + [Guid]::NewGuid().ToString('N'))
    # A bounded result subdirectory inside the existing task, not another task/workspace.
    [IO.Directory]::CreateDirectory($runRoot) | Out-Null
    Verify-PreparedNativeUiPackage
    if (-not ('ResidentSmokeNative' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ResidentSmokeNative.cs') }
    $setupComplete = $true
    $foreground = [ResidentSmokeNative]::GetForegroundWindow()
    $secondary = @([ResidentSmokeNative]::Monitors() | Where-Object { -not $_.Primary } | Select-Object -First 1)

    # Do not wait for tray/snapshot/pipe readiness here: this targets the startup
    # interval after primary ownership but before its pipe listener is ready.
    $startup = Start-Resident 'startup-client-race' $WindowExe (' --resident-api-base http://127.0.0.1:' + $ApiPort)
    $startupClient = Start-ExitClient $startup
    Assert-Smoke (@([ResidentSmokeNative]::Children([uint32]$startup.entry.id)).Count -eq 0) 'Immediate-exit startup spawned a window child.'
    Wait-Exited $startupClient
    Wait-Exited $startup.entry
    $startupFinal = Read-Snapshot $startup
    Assert-Smoke ($null -ne $startupFinal -and $startupFinal.ending -and [int]$startupFinal.windowPid -eq 0 -and [long]$startupFinal.windowHandle -eq 0) 'Immediate second-instance exit did not drain the background primary without a child.'
    Save-Snapshot 'startup-client-race-final' $startupFinal
    Add-Check 'immediate-second-instance-exit-before-readiness' ([ordered]@{ primaryExitCode = $startup.entry.process.ExitCode; clientExitCode = $startupClient.process.ExitCode; childCount = 0 })

    $round = Start-Resident 'real' $WindowExe (' --resident-api-base http://127.0.0.1:' + $ApiPort)
    $tray = Get-Tray $round
    $background = Wait-Snapshot $round { param($s) [int]$s.windowPid -eq 0 -and [long]$s.windowHandle -eq 0 -and -not $s.ending } 'background-only startup'
    Save-Snapshot 'background' $background
    Assert-Smoke ([ResidentSmokeNative]::Send($tray, 0x11, 0, 0) -eq 1) 'WM_QUERYENDSESSION must return TRUE.'
    [ResidentSmokeNative]::Send($tray, 0x16, 0, 0) | Out-Null
    Start-Sleep -Milliseconds 200
    Assert-Smoke (-not (Read-Snapshot $round).ending) 'Unconfirmed session ending must not end the resident.'
    Add-Check 'query-and-cancelled-session' $true
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $round.entry.process.Refresh()
    $cpuStart = $round.entry.process.TotalProcessorTime.TotalMilliseconds
    while ($clock.Elapsed.TotalSeconds -lt 10) {
        Assert-Smoke (Test-OwnedAlive $round.entry) 'Resident exited during tray-only sampling.'
        $round.entry.process.Refresh()
        $children = @([ResidentSmokeNative]::Children([uint32]$round.entry.id))
        Assert-Smoke ($children.Count -eq 0) 'Background startup spawned a child process.'
        $sample = Read-Snapshot $round
        Assert-Smoke ($null -ne $sample -and [int]$sample.windowPid -eq 0) 'A window child appeared during tray sampling.'
        $samples.Add([ordered]@{ elapsedSeconds = $clock.Elapsed.TotalSeconds; cpuMilliseconds = $round.entry.process.TotalProcessorTime.TotalMilliseconds;
            privateBytes = $round.entry.process.PrivateMemorySize64; workingSetBytes = $round.entry.process.WorkingSet64; childCount = $children.Count })
        Start-Sleep -Milliseconds 500
    }
    $round.entry.process.Refresh()
    $residentModules = @($round.entry.process.Modules | ForEach-Object ModuleName)
    Assert-Smoke (@($residentModules | Where-Object { $_ -in @('clr.dll', 'coreclr.dll') }).Count -eq 0) 'Tray resident loaded a .NET runtime.'
    $cpuPercentOneCore = 100 * ($round.entry.process.TotalProcessorTime.TotalMilliseconds - $cpuStart) / $clock.Elapsed.TotalMilliseconds
    Add-Check 'tray-only-10s-no-child' ([ordered]@{ elapsedSeconds = $clock.Elapsed.TotalSeconds; cpuPercentOneCore = $cpuPercentOneCore;
        cpuPercentMachine = $cpuPercentOneCore / [Environment]::ProcessorCount; privateBytes = $round.entry.process.PrivateMemorySize64; workingSetBytes = $round.entry.process.WorkingSet64 })
    Add-Check 'tray-resident-no-clr' $residentModules

    if ($secondary.Count -eq 1) {
        Assert-ForegroundUnchanged
        $monitor = $secondary[0]
        try {
            $menu = Open-DiagnosticMenu $round $tray $monitor 'tray-menu.bmp'
            Add-Check 'tray-menu-window-capture' 'tray-menu.bmp'
        } finally { [ResidentSmokeNative]::Post($tray, 0x1f, 0, 0) }
        $menuDeadline = [DateTime]::UtcNow.AddSeconds(3)
        while ([ResidentSmokeNative]::Menu([uint32]$round.entry.id) -ne [IntPtr]::Zero -and [DateTime]::UtcNow -lt $menuDeadline) { Start-Sleep -Milliseconds 100 }
        Assert-Smoke ([ResidentSmokeNative]::Menu([uint32]$round.entry.id) -eq [IntPtr]::Zero) 'Diagnostic menu did not close after WM_CANCELMODE.'
        foreach ($opening in @(1, 2)) {
            Assert-ForegroundUnchanged
            Send-InstanceCommand $round 'show-passive-secondary'
            $opened = Wait-Snapshot $round { param($s) [int]$s.windowPid -gt 0 -and [long]$s.windowHandle -ne 0 } 'real NativeUi window'
            $child = Remember-Child $round $opened
            $window = [IntPtr]::new([long]$opened.windowHandle)
            Assert-Smoke ([ResidentSmokeNative]::Owner($window) -eq $child.id) 'NativeUi HWND owner mismatch.'
            Assert-Smoke ([ResidentSmokeNative]::IsWindowVisible($window)) 'NativeUi window was not visible.'
            Assert-ForegroundUnchanged
            Assert-Smoke ($monitor.Bounds.Contains([ResidentSmokeNative]::Bounds($window))) 'NativeUi was not contained on the secondary monitor.'
            Start-Sleep -Milliseconds 250
            [ResidentSmokeNative]::CaptureWindow($window, [uint32]$child.id, (Join-Path $runRoot ("native-ui-${opening}.bmp")))
            Save-Snapshot ("native-ui-${opening}") $opened
            Add-Check ("passive-secondary-open-${opening}") ([ordered]@{ pid = $child.id; hwnd = $window.ToInt64(); capture = "native-ui-${opening}.bmp" })
            if ($opening -eq 1) {
                [ResidentSmokeNative]::Post($window, 0x10, 0, 0)
                Wait-Exited $child
                $closed = Wait-Snapshot $round { param($s) [int]$s.windowPid -eq 0 -and [long]$s.windowHandle -eq 0 -and -not $s.ending } 'user-close returning to tray'
                Save-Snapshot 'after-user-close' $closed
                Add-Check 'user-close-child-exits-resident-stays' $true
            }
        }
        [ResidentSmokeNative]::Post($tray, 0x111, 2, 0)
        Wait-Exited $child
        Wait-Exited $round.entry
        Add-Check 'tray-exit-drains-real-child-and-resident' $true
        Assert-ForegroundUnchanged
    } else {
        $limitations.Add('没有副屏：跳过托盘菜单显示、真实 NativeUi 被动开窗/关窗/重开与截图；未在主屏显示测试窗口。')
        [ResidentSmokeNative]::Post($tray, 0x111, 2, 0)
        Wait-Exited $round.entry
        Add-Check 'tray-exit-background-resident' $true
    }
    $final = Read-Snapshot $round
    Assert-Smoke ($null -ne $final -and $final.ending) 'Exit did not persist the ending diagnostic.'
    Save-Snapshot 'tray-exit-final' $final

    $session = Start-Resident 'session-ending' $WindowExe (' --resident-api-base http://127.0.0.1:' + $ApiPort)
    $sessionTray = Get-Tray $session
    Assert-Smoke ([ResidentSmokeNative]::Send($sessionTray, 0x11, 0, 0) -eq 1) 'Session-ending query refused shutdown.'
    [ResidentSmokeNative]::Post($sessionTray, 0x16, 1, 0)
    Wait-Exited $session.entry
    $ended = Read-Snapshot $session
    Assert-Smoke ($null -ne $ended -and $ended.ending -and [int]$ended.windowPid -eq 0) 'Confirmed WM_ENDSESSION did not drain the resident.'
    Save-Snapshot 'confirmed-session-final' $ended
    Add-Check 'confirmed-session-ending' $true

    if ($secondary.Count -eq 1) {
        foreach ($exitKind in @('pipe-exit', 'session-ending')) {
            $menuRound = Start-Resident ('menu-' + $exitKind) $WindowExe (' --resident-api-base http://127.0.0.1:' + $ApiPort)
            $menuTray = Get-Tray $menuRound
            $activeMenu = Open-DiagnosticMenu $menuRound $menuTray $secondary[0] ('menu-' + $exitKind + '.bmp')
            # Deliberately no WM_CANCELMODE or synthetic menu input: stopping the
            # tray owner must close its live TrackPopupMenu loop on its own.
            if ($exitKind -eq 'pipe-exit') { Send-InstanceCommand $menuRound 'exit' }
            else { [ResidentSmokeNative]::Post($menuTray, 0x16, 1, 0) }
            Wait-Exited $menuRound.entry
            Assert-Smoke (-not [ResidentSmokeNative]::IsWindow($activeMenu)) 'Tray menu survived its resident process.'
            Assert-Smoke ([ResidentSmokeNative]::Menu([uint32]$menuRound.entry.id) -eq [IntPtr]::Zero) 'A menu window remains after resident shutdown.'
            $menuFinal = Read-Snapshot $menuRound
            Assert-Smoke ($null -ne $menuFinal -and $menuFinal.ending -and [int]$menuFinal.windowPid -eq 0) 'Expanded-menu shutdown did not persist a drained ending snapshot.'
            Save-Snapshot ('menu-' + $exitKind + '-final') $menuFinal
            Assert-ForegroundUnchanged
            Add-Check ('expanded-menu-' + $exitKind + '-drains-without-input') $true
        }
    }

    if ($FixtureExe) {
        # Existing window_process_child.zig accepts arbitrary non-closing --mode
        # values; it waits for stop and reports a PID stand-in, never a real HWND.
        $fixtureRound = Start-Resident 'fixture' $FixtureExe ' --mode smoke-wait' -UseFixturePackage
        $fixtureTray = Get-Tray $fixtureRound
        Send-InstanceCommand $fixtureRound 'show-passive-secondary'
        $fixtureSnapshot = Wait-Snapshot $fixtureRound { param($s) [int]$s.windowPid -gt 0 -and [long]$s.windowHandle -ne 0 } 'protocol fixture'
        $fixtureChild = Remember-Child $fixtureRound $fixtureSnapshot
        Send-InstanceCommand $fixtureRound 'exit'
        Wait-Exited $fixtureChild
        Wait-Exited $fixtureRound.entry
        Add-Check 'optional-fixture-single-instance-exit' $true
    }
    foreach ($entry in $owned) { Assert-Smoke (-not (Test-OwnedAlive $entry)) "Owned process remains: $($entry.role)." }
    Add-Check 'owned-processes-drained' $true
    $status = if ($limitations.Count -eq 0) { 'passed' } else { 'partial' }
} catch {
    $failure = $_.ToString()
    if (-not $setupComplete) {
        $status = 'failed-setup'
        $checks.Add([ordered]@{ name = 'preflight'; status = 'failed-setup'; evidence = $failure })
    } else { $checks.Add([ordered]@{ name = 'smoke-failure'; status = 'failed'; evidence = $failure }) }
} finally {
    # Never enumerate/terminate the installed product, process names, services or
    # an arbitrary process tree. The enclosing JobQueue owns the overall job.
    $cleanup = [System.Collections.Generic.List[object]]::new()
    # A failure may precede the child HWND handshake. Take one bounded native
    # snapshot per still-owned resident and remember only its exact expected child.
    foreach ($resident in @($owned.ToArray() | Where-Object { $_.role.StartsWith('resident-') })) {
        try {
            if (Test-OwnedAlive $resident) {
                foreach ($candidate in [ResidentSmokeNative]::Children([uint32]$resident.id)) {
                    if (@($owned.ToArray() | Where-Object id -eq $candidate.Pid).Count -gt 0) { continue }
                    $candidateProcess = [Diagnostics.Process]::GetProcessById([int]$candidate.Pid)
                    if ([string]::Equals($candidateProcess.MainModule.FileName, $resident.childExe, [StringComparison]::OrdinalIgnoreCase)) {
                        Register-Owned $candidateProcess 'window-before-handshake' | Out-Null
                    } else { $candidateProcess.Dispose() }
                }
            }
        } catch {
            $status = 'failed'
            $cleanup.Add([ordered]@{ pid = $resident.id; role = $resident.role; discoveryError = $_.ToString() })
        }
    }
    for ($i = $owned.Count - 1; $i -ge 0; $i--) {
        $entry = $owned[$i]
        try {
            if (Test-OwnedAlive $entry) {
                $status = 'failed'
                Stop-Process -InputObject $entry.process -ErrorAction Stop
                $exited = $entry.process.WaitForExit(5000)
                $cleanup.Add([ordered]@{ pid = $entry.id; role = $entry.role; forced = $true; exited = $exited })
            }
        } catch {
            $status = 'failed'
            $cleanup.Add([ordered]@{ pid = $entry.id; role = $entry.role; error = $_.ToString() })
        }
    }
    if ($null -ne $runRoot) {
        $result = [ordered]@{ schemaVersion = 1; status = $status; startedUtc = $runStarted.ToString('o'); endedUtc = [DateTime]::UtcNow.ToString('o');
            residentExe = $ResidentExe; windowExe = $WindowExe; fixtureExe = $FixtureExe; preparedPackageRoot = $PreparedPackageRoot; apiPort = $ApiPort; outputRoot = $runRoot;
            checks = @($checks.ToArray()); limitations = @($limitations.ToArray()); traySamples = @($samples.ToArray()); snapshots = @($snapshots.ToArray());
            cleanup = @($cleanup.ToArray()); failure = $failure; captures = 'HWND PrintWindow only; BMP; no desktop capture' }
        [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), ($result | ConvertTo-Json -Depth 64), [Text.UTF8Encoding]::new($false))
        $report = [System.Collections.Generic.List[string]]::new()
        $report.Add('# 常驻进程真实桌面验收')
        $report.Add('')
        $report.Add("状态：$status。隔离端口：$ApiPort。只操作本轮显式 Test 管道与自有进程。")
        $report.Add("真实 NativeUi 共用已准备隔离包根：$PreparedPackageRoot。正常管理员环境先运行 Initialize-NativeUiFixture.ps1 准备一次，再通过 JobQueue 重跑 smoke；本脚本只读验证 guard 和用户目录 ACL，不修改保护。")
        $report.Add('')
        foreach ($check in $checks) { $report.Add("- $($check.name)：$($check.status)") }
        foreach ($limitation in $limitations) { $report.Add("- 限制：$limitation") }
        $report.Add('')
        $report.Add('资源样本、控制器快照和失败清理见 result.json；图像仅由目标窗口 PrintWindow 生成。Debug、后端不可用的本轮结果不证明 WebView 首帧、正式发布冷启动、后端身份验证、九语译文或覆盖层验收。')
        if ($failure) { $report.Add(''); $report.Add('失败：' + $failure) }
        [IO.File]::WriteAllLines((Join-Path $runRoot 'report.md'), $report, [Text.UTF8Encoding]::new($false))
        Write-Output (Join-Path $runRoot 'result.json')
    }
    foreach ($entry in $owned) { $entry.process.Dispose() }
}
if ($status -in @('failed', 'failed-setup')) {
    if ($failure) { [Console]::Error.WriteLine($failure) }
    exit 1
}
# Partial evidence is successful execution, clearly distinguished in the receipt.
exit 0
