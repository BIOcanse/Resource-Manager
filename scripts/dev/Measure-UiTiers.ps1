param(
    [Parameter(Mandatory)][string]$OutDirectory,
    [ValidateSet('visible', 'minimized', 'tray')][string[]]$Tiers = @('visible', 'minimized', 'tray'),
    [int]$SettleSeconds = 45,
    [int]$FootprintSeconds = 60,
    [int]$ThreadSeconds = 20,
    [string]$InstallRoot = 'C:\Program Files\ResourceManager Beta'
)
$ErrorActionPreference = 'Stop'
# Measures the installed Resource Manager in each UI tier, in the order given: puts the UI into the tier with
# Set-UiWindow.ps1, waits -SettleSeconds, then appends the tier to footprint.jsonl (Measure-Footprint.ps1) and
# writes threads-<tier>.txt (Measure-ThreadCpu.ps1, service process). Afterwards the UI returns to the state it
# was in before the run. Both measurement scripts elevate themselves. Changes only the UI window state.

$OutDirectory = [IO.Path]::GetFullPath($OutDirectory)
New-Item -ItemType Directory -Force $OutDirectory | Out-Null
$window = Join-Path $PSScriptRoot 'Set-UiWindow.ps1'
$actions = @{ visible = 'Restore'; minimized = 'Minimize'; tray = 'Close' }

function Set-Tier([string]$Tier) {
    $state = & $window -Action State -InstallRoot $InstallRoot
    if ($state -eq $Tier) { return }
    # A closed UI has no window to restore or minimize, so it is opened first.
    if ($state -in @('tray', 'not-running') -and $Tier -ne 'tray') { $state = & $window -Action Open -InstallRoot $InstallRoot }
    if ($state -ne $Tier) { $state = & $window -Action $actions[$Tier] -InstallRoot $InstallRoot }
    if ($state -ne $Tier) { throw "The UI did not reach '$Tier'; it is '$state'." }
}

$initial = & $window -Action State -InstallRoot $InstallRoot
try {
    foreach ($tier in $Tiers) {
        Set-Tier $tier
        Start-Sleep -Seconds $SettleSeconds
        & (Join-Path $PSScriptRoot 'Measure-Footprint.ps1') -OutPath (Join-Path $OutDirectory 'footprint.jsonl') `
            -Label $tier -Seconds $FootprintSeconds -InstallRoot $InstallRoot | Out-Null
        & (Join-Path $PSScriptRoot 'Measure-ThreadCpu.ps1') -OutPath (Join-Path $OutDirectory "threads-$tier.txt") `
            -Seconds $ThreadSeconds | Select-Object -First 1
    }
}
finally {
    if ($initial -in @('visible', 'minimized', 'tray')) { Set-Tier $initial }
}
