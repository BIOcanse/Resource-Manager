param(
    [Parameter(Mandatory)][string]$FilePath,
    # Passed to the program unchanged, e.g.
    # & Invoke-WithMemoryLimit.ps1 -FilePath dotnet -ArgumentList @('run', '--project', 'tool.csproj', '--', 'dump.dmp')
    [string[]]$ArgumentList = @(),
    [double]$LimitGiB = 4,
    # Machine-wide admission: all runs of this script together may declare at most this many GiB; a run that does not
    # fit waits. 0 turns admission off. Default: RM_MEMORY_RUN_BUDGET_GIB, else 8, so that many
    # parallel compiles queue instead of exhausting the machine's memory.
    [int]$BudgetGiB = $(if ($env:RM_MEMORY_RUN_BUDGET_GIB) { [int]$env:RM_MEMORY_RUN_BUDGET_GIB } else { 8 })
)
$ErrorActionPreference = 'Stop'
# Runs a diagnostic or helper program under a hard memory ceiling. This PowerShell process joins a new
# Windows job object whose committed-memory limit covers itself and every child it starts (dotnet run's
# build and the program it launches included); an allocation past the limit fails in the allocating
# process instead of exhausting the machine. Closing the job when this script ends kills anything left.
# A diagnostic program without a ceiling can take all of the machine's memory within seconds.
# Prints the job's peak committed memory and returns the program's exit code.

if ($LimitGiB -le 0) { throw '-LimitGiB must be positive.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MemoryLimitJob
{
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit Basic; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed;
    }
    const int ExtendedLimitInformation = 9;
    const uint LimitJobMemory = 0x200, LimitKillOnJobClose = 0x2000;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out ExtendedLimit info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint mode);

    // The handle stays open for the life of this process; closing it at exit kills what is left in the job.
    public static IntPtr JoinNew(ulong limitBytes)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new ExtendedLimit();
        info.Basic.LimitFlags = LimitJobMemory | LimitKillOnJobClose;
        info.JobMemoryLimit = new UIntPtr(limitBytes);
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()))
            throw new System.ComponentModel.Win32Exception();
        if (!AssignProcessToJobObject(job, GetCurrentProcess())) throw new System.ComponentModel.Win32Exception();
        return job;
    }

    public static ulong PeakJobBytes(IntPtr job)
    {
        if (!QueryInformationJobObject(job, ExtendedLimitInformation, out var info, (uint)Marshal.SizeOf<ExtendedLimit>(), IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();
        return info.PeakJobMemoryUsed.ToUInt64();
    }
}
'@
# A crashing diagnostic program must not open a modal Windows Error Reporting dialog on the user's desktop.
# SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX is inherited by the program; its
# crash still reaches the event log and the exit code.
[void][MemoryLimitJob]::SetErrorMode(32771)

# Admission: one named mutex per GiB of the budget. A run takes ceil(LimitGiB) of them under one admission mutex (so
# two runs never hold halves and wait on each other) and keeps them until it exits. Windows releases a dead process's
# mutexes, so a run that is killed gives its GiB back.
$slots = @()
if ($BudgetGiB -gt 0) {
    $need = [Math]::Min([int][Math]::Ceiling($LimitGiB), $BudgetGiB)
    $admission = [Threading.Mutex]::new($false, 'Global\RM-MemoryRun-Admission')
    $announced = $false
    while ($slots.Count -lt $need) {
        try { [void]$admission.WaitOne() } catch [Threading.AbandonedMutexException] { }
        for ($i = 0; $i -lt $BudgetGiB -and $slots.Count -lt $need; $i++) {
            $slot = [Threading.Mutex]::new($false, "Global\RM-MemoryRun-GiB-$i")
            $taken = $false
            try { $taken = $slot.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taken = $true }
            if ($taken) { $slots += $slot } else { $slot.Dispose() }
        }
        if ($slots.Count -lt $need) {
            foreach ($slot in $slots) { $slot.ReleaseMutex(); $slot.Dispose() }
            $slots = @()
        }
        $admission.ReleaseMutex()
        if ($slots.Count -lt $need) {
            if (-not $announced) { [Console]::Error.WriteLine("[memory-limit] waiting for $need GiB of the $BudgetGiB GiB machine budget"); $announced = $true }
            Start-Sleep -Seconds 3
        }
    }
}

$limitBytes = [uint64]($LimitGiB * 1GB)
$job = [MemoryLimitJob]::JoinNew($limitBytes)
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'
& $FilePath @ArgumentList
$exitCode = $LASTEXITCODE
$peak = [MemoryLimitJob]::PeakJobBytes($job)
[Console]::Error.WriteLine(('[memory-limit] peak {0:N2} GiB of {1:N2} GiB limit, exit {2}' -f ($peak / 1GB), $LimitGiB, $exitCode))
# The limit covers committed memory, so thread-stack growth fails too once it is spent: a job that reaches it can
# end as a stack overflow (0xC00000FD) or an allocation failure that looks like a code defect.
if ($exitCode -ne 0 -and $peak -ge 0.98 * $LimitGiB * 1GB) {
    [Console]::Error.WriteLine(('[memory-limit] limit reached: peak {0:N2} GiB is at the {1:N2} GiB limit; this exit is probably the limit, not a code defect. Raise -LimitGiB / MemoryLimitGiB and rerun.' -f ($peak / 1GB), $LimitGiB))
}
exit $exitCode
