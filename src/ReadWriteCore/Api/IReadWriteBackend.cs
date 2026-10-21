using System.Collections.Immutable;

namespace ResourceManager.ReadWrite;

/// <summary>The hardware backend owns its device resources and atomic current
/// values. Cached reads do not collect. Collection does not register a subscription.
/// Completed failures publish null values; cancelled work does not erase current values.</summary>
public interface IReadWriteBackend
{
    ReadWriteCatalog Catalog { get; }
    ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken);
    FieldReading ReadCached(FieldReference field);
    ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken);
    ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken);
}
