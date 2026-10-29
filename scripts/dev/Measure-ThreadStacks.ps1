#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$ProcessName = 'ResourceManager',
    # The CPU window is the trace window minus 4 s, so it needs at least 5.
    [ValidateRange(5, [int]::MaxValue)][int]$Seconds = 30,
    [int]$Top = 8,
    # Omitted: dotnet-trace from PATH, else the global tool under the user profile.
    [string]$DotnetTrace
)
$ErrorActionPreference = 'Stop'
# Attributes one process's real CPU to code. Over the same window it records a dotnet-trace speedscope and the
# per-thread user and kernel time deltas, then writes stacks.txt: for each of the -Top busiest threads, the
# product frames (first ResourceManager / FreedomPoint / FanControl / Ledger / BraidLink frame from the leaf)
# and leaves where it spends its running time. Spans whose leaf is a wait or sleep are left out, because the
# sampler records waiting managed threads too (the finalizer waiting in RunFinalizers looks like CPU otherwise).
# Reading the SYSTEM service needs elevation, so a non-elevated call re-runs this script elevated and waits.
# Read-only; the trace stays in -OutDir.
$OutDir = [IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force $OutDir | Out-Null
if (-not $DotnetTrace) {
    $DotnetTrace = (Get-Command dotnet-trace -ErrorAction SilentlyContinue).Source
    if (-not $DotnetTrace) { $DotnetTrace = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-trace.exe' }
}
if (-not (Test-Path -LiteralPath $DotnetTrace)) { throw "dotnet-trace not found: $DotnetTrace" }
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Start-Process joins arguments with spaces and does not quote them, so paths are quoted here.
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -OutDir `"$OutDir`" " +
        "-ProcessName `"$ProcessName`" -Seconds $Seconds -Top $Top -DotnetTrace `"$DotnetTrace`""
    $child = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $arguments
    $summary = Join-Path $OutDir 'stacks.txt'
    if (Test-Path -LiteralPath $summary) { Get-Content -LiteralPath $summary }
    exit $child.ExitCode
}

$process = Get-Process -Name $ProcessName | Select-Object -First 1
$trace = Join-Path $OutDir 'service.nettrace'
# dotnet-trace reads --duration as dd:hh:mm:ss, so every field is carried, not only the seconds.
$span = [TimeSpan]::FromSeconds($Seconds)
$duration = '{0:D2}:{1:D2}:{2:D2}:{3:D2}' -f $span.Days, $span.Hours, $span.Minutes, $span.Seconds
$tracer = Start-Process -FilePath $DotnetTrace -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $OutDir 'dotnet-trace.out') -RedirectStandardError (Join-Path $OutDir 'dotnet-trace.err') `
    -ArgumentList @('collect', '-p', $process.Id, '--duration', $duration, '--format', 'Speedscope', '-o', $trace)
# The CPU window sits inside the trace window so that every measured span also has samples.
Start-Sleep -Seconds 2
$window = $Seconds - 4
$before = @{}
foreach ($thread in $process.Threads) {
    try { $before[$thread.Id] = @($thread.UserProcessorTime.TotalMilliseconds, $thread.PrivilegedProcessorTime.TotalMilliseconds) } catch { }
}
Start-Sleep -Seconds $window
$process.Refresh()
$threads = foreach ($thread in $process.Threads) {
    if (-not $before.ContainsKey($thread.Id)) { continue }
    try {
        [pscustomobject]@{
            Tid = $thread.Id
            UserPct = [math]::Round(($thread.UserProcessorTime.TotalMilliseconds - $before[$thread.Id][0]) / ($window * 10), 2)
            KernelPct = [math]::Round(($thread.PrivilegedProcessorTime.TotalMilliseconds - $before[$thread.Id][1]) / ($window * 10), 2)
        }
    } catch { }
}
$threads = @($threads | Sort-Object { $_.UserPct + $_.KernelPct } -Descending)
$tracer.WaitForExit()
if ($tracer.ExitCode -ne 0) { throw "dotnet-trace failed with exit code $($tracer.ExitCode)" }

$speedscope = Get-Content -Raw -LiteralPath ([IO.Path]::ChangeExtension($trace, '.speedscope.json')) | ConvertFrom-Json
$frames = @($speedscope.shared.frames | ForEach-Object { [string]$_.name })
$profiles = @{}
foreach ($profile in $speedscope.profiles) {
    if ($profile.name -match '\((\d+)\)') { $profiles[[int]$Matches[1]] = $profile }
}
$wait = 'Wait|Sleep|SpinWait|Monitor\.Enter|LowLevelLifoSemaphore|Semaphore|Join|ReadFile|GetQueuedCompletionStatus|' +
    'WorkerThread\.WorkerThreadStart|UNMANAGED_CODE_TIME'
$product = '^(ResourceManager|Resource Manager|FreedomPoint|FanControl|Ledger|BraidLink)'
$lines = [System.Collections.Generic.List[string]]::new()
$total = ($threads | Measure-Object -Property UserPct -Sum).Sum + ($threads | Measure-Object -Property KernelPct -Sum).Sum
$lines.Add(('pid={0} window={1}s total={2:N1}% of one core; private={3:N0} MiB threads={4}' -f
    $process.Id, $window, $total, ($process.PrivateMemorySize64 / 1MB), $process.Threads.Count))
foreach ($row in $threads | Select-Object -First $Top) {
    $lines.Add('')
    $lines.Add(('== tid {0}: user {1}% kernel {2}% of one core' -f $row.Tid, $row.UserPct, $row.KernelPct))
    $profile = $profiles[[int]$row.Tid]
    if (-not $profile) { $lines.Add('   no managed samples (native thread or idle)'); continue }
    # Evented profile: the time between two open/close events belongs to the stack open at that moment.
    $owners = @{}; $leaves = @{}; $running = 0.0; $spanTotal = 0.0
    $stack = [System.Collections.Generic.List[int]]::new()
    $last = [double]$profile.startValue
    foreach ($event in $profile.events) {
        $span = [double]$event.at - $last
        if ($stack.Count -gt 0 -and $span -gt 0) {
            $spanTotal += $span
            $leaf = $frames[$stack[$stack.Count - 1]]
            if ($leaf -notmatch $wait) {
                $running += $span
                $leaves[$leaf] = [double]$leaves[$leaf] + $span
                for ($index = $stack.Count - 1; $index -ge 0; $index--) {
                    $name = $frames[$stack[$index]]
                    if ($name -match $product) { $owners[$name] = [double]$owners[$name] + $span; break }
                }
            }
        }
        $last = [double]$event.at
        if ($event.type -eq 'O') { $stack.Add([int]$event.frame) }
        elseif ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }
    }
    if ($spanTotal -le 0) { $lines.Add('   no samples in the window'); continue }
    $lines.Add(('   running share of samples: {0:P0}' -f ($running / $spanTotal)))
    foreach ($entry in $owners.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6) {
        $lines.Add(('   product {0,5:P0}  {1}' -f ($entry.Value / [math]::Max($running, 1)), $entry.Key.Substring(0, [math]::Min(150, $entry.Key.Length))))
    }
}
$lines | Set-Content -LiteralPath (Join-Path $OutDir 'stacks.txt') -Encoding utf8
