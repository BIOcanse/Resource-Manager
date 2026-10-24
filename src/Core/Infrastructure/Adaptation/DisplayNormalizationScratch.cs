using System.Buffers;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class DisplayNormalizationScratch(IComputeScratch resource) : INativeDisplayNormalizationScratch
{
    public ulong Length => resource.Length;
    public NativeDisplayNormalizationLease Acquire() => new Lease(resource.Acquire());
    public void Dispose() => resource.Dispose();

    private sealed class Lease(ComputeScratchLease use) : NativeDisplayNormalizationLease
    {
        internal override MemoryHandle Pin() => use.GetMemoryChunk(0, 1).Pin();
        internal override void Complete() => use.Complete();
        public override void Dispose() => use.Dispose();
    }
}
