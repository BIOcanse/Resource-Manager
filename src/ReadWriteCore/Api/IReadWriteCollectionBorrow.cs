using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite;

/// <summary>Whether a field borrow reads the owner's current value or can run its original source query.</summary>
public enum FieldCollectionSourceKind
{
    CurrentValue = 0,
    SourceQuery = 1
}

/// <summary>Original planner item, publication identities, and source behavior for one exact canonical field.</summary>
public sealed record FieldCollectionBinding(FieldReference Field,
    ImmutableArray<string> CollectionItemIds, ImmutableArray<string> PublicationKeys,
    FieldCollectionSourceKind SourceKind = FieldCollectionSourceKind.CurrentValue)
{
    public void Deconstruct(out FieldReference field, out ImmutableArray<string> collectionItemIds,
        out ImmutableArray<string> publicationKeys)
    {
        field = Field;
        collectionItemIds = CollectionItemIds;
        publicationKeys = PublicationKeys;
    }
}

/// <summary>The readings and exact fields for which the original source query ran in this attempt. Committed reports
/// publication separately; it does not imply that any query ran. QueriedFields may be nonempty when publication was
/// rejected, so consumers must check Committed before treating those readings as a committed fresh result.</summary>
public sealed record FieldCollectionResult(ImmutableArray<FieldReading> Readings, bool Committed,
    ImmutableArray<FieldReference> QueriedFields = default)
{
    public ImmutableArray<FieldReference> QueriedFields { get; init; } = QueriedFields.IsDefault ? [] : QueriedFields;

    public void Deconstruct(out ImmutableArray<FieldReading> readings, out bool committed)
    {
        readings = Readings;
        committed = Committed;
    }
}

/// <summary>A caller-owned capture/resource borrow. It creates no worker, broker or current cache.
/// Dispose removes only this borrow's demand. The read/write owner remains caller-owned.</summary>
public interface IReadWriteCollectionBorrow : IDisposable
{
    IReadWriteBackend Backend { get; }
    SamplingPublicationSignal Publications { get; }
    ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields);
    void SynchronizeDemand(ImmutableArray<FieldReference> activeFields);
    ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
        IMetricPublicationPermit permit, CancellationToken cancellationToken = default);
}
