using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.NativeCore;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAppliedOwnershipWorkspaceCapacity
{
    public uint StructSize;
    public uint RecordCapacity;
    public uint PrimaryIndexCapacity;
    public uint PayloadIndexCapacity;
    public ulong ResidentBytes;
    public NativeWorkspaceGrowthLog Growth;
}

internal sealed partial class NativeAppliedOwnershipSession : INativeWorkspaceCapacitySource
{
    private readonly NativeWorkspaceGrowthObserver growthObserver = new();

    public void SetGrowthObserver(Action<NativeWorkspaceGrowthEvent>? observer)
    {
        lock (sync)
        {
            growthObserver.Observer = observer;
            ObserveGrowth();
        }
    }

    private void ObserveGrowth()
    {
        if (growthObserver.Observer is not null) growthObserver.Read(WorkspaceCapacity.Growth);
    }

    internal unsafe NativeAppliedOwnershipWorkspaceCapacity WorkspaceCapacity
    {
        get
        {
            lock (sync)
            {
                NativeWorkspaceCapacityAbi.EnsureVersion();
                NativeAppliedOwnershipWorkspaceCapacity result = default;
                var status = NativeMethods.QueryWorkspaceCapacity(RequireHandle(), &result,
                    checked((uint)Unsafe.SizeOf<NativeAppliedOwnershipWorkspaceCapacity>()));
                NativeWorkspaceCapacityAbi.Validate(status, result.StructSize, 288, result.ResidentBytes);
                return result;
            }
        }
    }

