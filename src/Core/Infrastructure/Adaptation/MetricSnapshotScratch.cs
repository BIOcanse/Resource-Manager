using System.Buffers;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Adaptation;

// Thin storage adapter over the product's existing single-owner scratch path.
internal sealed class MetricSnapshotScratch(IComputeScratch resource) : INativeMetricSnapshotScratch
{
    internal static INativeMetricSnapshotScratch Create(Func<ulong, IComputeScratch> createScratch,
        ulong byteCount, uint alignment)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        // The session aligns the absolute pinned address, including small ordinary
        // storage selected by the existing owner. Slack is real allocated bytes.
        return new MetricSnapshotScratch(createScratch(checked(byteCount + alignment - 1)));
    }

    public ulong Length => resource.Length;
    public NativeMetricSnapshotScratchLease Acquire() => new Lease(resource);
    public void Dispose() => resource.Dispose();
    public void Describe(string workArea, string rows) => ComputeScratchNames.Name(resource, workArea, rows);

    private sealed class Lease(IComputeScratch resource) : NativeMetricSnapshotScratchLease
    {
        private readonly ComputeScratchLease lease = resource.Acquire();
        internal override ulong Length => lease.Length;
        internal override MemoryHandle Pin() => lease.GetMemoryChunk(0, 1).Pin();
        internal override void Complete() => lease.Complete();
        public override void Dispose() => lease.Dispose();
    }
}
