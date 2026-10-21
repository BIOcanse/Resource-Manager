using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Files.DiskUsage;
namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>The original single result owner. Information is derived during the
/// same atomic replacement; no scan, timer or second readings cache is created.</summary>
public sealed class DiskUsageTreeStore : IDiskUsageTreeStore, IOwnedDiskUsageTreeStore
{
    public const string InformationItemId = "information.mft.disk-usage";
    internal sealed record State(long Generation, bool Retired, IDiskUsageTreePayload? Payload,
        DiskUsageScanSummary? Summary, DiskUsageResultOwner? Owner, DeviceInformationSnapshot Information,
        Action? ReleasedHandler = null);
    private readonly object publicationGate = new();
    private readonly object metadataGate = new();
    private readonly IDiskUsageTreePayloadAllocator? allocator;
    private long generation;
    private State current = new(0, true, null, null, null, DeviceInformationSnapshot.Empty);
    public DiskUsageScanSummary? CurrentSummary => ReadState().Summary;
    internal State ReadState() => Volatile.Read(ref current);
    public DiskUsageScanSummary? SummaryFor(DiskUsageResultOwner owner)
    {
        if (!owner.IsValid) return null;
        var state = ReadState();
        return state.Owner is { } published && SameOwner(published, owner) ? state.Summary : null;
    }
    public DiskUsageTreeReadLease? TryAcquire() => TryAcquireCore(null);
    public DiskUsageTreeReadLease? TryAcquire(DiskUsageResultOwner owner)
        => owner.IsValid ? TryAcquireCore(owner) : null;
    private DiskUsageTreeReadLease? TryAcquireCore(DiskUsageResultOwner? owner)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var state = ReadState();
            if (owner is { } requested && (state.Owner is not { } published || !SameOwner(published, requested))) return null;
            var lease = TryAcquire(state);
            if (lease is not null)
            {
                if (ReferenceEquals(state, ReadState())) return lease;
                lease.Dispose();
            }
            if (ReferenceEquals(state, ReadState())) return null;
        }
        return null;
    }
    internal DiskUsageTreeReadLease? TryAcquire(State state)
    {
        if (state.Payload is null || state.Summary is null) return null;
        var lease = state.Payload.TryAcquire();
        if (lease is null) return null;
        if (ReferenceEquals(state, ReadState())) return new(lease, state.Summary);
        lease.Dispose();
        return null;
    }
    public MftDiskUsageInformationView CreateInformationView(IEnumerable<int> nodes) => new(this, nodes);
    public SamplingPublicationSignal Publications { get; } = new();
    public InformationReadWriteBackend Backend { get; }
    public DiskUsageTreeStore(IDiskUsageTreePayloadAllocator? allocator = null)
    {
        this.allocator = allocator;
        Backend = new(ReadInformation, token => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ReadInformation()); },
            _ => new(true, null, [DeviceInformationFieldRegistry.MftSourceId]));
    }
    public DeviceInformationSnapshot ReadInformation() => Volatile.Read(ref current).Information;
    public void Replace(DiskUsageScanResult result, CancellationToken cancellationToken = default)
        => ReplaceCore(result, null, cancellationToken);

    public void Replace(DiskUsageScanResult result, DiskUsageResultOwner owner, CancellationToken cancellationToken = default)
    {
        owner.Validate();
        ReplaceCore(result, owner, cancellationToken);
    }

    private void ReplaceCore(DiskUsageScanResult result, DiskUsageResultOwner? owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (publicationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = ReadState();
            // Push may evict unleased data. Protect the actual old Current until
            // the replacement is completely imported and atomically published.
            using var oldProtection = TryAcquire(old);
            var payload = allocator is null ? new ManagedPayload(result.Tree)
                : allocator.Import(result.Tree, cancellationToken) ?? throw new InvalidOperationException("The tree allocator returned no payload.");
            Action released = () => OnReleased(payload);
            var committed = false;
            payload.Released += released;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var publication = payload.TryAcquire()
                    ?? throw new InvalidOperationException("The imported tree could not be leased for publication.");
                var information = ProjectRoots(new(publication.Tree, result.Summary), cancellationToken);
                lock (metadataGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Volatile.Write(ref current, new State(++generation, false, payload, result.Summary, owner, information, released));
                }
                committed = true;
            }
            finally
            {
                if (!committed) { payload.Released -= released; payload.Retire(); }
            }
            // Neither a Close nor a lease return runs under the metadata lock
            // used by release callbacks. Existing readers retain the old region.
            Retire(old);
        }
        Publications.Publish([InformationItemId]);
    }
    public void Clear()
    {
        lock (publicationGate)
        {
            State old;
            lock (metadataGate)
            {
                old = current;
                Volatile.Write(ref current, new State(++generation, true, null, null, null, DeviceInformationSnapshot.Empty));
            }
            Retire(old);
        }
        _ = Backend.Catalog; // Drop the backend's metadata-only bound snapshot as well.
        Publications.Publish([InformationItemId]);
    }
    public void ClearCurrentValues()
    {
        lock (publicationGate)
        {
            var old = ReadState();
            // Preserve declarations so previously selected exact addresses become
            // Unavailable instead of being silently rebound to a replacement object.
            var cleared = new DeviceInformationSnapshot(null, old.Information.Devices.Select(device => device with
                { Fields = device.Fields.Select(field => field with { Value = null, ObservedAt = null }).ToArray() }).ToArray());
            lock (metadataGate)
                Volatile.Write(ref current, new State(++generation, false, null, null, null, cleared));
            Retire(old);
        }
        Publications.Publish([InformationItemId]);
    }
    private void OnReleased(IDiskUsageTreePayload payload)
    {
        lock (metadataGate)
        {
            if (!ReferenceEquals(current.Payload, payload)) return;
            Volatile.Write(ref current, new State(++generation, true, null, null, null, DeviceInformationSnapshot.Empty));
        }
        Publications.Publish([InformationItemId]);
    }
    private static void Retire(State state)
    {
        if (state.Payload is not { } payload) return;
        if (state.ReleasedHandler is { } handler) payload.Released -= handler;
        payload.Retire();
    }
    internal static IDiskUsageTreePayload ImportManagedPayload(DiskUsageTree source,
        DiskUsageTreeRegionLayout layout, CancellationToken cancellationToken)
    {
        var region = new ManagedRegion(layout.Length);
        layout.Write(source, region, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new ManagedPayload(layout.Open(region));
    }
    private sealed class ManagedRegion(ulong length) : IDiskUsageTreeRegion
    {
        private readonly byte[] bytes = new byte[checked((int)length)];
        public ulong Length => (ulong)bytes.Length;
        public Span<byte> GetSpanChunk(ulong offset, int count)
            => bytes.AsSpan(checked((int)offset), count);
    }
    private sealed class ManagedPayload(DiskUsageTree source) : IDiskUsageTreePayload
    {
        private readonly object gate = new();
        private DiskUsageTree? tree = source;
        public event Action? Released;
        public IDiskUsageTreePayloadReadLease? TryAcquire()
        { lock (gate) return tree is { } currentTree ? new ManagedReadLease(currentTree) : null; }
        public void Retire()
        {
            lock (gate) { if (tree is null) return; tree = null; }
            Released?.Invoke();
        }
    }
    private sealed class ManagedReadLease(DiskUsageTree source) : IDiskUsageTreePayloadReadLease
    {
        private DiskUsageTree? tree = source;
        public DiskUsageTree Tree => tree ?? throw new ObjectDisposedException(nameof(ManagedReadLease));
        public void Dispose() => tree = null;
    }
    /// <summary>Observes only explicit scan replacements; interval is not a scan cadence.</summary>
    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationAsync(
        string subscriptionId, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = Publications.Subscribe([InformationItemId]);
        cancellationToken.ThrowIfCancellationRequested();
        yield return ReadInformation();
        while (!cancellationToken.IsCancellationRequested)
        {
            await subscription.WaitAsync(cancellationToken);
            yield return ReadInformation();
        }
    }
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
        => new InformationCollectionBorrow(Backend, Publications, InformationItemId, (selection, permit, token) =>
        {
            token.ThrowIfCancellationRequested();
            DeviceInformationSnapshot snapshot = DeviceInformationSnapshot.Empty;
            var committed = permit.TryPublish(() => snapshot = ReadInformation());
            return ValueTask.FromResult(new FieldCollectionResult(InformationReadWriteBackend.Read(selection, snapshot), committed));
        });
    public static string Identity(DiskUsageObjectIdentity identity, bool directory, string path)
        => string.Create(CultureInfo.InvariantCulture, $"ntfs-object|{identity.VolumeSerialNumber:x16}|{identity.RecordNumber:x}|{identity.SequenceNumber:x4}|{(directory ? "directory" : "file")}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(path))}");
    // Default host publication is O(root count), never O(tree node count).
    private static DeviceInformationSnapshot ProjectRoots(DiskUsageScanResult result, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceInformationObject>();
        foreach (var node in result.Tree.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Tree.IdentityOf(node) is not null) devices.Add(ProjectNode(result, node));
        }
        return new(result.Summary.CompletedAt, devices.ToArray());
    }
    internal static DeviceInformationObject ProjectNode(DiskUsageScanResult result, int node)
    {
        var tree = result.Tree;
        var identity = tree.IdentityOf(node) ?? throw new ArgumentException("This node has no MFT object identity.", nameof(node));
        var directory = tree.IsDirectory(node);
        DeviceInformationField Field(DeviceInformationDefinition definition, long value, bool known)
            => new(definition, known && value >= 0 ? JsonSerializer.SerializeToElement(value.ToString(CultureInfo.InvariantCulture)) : null)
                { ObservedAt = result.Summary.CompletedAt };
        DeviceInformationField[] fields = directory
            ? [Field(DeviceInformationFieldRegistry.DirectoryAllFileCountAggregated, tree.FileCountOf(node), tree.CountKnownOf(node)),
               Field(DeviceInformationFieldRegistry.DirectoryAllLogicalSizeAggregated, tree.SizeOf(node), tree.LogicalSizeKnownOf(node)),
               Field(DeviceInformationFieldRegistry.DirectoryAllUniqueAllocatedSizeAggregated, tree.AllocatedOf(node), tree.AllocationKnownOf(node))]
            : [Field(DeviceInformationFieldRegistry.FileAllLogicalSizeActual, tree.SizeOf(node), tree.LogicalSizeKnownOf(node)),
               Field(DeviceInformationFieldRegistry.FileAllAllocatedSizeActual, tree.AllocatedOf(node), tree.AllocationKnownOf(node))];
        return new(Identity(identity, directory, tree.PathOf(node)), directory ? "directory" : "file", fields);
    }

    private static bool SameOwner(DiskUsageResultOwner first, DiskUsageResultOwner second)
        => first.WindowsSessionId == second.WindowsSessionId
            && string.Equals(first.WindowsSid, second.WindowsSid, StringComparison.OrdinalIgnoreCase);
}