    public NativeWorkspaceCapacityDescription ReadWorkspaceCapacity()
    {
        var c = WorkspaceCapacity;
        return new($"records={c.RecordCapacity}, primary-index={c.PrimaryIndexCapacity}, payload-index={c.PayloadIndexCapacity}", c.ResidentBytes);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_applied_ownership_query_workspace_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceCapacity(IntPtr handle,
            NativeAppliedOwnershipWorkspaceCapacity* capacity, uint outputSize);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTransactionJournalWorkspaceCapacity
{
    public uint StructSize;
    public uint RecordCapacity;
    public uint StagingRecordCapacity;
    public uint SortCapacity;
    public ulong IndexCapacity;
    public ulong ResidentBytes;
    public NativeWorkspaceGrowthLog Growth;
}

internal sealed partial class NativeTransactionJournalSession : INativeWorkspaceCapacitySource
{
    private readonly NativeWorkspaceGrowthObserver growthObserver = new();

    public void SetGrowthObserver(Action<NativeWorkspaceGrowthEvent>? observer)
    {
        lock (sync)
        {
            growthObserver.Observer = observer;
            ObserveGrowth();
        }
    }

    private void ObserveGrowth()
    {
        if (growthObserver.Observer is not null) growthObserver.Read(WorkspaceCapacity.Growth);
    }

    internal unsafe NativeTransactionJournalWorkspaceCapacity WorkspaceCapacity
    {
        get
        {
            lock (sync)
            {
                NativeWorkspaceCapacityAbi.EnsureVersion();
                NativeTransactionJournalWorkspaceCapacity result = default;
                var status = NativeMethods.QueryWorkspaceCapacity(RequireHandle(), &result,
                    checked((uint)Unsafe.SizeOf<NativeTransactionJournalWorkspaceCapacity>()));
                NativeWorkspaceCapacityAbi.Validate(status, result.StructSize, 296, result.ResidentBytes);
                return result;
            }
        }
    }

    public NativeWorkspaceCapacityDescription ReadWorkspaceCapacity()
    {
        var c = WorkspaceCapacity;
        return new($"records={c.RecordCapacity}, staging={c.StagingRecordCapacity}, sort={c.SortCapacity}, index={c.IndexCapacity}", c.ResidentBytes);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_transaction_journal_query_workspace_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceCapacity(IntPtr handle,
            NativeTransactionJournalWorkspaceCapacity* capacity, uint outputSize);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSmartCoordinatorWorkspaceCapacity
{
    public uint AbiVersion;
    public uint StructSize;
    public uint ProcessCapacity;
    public uint SoftwareCapacity;
    public uint GpuStateCapacity;
    public uint ReservationCapacity;
    public uint AtomicGroupCapacity;
    public ulong ProcessIndexCapacity;
    public ulong SoftwareIndexCapacity;
    public ulong GpuStateIndexCapacity;
    public ulong ReservationIndexCapacity;
    public ulong AtomicGroupIndexCapacity;
    public ulong ResidentBytes;
    public NativeWorkspaceGrowthLog Growth;
}

internal sealed partial class NativeSmartCoordinatorSession : INativeWorkspaceCapacitySource
{
    private readonly NativeWorkspaceGrowthObserver growthObserver = new();

    public void SetGrowthObserver(Action<NativeWorkspaceGrowthEvent>? observer)
    {
        lock (sync)
        {
            growthObserver.Observer = observer;
            ObserveGrowth();
        }
    }

    private void ObserveGrowth()
    {
        if (growthObserver.Observer is not null) growthObserver.Read(WorkspaceCapacity.Growth);
    }

    internal unsafe NativeSmartCoordinatorWorkspaceCapacity WorkspaceCapacity
    {
        get
        {
            lock (sync)
            {
                NativeWorkspaceCapacityAbi.EnsureVersion();
                NativeSmartCoordinatorWorkspaceCapacity result = default;
                var status = NativeMethods.QueryWorkspaceCapacity(RequireHandle(), &result,
                    checked((uint)Unsafe.SizeOf<NativeSmartCoordinatorWorkspaceCapacity>()));
                NativeWorkspaceCapacityAbi.Validate(status, result.StructSize, 344, result.ResidentBytes);
                if (result.AbiVersion != NativeSmartCoordinatorAbi.Version)
                    throw new InvalidOperationException("Native smart workspace capacity ABI mismatch.");
                return result;
            }
        }
    }

    public NativeWorkspaceCapacityDescription ReadWorkspaceCapacity()
    {
        var c = WorkspaceCapacity;
        return new($"processes={c.ProcessCapacity}, software={c.SoftwareCapacity}, gpu={c.GpuStateCapacity}, reservations={c.ReservationCapacity}, atomic-groups={c.AtomicGroupCapacity}, process-index={c.ProcessIndexCapacity}, software-index={c.SoftwareIndexCapacity}, gpu-index={c.GpuStateIndexCapacity}, reservation-index={c.ReservationIndexCapacity}, atomic-group-index={c.AtomicGroupIndexCapacity}", c.ResidentBytes);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_smart_coordinator_query_workspace_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceCapacity(IntPtr handle,
            NativeSmartCoordinatorWorkspaceCapacity* capacity, uint outputSize);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeReportCoordinatorWorkspaceCapacity
{
    public uint StructSize;
    public uint SourceCapacity;
    public uint RuleCapacity;
    public uint ObservationCapacity;
    public uint ReportCapacity;
    public uint TrustCapacity;
    public uint BucketCapacity;
    public uint PersistenceOperationCapacity;
    public ulong ResidentByteCount;
    public NativeWorkspaceGrowthLog Growth;
}

internal sealed partial class NativeReportCoordinatorSession : INativeWorkspaceCapacitySource
{
    private readonly NativeWorkspaceGrowthObserver growthObserver = new();

    public void SetGrowthObserver(Action<NativeWorkspaceGrowthEvent>? observer)
    {
        lock (sync)
        {
            growthObserver.Observer = observer;
            ObserveGrowth();
        }
    }

    private void ObserveGrowth()
    {
        if (growthObserver.Observer is not null) growthObserver.Read(WorkspaceCapacity.Growth);
    }

    internal unsafe NativeReportCoordinatorWorkspaceCapacity WorkspaceCapacity
    {
        get
        {
            lock (sync)
            {
                NativeWorkspaceCapacityAbi.EnsureVersion();
                NativeReportCoordinatorWorkspaceCapacity result = default;
                var status = NativeMethods.QueryWorkspaceCapacity(RequireHandle(), &result,
                    checked((uint)Unsafe.SizeOf<NativeReportCoordinatorWorkspaceCapacity>()));
                NativeWorkspaceCapacityAbi.Validate(status, result.StructSize, 304, result.ResidentByteCount);
                return result;
            }
        }
    }

    public NativeWorkspaceCapacityDescription ReadWorkspaceCapacity()
    {
        var c = WorkspaceCapacity;
        return new($"sources={c.SourceCapacity}, rules={c.RuleCapacity}, observations={c.ObservationCapacity}, reports={c.ReportCapacity}, trust={c.TrustCapacity}, buckets={c.BucketCapacity}, persistence={c.PersistenceOperationCapacity}", c.ResidentByteCount);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_report_coordinator_query_workspace_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceCapacity(IntPtr handle,
            NativeReportCoordinatorWorkspaceCapacity* capacity, uint outputSize);
    }
}

