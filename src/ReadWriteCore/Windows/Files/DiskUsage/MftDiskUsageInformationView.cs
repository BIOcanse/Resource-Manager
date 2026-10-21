using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
namespace ResourceManager.ReadWrite.Windows.Files.DiskUsage;

/// <summary>An explicitly selected bounded view of the original committed tree.
/// Node numbers select only at construction; later commits require the captured
/// path, root identity and exact NTFS file identity. No scan or path index exists.</summary>
public sealed class MftDiskUsageInformationView : IDisposable
{
    public const int MaximumSelectedNodes = 256;
    private sealed record Binding(string[] Segments, DiskUsageObjectIdentity RootIdentity,
        DiskUsageObjectIdentity Identity, bool Directory, DeviceInformationObject Declaration);
    private readonly DiskUsageTreeStore store;
    private readonly object gate = new();
    private Binding[] bindings;
    private DeviceInformationSnapshot projected = DeviceInformationSnapshot.Empty;
    private long projectedGeneration = -1;
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    public InformationReadWriteBackend Backend { get; }
    public SamplingPublicationSignal Publications => store.Publications;
    internal MftDiskUsageInformationView(DiskUsageTreeStore store, IEnumerable<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        this.store = store;
        var selected = nodes.Distinct().Take(MaximumSelectedNodes + 1).ToArray();
        if (selected.Length == 0 || selected.Length > MaximumSelectedNodes)
            throw new ArgumentOutOfRangeException(nameof(nodes), "Select between 1 and 256 nodes explicitly.");
        using var lease = store.TryAcquire() ?? throw new InvalidOperationException("Commit an explicit scan before selecting details.");
        var result = lease.Result;
        var tree = result.Tree;
        bindings = selected.Select(node =>
        {
            if ((uint)node >= (uint)tree.Count || tree.IdentityOf(node) is not { } identity)
                throw new ArgumentOutOfRangeException(nameof(nodes), "Select actual MFT nodes in the committed tree.");
            var path = new List<string>();
            var root = node;
            var depth = 0;
            for (var current = node; current >= 0 && depth++ <= tree.Count; current = tree.ParentOf(current))
            { path.Add(tree.NameOf(current).ToString()); root = current; }
            if (depth > tree.Count + 1) throw new InvalidDataException("The selected tree has a cyclic parent chain.");
            path.Reverse();
            var declaration = DiskUsageTreeStore.ProjectNode(result, node);
            return new Binding(path.ToArray(), tree.IdentityOf(root)!.Value, identity, tree.IsDirectory(node),
                declaration with { Fields = declaration.Fields.Select(field => field with { Value = null, ObservedAt = null }).ToArray() });
        }).ToArray();
        Backend = new(ReadInformation, token => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ReadInformation()); },
            _ => new(true, null, [DeviceInformationFieldRegistry.MftSourceId]));
    }
    public DeviceInformationSnapshot ReadInformation()
    {
        lock (gate)
        {
            if (disposed) return DeviceInformationSnapshot.Empty;
            var state = store.ReadState();
            if (projectedGeneration == state.Generation) return projected;
            if (state.Retired) projected = DeviceInformationSnapshot.Empty;
            else
            {
                using var lease = store.TryAcquire(state);
                var result = lease?.Result;
                var objects = bindings.Select(binding =>
                {
                    if (result is not null && Resolve(result.Tree, binding) is var node && node >= 0)
                        return DiskUsageTreeStore.ProjectNode(result, node);
                    return binding.Declaration; // Missing/replaced/failed objects remain null, never rebound.
                }).ToArray();
                projected = new(result?.Summary.CompletedAt, objects);
                // A replacement racing Use is retried by the next passive read;
                // never memoize a failed acquisition as this generation's data.
                if (lease is null && state.Payload is not null) return projected;
            }
            projectedGeneration = state.Generation;
            return projected;
        }
    }
    private static int Resolve(DiskUsageTree tree, Binding binding)
    {
        var current = -1;
        foreach (var root in tree.Roots)
            if (tree.IdentityOf(root) == binding.RootIdentity && tree.NameOf(root).SequenceEqual(binding.Segments[0].AsSpan()))
            { current = root; break; }
        for (var segment = 1; current >= 0 && segment < binding.Segments.Length; segment++)
        {
            var found = -1;
            var visited = 0;
            for (var child = tree.FirstChildOf(current); child >= 0 && visited++ < tree.Count; child = tree.NextSiblingOf(child))
                if (tree.NameOf(child).SequenceEqual(binding.Segments[segment].AsSpan())) { found = child; break; }
            current = found;
        }
        return current >= 0 && tree.IdentityOf(current) == binding.Identity && tree.IsDirectory(current) == binding.Directory ? current : -1;
    }
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        return new InformationCollectionBorrow(Backend, Publications, DiskUsageTreeStore.InformationItemId, (selection, permit, token) =>
        {
            token.ThrowIfCancellationRequested();
            DeviceInformationSnapshot snapshot = DeviceInformationSnapshot.Empty;
            var observed = false;
            var permitted = permit.TryPublish(() =>
            {
                lock (gate) if (!disposed) { snapshot = ReadInformation(); observed = true; }
            });
            var committed = permitted && observed;
            return ValueTask.FromResult(new FieldCollectionResult(InformationReadWriteBackend.Read(selection, snapshot), committed));
        });
    }
    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CancellationToken viewToken;
        lock (gate) { if (disposed) yield break; viewToken = lifetime.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, viewToken);
        using var subscription = Publications.Subscribe([DiskUsageTreeStore.InformationItemId]);
        linked.Token.ThrowIfCancellationRequested();
        yield return ReadInformation();
        while (!linked.IsCancellationRequested)
        {
            await subscription.WaitAsync(linked.Token);
            lock (gate) if (disposed) yield break;
            yield return ReadInformation();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; bindings = []; projected = DeviceInformationSnapshot.Empty;
        }
        lifetime.Cancel();
        _ = Backend.Catalog; // Refresh outside the view lock; backend calls ReadInformation.
    }
}
