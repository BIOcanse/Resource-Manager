#Requires -Version 7
<#
Measures the installed backend in tray form, per thread (user rule 10-27: tray backend under 7% of one core).
Closes the NativeUi window to the tray (WM_CLOSE), waits -SettleSeconds, then takes per-thread user and kernel
CPU deltas of the service over -Seconds (sampled thread time is not used: it counts a waiting finalizer as CPU).
Writes threads.txt and context.txt (machine load, process count, private bytes, working set, threads, handles)
into -OutDir. Run elevated: the service runs as SYSTEM, so its threads cannot be opened otherwise.
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [int]$SettleSeconds = 240,
    [int]$Seconds = 60,
    [string]$ServiceProcessName = 'ResourceManager'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
Add-Type -Namespace RmTray -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenThread(uint access, bool inherit, uint id);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
[DllImport("kernel32.dll")] public static extern int GetThreadDescription(IntPtr h, out IntPtr desc);
[DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);
'@
foreach ($ui in @(Get-Process -Name ResourceManager.NativeUi -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0)) {
    [void][RmTray.Native]::PostMessage($ui.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
}
function Get-ThreadName([int]$id) {
    $h = [RmTray.Native]::OpenThread(0x1000, $false, $id)
    if ($h -eq [IntPtr]::Zero) { return '?' }
    try {
        $p = [IntPtr]::Zero
        if ([RmTray.Native]::GetThreadDescription($h, [ref]$p) -ge 0 -and $p -ne [IntPtr]::Zero) {
            $s = [Runtime.InteropServices.Marshal]::PtrToStringUni($p); [void][RmTray.Native]::LocalFree($p); return $s
        }
        ''
    } finally { [void][RmTray.Native]::CloseHandle($h) }
}
Start-Sleep -Seconds $SettleSeconds
$service = Get-Process -Name $ServiceProcessName | Sort-Object StartTime -Descending | Select-Object -First 1
$cpu0 = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
$user0 = @{}; $kernel0 = @{}
foreach ($t in $service.Threads) { $user0[$t.Id] = $t.UserProcessorTime.TotalMilliseconds; $kernel0[$t.Id] = $t.PrivilegedProcessorTime.TotalMilliseconds }
$pu = $service.UserProcessorTime.TotalMilliseconds; $pk = $service.PrivilegedProcessorTime.TotalMilliseconds
Start-Sleep -Seconds $Seconds
$service.Refresh(); $ms = $Seconds * 10
$cpu1 = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
$lines = @('process user%={0:N1} kernel%={1:N1} (of one core, {2}s)' -f (($service.UserProcessorTime.TotalMilliseconds - $pu) / $ms), (($service.PrivilegedProcessorTime.TotalMilliseconds - $pk) / $ms), $Seconds)
$rows = foreach ($t in $service.Threads) {
    $bu = if ($user0.ContainsKey($t.Id)) { $user0[$t.Id] } else { 0 }
    $bk = if ($kernel0.ContainsKey($t.Id)) { $kernel0[$t.Id] } else { 0 }
    [pscustomobject]@{ Tid = $t.Id; User = [math]::Round(($t.UserProcessorTime.TotalMilliseconds - $bu) / $ms, 1); Kernel = [math]::Round(($t.PrivilegedProcessorTime.TotalMilliseconds - $bk) / $ms, 1); Name = (Get-ThreadName $t.Id) }
}
$lines += ($rows | Sort-Object { $_.User + $_.Kernel } -Descending | Select-Object -First 15 | Format-Table -AutoSize | Out-String -Width 200)
$lines | Set-Content -LiteralPath (Join-Path $OutDir 'threads.txt') -Encoding utf8
"pid=$($service.Id) processes=$((Get-Process).Count) machineCpuStart=$cpu0 machineCpuEnd=$cpu1 private=$([int]($service.PrivateMemorySize64 / 1MB))MiB ws=$([int]($service.WorkingSet64 / 1MB))MiB threads=$($service.Threads.Count) handles=$($service.HandleCount) at=$([DateTime]::UtcNow.ToString('o'))" |
    Set-Content -LiteralPath (Join-Path $OutDir 'context.txt') -Encoding utf8
Get-Content (Join-Path $OutDir 'threads.txt') -TotalCount 1
Get-Content (Join-Path $OutDir 'context.txt')
