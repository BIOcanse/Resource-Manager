using System.Collections.Immutable;
using ResourceManager.ReadWrite.Dependencies;

namespace ResourceManager.ReadWrite;

/// <summary>Public entry shared by direct callers and external subscribers.
/// The backend remains the only owner of sampling and current values.</summary>
public sealed class ReadWriteCore(IReadWriteBackend backend, IReadWriteDependencies dependencies)
{
    private readonly IReadWriteBackend backend = backend ?? throw new ArgumentNullException(nameof(backend));
    public IReadWriteDependencies Dependencies { get; } = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    public ReadWriteCatalog Catalog => backend.Catalog;
    public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return backend.DiscoverAsync(cancellationToken);
    }
    public FieldCapability Query(FieldReference field) => Catalog.Require(field);

    public FieldReading ReadCached(FieldReference field)
    {
        _ = Query(field);
        return backend.ReadCached(field);
    }

    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken = default)
    {
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one field.", nameof(fields));
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = Catalog;
        foreach (var field in fields) _ = catalog.Require(field);
        return backend.CollectAsync(fields, cancellationToken);
    }

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var capability = Query(field);
        if (!capability.Write.Supported)
            return ValueTask.FromResult(new FieldWriteResult(field, "unsupported", capability.Write.Reason));
        if (!value.Matches(capability.Definition.ValueKind))
            return ValueTask.FromResult(new FieldWriteResult(field, "failed", "field-value-kind-mismatch"));
        return backend.WriteAsync(field, value, cancellationToken);
    }
}
