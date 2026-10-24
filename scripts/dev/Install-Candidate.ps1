param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$OutPath,
    [string]$InstallRoot = 'C:\Program Files\ResourceManager Beta',
    [int]$DebugPort = 9333,
    [switch]$NoUiRestart
)
$ErrorActionPreference = 'Stop'
# Installs a locally built candidate package over an existing install with the package's own update
# manager (--plan-only, then --apply), the same path a real update takes, so an older installed manager
# cannot copy the new files wrongly. Before applying, it closes only this install's NativeUi; afterwards
# it logs the result, the registered manager, the uninstall entry and the service state, and restarts the
# UI with WebView2 remote debugging on -DebugPort for read-only checks. A non-elevated call re-runs this
# script elevated and waits.

$Package = [IO.Path]::GetFullPath($Package)
$OutPath = [IO.Path]::GetFullPath($OutPath)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-Package', $Package, '-OutPath', $OutPath, '-InstallRoot', $InstallRoot,
        '-DebugPort', $DebugPort, '-NoUiRestart')
    $child = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $arguments
    Get-Content -LiteralPath $OutPath
    if ($child.ExitCode -ne 0) { exit $child.ExitCode }
    # The UI belongs to the signed-in user, so it is started from the non-elevated caller.
    if (-not $NoUiRestart) {
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$DebugPort"
        Start-Process (Join-Path $InstallRoot 'Start.exe')
        "ui started with debugging port $DebugPort"
    }
    exit 0
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
    Log 'done'
}
catch {
    Log ('FAILED ' + $_.Exception.Message)
    exit 1
}
