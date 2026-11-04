using System.Buffers;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class DisplayNormalizationScratch(IComputeScratch resource) : INativeDisplayNormalizationScratch
{
    internal const string WorkArea = "display-normalization";

    // The session asks for exactly its text bytes, so allocation and live bytes match.
    internal static DisplayNormalizationScratch Create(Func<ulong, IComputeScratch> createScratch, ulong bytes)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        var resource = createScratch(bytes);
        ComputeScratchNames.Name(resource, WorkArea, $"bytes={resource.Length}/live={bytes}");
        return new DisplayNormalizationScratch(resource);
    }

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
