using System.Buffers;
using ResourceManager.ReadWrite.Native.Pdh;

namespace ResourceManager.App.Infrastructure.Adaptation;

// The enclosing session serializes calls. No parser address leaves a sample,
// and no scratch outlives one: EndSample returns and closes it, and the next
// sample creates it again at the size PDH reports then.
internal sealed class PdhParsingScratch(Func<ulong, IComputeScratch> createScratch, ulong pageSize)
    : NativePdhScratch
{
    private IComputeScratch? resource;
    private ComputeScratchLease? use;
    private MemoryHandle pin;
    private NativePdhScratchRegion region;
    private bool pinned, active, blocked, closePending, disposed;

    internal override void BeginSample()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (active || blocked) throw new InvalidOperationException("PDH scratch requires explicit return before another sample.");
        active = true;
    }

    internal override unsafe NativePdhScratchRegion Supply(uint requiredBytes)
    {
        if (!active || blocked) throw new InvalidOperationException("PDH scratch is not available for supply.");
        if (requiredBytes is 0 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        if (use is not null && requiredBytes <= region.Length) return region;
        try
        {
            var length = checked((ulong)requiredBytes + 7);
            if (pageSize == 0) throw new InvalidOperationException("The owner returned no native page size.");
            if (length >= pageSize) length = checked((length + pageSize - 1) / pageSize * pageSize);
            if (resource is not null && resource.Length < length)
            {
                closePending = true;
                ReturnUse();
                resource.Dispose();
                resource = null;
                closePending = false;
            }
            if (resource is null)
            {
                resource = createScratch(length);
                ComputeScratchNames.Name(resource, "pdh-parsing", $"bytes={length}/live={requiredBytes}");
            }
            use ??= resource.Acquire();
            pin = use.GetMemoryChunk(0, 1).Pin();
            pinned = true;
            if (pin.Pointer == null) throw new InvalidOperationException("PDH scratch has no backing address.");
            var first = (nuint)pin.Pointer;
            var aligned = checked(first + 7) & ~(nuint)7;
            var skip = checked((ulong)(aligned - first));
            if (skip > use.Length || requiredBytes > use.Length - skip)
                throw new InvalidOperationException("PDH scratch exceeds its allocation.");
            var available = checked((uint)(use.Length - skip));
            _ = checked(aligned + available);
            region = new() { Address = (IntPtr)(void*)aligned, Length = available };
            return region;
        }
        catch { blocked = true; throw; }
    }

    internal override void EndSample()
    {
        if (!active) throw new InvalidOperationException("No PDH sample is active.");
        active = false;
        closePending = true; // Nothing is held between samples.
        // A failed callback already owns the original failure. Do not silently
        // retry its return/close inside the same call's finally block.
        if (blocked) return;
        try { ReturnAndClose(); }
        catch { blocked = true; throw; }
    }

    private void ReturnAndClose()
    {
        ReturnUse();
        if (!closePending) return;
        resource?.Dispose();
        resource = null;
        closePending = false;
    }

    private void ReturnUse()
    {
        region = default;
        if (pinned)
        {
            pin.Dispose();
            pin = default;
            pinned = false;
        }
        if (use is null) return;
        use.Complete();
        use.Dispose();
        use = null;
    }

    internal override void RetryReturn()
    {
        if (active) throw new InvalidOperationException("PDH scratch is still in use.");
        try
        {
            ReturnAndClose();
            blocked = false;
        }
        catch { blocked = true; throw; }
    }

    public override void Dispose()
    {
        if (disposed) return;
        closePending = true;
        RetryReturn();
        disposed = true;
    }
}
