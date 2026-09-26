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

    public static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

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
        ref NativeSmartCoordinatorSnapshot snapshot)
    {
        lock (sync)
        {
            fixed (NativeSmartCoordinatorInputRow* inputPointer = inputRows)
            fixed (NativeSmartCoordinatorAction* actionPointer = actions)
            {
                return (NativeSmartCoordinatorStatus)NativeMethods.Plan(
                    RequireHandle(),
                    in input,
                    inputPointer,
                    checked((uint)inputRows.Length),
                    actionPointer,
                    checked((uint)actions.Length),
                    ref snapshot);
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

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeSmartCoordinatorConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reset(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_smart_coordinator_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            in NativeSmartCoordinatorCycleInput input,
            NativeSmartCoordinatorInputRow* inputRows,
            uint inputRowCapacity,
            NativeSmartCoordinatorAction* actions,
            uint actionCapacity,
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
