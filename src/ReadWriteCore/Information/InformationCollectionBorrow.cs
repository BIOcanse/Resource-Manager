using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Information;

// Selection metadata belongs to this borrow, never a readings cache. A complete
// information inventory is the existing capture item; no resources stay open.
internal sealed class InformationCollectionBorrow(InformationReadWriteBackend backend,
    SamplingPublicationSignal publications, string itemId,
    Func<ImmutableArray<InformationFieldSelection>, IMetricPublicationPermit, CancellationToken,
        ValueTask<FieldCollectionResult>> collect,
    FieldCollectionSourceKind sourceKind = FieldCollectionSourceKind.CurrentValue) : IReadWriteCollectionBorrow
{
    private readonly object gate = new();
    private readonly Dictionary<FieldReference, InformationFieldSelection> selections = [];
    private bool disposed;
    internal static ImmutableArray<FieldReference> QueriedFieldsAfterSourceRead(
        ImmutableArray<InformationFieldSelection> selection, bool sourceRead)
        => sourceRead ? [.. selection.Select(static item => item.Field)] : [];

    public IReadWriteBackend Backend => backend;
    public SamplingPublicationSignal Publications => publications;
    public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
    {
        _ = Select(fields);
        return [.. fields.Select(field => new FieldCollectionBinding(field, [itemId], [itemId], sourceKind))];
    }
    private ImmutableArray<InformationFieldSelection> Select(ImmutableArray<FieldReference> fields)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var field in fields)
                if (!selections.ContainsKey(field)) selections.Add(field, backend.Select([field])[0]);
            return [.. fields.Select(field => selections[field])];
        }
    }
    public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields) => _ = Select(activeFields);
    public ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
        IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permit);
        cancellationToken.ThrowIfCancellationRequested();
        return collect(Select(fields), new BorrowPermit(this, permit), cancellationToken);
    }
    public void Dispose() { lock (gate) disposed = true; }
    private sealed class BorrowPermit(InformationCollectionBorrow owner, IMetricPublicationPermit outer) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => outer.WorkspaceIncarnation;
        public ulong ConfigurationGeneration => outer.ConfigurationGeneration;
        public bool IsActive { get { lock (owner.gate) return !owner.disposed && outer.IsActive; } }
        public bool TryPublish(Action commit)
        { lock (owner.gate) return !owner.disposed && outer.TryPublish(commit); }
    }
}
