using System.Buffers;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.Adaptation;

// Canonical ETW history cannot be reconstructed. Each admitted chunk is held
// while its window/run uses it, ending on expiry, freeze, or scope return.
// Leases and pins are locals of this method, never fields of a bucket object.
internal static class LedgerCaptureBucketStorage
{
    internal static async Task RunAsync(ResourceManagerLedgerOwner owner,
        Func<Func<int, CaptureBucketMemory>, Task> run)
    {
        var resources = new Dictionary<long, ResourceManagerLedgerOwner.ComputeScratch>();
        var leases = new Dictionary<long, ComputeScratchLease>();
        var pins = new Dictionary<long, MemoryHandle>();
        var regions = new Dictionary<long, Region>();
        var gate = new object();
        var closed = false;
        long next = 0;
        try { await run(Allocate).ConfigureAwait(false); }
        finally
        {
            // Close unrelated chunks even if one close fails.
            lock (gate)
            {
                closed = true;
                List<Exception>? errors = null;
                foreach (var id in resources.Keys.ToArray())
                    try { Release(id); } catch (Exception error) { (errors ??= []).Add(error); }
                if (errors is not null) throw new AggregateException(errors);
            }
        }

        CaptureBucketMemory Allocate(int length)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closed, typeof(LedgerCaptureBucketStorage));
                if ((ulong)length < owner.NativePageSize) return CaptureBucketMemory.AllocateManaged(length);
                var id = checked(++next);
                var resource = owner.CreateScratch((ulong)length);
                resource.WorkArea = "capture-buckets";
                try { resources.Add(id, resource); }
                catch { resource.Dispose(); throw; }
                try
                {
                    var lease = resource.Acquire();
                    resource.CaptureBucketCommittedBytes = checked(((lease.Length + owner.NativePageSize - 1) / owner.NativePageSize) * owner.NativePageSize);
                    try { leases.Add(id, lease); }
                    catch { lease.Complete(); lease.Dispose(); throw; }
                    var pin = lease.GetMemoryChunk(0, 1).Pin();
                    try { pins.Add(id, pin); }
                    catch { pin.Dispose(); throw; }
                    var region = CreateRegion(pin, length, () => Release(id));
                    regions.Add(id, region);
                    region.Bytes.Clear();
                    return region;
                }
                catch { Release(id); throw; }
            }
        }
        void Release(long id)
        {
            lock (gate)
            {
                if (regions.Remove(id, out var region)) region.Revoke();
                if (pins.Remove(id, out var pin)) pin.Dispose();
                if (leases.TryGetValue(id, out var lease))
                {
                    lease.Complete();
                    lease.Dispose();
                    leases.Remove(id);
                }
                if (resources.TryGetValue(id, out var resource))
                {
                    resource.Dispose();
                    resources.Remove(id);
                }
                if (resources.Count == 0)
                {
                    resources.TrimExcess();
                    leases.TrimExcess();
                    pins.TrimExcess();
                    regions.TrimExcess();
                }
            }
        }
    }
    private static unsafe Region CreateRegion(MemoryHandle pin, int length, Action release)
        => new((nint)pin.Pointer, length, release);

    // A borrowed address, not a pin or lease. Its enclosing RunAsync revokes it.
    private sealed unsafe class Region(nint address, int length, Action release) : CaptureBucketMemory
    {
        private nint address = address;
        internal override Span<byte> Bytes => address != 0 ? new((void*)address, length)
            : throw new ObjectDisposedException(nameof(Region));
        internal void Revoke() => address = 0;
        public override void Dispose() => release();
    }
}
