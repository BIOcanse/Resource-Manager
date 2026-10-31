#Requires -Version 7
<# Run by maintenance after installing a candidate. Starts the real user entry and leaves the product running.
   Example: pwsh -NoProfile -File scripts/dev/Test-InstalledDesktopStart.ps1 -InstallRoot '<installed root>' -TimeoutSeconds 30 #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 30,
    [ValidateRange(1, 10000)][int]$PollIntervalMilliseconds = 200
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../ResourceManager.DesktopLayout.ps1')
$layout = Get-ResourceManagerDesktopLayout
$root = [IO.Path]::GetFullPath($InstallRoot)
$start = Join-Path $root 'Start.exe'
$resident = Join-Path $root ('Bin/' + $layout.DirectoryName + '/' + $layout.ResidentExecutableName)
$window = Join-Path $root ('Bin/' + $layout.DirectoryName + '/' + $layout.WindowExecutableName)
foreach ($file in @($start, $resident, $window)) { if (-not [IO.File]::Exists($file)) { throw "Missing installed executable: $file" } }
function Find-Image([string]$Path) {
    foreach ($process in @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Path)) -ErrorAction SilentlyContinue)) {
        try { if ([string]::Equals($process.MainModule.FileName, $Path, [StringComparison]::OrdinalIgnoreCase) -and -not $process.HasExited) { $process.Id } }
        catch [InvalidOperationException] { }
        finally { $process.Dispose() }
    }
}
$watch = [Diagnostics.Stopwatch]::StartNew()
$entry = Start-Process -FilePath $start -WorkingDirectory $root -WindowStyle Hidden -PassThru
try {
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($entry.HasExited -and $entry.ExitCode -ne 0) { throw "Start.exe failed: $($entry.ExitCode)" }
        $residentPids = @(Find-Image $resident)
        $windowPids = @(Find-Image $window)
        if ($residentPids.Count -gt 0 -and $windowPids.Count -gt 0) {
            [pscustomobject]@{ Accepted = $true; Start = $start; ResidentPids = $residentPids; WindowPids = $windowPids }
            return
        }
        Start-Sleep -Milliseconds ([Math]::Min($PollIntervalMilliseconds, [Math]::Max(1, [int](($TimeoutSeconds - $watch.Elapsed.TotalSeconds) * 1000))))
    }
    throw "Start.exe did not leave both resident and window running within $TimeoutSeconds seconds. Resident PIDs: $($residentPids -join ','); window PIDs: $($windowPids -join ',')."
} finally { $entry.Dispose() }
