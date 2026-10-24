using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Processes;

// Allocation policy comes from the host. The independent library uses ordinary owned memory.
internal abstract class CaptureBucketMemory : IDisposable
{
    internal abstract Span<byte> Bytes { get; }
    public abstract void Dispose();
    internal static CaptureBucketMemory AllocateManaged(int length) => new Managed(length);
    private sealed class Managed(int length) : CaptureBucketMemory
    {
        private byte[]? bytes = new byte[length];
        internal override Span<byte> Bytes => bytes ?? throw new ObjectDisposedException(nameof(Managed));
        public override void Dispose() => bytes = null;
    }
}
