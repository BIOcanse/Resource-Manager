using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeSamplingSubscriptionSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeSamplingSubscriptionCapacity capacity;
    private ulong configurationGeneration;

    public NativeSamplingSubscriptionSession(in NativeSamplingSubscriptionConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeSamplingSubscriptionAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native sampling subscription ABI version mismatch: expected 0x{NativeSamplingSubscriptionAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeSamplingSubscriptionStatus)NativeMethods.Create(in configuration, out handle);
        if (status != NativeSamplingSubscriptionStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native sampling subscription session creation failed with {status}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
            configurationGeneration = configuration.Generation;
        }
        catch
        {
            NativeMethods.Destroy(Interlocked.Exchange(ref handle, IntPtr.Zero));
            throw;
        }
    }

    ~NativeSamplingSubscriptionSession() => Dispose(false);

    public NativeSamplingSubscriptionCapacity Capacity
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

    internal ulong ConfigurationGeneration
    {
        get
        {
            lock (sync)
            {
                _ = RequireHandle();
                return configurationGeneration;
            }
        }
    }

    public static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    public NativeSamplingSubscriptionStatus Reconfigure(
        in NativeSamplingSubscriptionConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var status = (NativeSamplingSubscriptionStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (status == NativeSamplingSubscriptionStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
                configurationGeneration = configuration.Generation;
            }
            return status;
        }
    }

    public NativeSamplingSubscriptionStatus Reset(in NativeSamplingSubscriptionControlInput input)
    {
        lock (sync)
        {
            return (NativeSamplingSubscriptionStatus)NativeMethods.Reset(RequireHandle(), in input);
        }
    }

    public unsafe NativeSamplingSubscriptionStatus Track(
        in NativeSamplingSubscriptionTrackInput input,
        ReadOnlySpan<NativeSamplingSubscriptionItemReference> items)
    {
        lock (sync)
        {
            fixed (NativeSamplingSubscriptionItemReference* itemPointer = items)
            {
                return (NativeSamplingSubscriptionStatus)NativeMethods.Track(
                    RequireHandle(),
                    in input,
                    itemPointer,
                    checked((uint)items.Length));
            }
        }
    }

    public NativeSamplingSubscriptionStatus Remove(in NativeSamplingSubscriptionRemoveInput input)
    {
        lock (sync)
        {
            return (NativeSamplingSubscriptionStatus)NativeMethods.Remove(RequireHandle(), in input);
        }
    }

    public unsafe NativeSamplingSubscriptionStatus Plan(
        ref NativeSamplingSubscriptionPlanHeader header,
        Span<NativeSamplingSubscriptionDueItem> dueItems,
        Span<NativeSamplingSubscriptionSourceView> sourceViews,
        Span<NativeSamplingSubscriptionExpiredSource> expiredSources)
    {
        lock (sync)
        {
            fixed (NativeSamplingSubscriptionDueItem* duePointer = dueItems)
            fixed (NativeSamplingSubscriptionSourceView* sourcePointer = sourceViews)
            fixed (NativeSamplingSubscriptionExpiredSource* expiredPointer = expiredSources)
            {
                return (NativeSamplingSubscriptionStatus)NativeMethods.Plan(
                    RequireHandle(),
                    ref header,
                    duePointer,
                    checked((uint)dueItems.Length),
                    sourcePointer,
                    checked((uint)sourceViews.Length),
                    expiredPointer,
                    checked((uint)expiredSources.Length));
            }
        }
    }

    public unsafe NativeSamplingSubscriptionStatus Complete(
        in NativeSamplingSubscriptionCompletionInput input,
        ReadOnlySpan<NativeSamplingSubscriptionItemReference> items,
        out NativeSamplingSubscriptionCompletionOutput output)
    {
        lock (sync)
        {
            output = new NativeSamplingSubscriptionCompletionOutput
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = SizeOf<NativeSamplingSubscriptionCompletionOutput>()
            };
            fixed (NativeSamplingSubscriptionItemReference* itemPointer = items)
            {
                return (NativeSamplingSubscriptionStatus)NativeMethods.Complete(
                    RequireHandle(),
                    in input,
                    itemPointer,
                    checked((uint)items.Length),
                    ref output);
            }
        }
    }

    public unsafe NativeSamplingSubscriptionStatus Snapshot(
        ref NativeSamplingSubscriptionSnapshotHeader header,
        Span<NativeSamplingSubscriptionSourceView> sources,
        Span<NativeSamplingSubscriptionItemState> items)
    {
        lock (sync)
        {
            fixed (NativeSamplingSubscriptionSourceView* sourcePointer = sources)
            fixed (NativeSamplingSubscriptionItemState* itemPointer = items)
            {
                return (NativeSamplingSubscriptionStatus)NativeMethods.Snapshot(
                    RequireHandle(),
                    ref header,
                    sourcePointer,
                    checked((uint)sources.Length),
                    itemPointer,
                    checked((uint)items.Length));
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

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
            : throw new ObjectDisposedException(nameof(NativeSamplingSubscriptionSession));
    }

    private static NativeSamplingSubscriptionCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeSamplingSubscriptionConfiguration configuration)
    {
        var status = (NativeSamplingSubscriptionStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeSamplingSubscriptionCapacity>());
        if (status != NativeSamplingSubscriptionStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native sampling subscription capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeSamplingSubscriptionCapacity>()
                || result.SourceCapacity != configuration.MaximumSourceCount
                || result.ItemCapacity != configuration.MaximumItemCount
                || result.MembershipCapacity != configuration.MaximumMembershipCount
                || result.DueItemCapacity != configuration.MaximumDueItemCount
                || result.SourceViewCapacity != configuration.MaximumSourceViewCount
                || result.ExpiredSourceCapacity != configuration.MaximumExpiredSourceCount
                || result.SnapshotSourceCapacity != configuration.MaximumSourceCount
                || result.SnapshotItemCapacity != configuration.MaximumItemCount
                || result.ReservedU32 != 0
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0
                || result.Reserved[2] != 0)
            {
                throw new InvalidOperationException(
                    "Native sampling subscription capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeSamplingSubscriptionConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeSamplingSubscriptionConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reset(
            IntPtr handle,
            in NativeSamplingSubscriptionControlInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeSamplingSubscriptionCapacity capacity,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_track")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Track(
            IntPtr handle,
            in NativeSamplingSubscriptionTrackInput input,
            NativeSamplingSubscriptionItemReference* items,
            uint itemCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_remove")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Remove(
            IntPtr handle,
            in NativeSamplingSubscriptionRemoveInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            ref NativeSamplingSubscriptionPlanHeader header,
            NativeSamplingSubscriptionDueItem* dueItems,
            uint dueCapacity,
            NativeSamplingSubscriptionSourceView* sourceViews,
            uint sourceCapacity,
            NativeSamplingSubscriptionExpiredSource* expiredSources,
            uint expiredCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_complete")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Complete(
            IntPtr handle,
            in NativeSamplingSubscriptionCompletionInput input,
            NativeSamplingSubscriptionItemReference* items,
            uint itemCount,
            ref NativeSamplingSubscriptionCompletionOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Snapshot(
            IntPtr handle,
            ref NativeSamplingSubscriptionSnapshotHeader header,
            NativeSamplingSubscriptionSourceView* sources,
            uint sourceCapacity,
            NativeSamplingSubscriptionItemState* items,
            uint itemCapacity);
    }
}
