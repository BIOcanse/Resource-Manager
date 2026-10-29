<#
.SYNOPSIS
Dev-time trial: which process-policy actions can be applied to which processes, protected (PPL) ones included.

.DESCRIPTION
Run elevated. Enables SeDebugPrivilege so the access checks match the LocalSystem service.
For every running process and every action, opens the process with exactly the access that
action needs. For system and protected processes it then reads the current value and writes
the same value back, so nothing changes; other processes get the open check only (the
installed service may be writing to them). Kernel processes (no user-mode image) get the open
check only. Working-set trim and suspend are open checks only.

Writes processes.json (one row per process) and summary.json (protection level x action counts)
into -OutDir.
#>
param(
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ProcessPolicyAccessProbe
{
    public const uint QueryLimited = 0x1000;
    public const uint SetInformation = 0x0200;
    public const uint SetQuota = 0x0100;
    public const uint SetLimited = 0x2000;
    public const uint SuspendResume = 0x0800;

    const int ProcessMemoryPriority = 0;
    const int ProcessPowerThrottling = 4;
    const int ProcessProtectionLevelInfo = 7;

    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetPriorityClass(IntPtr h, uint value);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr process, out UIntPtr system);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetProcessInformation(IntPtr h, int cls, byte[] info, uint size);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, byte[] info, uint size);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count, out uint required);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetProcessDefaultCpuSets(IntPtr h, uint[] ids, uint count);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("advapi32", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValueW(string system, string name, out long luid);
    [DllImport("advapi32", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, uint length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivilege { public uint Count; public long Luid; public uint Attributes; }

    public static int EnableDebugPrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token)) return Marshal.GetLastWin32Error();
        try
        {
            long luid;
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out luid)) return Marshal.GetLastWin32Error();
            var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = 0x2 };
            AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            return Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(token); }
    }

    // Returns "" on success or "open:<err>".
    static string Open(uint pid, uint access, out IntPtr h)
    {
        h = OpenProcess(access, false, pid);
        return h == IntPtr.Zero ? "open:" + Marshal.GetLastWin32Error() : "";
    }

    public static string Image(uint pid)
    {
        IntPtr h;
        if (Open(pid, QueryLimited, out h) != "") return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // -1 when unreadable; 0xFFFFFFFE means not protected.
    public static long Protection(uint pid)
    {
        IntPtr h;
        if (Open(pid, QueryLimited, out h) != "") return -1;
        try
        {
            var buf = new byte[4];
            return GetProcessInformation(h, ProcessProtectionLevelInfo, buf, 4) ? BitConverter.ToUInt32(buf, 0) : -1;
        }
        finally { CloseHandle(h); }
    }

    public static string OpenOnly(uint pid, uint access)
    {
        IntPtr h;
        var e = Open(pid, access, out h);
        if (e != "") return e;
        CloseHandle(h);
        return "ok";
    }

    // Each returns "ok", "open:<err>", "read:<err>", "write:<err>" or "skip:<why>".
    public static string Priority(uint pid, bool write)
    {
        IntPtr h;
        var e = Open(pid, QueryLimited | SetInformation, out h);
        if (e != "") return e;
        try
        {
            if (!write) return "ok-open";
            uint value = GetPriorityClass(h);
            if (value == 0) return "read:" + Marshal.GetLastWin32Error();
            return SetPriorityClass(h, value) ? "ok" : "write:" + Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }

    public static string Affinity(uint pid, bool write)
    {
        IntPtr h;
        var e = Open(pid, QueryLimited | SetInformation, out h);
        if (e != "") return e;
        try
        {
            if (!write) return "ok-open";
            UIntPtr mask, system;
            if (!GetProcessAffinityMask(h, out mask, out system)) return "read:" + Marshal.GetLastWin32Error();
            if (mask == UIntPtr.Zero) return "skip:multi-group";
            return SetProcessAffinityMask(h, mask) ? "ok" : "write:" + Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }

    public static string MemoryPriority(uint pid, bool write)
    {
        IntPtr h;
        var e = Open(pid, QueryLimited | SetInformation, out h);
        if (e != "") return e;
        try
        {
            if (!write) return "ok-open";
            var buf = new byte[4];
            if (!GetProcessInformation(h, ProcessMemoryPriority, buf, 4)) return "read:" + Marshal.GetLastWin32Error();
            return SetProcessInformation(h, ProcessMemoryPriority, buf, 4) ? "ok" : "write:" + Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }

    public static string PowerThrottling(uint pid, bool write)
    {
        IntPtr h;
        var e = Open(pid, QueryLimited | SetInformation, out h);
        if (e != "") return e;
        try
        {
            if (!write) return "ok-open";
            var buf = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(buf, 0);
            if (!GetProcessInformation(h, ProcessPowerThrottling, buf, 12)) return "read:" + Marshal.GetLastWin32Error();
            return SetProcessInformation(h, ProcessPowerThrottling, buf, 12) ? "ok" : "write:" + Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }

    // access: the mask to open with (the current writer's mask, or the documented minimum).
    public static string CpuSets(uint pid, uint access, bool write)
    {
        IntPtr h;
        var e = Open(pid, access, out h);
        if (e != "") return e;
        try
        {
            if (!write) return "ok-open";
            uint required;
            uint[] ids = new uint[256];
            if (!GetProcessDefaultCpuSets(h, ids, (uint)ids.Length, out required)) return "read:" + Marshal.GetLastWin32Error();
            uint[] current = null;
            if (required > 0) { current = new uint[required]; Array.Copy(ids, current, required); }
            return SetProcessDefaultCpuSets(h, current, required) ? "ok" : "write:" + Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }
}
'@

$privilegeError = [ProcessPolicyAccessProbe]::EnableDebugPrivilege()
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$windowsRoot = [Environment]::GetFolderPath('Windows').TrimEnd('\') + '\'
$notProtected = [long]4294967294  # PROTECTION_LEVEL_NONE; a bare 0xFFFFFFFE literal parses as int32 -2
$protectionNames = @{
    0 = 'WinTcbLight'; 1 = 'Windows'; 2 = 'WindowsLight'; 3 = 'AntimalwareLight'; 4 = 'LsaLight'
    5 = 'WinTcb'; 6 = 'CodeGenLight'; 7 = 'Authenticode'; 8 = 'PplApp'
}

$rows = foreach ($process in Get-Process) {
    $processId = [uint32]$process.Id
    if ($processId -eq 0) { continue }
    $image = [ProcessPolicyAccessProbe]::Image($processId)
    $protection = [ProcessPolicyAccessProbe]::Protection($processId)
    $kernel = $processId -eq 4 -or -not $image -or -not $image.Contains('\')
    $isProtected = $protection -ge 0 -and $protection -ne $notProtected
    $system = $image -and $image.StartsWith($windowsRoot, [StringComparison]::OrdinalIgnoreCase)
    $write = -not $kernel -and ($isProtected -or $system)
    $minimalCpuSets = [ProcessPolicyAccessProbe]::QueryLimited -bor [ProcessPolicyAccessProbe]::SetLimited
    $writerCpuSets = [ProcessPolicyAccessProbe]::QueryLimited -bor [ProcessPolicyAccessProbe]::SetInformation
    [ordered]@{
        pid             = $processId
        name            = $process.ProcessName
        session         = $process.SessionId
        kind            = if ($kernel) { 'kernel' } elseif ($isProtected) { 'protected' } elseif ($system) { 'system' } else { 'other' }
        protection      = if ($protection -lt 0) { 'unreadable' } elseif ($protection -eq $notProtected) { 'none' } else { $protectionNames[[int]$protection] ?? "level-$protection" }
        writeBack       = $write
        priority        = [ProcessPolicyAccessProbe]::Priority($processId, $write)
        affinity        = [ProcessPolicyAccessProbe]::Affinity($processId, $write)
        memoryPriority  = [ProcessPolicyAccessProbe]::MemoryPriority($processId, $write)
        powerThrottling = [ProcessPolicyAccessProbe]::PowerThrottling($processId, $write)
        cpuSetsWriter   = [ProcessPolicyAccessProbe]::OpenOnly($processId, $writerCpuSets)
        cpuSetsMinimal  = [ProcessPolicyAccessProbe]::CpuSets($processId, $minimalCpuSets, $write)
        trimOpen        = [ProcessPolicyAccessProbe]::OpenOnly($processId, ([ProcessPolicyAccessProbe]::QueryLimited -bor [ProcessPolicyAccessProbe]::SetQuota))
        suspendOpen     = [ProcessPolicyAccessProbe]::OpenOnly($processId, ([ProcessPolicyAccessProbe]::QueryLimited -bor [ProcessPolicyAccessProbe]::SuspendResume))
    }
}

$actions = 'priority', 'affinity', 'memoryPriority', 'powerThrottling', 'cpuSetsWriter', 'cpuSetsMinimal', 'trimOpen', 'suspendOpen'
$summary = foreach ($group in $rows | Group-Object { "$($_.kind)/$($_.protection)" }) {
    $entry = [ordered]@{ group = $group.Name; processes = $group.Count; names = @($group.Group.name | Sort-Object -Unique) }
    foreach ($action in $actions) {
        $counts = [ordered]@{}
        foreach ($result in $group.Group | Group-Object { $_[$action] }) { $counts[$result.Name] = $result.Count }
        $entry[$action] = $counts
    }
    $entry
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$header = [ordered]@{ elevated = $isElevated; seDebugPrivilegeError = $privilegeError; processCount = @($rows).Count; capturedUtc = [DateTime]::UtcNow.ToString('o') }
$rows | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $OutDir 'processes.json')
[ordered]@{ run = $header; groups = @($summary) } | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $OutDir 'summary.json')
"processes=$(@($rows).Count) groups=$(@($summary).Count) out=$OutDir"
