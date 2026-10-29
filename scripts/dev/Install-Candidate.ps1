param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$OutPath,
    [string]$InstallRoot = 'C:\Program Files\ResourceManager Beta',
    [int]$DebugPort = 9333,
    [int]$ServicePort = 9321,
    [switch]$NoUiRestart
)
$ErrorActionPreference = 'Stop'
# Installs a locally built candidate package over an existing install with the package's own update
# manager (--plan-only, then --apply), the same path a real update takes, so an older installed manager
# cannot copy the new files wrongly. Before applying, it closes only this install's NativeUi; afterwards
# it logs the result, the registered manager, the uninstall entry and the service state, and restarts the
# UI with WebView2 remote debugging on -DebugPort for read-only checks. A non-elevated call re-runs this
# script elevated and waits. The UI requires an elevated administrator and environment variables do not
# cross a UAC prompt, so the elevated run starts it: the debugging port reaches the UI only from there.

$Package = [IO.Path]::GetFullPath($Package)
$OutPath = [IO.Path]::GetFullPath($OutPath)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Start-Process joins arguments with spaces and does not quote them, so paths are quoted here.
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Package `"$Package`" " +
        "-OutPath `"$OutPath`" -InstallRoot `"$InstallRoot`" -DebugPort $DebugPort -ServicePort $ServicePort"
    if ($NoUiRestart) { $arguments += ' -NoUiRestart' }
    $child = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $arguments
    # Only the elevated script itself is awaited: Start-Process -Wait would also wait for the UI it starts.
    $child.WaitForExit()
    if (Test-Path -LiteralPath $OutPath) { Get-Content -LiteralPath $OutPath }
    exit $child.ExitCode
}

function Log([string]$text) { ((Get-Date -Format o) + ' ' + $text) | Add-Content -LiteralPath $OutPath }
New-Item -ItemType Directory -Force (Split-Path $OutPath) | Out-Null
Set-Content -LiteralPath $OutPath -Value ((Get-Date -Format o) + " start package=$Package")
try {
    $manager = Join-Path $Package 'Internal\UpdateManager\ResourceManager.UpdateManager.exe'
    if (-not (Test-Path -LiteralPath $manager)) { throw "The package has no update manager: $manager" }
    $key = 'HKLM:\Software\ResourceManager'

    $ui = @(Get-Process -Name 'ResourceManager.NativeUi' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) })
    foreach ($process in $ui) { [void]$process.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and @($ui | Where-Object { -not $_.HasExited }).Count -gt 0) { Start-Sleep -Milliseconds 500 }
    foreach ($process in @($ui | Where-Object { -not $_.HasExited })) { Stop-Process -Id $process.Id -Force }
    Log "closed ui processes=$($ui.Count)"

    Log ('before UpdateManagerPath=' + (Get-ItemProperty $key).UpdateManagerPath)
    $plan = Start-Process -FilePath $manager -ArgumentList "--plan-only `"$Package`" `"$InstallRoot`"" -WindowStyle Hidden -Wait -PassThru
    Log "plan exit=$($plan.ExitCode)"
    if ($plan.ExitCode -ne 0) { throw 'The update plan failed.' }
    $apply = Start-Process -FilePath $manager -ArgumentList "--apply `"$Package`" `"$InstallRoot`"" -WindowStyle Hidden -Wait -PassThru
    Log "apply exit=$($apply.ExitCode)"
    foreach ($result in (Join-Path (Split-Path $manager) 'last-operation-result.json'),
        (Join-Path $InstallRoot 'Internal\Management\last-operation-result.json')) {
        if (Test-Path -LiteralPath $result) { Log ("$result " + (Get-Content -LiteralPath $result -Raw)) }
    }
    if ($apply.ExitCode -ne 0) { throw 'The update apply failed.' }

    $expected = Join-Path $InstallRoot 'Internal\Management\ResourceManager.UpdateManager.exe'
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline -and (Get-ItemProperty $key).UpdateManagerPath -ne $expected) { Start-Sleep -Seconds 2 }
    Log ('after UpdateManagerPath=' + (Get-ItemProperty $key).UpdateManagerPath)
    $uninstall = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager'
    Log ("uninstall DisplayVersion=$($uninstall.DisplayVersion) Quiet=$($uninstall.QuietUninstallString)")
    Log ('installed manager matches package: ' + ((Get-FileHash -LiteralPath $expected).Hash -eq (Get-FileHash -LiteralPath $manager).Hash))
    Log ('service ' + (Get-Service ResourceManager.Service).Status)
    # The service saves the normal running point and restores it on start, so an install keeps the user's point and
    # never creates one at install time (a point taken mid-build is not a normal running point, user 10-28). Log the
    # restored state so an install that leaves protection inactive is visible.
    $status = $null; $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $deadline -and -not $status) {
        try { $status = Invoke-RestMethod -Uri "http://127.0.0.1:$ServicePort/api/memory-protection" -UseDefaultCredentials -AllowUnencryptedAuthentication -TimeoutSec 10 } catch { Start-Sleep -Seconds 3 }
    }
    Log "memory protection enabled=$($status.enabled) active=$($status.active) inactiveReason=$($status.inactiveReason) point=$($status.normalRunningPoint.at)"
    if (-not $NoUiRestart) {
        # Started from this elevated process, the launcher starts the UI with its own (elevated) token, so the
        # UI inherits this variable. Through a UAC prompt it would not.
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$DebugPort"
        Start-Process (Join-Path $InstallRoot 'Start.exe')
        Log "ui started with debugging port $DebugPort"
    }
    Log 'done'
}
catch {
    Log ('FAILED ' + $_.Exception.Message)
    exit 1
}
