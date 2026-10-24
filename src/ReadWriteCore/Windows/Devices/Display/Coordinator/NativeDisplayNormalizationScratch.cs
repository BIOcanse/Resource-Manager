using System.Buffers;

namespace ResourceManager.ReadWrite.Native;

internal interface INativeDisplayNormalizationScratch : IDisposable
{
    ulong Length { get; }
    NativeDisplayNormalizationLease Acquire();
}

internal abstract class NativeDisplayNormalizationLease : IDisposable
{
    internal abstract MemoryHandle Pin();
    internal abstract void Complete();
    public abstract void Dispose();
}
