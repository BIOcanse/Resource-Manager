using System.Buffers;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.Adaptation;

// One sampling operation's borrow. It creates scratch for exactly the item rows
// that operation reads (32 bytes per row, plus 7 bytes to align the start),
// names it sampling-session with those rows, and closes it when the operation
// returns, so a session holds no scratch between operations. A failed return or
// close keeps the same use and resource for the session's explicit retry.
internal sealed class SamplingSessionScratch : NativeSamplingSessionScratchLease
{
    internal const string WorkArea = "sampling-session";
    private const ulong AlignmentBytes = 7;
    private readonly IComputeScratch resource;
    private readonly ulong length;
    private ComputeScratchLease? use;
    private MemoryHandle pin;
    private bool pinned, closed;

    private SamplingSessionScratch(IComputeScratch resource, ulong length)
    {
        this.resource = resource;
        this.length = length;
    }

    internal static SamplingSessionScratch Create(
        Func<ulong, IComputeScratch> createScratch, NativeSamplingScratchRows rows)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        var length = rows.Length;
        if (length == 0) throw new ArgumentOutOfRangeException(nameof(rows), "A sampling borrow needs at least one row.");
        var allocation = checked(length + AlignmentBytes);
        var resource = createScratch(allocation)
            ?? throw new InvalidOperationException("The sampling scratch factory returned no resource.");
        try
        {
            ComputeScratchNames.Name(resource, WorkArea, $"items={rows.Rows}/live={rows.LiveItems}");
            if (resource.Length < allocation)
                throw new InvalidOperationException("The sampling scratch is shorter than its rows.");
            var borrowed = new SamplingSessionScratch(resource, length);
            borrowed.use = resource.Acquire();
            return borrowed;
        }
        catch (Exception failure)
        {
            try { resource.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    internal override ulong Length => length;

    internal override unsafe MemoryHandle Pin()
    {
        var current = use ?? throw new ObjectDisposedException(nameof(SamplingSessionScratch));
        if (pinned) throw new InvalidOperationException("Sampling scratch is already pinned.");
        pin = current.GetMemoryChunk(0, 1).Pin();
        pinned = true;
        try
        {
            if (pin.Pointer == null) throw new InvalidOperationException("Sampling scratch has no address.");
            var first = (nuint)pin.Pointer;
            var aligned = checked(first + 7) & ~(nuint)7;
            var skip = (ulong)(aligned - first);
            if (skip > current.Length || length > current.Length - skip)
                throw new InvalidOperationException("Sampling rows exceed their allocation.");
            _ = checked(aligned + checked((nuint)length));
            return new MemoryHandle((void*)aligned);
        }
        catch { Unpin(); throw; }
    }

    internal override void Complete()
        => (use ?? throw new ObjectDisposedException(nameof(SamplingSessionScratch))).Complete();

    public override void Dispose()
    {
        Unpin();
        if (use is not null)
        {
            use.Dispose(); // Failure keeps this use for the retry.
            use = null;
        }
        if (closed) return;
        resource.Dispose(); // Failure keeps this resource for the retry.
        closed = true;
    }

    private void Unpin()
    {
        if (!pinned) return;
        pin.Dispose();
        pin = default;
        pinned = false;
    }
}
