using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Metrics;

public sealed partial class WindowsHardwareMetricOwner
{
    private readonly HashSet<HardwareCollectionBorrow> collectionBorrows = [];
    private bool directNotebookDemand;

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        capture.SampleGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var borrow = new HardwareCollectionBorrow(this);
            collectionBorrows.Add(borrow);
            return borrow;
        }
        finally { capture.SampleGate.Release(); }
    }

    private void SetBorrowDemand(HardwareCollectionBorrow borrow, bool notebookDemand, bool remove)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        capture.SampleGate.Wait();
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (remove) collectionBorrows.Remove(borrow);
            else { borrow.RequireActive(); borrow.NotebookDemand = notebookDemand; }
            // The original resource owns release/reopen. Other passive readers retain explicit owner lifetime.
            if (!directNotebookDemand && !collectionBorrows.Any(static item => !item.IsDisposed && item.NotebookDemand))
                capture.ReleasePlatformSensorDemand();
        }
        finally { capture.SampleGate.Release(); }
    }

    private sealed class HardwareCollectionBorrow(WindowsHardwareMetricOwner owner) : IReadWriteCollectionBorrow
    {
        private readonly object bindingGate = new();
        private readonly Dictionary<FieldReference, MetricFieldSelection> bindings = [];
        private int disposed;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal bool NotebookDemand;
        public IReadWriteBackend Backend => owner.CreateBackend();
        public SamplingPublicationSignal Publications => owner.Publications;
        internal void RequireActive() => ObjectDisposedException.ThrowIf(IsDisposed, this);
        private MetricFieldSelection[] Select(ImmutableArray<FieldReference> fields)
        {
            RequireActive();
            if (fields.IsDefault) throw new ArgumentException("Supply an initialized field selection.", nameof(fields));
            lock (bindingGate)
            {
                var missing = fields.Where(field => !bindings.ContainsKey(field)).Distinct().ToImmutableArray();
                if (!missing.IsEmpty)
                {
                    var selected = ((MetricReadWriteBackend)Backend).Select(missing);
                    for (var index = 0; index < missing.Length; index++)
                        bindings.Add(missing[index], selected[index]);
                }
                return fields.Select(field => bindings[field]).ToArray();
            }
        }
        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
        {
            var selected = Select(fields);
            return [.. selected.Select((item, index) =>
            {
                var key = MetricDatasetIds.ForSystemMetric(item.Binding.RequestAddress);
                var queryKey = MetricDatasetIds.ForSystemMetric(item.Binding.EffectiveSampleRequestAddress);
                return new FieldCollectionBinding(fields[index], item.Binding.Field.Resource == MetricResource.Gpu
                    ? [key, MetricDatasetIds.SystemGpuInventory] : [key], [queryKey], FieldCollectionSourceKind.SourceQuery);
            })];
        }
        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields)
        {
            var selected = Select(activeFields);
            owner.SetBorrowDemand(this, selected.Any(static item =>
                item.Binding.SourceId == NativeMetricSnapshotSources.NotebookOemFan), false);
        }
        public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
            IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(permit);
            var selected = Select(fields);
            if (selected.Length == 0) throw new ArgumentException("Select at least one field.", nameof(fields));
            var request = MetricSampleRequest.ForIds(selected.Select(static item => item.Binding.EffectiveSampleRequestAddress));
            var queriedMetricAddresses = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);
            var snapshot = await owner.CaptureCoreAsync(request, cancellationToken, permit, this,
                addresses => queriedMetricAddresses = addresses);
            ImmutableArray<FieldReference> queriedFields = [.. selected.Select((item, index) => (Selection: item, Index: index))
                .Where(pair => queriedMetricAddresses.Contains(pair.Selection.Binding.EffectiveSampleRequestAddress))
                .Select(pair => fields[pair.Index])];
            return snapshot is null
                ? new([], false, queriedFields)
                : new(MetricReadWriteBackend.Read(fields, selected, snapshot), true, queriedFields);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.SetBorrowDemand(this, false, true);
        }
    }
}

