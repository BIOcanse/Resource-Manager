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
    /// <summary>Reads each exact selection; bindings removed during a catalog race
    /// return unavailable without discarding the other selected fields.</summary>
    ImmutableArray<FieldReading> ReadCachedBatch(ImmutableArray<FieldReference> fields)
    {
        var readings = ImmutableArray.CreateBuilder<FieldReading>(fields.Length);
        foreach (var field in fields)
        {
            try { readings.Add(ReadCached(field)); }
            catch (ArgumentException) { readings.Add(new(field, FieldObservationStatus.Unavailable, null, null, string.Empty)); }
        }
        return readings.MoveToImmutable();
    }
    ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken);
    ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken);
}
