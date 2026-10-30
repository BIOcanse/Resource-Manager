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
            checked((int)value.MaximumActivePairs), checked((int)value.ActivePairCount), checked((int)value.BucketCount),
            checked((int)value.MaximumBucketCount), value.BucketWidthQpc, checked((long)value.RetainedBucketBytes),
            checked((long)value.MaximumBucketBytes), checked((int)value.CurrentProcessCount), checked((int)value.CurrentThreadCount),
            checked((int)value.RetainedProcessIdentityCount), checked((int)value.RetainedThreadIdentityCount),
            value.ContinuityEpoch, value.CapacityResetCount);
        if (value.LastCapacityReset.Reason == 0) return result;
        var reset = value.LastCapacityReset;
        return result with
        {
            LastCapacityReset = new CpuResidencyCapacityResetDiagnostics(reset.Reason switch
            {
                1 => "active-pair-limit",
                2 => "bucket-storage-limit",
                3 => "bucket-retention-span",
                4 => "bucket-resize-limit",
                5 => "bucket-collision",
                _ => throw new InvalidDataException("Native CPU capacity-reset reason is unknown.")
            }, reset.EventQpc, reset.RetainedWindowStartQpc == long.MinValue ? null : reset.RetainedWindowStartQpc,
                checked((int)reset.ActivePairCount), checked((int)reset.MaximumActivePairs), checked((long)reset.RetainedBucketSlots),
                checked((long)reset.MaximumBucketSlots), checked((long)reset.RequiredBucketCount), checked((int)reset.MaximumBucketCount),
                checked((int)reset.CurrentProcessCount), checked((int)reset.CurrentThreadCount))
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AggregationDiagnostics
    {
        public long QpcFrequency, WindowQpc;
        public ulong MaximumActivePairs, ActivePairCount, BucketCount, MaximumBucketCount;
        public long BucketWidthQpc;
        public ulong RetainedBucketBytes, MaximumBucketBytes;
        public ulong CurrentProcessCount, CurrentThreadCount, RetainedProcessIdentityCount, RetainedThreadIdentityCount;
        public long ContinuityEpoch, CapacityResetCount;
        public CapacityResetDiagnostics LastCapacityReset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CapacityResetDiagnostics
    {
        public uint Reason, Reserved;
        public long EventQpc, RetainedWindowStartQpc;
        public ulong ActivePairCount, MaximumActivePairs, RetainedBucketSlots, MaximumBucketSlots;
        public ulong RequiredBucketCount, MaximumBucketCount, CurrentProcessCount, CurrentThreadCount;
    }

    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_residency_diagnostics", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryResidencyDiagnostics(nint owner, out AggregationDiagnostics diagnostics, uint size);
}
