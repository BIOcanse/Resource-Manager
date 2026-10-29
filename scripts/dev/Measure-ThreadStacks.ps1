#Requires -Version 7
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$ProcessName = 'ResourceManager',
    # The CPU window is the trace window minus 4 s, so it needs at least 5.
    [ValidateRange(5, [int]::MaxValue)][int]$Seconds = 30,
    [int]$Top = 8,
    # Omitted: dotnet-trace from PATH, else the global tool under the user profile.
    [string]$DotnetTrace,
    # Frame-name patterns whose running time is summed over all threads, e.g. 'BasicMetricReader\.ReadSingle'.
    [string[]]$Frame = @(),
    # Re-attributes a directory recorded earlier (service.speedscope.json and threads.json) without recording.
    [switch]$AttributeOnly
)
$ErrorActionPreference = 'Stop'
# Attributes one process's real CPU to code. Over the same window it records a dotnet-trace speedscope and the
# per-thread user and kernel time deltas (threads.json), then writes stacks.txt: for each of the -Top busiest
# threads, the product frames (first ResourceManager / FreedomPoint / FanControl / Ledger / BraidLink frame from
# the leaf) and the frames where it spends its running time; then, for each -Frame pattern, the running time of
# every stack that contains a matching frame, summed over all threads.
# Every sampled stack of the thread-time speedscope ends in a pseudo-frame: CPU_TIME while the thread ran managed
# code, UNMANAGED_CODE_TIME while it was in native or kernel code. Neither says whether the thread was running,
# so a span is decided by its real frame, the one just below the pseudo-frame: a wait or sleep there is left out
# (the sampler records waiting threads too; the finalizer waiting in GC.RunFinalizers carries CPU_TIME), and
# everything else counts as running, native and kernel time included (a kernel table query is UNMANAGED_CODE_TIME).
# Running time is sampled time, not measured CPU: compare it with the thread deltas, which are the real CPU.
# Reading the SYSTEM service needs elevation, so a non-elevated recording re-runs this script elevated and waits.
# Read-only; the trace stays in -OutDir.
$OutDir = [IO.Path]::GetFullPath($OutDir)
$speedscopePath = Join-Path $OutDir 'service.speedscope.json'
$threadsPath = Join-Path $OutDir 'threads.json'
if ($AttributeOnly) {
    # Without threads.json there are no real per-thread CPU deltas: only the -Frame totals are written.
    $threads = @(if (Test-Path -LiteralPath $threadsPath) {
        Get-Content -Raw -LiteralPath $threadsPath | ConvertFrom-Json | Sort-Object { $_.UserPct + $_.KernelPct } -Descending })
    $total = ($threads | Measure-Object -Property UserPct -Sum).Sum + ($threads | Measure-Object -Property KernelPct -Sum).Sum
    $header = if ($threads.Count -gt 0) { 'recorded {0}; total={1:N1}% of one core' -f $OutDir, $total }
        else { 'recorded {0}; no thread CPU deltas (threads.json absent or empty)' -f $OutDir }
}
else {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    if (-not $DotnetTrace) {
        $DotnetTrace = (Get-Command dotnet-trace -ErrorAction SilentlyContinue).Source
        if (-not $DotnetTrace) { $DotnetTrace = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-trace.exe' }
    }
    if (-not (Test-Path -LiteralPath $DotnetTrace)) { throw "dotnet-trace not found: $DotnetTrace" }
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        # An encoded command carries paths and the -Frame array intact; -File would pass 'a,b' as one string.
        $quote = { param([string]$value) "'" + ($value -replace "'", "''") + "'" }
        $command = "& $(& $quote $PSCommandPath) -OutDir $(& $quote $OutDir) -ProcessName $(& $quote $ProcessName) " +
            "-Seconds $Seconds -Top $Top -DotnetTrace $(& $quote $DotnetTrace) -Frame @($(($Frame | ForEach-Object { & $quote $_ }) -join ','))"
        $arguments = '-NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
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
    ConvertTo-Json -InputObject $threads | Set-Content -LiteralPath $threadsPath -Encoding utf8
    $total = ($threads | Measure-Object -Property UserPct -Sum).Sum + ($threads | Measure-Object -Property KernelPct -Sum).Sum
    $header = 'pid={0} window={1}s total={2:N1}% of one core; private={3:N0} MiB threads={4}' -f
        $process.Id, $window, $total, ($process.PrivateMemorySize64 / 1MB), $process.Threads.Count
}

$speedscope = Get-Content -Raw -LiteralPath $speedscopePath | ConvertFrom-Json
$frames = @($speedscope.shared.frames | ForEach-Object { [string]$_.name })
$pseudo = '^(CPU_TIME|UNMANAGED_CODE_TIME)$'
# Matched against the real frame. GC.RunFinalizers as the top real frame is the finalizer thread waiting for work
# (a running finalizer has its own frame above it); PollGC is a thread parked for a garbage collection.
$wait = 'Wait|Sleep|SpinWait|Monitor\.Enter|LowLevelLifoSemaphore|Semaphore|Join|ReadFile|GetQueuedCompletionStatus|' +
    'WorkerThread\.WorkerThreadStart|GC\.RunFinalizers\(|PollGC'
$product = '^(ResourceManager|Resource Manager|FreedomPoint|FanControl|Ledger|BraidLink)'
# Per frame index: which -Frame patterns it matches, so a stack's membership is kept as counts while it changes.
$frameHits = [object[]]::new($frames.Count)
for ($index = 0; $index -lt $frames.Count; $index++) {
    $hits = [bool[]]::new($Frame.Count)
    for ($pattern = 0; $pattern -lt $Frame.Count; $pattern++) { $hits[$pattern] = $frames[$index] -match $Frame[$pattern] }
    $frameHits[$index] = $hits
}

# Evented profile: the time between two open/close events belongs to the stack open at that moment.
function Measure-Timeline($timeline, [bool]$detail) {
    $result = [pscustomobject]@{ Running = 0.0; Native = 0.0; Total = 0.0; Owners = @{}; Leaves = @{}; Frames = [double[]]::new($Frame.Count) }
    $stack = [System.Collections.Generic.List[int]]::new()
    $open = [int[]]::new($Frame.Count)
    $last = [double]$timeline.startValue
    foreach ($event in $timeline.events) {
        $span = [double]$event.at - $last
        if ($stack.Count -gt 0 -and $span -gt 0) {
            $result.Total += $span
            $top = $frames[$stack[$stack.Count - 1]]
            $realIndex = if ($top -match $pseudo) { $stack.Count - 2 } else { $stack.Count - 1 }
            $real = if ($realIndex -ge 0) { $frames[$stack[$realIndex]] } else { '(no managed frame)' }
            if ($real -notmatch $wait) {
                $result.Running += $span
                if ($top -eq 'UNMANAGED_CODE_TIME') { $result.Native += $span }
                for ($pattern = 0; $pattern -lt $Frame.Count; $pattern++) { if ($open[$pattern] -gt 0) { $result.Frames[$pattern] += $span } }
                if ($detail) {
                    $result.Leaves[$real] = [double]$result.Leaves[$real] + $span
                    for ($index = $realIndex; $index -ge 0; $index--) {
                        $name = $frames[$stack[$index]]
                        if ($name -match $product) { $result.Owners[$name] = [double]$result.Owners[$name] + $span; break }
                    }
                }
            }
        }
        $last = [double]$event.at
        if ($event.type -eq 'O') {
            $stack.Add([int]$event.frame)
            for ($pattern = 0; $pattern -lt $Frame.Count; $pattern++) { if ($frameHits[[int]$event.frame][$pattern]) { $open[$pattern]++ } }
        }
        elseif ($stack.Count -gt 0) {
            $closed = $stack[$stack.Count - 1]
            $stack.RemoveAt($stack.Count - 1)
            for ($pattern = 0; $pattern -lt $Frame.Count; $pattern++) { if ($frameHits[$closed][$pattern]) { $open[$pattern]-- } }
        }
    }
    $result
}

$timelines = @{}
foreach ($timeline in $speedscope.profiles) {
    if ($timeline.name -match '\((\d+)\)') { $timelines[[int]$Matches[1]] = $timeline }
}
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add($header)
foreach ($row in $threads | Select-Object -First $Top) {
    $lines.Add('')
    $lines.Add(('== tid {0}: user {1}% kernel {2}% of one core' -f $row.Tid, $row.UserPct, $row.KernelPct))
    $timeline = $timelines[[int]$row.Tid]
    if (-not $timeline) { $lines.Add('   no managed samples (native thread or idle)'); continue }
    $measured = Measure-Timeline $timeline $true
    if ($measured.Total -le 0) { $lines.Add('   no samples in the window'); continue }
    $running = [math]::Max($measured.Running, 1)
    $lines.Add(('   running share of samples: {0:P0}; native or kernel share of running: {1:P0}' -f
        ($measured.Running / $measured.Total), ($measured.Native / $running)))
    foreach ($entry in $measured.Owners.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6) {
        $lines.Add(('   product {0,5:P0}  {1}' -f ($entry.Value / $running), $entry.Key.Substring(0, [math]::Min(150, $entry.Key.Length))))
    }
    foreach ($entry in $measured.Leaves.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 4) {
        $lines.Add(('   frame   {0,5:P0}  {1}' -f ($entry.Value / $running), $entry.Key.Substring(0, [math]::Min(150, $entry.Key.Length))))
    }
}
if ($Frame.Count -gt 0) {
    # Speedscope values from dotnet-trace are milliseconds; the trace span is the widest profile span.
    $traceMilliseconds = ($speedscope.profiles | ForEach-Object { [double]$_.endValue - [double]$_.startValue } | Measure-Object -Maximum).Maximum
    $sums = [double[]]::new($Frame.Count)
    foreach ($timeline in $speedscope.profiles) {
        $measured = Measure-Timeline $timeline $false
        for ($pattern = 0; $pattern -lt $Frame.Count; $pattern++) { $sums[$pattern] += $measured.Frames[$pattern] }
    }
    $lines.Add('')
    $lines.Add(('== frames, running time over all threads in {0:N1} s of trace' -f ($traceMilliseconds / 1000)))
    for ($pattern = 0; $pattern -lt $Frame.Count; $pattern++) {
        $lines.Add(('   {0,9:N0} ms {1,6:N2}% of one core  {2}' -f $sums[$pattern], (100 * $sums[$pattern] / [math]::Max($traceMilliseconds, 1)), $Frame[$pattern]))
    }
}
$lines | Set-Content -LiteralPath (Join-Path $OutDir 'stacks.txt') -Encoding utf8
