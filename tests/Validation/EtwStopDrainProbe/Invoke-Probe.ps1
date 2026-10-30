param(
    [Parameter(Mandatory)][ValidateSet('legacy','drain')][string]$Mode,
    [Parameter(Mandatory)][string]$ProbeDll,
    [Parameter(Mandatory)][string]$Ledger,
    [switch]$Delayed,
    [switch]$MemoryLimitedChild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$label = if ($Delayed) { "$Mode-delayed" } else { $Mode }
if ($MemoryLimitedChild) {
    $probeArguments = @($ProbeDll, $Mode, (Join-Path $Ledger "$label.jsonl"))
    if ($Delayed) { $probeArguments += 'delayed' }
    & (Join-Path $root 'scripts/dev/Invoke-WithMemoryLimit.ps1') -FilePath (Get-Command dotnet.exe).Source `
        -ArgumentList $probeArguments
    exit $LASTEXITCODE
}
$result = 1
function Save-Sessions([string]$Path) {
    $attempt = 0
    while (++$attempt -le 3) {
        & logman.exe query -ets *> $Path
        if ($LASTEXITCODE -eq 0) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}
try {
    if (-not (Save-Sessions (Join-Path $Ledger "$label-sessions-before.txt"))) { throw 'logman query before failed.' }
    # MemoryLimit exits its host, so give it a separate PowerShell process.
    & (Get-Process -Id $PID).Path -NoProfile -File $PSCommandPath -Mode $Mode -ProbeDll $ProbeDll -Ledger $Ledger -MemoryLimitedChild -Delayed:$Delayed `
        *> (Join-Path $Ledger "$label-console.txt")
    $result = $LASTEXITCODE
}
finally {
    if (-not (Save-Sessions (Join-Path $Ledger "$label-sessions-after.txt"))) { $result = 1 }
    if (Select-String -LiteralPath (Join-Path $Ledger "$label-sessions-after.txt") -Pattern 'RM-stop-drain-probe-' -Quiet) {
        $result = 1
        Add-Content -LiteralPath (Join-Path $Ledger "$label-console.txt") -Value 'Probe session remains; cleanup NOT confirmed.'
    }
    [IO.File]::WriteAllText((Join-Path $Ledger "$label-exit.txt"), [string]$result)
}
exit $result
