using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal static unsafe partial class KernelEtwNativeReaders
{
    internal static CpuResidencyAggregationDiagnostics? ReadResidencyDiagnostics(NativeKernelEtwSession session)
    {
        using var owner = session.AcquireReadLease();
        var status = QueryResidencyDiagnostics(owner.OwnerHandle, out var value, checked((uint)sizeof(AggregationDiagnostics)));
        if (status == 3) return null;
        Check(status, "read residency aggregation diagnostics");
        var result = new CpuResidencyAggregationDiagnostics(value.QpcFrequency, value.WindowQpc,
            checked((int)value.MaximumActivePairs), checked((int)value.ActivePairCount), checked((int)value.SlotsPerWindow),
            checked((int)value.RingSlots), checked((int)value.PairRecordBytes), checked((long)value.WindowStorageBytes),
            checked((long)value.MaximumWindowStorageBytes), checked((int)value.CurrentProcessCount), checked((int)value.CurrentThreadCount),
            checked((int)value.RetainedProcessIdentityCount), checked((int)value.RetainedThreadIdentityCount),
            value.ContinuityEpoch, value.CapacityResetCount);
        if (value.LastCapacityReset.Reason == 0) return result;
        var reset = value.LastCapacityReset;
        return result with
        {
            LastCapacityReset = new CpuResidencyCapacityResetDiagnostics(reset.Reason switch
            {
                1 => "active-pair-limit",
                3 => "ring-span",
                _ => throw new InvalidDataException("Native CPU capacity-reset reason is unknown.")
            }, reset.EventQpc, reset.WindowStartQpc == long.MinValue ? null : reset.WindowStartQpc,
                checked((int)reset.ActivePairCount), checked((int)reset.MaximumActivePairs), checked((long)reset.RequiredSlotSpan),
                checked((int)reset.RingSlots), checked((int)reset.CurrentProcessCount), checked((int)reset.CurrentThreadCount))
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AggregationDiagnostics
    {
        public long QpcFrequency, WindowQpc;
        public ulong MaximumActivePairs, ActivePairCount, SlotsPerWindow, RingSlots;
        public ulong PairRecordBytes, WindowStorageBytes, MaximumWindowStorageBytes;
        public ulong CurrentProcessCount, CurrentThreadCount, RetainedProcessIdentityCount, RetainedThreadIdentityCount;
        public long ContinuityEpoch, CapacityResetCount;
        public CapacityResetDiagnostics LastCapacityReset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapacityResetDiagnostics
    {
        public uint Reason, Reserved;
        // 72 B: window_start_qpc is long.MinValue when no window had started; required_slot_span is 0 for the pair limit.
        public long EventQpc, WindowStartQpc;
        public ulong ActivePairCount, MaximumActivePairs, RequiredSlotSpan, RingSlots;
        public ulong CurrentProcessCount, CurrentThreadCount;
    }

    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_residency_diagnostics", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryResidencyDiagnostics(nint owner, out AggregationDiagnostics diagnostics, uint size);
}
