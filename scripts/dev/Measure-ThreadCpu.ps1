param(
    [Parameter(Mandatory)][string]$OutPath,
    [string]$ProcessName = 'ResourceManager',
    [int]$Seconds = 15,
    [int]$Top = 15
)
$ErrorActionPreference = 'Stop'
# Measures one process's real CPU per thread: user and kernel time deltas over -Seconds, busiest threads first,
# plus the process totals, written to -OutPath. Use it instead of dotnet-trace sampled-thread-time, which counts
# a thread waiting inside a managed frame (for example the finalizer thread) as CPU. Thread ids match the ids in
# a dotnet-trace speedscope of the same process. Reading another account's thread times needs elevation, so a
# non-elevated call re-runs this script elevated and waits. Read-only.

$OutPath = [IO.Path]::GetFullPath($OutPath)
New-Item -ItemType Directory -Force (Split-Path $OutPath) | Out-Null
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Start-Process joins arguments with spaces and does not quote them, so paths are quoted here.
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -OutPath `"$OutPath`" " +
        "-ProcessName `"$ProcessName`" -Seconds $Seconds -Top $Top"
    $child = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $arguments
    if (Test-Path -LiteralPath $OutPath) { Get-Content -LiteralPath $OutPath }
    exit $child.ExitCode
}

$process = Get-Process -Name $ProcessName | Select-Object -First 1
$before = @{}
foreach ($thread in $process.Threads) {
    try { $before[$thread.Id] = @($thread.UserProcessorTime.TotalMilliseconds, $thread.PrivilegedProcessorTime.TotalMilliseconds) }
    catch { }
}
Start-Sleep -Seconds $Seconds
$process.Refresh()
$rows = foreach ($thread in $process.Threads) {
    try {
        $start = $before[$thread.Id]
        if (-not $start) { $start = @(0, 0) }
        [pscustomobject]@{
            Tid = $thread.Id
            UserPct = [math]::Round(($thread.UserProcessorTime.TotalMilliseconds - $start[0]) / ($Seconds * 10), 2)
            KernelPct = [math]::Round(($thread.PrivilegedProcessorTime.TotalMilliseconds - $start[1]) / ($Seconds * 10), 2)
            Priority = $thread.CurrentPriority
            State = "$($thread.ThreadState)/$($thread.WaitReason)"
        }
    }
    catch { }
}
$user = [math]::Round(($rows | Measure-Object UserPct -Sum).Sum, 1)
$kernel = [math]::Round(($rows | Measure-Object KernelPct -Sum).Sum, 1)
"$ProcessName pid $($process.Id), $Seconds s, percent of one core: user $user, kernel $kernel, threads $($process.Threads.Count)" |
    Set-Content -LiteralPath $OutPath -Encoding utf8
$rows | Sort-Object { $_.UserPct + $_.KernelPct } -Descending | Select-Object -First $Top |
    Format-Table -AutoSize | Out-String -Width 200 | Add-Content -LiteralPath $OutPath -Encoding utf8
