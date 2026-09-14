[CmdletBinding()]
param([switch]$Quiet, [switch]$Restart, [switch]$BackgroundStartup)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $root.OpenSubKey('Software\ResourceManager')
        if ($null -eq $key) { throw 'Run the Resource Manager directory registration first.' }
        try { $launcher = [string]$key.GetValue('LauncherPath') }
        finally { $key.Dispose() }
    }
    finally { $root.Dispose() }
    if (-not [IO.File]::Exists($launcher)) { throw "Registered desktop launcher is missing: $launcher" }
    $arguments = @()
    if ($Restart) { $arguments += '--restart' }
    if ($BackgroundStartup) { $arguments += '--background-startup' }
    $start = @{ FilePath = $launcher; WorkingDirectory = [IO.Path]::GetDirectoryName($launcher)
        PassThru = $true; WindowStyle = 'Hidden' }
    if ($arguments.Count -gt 0) { $start.ArgumentList = $arguments }
    $process = Start-Process @start
    try { $process.WaitForExit(); exit $process.ExitCode }
    finally { $process.Dispose() }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
