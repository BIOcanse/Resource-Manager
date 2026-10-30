using System.Buffers;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Adaptation;

// Thin storage adapter over the product's existing single-owner scratch path.
internal sealed class FileQueryScratch(IComputeScratch resource) : INativeFileQueryScratch
{
    internal const string WorkArea = "file-query";

    internal static INativeFileQueryScratch Create(Func<ulong, IComputeScratch> createScratch,
        ulong byteCount, uint alignment)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        // The session aligns the absolute pinned address, including small ordinary
        // storage selected by the existing owner. Slack is real allocated bytes.
        var allocation = checked(byteCount + alignment - 1);
        var resource = createScratch(allocation);
        ComputeScratchNames.Name(resource, WorkArea, $"bytes={allocation}/live={byteCount}");
        return new FileQueryScratch(resource);
    }

    public ulong Length => resource.Length;
    public NativeFileQueryScratchLease Acquire() => new Lease(resource);
    public void Dispose() => resource.Dispose();

    private sealed class Lease(IComputeScratch resource) : NativeFileQueryScratchLease
    {
        private readonly ComputeScratchLease lease = resource.Acquire();
        internal override ulong Length => lease.Length;
        internal override MemoryHandle Pin() => lease.GetMemoryChunk(0, 1).Pin();
        internal override void Complete() => lease.Complete();
        public override void Dispose() => lease.Dispose();
    }
}
