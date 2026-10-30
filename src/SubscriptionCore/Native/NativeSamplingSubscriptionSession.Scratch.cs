using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.Subscription.Native;

// A product workspace lends one operation exactly the item rows it reads, 32
// bytes per row, and takes them back when the operation returns. Ordinary
// independent consumers use native-owned rows with the same per-operation life.
internal abstract class NativeSamplingSessionScratchLease : IDisposable
{
    internal abstract ulong Length { get; }
    internal abstract MemoryHandle Pin();
    internal abstract void Complete();
    public abstract void Dispose();
}

/// <summary>One operation's borrow: the rows it binds and the live items the session already knows.</summary>
internal readonly record struct NativeSamplingScratchRows(uint Rows, uint LiveItems)
{
    internal ulong Length => checked(NativeSamplingSubscriptionSession.ScratchRowBytes * Rows);
}

/// <summary>Rows the session currently stores, as `rm_sampling_subscription_query_workspace_capacity` reports them.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeSamplingSubscriptionWorkspaceCapacity
{
    public uint StructSize;
    public uint SourceRows;
    public uint ItemRows;
    public uint MembershipRows;
    public uint ActiveSourceCount;
    public uint KnownItemCount;
    public uint IssuedTriggerRows;
    public uint ScratchRows;
    public ulong ResidentBytes;
}

public sealed partial class NativeSamplingSubscriptionSession
{
    internal const ulong ScratchRowBytes = 32;
    private readonly Func<NativeSamplingScratchRows, NativeSamplingSessionScratchLease>? acquireScratch;
    private NativeSamplingSessionScratchLease? scratchUse;
    private MemoryHandle scratchPin;
    private bool scratchBound;
    private bool scratchCompleted;

    /// <summary>Stored rows and resident bytes; they change only when an operation adds, removes or compacts rows.</summary>
    [global::ResourceManager.ReadWrite.RuntimeRead(global::ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "sampling session operation")]
    internal NativeSamplingSubscriptionWorkspaceCapacity WorkspaceCapacity
    {
        get
        {
            lock (sync)
            {
                return QueryWorkspaceCapacity(RequireHandle());
            }
        }
    }

    /// <summary>The same row rule the native operation checks before it reads scratch.</summary>
    internal static uint ScratchRowsFor(uint itemRows, uint referenceRows, bool addsItems, uint limit)
    {
        var rows = addsItems ? (ulong)itemRows + referenceRows : Math.Max(itemRows, referenceRows);
        return (uint)Math.Min(rows, limit);
    }

    private unsafe ScratchOperation AcquireInternalScratch(int referenceRows = 0, bool addsItems = false)
    {
        var current = RequireHandle();
        if (acquireScratch is null) return default;
        EndInternalScratch(); // Retry the same failed endUse before a new borrow.
        var stored = QueryWorkspaceCapacity(current);
        var demand = new NativeSamplingScratchRows(
            ScratchRowsFor(stored.ItemRows, checked((uint)referenceRows), addsItems, capacity.ItemCapacity),
            stored.KnownItemCount);
        if (demand.Rows == 0) return default; // No item row is read, so nothing is borrowed.
        scratchUse = acquireScratch(demand)
            ?? throw new InvalidOperationException("The sampling scratch factory returned no lease.");
        scratchCompleted = false;
        try
        {
            var length = demand.Length;
            if (scratchUse.Length < length)
                throw new InvalidOperationException("The sampling scratch lease is too short.");
            scratchPin = scratchUse.Pin();
            if (scratchPin.Pointer == null || (nuint)scratchPin.Pointer % 8 != 0)
                throw new InvalidOperationException("The sampling scratch lease is not aligned.");
            var status = (NativeSamplingSubscriptionStatus)NativeMethods.BindScratch(
                current, (IntPtr)scratchPin.Pointer, length);
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

    private static unsafe NativeSamplingSubscriptionWorkspaceCapacity QueryWorkspaceCapacity(IntPtr session)
    {
        NativeSamplingSubscriptionWorkspaceCapacity result = default;
        var size = SizeOf<NativeSamplingSubscriptionWorkspaceCapacity>();
        var status = (NativeSamplingSubscriptionStatus)NativeMethods.QueryWorkspaceCapacity(session, &result, size);
        if (status != NativeSamplingSubscriptionStatus.Ok || result.StructSize != size || result.ResidentBytes == 0)
            throw new InvalidOperationException(
                $"Native sampling workspace capacity query failed with {status}, size {result.StructSize}.");
        return result;
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

        [LibraryImport(LibraryName, EntryPoint = "rm_sampling_subscription_query_workspace_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryWorkspaceCapacity(IntPtr handle,
            NativeSamplingSubscriptionWorkspaceCapacity* capacity, uint outputSize);
    }
}
