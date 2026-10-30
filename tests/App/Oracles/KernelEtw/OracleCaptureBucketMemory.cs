using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

// Allocation policy comes from the host. The independent library uses ordinary owned memory.
internal abstract class OracleCaptureBucketMemory : IDisposable
{
    internal abstract Span<byte> Bytes { get; }
    public abstract void Dispose();
    internal static OracleCaptureBucketMemory AllocateManaged(int length) => new Managed(length);
    private sealed class Managed(int length) : OracleCaptureBucketMemory
    {
        private byte[]? bytes = new byte[length];
        internal override Span<byte> Bytes => bytes ?? throw new ObjectDisposedException(nameof(Managed));
        public override void Dispose() => bytes = null;
    }
}
