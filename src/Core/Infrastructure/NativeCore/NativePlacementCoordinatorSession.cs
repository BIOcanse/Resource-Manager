using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativePlacementCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativePlacementCoordinatorCapacity capacity;

    public NativePlacementCoordinatorSession(in NativePlacementCoordinatorConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativePlacementCoordinatorAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native placement coordinator ABI version mismatch: expected 0x{NativePlacementCoordinatorAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var result = (NativePlacementCoordinatorStatus)NativeMethods.Create(in configuration, out handle);
        if (result != NativePlacementCoordinatorStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException($"Native placement coordinator session creation failed with {result}.");
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

    ~NativePlacementCoordinatorSession() => Dispose(false);

    public NativePlacementCoordinatorCapacity Capacity
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

    public NativePlacementCoordinatorStatus Reconfigure(
        in NativePlacementCoordinatorConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var result = (NativePlacementCoordinatorStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (result == NativePlacementCoordinatorStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
            }
            return result;
        }
    }

    public NativePlacementCoordinatorStatus Reset()
    {
        lock (sync)
        {
            return (NativePlacementCoordinatorStatus)NativeMethods.Reset(RequireHandle());
        }
    }

    public unsafe NativePlacementCoordinatorStatus Plan(
        ref NativePlacementCoordinatorCycleInput cycle,
        ReadOnlySpan<NativePlacementDesiredInput> desired,
        ReadOnlySpan<NativePlacementAppliedInput> applied,
        Span<NativePlacementAction> actions)
    {
        lock (sync)
        {
            fixed (NativePlacementDesiredInput* desiredPointer = desired)
            fixed (NativePlacementAppliedInput* appliedPointer = applied)
            fixed (NativePlacementAction* actionPointer = actions)
            {
                return (NativePlacementCoordinatorStatus)NativeMethods.Plan(
                    RequireHandle(),
                    ref cycle,
                    desiredPointer,
                    checked((uint)desired.Length),
                    appliedPointer,
                    checked((uint)applied.Length),
                    actionPointer,
                    checked((uint)actions.Length));
            }
        }
    }

    public unsafe NativePlacementCoordinatorStatus ApplyFeedback(
        ReadOnlySpan<NativePlacementFeedback> feedback)
    {
        lock (sync)
        {
            fixed (NativePlacementFeedback* feedbackPointer = feedback)
            {
                return (NativePlacementCoordinatorStatus)NativeMethods.Feedback(
                    RequireHandle(),
                    feedbackPointer,
                    checked((uint)feedback.Length));
            }
        }
    }

    public unsafe NativePlacementCoordinatorStatus GetSnapshot(
        ref NativePlacementSnapshotHeader header,
        Span<NativePlacementState> states)
    {
        lock (sync)
        {
            fixed (NativePlacementState* statePointer = states)
            {
                return (NativePlacementCoordinatorStatus)NativeMethods.Snapshot(
                    RequireHandle(),
                    ref header,
                    statePointer,
                    checked((uint)states.Length));
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
            : throw new ObjectDisposedException(nameof(NativePlacementCoordinatorSession));
    }

    private static NativePlacementCoordinatorCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativePlacementCoordinatorConfiguration configuration)
    {
        var result = (NativePlacementCoordinatorStatus)NativeMethods.QueryCapacity(
            session,
            out var resultCapacity,
            SizeOf<NativePlacementCoordinatorCapacity>());
        if (result != NativePlacementCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native placement coordinator capacity query failed with {result}.");
        }

        unsafe
        {
            if (resultCapacity.StructSize != SizeOf<NativePlacementCoordinatorCapacity>() ||
                resultCapacity.StateCapacity != configuration.MaximumStateCount ||
                resultCapacity.ActionCapacity != configuration.MaximumActionCount ||
                resultCapacity.SnapshotStateCapacity != configuration.MaximumStateCount ||
                resultCapacity.Reserved[0] != 0 ||
                resultCapacity.Reserved[1] != 0 ||
                resultCapacity.Reserved[2] != 0 ||
                resultCapacity.Reserved[3] != 0)
            {
                throw new InvalidOperationException("Native placement coordinator capacity contract mismatch.");
            }
        }
        return resultCapacity;
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativePlacementCoordinatorConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativePlacementCoordinatorConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reset(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativePlacementCoordinatorCapacity capacity,
            uint capacitySize);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            ref NativePlacementCoordinatorCycleInput cycle,
            NativePlacementDesiredInput* desired,
            uint desiredCapacity,
            NativePlacementAppliedInput* applied,
            uint appliedCapacity,
            NativePlacementAction* actions,
            uint actionCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_feedback")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Feedback(
            IntPtr handle,
            NativePlacementFeedback* feedback,
            uint feedbackCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_placement_coordinator_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Snapshot(
            IntPtr handle,
            ref NativePlacementSnapshotHeader header,
            NativePlacementState* states,
            uint stateCapacity);
    }
}
