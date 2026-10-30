using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeSmartCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeSmartCoordinatorCapacity capacity;

    public NativeSmartCoordinatorSession(in NativeSmartCoordinatorConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeSmartCoordinatorAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native smart coordinator ABI version mismatch: expected 0x{NativeSmartCoordinatorAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var result = (NativeSmartCoordinatorStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (result != NativeSmartCoordinatorStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native smart coordinator session creation failed with {result}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
        }
        catch
        {
            NativeMethods.Destroy(Interlocked.Exchange(ref handle, IntPtr.Zero));
            throw;
        }
    }

    ~NativeSmartCoordinatorSession() => Dispose(false);

    public NativeSmartCoordinatorCapacity Capacity
    {
        get
        {
            lock (sync)
            {
                _ = RequireHandle();
                return capacity;
            }
        }
    }

    internal NativeSmartCoordinatorScratchRequirements ScratchRequirements
    {
        get
        {
            lock (sync)
            {
                var status = (NativeSmartCoordinatorStatus)NativeMethods.QueryScratchLayout(RequireHandle(),
                    out var result, SizeOf<NativeSmartCoordinatorScratchRequirements>());
                if (status != NativeSmartCoordinatorStatus.Ok ||
                    result.AbiVersion != NativeSmartCoordinatorAbi.Version ||
                    result.StructSize != SizeOf<NativeSmartCoordinatorScratchRequirements>() ||
                    result.ByteCount == 0 || result.Alignment == 0 ||
                    (result.Alignment & (result.Alignment - 1)) != 0 || result.Reserved != 0 || result.Reserved1 != 0 ||
                    result.ProcessCount != capacity.ProcessCapacity ||
                    result.SoftwareCount != capacity.SoftwareCapacity ||
                    result.GpuCount != capacity.GpuStateCapacity ||
                    result.ProcessSize == 0 || result.SoftwareSize == 0 || result.GpuSize == 0 || result.SoftwareGpuSize == 0 ||
                    result.ProcessIndexCount == 0 || result.SoftwareIndexCount == 0 || result.GpuIndexCount == 0)
                    throw new InvalidOperationException("Native smart scratch layout contract mismatch.");
                return result;
            }
        }
    }

    public static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    // Slot count of the measure table that the caller places in its fact region.
    internal ulong MeasureTableCount(uint rows)
    {
        lock (sync)
        {
            var status = (NativeSmartCoordinatorStatus)NativeMethods.QueryMeasureTable(RequireHandle(), rows, out var count);
            if (status != NativeSmartCoordinatorStatus.Ok || count == 0 || (count & (count - 1)) != 0)
                throw new InvalidOperationException("Native smart measure table contract mismatch.");
            return count;
        }
    }

    // Native counts this round's staging rows per entity kind and the candidate
    // rows its plan writes, so the caller opens exactly those columns.
    internal unsafe NativeSmartCoordinatorStatus MeasurePlan(
        in NativeSmartCoordinatorCycleInput input,
        ReadOnlySpan<NativeSmartCoordinatorInputRow> inputRows,
        Span<uint> measureTable,
        out NativeSmartCoordinatorScratchRequirements staging,
        out uint actionRows)
    {
        lock (sync)
        {
            fixed (NativeSmartCoordinatorInputRow* inputPointer = inputRows)
            fixed (uint* tablePointer = measureTable)
            {
                var status = (NativeSmartCoordinatorStatus)NativeMethods.MeasurePlan(
                    RequireHandle(),
                    in input,
                    inputPointer,
                    checked((uint)inputRows.Length),
                    tablePointer,
                    checked((ulong)measureTable.Length),
                    out staging,
                    SizeOf<NativeSmartCoordinatorScratchRequirements>(),
                    out actionRows);
                if (status != NativeSmartCoordinatorStatus.Ok) return status;
                var full = ScratchRequirements;
                if (staging.AbiVersion != full.AbiVersion || staging.StructSize != full.StructSize ||
                    staging.ByteCount == 0 || staging.Alignment != full.Alignment ||
                    staging.ProcessSize != full.ProcessSize || staging.SoftwareSize != full.SoftwareSize ||
                    staging.GpuSize != full.GpuSize || staging.SoftwareGpuSize != full.SoftwareGpuSize ||
                    staging.ProcessCount > full.ProcessCount || staging.SoftwareCount > full.SoftwareCount ||
                    staging.GpuCount > full.GpuCount || staging.ByteCount > full.ByteCount ||
                    staging.Reserved != 0 || staging.Reserved1 != 0 ||
                    actionRows > capacity.ProcessCapacity)
                    throw new InvalidOperationException("Native smart measured scratch contract mismatch.");
                return status;
            }
        }
    }

    public static NativeSmartCoordinatorStatus CapacityForConfiguration(
        in NativeSmartCoordinatorConfiguration configuration,
        out NativeSmartCoordinatorCapacity capacity)
    {
        var status = (NativeSmartCoordinatorStatus)NativeMethods.CapacityForConfiguration(
            in configuration,
            out capacity);
        if (status == NativeSmartCoordinatorStatus.Ok)
        {
            ValidateCapacity(in capacity, in configuration);
        }
        return status;
    }

    public NativeSmartCoordinatorStatus Reconfigure(
        in NativeSmartCoordinatorConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var result = (NativeSmartCoordinatorStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (result == NativeSmartCoordinatorStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
            }
            return result;
        }
    }

    public NativeSmartCoordinatorStatus Reset()
    {
        lock (sync)
        {
            return (NativeSmartCoordinatorStatus)NativeMethods.Reset(RequireHandle());
        }
    }

    public unsafe NativeSmartCoordinatorStatus Plan(
        in NativeSmartCoordinatorCycleInput input,
        ReadOnlySpan<NativeSmartCoordinatorInputRow> inputRows,
        Span<NativeSmartCoordinatorAction> actions,
        IntPtr scratchPointer,
        in NativeSmartCoordinatorScratchRequirements staging,
        ref NativeSmartCoordinatorSnapshot snapshot)
    {
        lock (sync)
        {
            fixed (NativeSmartCoordinatorInputRow* inputPointer = inputRows)
            fixed (NativeSmartCoordinatorAction* actionPointer = actions)
            {
                try
                {
                    return (NativeSmartCoordinatorStatus)NativeMethods.Plan(
                        RequireHandle(),
                        in input,
                        inputPointer,
                        checked((uint)inputRows.Length),
                        actionPointer,
                        checked((uint)actions.Length),
                        (void*)scratchPointer,
                        staging.ByteCount,
                        in staging,
                        ref snapshot);
                }
                finally
                {
                    ObserveGrowth();
                }
            }
        }
    }

    public unsafe NativeSmartCoordinatorStatus ApplyFeedback(
        ReadOnlySpan<NativeSmartCoordinatorFeedback> feedbackRows,
        ref NativeSmartCoordinatorSnapshot snapshot)
    {
        lock (sync)
        {
            fixed (NativeSmartCoordinatorFeedback* feedbackPointer = feedbackRows)
            {
                var count = checked((uint)feedbackRows.Length);
                return (NativeSmartCoordinatorStatus)NativeMethods.ApplyFeedback(
                    RequireHandle(),
                    feedbackPointer,
                    count,
                    count,
                    ref snapshot);
            }
        }
    }

    public unsafe NativeSmartCoordinatorStatus GetSnapshot(
        ref NativeSmartCoordinatorSnapshot snapshot,
        Span<NativeSmartCoordinatorSnapshotRow> rows)
    {
        lock (sync)
        {
            fixed (NativeSmartCoordinatorSnapshotRow* rowPointer = rows)
            {
                return (NativeSmartCoordinatorStatus)NativeMethods.GetSnapshot(
                    RequireHandle(),
                    ref snapshot,
                    rowPointer,
                    checked((uint)rows.Length));
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private void Dispose(bool disposing)
    {
        _ = disposing;
        lock (sync)
        {
            var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (current != IntPtr.Zero)
            {
                NativeMethods.Destroy(current);
            }
        }
    }

    private IntPtr RequireHandle()
    {
        var current = Volatile.Read(ref handle);
        return current != IntPtr.Zero
            ? current
            : throw new ObjectDisposedException(nameof(NativeSmartCoordinatorSession));
    }

    private static NativeSmartCoordinatorCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeSmartCoordinatorConfiguration configuration)
    {
        var result = (NativeSmartCoordinatorStatus)NativeMethods.QueryCapacity(
            session,
            out var resultCapacity);
        if (result != NativeSmartCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native smart coordinator capacity query failed with {result}.");
        }

        ValidateCapacity(in resultCapacity, in configuration);
        return resultCapacity;
    }

    private static unsafe void ValidateCapacity(
        in NativeSmartCoordinatorCapacity resultCapacity,
        in NativeSmartCoordinatorConfiguration configuration)
    {
        var expectedSnapshotRows = checked(
            configuration.MaximumProcesses + configuration.MaximumSoftwareGroups);
        if (resultCapacity.AbiVersion != NativeSmartCoordinatorAbi.Version ||
            resultCapacity.StructSize != SizeOf<NativeSmartCoordinatorCapacity>() ||
            resultCapacity.ConfigurationGeneration != configuration.Generation ||
            resultCapacity.InputRowStructSize != SizeOf<NativeSmartCoordinatorInputRow>() ||
            resultCapacity.CycleInputStructSize != SizeOf<NativeSmartCoordinatorCycleInput>() ||
            resultCapacity.ActionStructSize != SizeOf<NativeSmartCoordinatorAction>() ||
            resultCapacity.FeedbackStructSize != SizeOf<NativeSmartCoordinatorFeedback>() ||
            resultCapacity.SnapshotStructSize != SizeOf<NativeSmartCoordinatorSnapshot>() ||
            resultCapacity.SnapshotRowStructSize != SizeOf<NativeSmartCoordinatorSnapshotRow>() ||
            resultCapacity.InputRowCapacity != configuration.MaximumInputRows ||
            resultCapacity.ActionCapacity != configuration.MaximumActions ||
            resultCapacity.FeedbackCapacity != configuration.MaximumReservations ||
            resultCapacity.SnapshotRowCapacity != expectedSnapshotRows ||
            resultCapacity.ProcessCapacity != configuration.MaximumProcesses ||
            resultCapacity.SoftwareCapacity != configuration.MaximumSoftwareGroups ||
            resultCapacity.GpuStateCapacity != configuration.MaximumGpuStates ||
            resultCapacity.AtomicGroupCapacity != configuration.MaximumAtomicGroups ||
            resultCapacity.InputRowCapacity == 0 ||
            resultCapacity.ActionCapacity == 0 ||
            resultCapacity.FeedbackCapacity == 0 ||
            resultCapacity.SnapshotRowCapacity == 0 ||
            resultCapacity.ProcessCapacity == 0 ||
            resultCapacity.SoftwareCapacity == 0 ||
            resultCapacity.GpuStateCapacity == 0 ||
            resultCapacity.AtomicGroupCapacity == 0 ||
            resultCapacity.Reserved[0] != 0 ||
            resultCapacity.Reserved[1] != 0)
        {
            throw new InvalidOperationException("Native smart coordinator capacity contract mismatch.");
        }
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_capacity_for_config")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CapacityForConfiguration(
            in NativeSmartCoordinatorConfiguration configuration,
            out NativeSmartCoordinatorCapacity capacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeSmartCoordinatorConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeSmartCoordinatorCapacity capacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_query_scratch_layout")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryScratchLayout(IntPtr handle,
            out NativeSmartCoordinatorScratchRequirements requirements, uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_query_measure_table")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryMeasureTable(IntPtr handle, uint rows, out ulong tableCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_measure_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int MeasurePlan(
            IntPtr handle,
            in NativeSmartCoordinatorCycleInput input,
            NativeSmartCoordinatorInputRow* inputRows,
            uint inputRowCapacity,
            uint* table,
            ulong tableCount,
            out NativeSmartCoordinatorScratchRequirements requirements,
            uint outputSize,
            out uint actionRows);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeSmartCoordinatorConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reset(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_plan_measured")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            in NativeSmartCoordinatorCycleInput input,
            NativeSmartCoordinatorInputRow* inputRows,
            uint inputRowCapacity,
            NativeSmartCoordinatorAction* actions,
            uint actionCapacity,
            void* scratchPointer,
            ulong scratchLength,
            in NativeSmartCoordinatorScratchRequirements requirements,
            ref NativeSmartCoordinatorSnapshot snapshot);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_feedback")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ApplyFeedback(
            IntPtr handle,
            NativeSmartCoordinatorFeedback* feedbackRows,
            uint feedbackCount,
            uint feedbackCapacity,
            ref NativeSmartCoordinatorSnapshot snapshot);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int GetSnapshot(
            IntPtr handle,
            ref NativeSmartCoordinatorSnapshot snapshot,
            NativeSmartCoordinatorSnapshotRow* rows,
            uint rowCapacity);
    }
}
