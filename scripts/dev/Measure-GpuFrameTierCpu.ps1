#Requires -Version 7
<#
Compares the same candidate service's single-core CPU cost with an ordinary frame lease and with the
optional GPU tier. Does not start/stop a service, inject, alter settings, or create an ETL file.
Run elevated, against a candidate during an approved test workload. OutDir must be inside the caller's
registered running ledger. All measurement choices are explicit parameters. Other GPU subscribers must
be absent for the baseline; save subscription diagnostics alongside the result when measuring overlays.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$ServiceProcessId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][int[]]$TargetProcessIds,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$Seconds,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$Repeats,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$RequestOverheadTimeoutSeconds,
    [Parameter(Mandatory)][string]$OutDir,
    [hashtable]$Headers = @{}
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this measurement elevated.' }
if (-not $BaseUri.IsLoopback) { throw 'BaseUri must point to the local candidate service.' }
if ($TargetProcessIds | Where-Object { $_ -le 0 }) { throw 'Every target PID must be positive.' }
$output = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutDir)).FullName
$client = [Net.Http.HttpClient]::new()
$service = Get-Process -Id $ServiceProcessId
try {
    $client.Timeout = [TimeSpan]::FromSeconds([long]$Seconds + $RequestOverheadTimeoutSeconds)
    foreach ($name in $Headers.Keys) { $client.DefaultRequestHeaders.Add([string]$name, [string]$Headers[$name]) }
    $rows = [Collections.Generic.List[object]]::new()
    for ($iteration = 0; $iteration -lt $Repeats; $iteration++) {
        # Alternate order to expose warm-up/order effects without changing the chosen workload.
        $tiers = if ($iteration % 2 -eq 0) { @($false, $true) } else { @($true, $false) }
        foreach ($tier in $tiers) {
            $query = 'api/debug/frame-timing?seconds=' + $Seconds + '&gpuFrames=' + $tier.ToString().ToLowerInvariant()
            foreach ($target in $TargetProcessIds) { $query += '&processIds=' + $target }
            $uri = [uri]::new($BaseUri.AbsoluteUri.TrimEnd('/') + '/' + $query)
            $service.Refresh()
            $before = $service.TotalProcessorTime.TotalMilliseconds
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $response = $client.GetAsync($uri).GetAwaiter().GetResult()
            try {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                [IO.File]::WriteAllText((Join-Path $output "sample-$iteration-gpu-$tier.json"), $body, [Text.UTF8Encoding]::new($false))
                [void]$response.EnsureSuccessStatusCode()
            } finally { $response.Dispose() }
            $watch.Stop(); $service.Refresh()
            $cpu = $service.TotalProcessorTime.TotalMilliseconds - $before
            $rows.Add([pscustomobject]@{ Iteration=$iteration; GpuTier=$tier; ProcessId=$ServiceProcessId;
                TargetProcessIds=$TargetProcessIds; ElapsedMs=$watch.Elapsed.TotalMilliseconds; CpuMs=$cpu;
                SingleCoreCpuPercent=$cpu / $watch.Elapsed.TotalMilliseconds * 100;
                PrivateBytes=$service.PrivateMemorySize64; Handles=$service.HandleCount })
        }
    }
    $rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'cpu-comparison.json') -Encoding utf8
    $rows | Select-Object Iteration,GpuTier,ElapsedMs,CpuMs,SingleCoreCpuPercent | Format-Table
} finally { $client.Dispose(); $service.Dispose() }
