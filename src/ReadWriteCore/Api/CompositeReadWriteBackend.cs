using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ResourceManager.ReadWrite;

public sealed record BackendFieldSelection(IReadWriteBackend Backend, ImmutableArray<FieldReference> Fields,
    ImmutableArray<int> Positions);

/// <summary>Routes explicit field bindings to their existing owners. It stores
/// metadata only; providers retain collection, resources and current values.</summary>
public sealed class CompositeReadWriteBackend : IReadWriteBackend
{
    public ImmutableArray<IReadWriteBackend> Backends { get; }
    private readonly object gate = new();
    private ImmutableArray<ReadWriteCatalog> boundCatalogs = [];
    private ReadWriteCatalog catalog = new([], []);
    private FrozenDictionary<FieldReference, int> routes = FrozenDictionary<FieldReference, int>.Empty;

    public CompositeReadWriteBackend(IEnumerable<IReadWriteBackend> backends)
    {
        Backends = [.. backends];
        if (Backends.IsDefaultOrEmpty || Backends.Any(static backend => backend is null)
            || Backends.Distinct(ReferenceEqualityComparer.Instance).Count() != Backends.Length)
            throw new ArgumentException("Supply distinct explicit field owners.", nameof(backends));
    }

    public ReadWriteCatalog Catalog { get { lock (gate) { Refresh(); return catalog; } } }

    public async ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        foreach (var backend in Backends)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await backend.DiscoverAsync(cancellationToken);
        }
        return Catalog;
    }

    public ImmutableArray<BackendFieldSelection> Select(ImmutableArray<FieldReference> fields)
        => SelectCore(fields, allowMissing: false);

    private ImmutableArray<BackendFieldSelection> SelectCore(ImmutableArray<FieldReference> fields, bool allowMissing)
    {
        lock (gate)
        {
            Refresh();
            var selected = new Dictionary<int, List<(FieldReference Field, int Position)>>();
            for (var position = 0; position < fields.Length; position++)
            {
                var field = fields[position];
                if (allowMissing && catalog.Find(field) is null) continue;
                _ = catalog.Require(field);
                var owner = routes[field];
                if (!selected.TryGetValue(owner, out var group)) selected.Add(owner, group = []);
                group.Add((field, position));
            }
            return [.. selected.Select(pair => new BackendFieldSelection(Backends[pair.Key],
                [.. pair.Value.Select(static item => item.Field)], [.. pair.Value.Select(static item => item.Position)]))];
        }
    }

    public FieldReading ReadCached(FieldReference field)
    {
        IReadWriteBackend backend;
        lock (gate) { Refresh(); _ = catalog.Require(field); backend = Backends[routes[field]]; }
        return backend.ReadCached(field);
    }

    public async ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
    {
        var groups = Select(fields);
        var values = new FieldReading[fields.Length];
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = await group.Backend.CollectAsync(group.Fields, cancellationToken);
            if (part.Length != group.Fields.Length) throw new InvalidOperationException("Field owner omitted selected readings.");
            for (var index = 0; index < part.Length; index++)
            {
                if (part[index].Field != group.Fields[index]) throw new InvalidOperationException("Field owner changed selected identities.");
                values[group.Positions[index]] = part[index];
            }
        }
        return [.. values];
    }

    public ImmutableArray<FieldReading> ReadCachedBatch(ImmutableArray<FieldReference> fields)
    {
        var values = fields.Select(static field => new FieldReading(field, FieldObservationStatus.Unavailable,
            null, null, string.Empty)).ToArray();
        foreach (var group in SelectCore(fields, allowMissing: true))
        {
            var part = group.Backend.ReadCachedBatch(group.Fields);
            if (part.Length != group.Fields.Length) throw new InvalidOperationException("Field owner omitted selected readings.");
            for (var index = 0; index < part.Length; index++)
            {
                if (part[index].Field != group.Fields[index]) throw new InvalidOperationException("Field owner changed selected identities.");
                values[group.Positions[index]] = part[index];
            }
        }
        return [.. values];
    }

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
    {
        IReadWriteBackend backend;
        lock (gate) { Refresh(); _ = catalog.Require(field); backend = Backends[routes[field]]; }
        return backend.WriteAsync(field, value, cancellationToken);
    }

    private void Refresh()
    {
        var changed = boundCatalogs.Length != Backends.Length;
        if (!changed)
            for (var owner = 0; owner < Backends.Length; owner++)
                if (!ReferenceEquals(Backends[owner].Catalog, boundCatalogs[owner])) { changed = true; break; }
        if (!changed) return;
        var current = Backends.Select(static backend => backend.Catalog).ToImmutableArray();
        var devices = new Dictionary<string, DeviceDescriptor>(StringComparer.Ordinal);
        var fields = new List<FieldCapability>();
        var nextRoutes = new Dictionary<FieldReference, int>();
        for (var owner = 0; owner < current.Length; owner++)
        {
            foreach (var device in current[owner].Devices)
            {
                if (devices.TryGetValue(device.Identity, out var previous))
                {
                    if (previous.Resource != device.Resource || previous.Vendor is not null && device.Vendor is not null && previous.Vendor != device.Vendor)
                        throw new InvalidOperationException("Field owners disagree on device metadata.");
                    devices[device.Identity] = previous with { Vendor = previous.Vendor ?? device.Vendor };
                }
                else devices.Add(device.Identity, device);
            }
            foreach (var field in current[owner].Fields) { nextRoutes.Add(field.Field, owner); fields.Add(field); }
        }
        catalog = new(devices.Values, fields);
        routes = nextRoutes.ToFrozenDictionary();
        boundCatalogs = current;
    }
}
