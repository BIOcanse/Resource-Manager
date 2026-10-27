using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

/// <summary>Owns the ordered reserve entries. One lease holds all of them for one segmented dump.</summary>
public sealed class HostMemoryDiskReserves : IHostMemoryProtectionReserve
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private HostMemoryDiskReserve[] owners = [];
    private bool disposed;

    public HostMemoryReserveStatus ReadStatus()
    {
        var entries = Volatile.Read(ref owners).Select(owner => owner.ReadStatus()).ToArray();
        return new(entries, entries.Aggregate(0UL, (total, entry) => checked(total + entry.ConfiguredSizeBytes)),
            entries.Length > 0 && entries.All(entry => entry.Built),
            entries.Length == 0 ? "reserve-not-configured" : entries.FirstOrDefault(entry => !entry.Built)?.Reason,
            entries.Aggregate(0UL, (total, entry) => checked(total + entry.RemainingBytes)));
    }

    public HostMemoryReserveStatus Prepare(IReadOnlyList<HostMemoryReserveSettings> entries)
    {
        var normalized = entries.Select(entry => entry with { Location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Location)) }).ToArray();
        if (normalized.Select(entry => entry.Location).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException("duplicate-reserve-location");
        _ = normalized.Aggregate(0UL, (total, entry) => checked(total + entry.SizeBytes));
        operation.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = owners.ToDictionary(owner => owner.ReadStatus().Location, StringComparer.OrdinalIgnoreCase);
            var next = new List<HostMemoryDiskReserve>();
            try
            {
                foreach (var entry in normalized)
                {
                    if (!previous.Remove(entry.Location, out var owner)) owner = new();
                    next.Add(owner);
                    owner.Prepare(entry.Location, entry.SizeBytes);
                }
            }
            finally
            {
                foreach (var retired in previous.Values)
                {
                    // Retire only our unused reserve; completed slice files are never touched.
                    var retiredStatus = retired.Prepare(retired.ReadStatus().Location, 0);
                    if (retiredStatus.RemainingBytes == 0 && retiredStatus.Reason == "reserve-not-configured") retired.Dispose();
                    else next.Add(retired); // A failed retirement still owns space and must remain visible and unusable.
                }
                Volatile.Write(ref owners, next.ToArray());
            }
            return ReadStatus();
        }
        finally { operation.Release(); }
    }

    public HostMemoryReserveStatus Rebuild()
    {
        operation.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var retained = new List<HostMemoryDiskReserve>();
            foreach (var owner in owners)
            {
                var status = owner.ReadStatus();
                if (status.ConfiguredSizeBytes == 0)
                {
                    status = owner.Prepare(status.Location, 0);
                    if (status.RemainingBytes == 0 && status.Reason == "reserve-not-configured") { owner.Dispose(); continue; }
                }
                else owner.Rebuild();
                retained.Add(owner);
            }
            Volatile.Write(ref owners, retained.ToArray());
            return ReadStatus();
        }
        finally { operation.Release(); }
    }

    public IHostMemoryProtectionReserveLease? TryAcquire()
    {
        if (!operation.Wait(0)) return null;
        var acquired = new List<IHostMemoryProtectionReserveLease>();
        var transferred = false;
        try
        {
            if (disposed || !ReadStatus().Built) return null;
            foreach (var owner in owners)
            {
                if (owner.TryAcquire() is not { } lease) return null;
                acquired.Add(lease);
            }
            var result = new Lease(this, acquired.ToArray(), owners.Select(owner => owner.ReadStatus().ClusterBytes).ToArray());
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
            {
                foreach (var lease in acquired) lease.Dispose();
                operation.Release();
            }
        }
    }

    public void Dispose()
    {
        operation.Wait();
        try
        {
            if (disposed) return;
            disposed = true;
            foreach (var owner in owners) owner.Dispose();
        }
        finally { operation.Release(); }
    }

    private sealed class Lease(HostMemoryDiskReserves owner, IHostMemoryProtectionReserveLease[] entries, ulong[] clusters)
        : IHostMemoryProtectionReserveLease
    {
        private readonly List<Stream> streams = [];
        private bool disposed;
        private bool created;
        public string Location => entries[0].Location;
        public ulong RemainingBytes => entries.Aggregate(0UL, (total, entry) => checked(total + entry.RemainingBytes));
        public IReadOnlyList<string> SlicePaths => entries.SelectMany(entry => entry.SlicePaths).ToArray();
        public bool CanFit(ulong bytes)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (bytes > long.MaxValue) return false;
            for (var index = 0; index < entries.Length && bytes > 0; index++)
            {
                var available = entries[index].RemainingBytes;
                var segment = Math.Min(bytes, available);
                if (segment == 0) continue;
                if (HostMemoryDiskReserve.RoundUp(segment, clusters[index]) > available) return false;
                bytes -= segment;
            }
            return bytes == 0;
        }
        public Stream CreateSlice(string fileName)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (created) throw new InvalidOperationException("One reserve lease writes one slice.");
            created = true;
            var stream = new HostMemorySegmentedSliceStream(entries, fileName);
            streams.Add(stream);
            return stream;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { foreach (var stream in streams) stream.Dispose(); }
            finally
            {
                try { foreach (var entry in entries) entry.Dispose(); }
                finally { owner.operation.Release(); }
            }
        }
    }
}
