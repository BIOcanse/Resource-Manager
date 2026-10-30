using System.Buffers;
using ResourceManager.Subscription;

namespace ResourceManager.App.Infrastructure.Adaptation;

// Storage bridge only; the existing tracker owns replacement and the sampler drains it.
internal sealed class SamplingOutputScratch(IComputeScratch resource) : INativeSamplingOutputScratch
{
    internal static INativeSamplingOutputScratch Create(Func<ulong, IComputeScratch> createScratch,
        ulong byteCount, uint alignment)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        return new SamplingOutputScratch(createScratch(checked(byteCount + alignment - 1)));
    }

    public ulong Length => resource.Length;
    public NativeSamplingOutputScratchLease Acquire() => new Lease(resource.Acquire());
    public void Dispose() => resource.Dispose();
    public void Describe(string workArea, string rows) => ComputeScratchNames.Name(resource, workArea, rows);

    private sealed class Lease(ComputeScratchLease lease) : NativeSamplingOutputScratchLease
    {
        internal override ulong Length => lease.Length;
        internal override MemoryHandle Pin() => lease.GetMemoryChunk(0, 1).Pin();
        internal override void Complete() => lease.Complete();
        public override void Dispose() => lease.Dispose();
    }
}
