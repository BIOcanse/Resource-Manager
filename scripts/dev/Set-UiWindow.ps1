param(
    [Parameter(Mandatory)][ValidateSet('State', 'Minimize', 'Restore', 'Close', 'Open')][string]$Action,
    [string]$InstallRoot = 'C:\Program Files\ResourceManager Beta',
    [int]$DebugPort = 9333
)
$ErrorActionPreference = 'Stop'
# Puts the installed Resource Manager UI into one window state for measurements: minimize or restore the
# main window, close it (the product closes the frontend and stays in the tray), or open it again through
# Start.exe with WebView2 remote debugging. Acts only on this install's NativeUi main window, by window
# messages, without moving the mouse or taking input. Prints the resulting state.

if (-not ('RmUiWindow' -as [type])) {
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class RmUiWindow {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
}
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
function Get-Ui {
    @(Get-Process -Name 'ResourceManager.NativeUi' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }) | Select-Object -First 1
}
function Get-State {
    $ui = Get-Ui
    if (-not $ui) { return 'not-running' }
    $ui.Refresh()
    $window = $ui.MainWindowHandle
    if ($window -eq [IntPtr]::Zero) { return 'tray' }
    if ([RmUiWindow]::IsIconic($window)) { return 'minimized' }
    if ([RmUiWindow]::IsWindowVisible($window)) { return 'visible' }
    return 'hidden'
}

$ui = Get-Ui
switch ($Action) {
    'Minimize' { if ($ui -and $ui.MainWindowHandle -ne [IntPtr]::Zero) { [void][RmUiWindow]::ShowWindowAsync($ui.MainWindowHandle, 6) } }
    'Restore' { if ($ui -and $ui.MainWindowHandle -ne [IntPtr]::Zero) { [void][RmUiWindow]::ShowWindowAsync($ui.MainWindowHandle, 9) } }
    'Close' { if ($ui -and $ui.MainWindowHandle -ne [IntPtr]::Zero) { [void][RmUiWindow]::PostMessage($ui.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) } }
    'Open' {
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$DebugPort"
        Start-Process (Join-Path $InstallRoot 'Start.exe')
    }
}
if ($Action -ne 'State') {
    $deadline = (Get-Date).AddSeconds(20)
    $expected = @{ Minimize = 'minimized'; Restore = 'visible'; Close = 'tray'; Open = 'visible' }[$Action]
    while ((Get-Date) -lt $deadline -and (Get-State) -ne $expected) { Start-Sleep -Milliseconds 500 }
}
Get-State
