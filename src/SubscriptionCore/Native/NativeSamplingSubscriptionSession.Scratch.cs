using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.Subscription.Native;

// A product workspace owns the resource identity. A session borrows only its
// five-column slice; ordinary independent consumers keep native-owned storage.
internal abstract class NativeSamplingSessionScratchLease : IDisposable
{
    internal abstract ulong Length { get; }
    internal abstract MemoryHandle Pin();
    internal abstract void Complete();
    public abstract void Dispose();
}

public sealed partial class NativeSamplingSubscriptionSession
{
    private readonly Func<NativeSamplingSessionScratchLease>? acquireScratch;
    private NativeSamplingSessionScratchLease? scratchUse;
    private MemoryHandle scratchPin;
    private bool scratchBound;
    private bool scratchCompleted;

    private unsafe ScratchOperation AcquireInternalScratch()
    {
        _ = RequireHandle();
        if (acquireScratch is null) return default;
        EndInternalScratch(); // Retry the same failed endUse before a new borrow.
        scratchUse = acquireScratch()
            ?? throw new InvalidOperationException("The sampling scratch factory returned no lease.");
        scratchCompleted = false;
        try
        {
            var length = checked(32UL * capacity.ItemCapacity);
            if (scratchUse.Length < length)
                throw new InvalidOperationException("The sampling scratch slice is too short.");
            scratchPin = scratchUse.Pin();
            if (scratchPin.Pointer == null || (nuint)scratchPin.Pointer % 8 != 0)
                throw new InvalidOperationException("The sampling scratch slice is not aligned.");
            var status = (NativeSamplingSubscriptionStatus)NativeMethods.BindScratch(
                handle, (IntPtr)scratchPin.Pointer, length);
            if (status != NativeSamplingSubscriptionStatus.Ok)
                throw new InvalidOperationException($"Native sampling scratch binding failed with {status}.");
            scratchBound = true;
            return new ScratchOperation(this);
        }
        catch (Exception failure)
        {
            try { EndInternalScratch(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    private void EndInternalScratch()
    {
        if (scratchUse is null) return;
        if (scratchBound)
        {
            var status = (NativeSamplingSubscriptionStatus)NativeMethods.UnbindScratch(RequireHandle());
            if (status != NativeSamplingSubscriptionStatus.Ok)
                throw new InvalidOperationException($"Native sampling scratch unbinding failed with {status}.");
            scratchBound = false;
        }
        if (!scratchCompleted)
        {
            scratchUse.Complete();
            scratchCompleted = true;
        }
        scratchPin.Dispose();
        scratchPin = default;
        scratchUse.Dispose(); // Failure retains the same lease and Native session.
        scratchUse = null;
    }

    private readonly struct ScratchOperation(NativeSamplingSubscriptionSession owner) : IDisposable
    {
        private readonly NativeSamplingSubscriptionSession? owner = owner;
        public void Dispose() => owner?.EndInternalScratch();
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_create_external_scratch")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CreateExternalScratch(
            in NativeSamplingSubscriptionConfiguration configuration, out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_bind_scratch")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BindScratch(IntPtr handle, IntPtr pointer, ulong byteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_unbind_scratch")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int UnbindScratch(IntPtr handle);
    }
}
