using System.Buffers;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.Adaptation;

// Bucket history cannot be refilled from ETW. Its lease stays live until the pair expires;
// a synchronous pin protects the same real region across intake and reads. No mirror array.
internal sealed unsafe class LedgerCaptureBucketMemory : CaptureBucketMemory
{
    private IComputeScratch? resource;
    private ComputeScratchLease? use;
    private MemoryHandle pin;
    private bool pinned;
    private readonly int length;
    private LedgerCaptureBucketMemory(ResourceManagerLedgerOwner owner, int length)
    {
        this.length = length;
        resource = owner.CreateScratch((ulong)length);
        try
        {
            use = resource.Acquire();
            pin = use.GetMemoryChunk(0, 1).Pin(); pinned = true;
            Bytes.Clear();
        }
        catch { Dispose(); throw; }
    }
    internal static CaptureBucketMemory Create(ResourceManagerLedgerOwner owner, int length)
        => (ulong)length < owner.NativePageSize ? AllocateManaged(length) : new LedgerCaptureBucketMemory(owner, length);
    internal override Span<byte> Bytes => use is null || !pinned
        ? throw new ObjectDisposedException(nameof(LedgerCaptureBucketMemory)) : new Span<byte>(pin.Pointer, length);
    public override void Dispose()
    {
        if (pinned) { pin.Dispose(); pinned = false; }
        if (use is not null) { use.Complete(); use.Dispose(); use = null; }
        if (resource is not null) { resource.Dispose(); resource = null; }
    }
}
