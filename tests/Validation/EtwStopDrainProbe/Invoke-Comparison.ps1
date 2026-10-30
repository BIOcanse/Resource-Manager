param(
    [Parameter(Mandatory)][string]$ProbeDll,
    [Parameter(Mandatory)][string]$Ledger
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllText((Join-Path $Ledger 'comparison-exit.txt'), 'running')
$modes = @('legacy', 'drain', 'legacy', 'drain')
$index = 0
$result = 0
while ($index -lt $modes.Count) {
    $delayed = $index -ge 2
    & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-Probe.ps1') `
        -Mode $modes[$index] -ProbeDll $ProbeDll -Ledger $Ledger -Delayed:$delayed
    if ($LASTEXITCODE -ne 0) { $result = $LASTEXITCODE; break }
    $index++
}
[IO.File]::WriteAllText((Join-Path $Ledger 'comparison-exit.txt'), [string]$result)
exit $result
