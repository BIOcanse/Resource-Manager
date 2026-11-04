using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.CpuMonopolyProtection;

// A borrowed view, valid only under the service gate while RunAsync's local
// Ledger lease and pin live. It owns neither allocation nor lease.
internal sealed class CpuMonopolyNative(nint rows, int capacity)
{
    internal int Capacity => capacity;
    internal void CopyTo(CpuMonopolyNative destination)
    {
        if (MigrateNative(rows, (uint)capacity, destination.Rows, (uint)destination.Capacity) == 0)
            throw new InvalidOperationException("cpu-monopoly-state-migration-capacity");
    }
    private nint Rows => rows;
    private const string Library = "ResourceManager.NativeCore";
    [StructLayout(LayoutKind.Sequential)] internal struct Configuration { internal double Threshold; internal long DurationMs, MaximumGapMs; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { internal ulong Key; internal long At; internal double Cpu; internal uint Pid, Eligible; }
    [StructLayout(LayoutKind.Sequential)] internal struct Result { internal ulong Key; internal long ElapsedMs; internal double Cpu; internal uint Pid, Flags; }
    internal static ulong Bytes(int count) => checked((ulong)RowSize() * (ulong)count);
    internal void Begin() => BeginNative(rows, (uint)capacity);
    internal void Unavailable() => UnavailableNative(rows, (uint)capacity);
    internal Result Observe(Configuration config, Input input) { ObserveNative(rows, (uint)capacity, in config, in input, out var result); return result; }
    internal Result Select() { SelectNative(rows, (uint)capacity, out var result); return result; }
    internal void Finish(int pid, ulong key, bool success) => FinishNative(rows, (uint)capacity, (uint)pid, key, success ? 1u : 0u);
    internal bool MarkExplicit(int pid, ulong key) => ExplicitNative(rows, (uint)capacity, (uint)pid, key) != 0;
    internal bool Eligible(int pid, ulong key) => EligibleNative(rows, (uint)capacity, (uint)pid, key) != 0;
    internal bool CanAutomaticWrite(int pid, ulong key) => CanAutomaticWriteNative(rows, (uint)capacity, (uint)pid, key) != 0;
    internal uint EndReason(int pid, ulong key) => EndReasonNative(rows, (uint)capacity, (uint)pid, key);
    internal bool Restore(int pid, ulong key) => RestoreNative(rows, (uint)capacity, (uint)pid, key) != 0;
    internal Result Sweep() { SweepNative(rows, (uint)capacity, out var result); return result; }
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_row_size")] private static extern uint RowSize();
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_migrate")] private static extern uint MigrateNative(nint source, uint sourceCount, nint destination, uint destinationCount);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_begin")] private static extern void BeginNative(nint rows, uint count);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_unavailable")] private static extern void UnavailableNative(nint rows, uint count);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_observe")] private static extern void ObserveNative(nint rows, uint count, in Configuration config, in Input input, out Result result);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_select")] private static extern void SelectNative(nint rows, uint count, out Result result);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_finish")] private static extern void FinishNative(nint rows, uint count, uint pid, ulong key, uint success);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_explicit")] private static extern uint ExplicitNative(nint rows, uint count, uint pid, ulong key);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_eligible")] private static extern uint EligibleNative(nint rows, uint count, uint pid, ulong key);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_can_automatic_write")] private static extern uint CanAutomaticWriteNative(nint rows, uint count, uint pid, ulong key);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_end_reason")] private static extern uint EndReasonNative(nint rows, uint count, uint pid, ulong key);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_restore")] private static extern uint RestoreNative(nint rows, uint count, uint pid, ulong key);
    [DllImport(Library, EntryPoint = "rm_cpu_monopoly_sweep")] private static extern void SweepNative(nint rows, uint count, out Result result);
}
