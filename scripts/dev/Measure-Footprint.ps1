param(
    [Parameter(Mandatory)][string]$OutPath,
    [string]$Label = 'sample',
    [int]$Seconds = 60,
    [int]$IntervalSeconds = 2,
    [string]$InstallRoot = 'C:\Program Files\ResourceManager Beta'
)
$ErrorActionPreference = 'Stop'
# Measures Resource Manager's own footprint for one UI state: each process running from the install root
# (service, NativeUi, hardware bridge, fan core) with its descendants; WebView2 processes form one
# "webview" group. Samples working set and private bytes every -IntervalSeconds and CPU time at start and
# end, then appends one JSON line per group to -OutPath. Reading another account's CPU time needs
# elevation, so a non-elevated call re-runs this script elevated and waits. Read-only.

$OutPath = [IO.Path]::GetFullPath($OutPath)
New-Item -ItemType Directory -Force (Split-Path $OutPath) | Out-Null
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Start-Process joins arguments with spaces and does not quote them, so paths are quoted here.
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -OutPath `"$OutPath`" " +
        "-Label `"$Label`" -Seconds $Seconds -IntervalSeconds $IntervalSeconds -InstallRoot `"$InstallRoot`""
    $child = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $arguments
    if (Test-Path -LiteralPath $OutPath) { Get-Content -LiteralPath $OutPath -Tail 4 }
    exit $child.ExitCode
}

trap {
    ([ordered]@{ at = (Get-Date).ToString('o'); label = $Label; error = $_.Exception.Message } | ConvertTo-Json -Compress) |
        Add-Content -LiteralPath $OutPath
    exit 1
}
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
function Get-Members {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, ExecutablePath)
    $children = @{}
    foreach ($process in $all) {
        if (-not $children.ContainsKey([int]$process.ParentProcessId)) { $children[[int]$process.ParentProcessId] = [Collections.Generic.List[object]]::new() }
        $children[[int]$process.ParentProcessId].Add($process)
    }
    $groups = @{}
    $queue = [Collections.Generic.Queue[object]]::new()
    foreach ($process in $all) {
        if (-not $process.ExecutablePath -or -not $process.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { continue }
        # Each install-root process is its own group (ResourceManager = service, ResourceManager.NativeUi = UI,
        # HardwareBridge, FanControlCore, ...).
        $groups[[int]$process.ProcessId] = [IO.Path]::GetFileNameWithoutExtension($process.Name)
        $queue.Enqueue($process)
    }
    # Descendants (WebView2 browser tree under NativeUi, helpers under the service) join their root's group.
    $visited = [Collections.Generic.HashSet[int]]::new()
    while ($queue.Count -gt 0 -and $visited.Count -lt 4096) {
        $process = $queue.Dequeue()
        if (-not $visited.Add([int]$process.ProcessId)) { continue }
        if (-not $children.ContainsKey([int]$process.ProcessId)) { continue }
        foreach ($child in $children[[int]$process.ProcessId]) {
            if ($groups.ContainsKey([int]$child.ProcessId)) { continue }
            $groups[[int]$child.ProcessId] = if ($child.Name -like 'msedgewebview2*') { 'webview' } else { $groups[[int]$process.ProcessId] }
            $queue.Enqueue($child)
        }
    }
    $groups
}

function Read-Sample([hashtable]$Groups) {
    $rows = foreach ($processId in $Groups.Keys) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        [pscustomobject]@{ Id = $processId; Group = $Groups[$processId]; WorkingSet = $process.WorkingSet64
            Private = $process.PrivateMemorySize64; Cpu = $process.TotalProcessorTime.TotalMilliseconds }
    }
    @($rows)
}

$groups = Get-Members
$first = Read-Sample $groups
$started = Get-Date
$memory = @{}
while (((Get-Date) - $started).TotalSeconds -lt $Seconds) {
    Start-Sleep -Seconds $IntervalSeconds
    $groups = Get-Members
    foreach ($group in (Read-Sample $groups | Group-Object Group)) {
        if (-not $memory.ContainsKey($group.Name)) { $memory[$group.Name] = [Collections.Generic.List[object]]::new() }
        $memory[$group.Name].Add([pscustomobject]@{
            WorkingSet = ($group.Group | Measure-Object WorkingSet -Sum).Sum
            Private = ($group.Group | Measure-Object Private -Sum).Sum
            Count = $group.Count })
    }
}
$last = Read-Sample $groups
$elapsedMs = ((Get-Date) - $started).TotalMilliseconds
$logical = [Environment]::ProcessorCount
foreach ($name in ($memory.Keys | Sort-Object)) {
    # CPU counts only processes present at both ends, so exits and starts do not distort it.
    $cpuMs = 0.0
    foreach ($end in @($last | Where-Object Group -eq $name)) {
        $begin = $first | Where-Object Id -eq $end.Id | Select-Object -First 1
        if ($begin) { $cpuMs += $end.Cpu - $begin.Cpu }
    }
    $samples = $memory[$name]
    $line = [ordered]@{
        at = (Get-Date).ToString('o'); label = $Label; group = $name; seconds = [math]::Round($elapsedMs / 1000, 1)
        processes = ($samples | Measure-Object Count -Maximum).Maximum
        cpuCorePercent = [math]::Round(100 * $cpuMs / $elapsedMs, 2)
        cpuMachinePercent = [math]::Round(100 * $cpuMs / $elapsedMs / $logical, 3)
        privateMiBStart = [math]::Round($samples[0].Private / 1MB, 1)
        privateMiBEnd = [math]::Round($samples[$samples.Count - 1].Private / 1MB, 1)
        privateMiBMax = [math]::Round(($samples | Measure-Object Private -Maximum).Maximum / 1MB, 1)
        workingSetMiBEnd = [math]::Round($samples[$samples.Count - 1].WorkingSet / 1MB, 1)
    }
    ($line | ConvertTo-Json -Compress) | Add-Content -LiteralPath $OutPath
}
