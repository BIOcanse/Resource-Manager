using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class WindowsProcessFieldOwner
{
    // Resource intent only; frequency, logical demand and values remain with their original owners.
    private readonly HashSet<ProcessCollectionBorrow> collectionBorrows = [];
    private bool directNetworkDemand;
    private bool directDiskDemand;
    private bool directGpuDemand;
    private bool directResidencyDemand;

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        CaptureGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var borrow = new ProcessCollectionBorrow(this);
            collectionBorrows.Add(borrow);
            return borrow;
        }
        finally { CaptureGate.Release(); }
    }

    private void SetBorrowDemand(ProcessCollectionBorrow borrow, bool networkDemand, bool diskDemand, bool gpuDemand, bool residencyDemand, bool remove)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { CaptureGate.Wait(); }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { return; }
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (remove) collectionBorrows.Remove(borrow);
            else { borrow.RequireActive(); borrow.NetworkDemand = networkDemand; borrow.DiskDemand = diskDemand; borrow.GpuDemand = gpuDemand; borrow.ResidencyDemand = residencyDemand; }
            if (!directNetworkDemand && !collectionBorrows.Any(static item => !item.IsDisposed && item.NetworkDemand))
                network?.Stop();
            if (!directDiskDemand && !collectionBorrows.Any(static item => !item.IsDisposed && item.DiskDemand))
                disk?.Stop();
            if (!directGpuDemand && !collectionBorrows.Any(static item => !item.IsDisposed && item.GpuDemand))
                ReleaseOwnedGpuSource();
            if (!directResidencyDemand && !collectionBorrows.Any(static item => !item.IsDisposed && item.ResidencyDemand))
                residency?.Stop();
        }
        finally { CaptureGate.Release(); TryDisposeRetiredGate(); }
    }

    private sealed class ProcessCollectionBorrow(WindowsProcessFieldOwner owner) : IReadWriteCollectionBorrow
    {
        private readonly object bindingGate = new();
        private readonly Dictionary<FieldReference, ProcessFieldReadWriteBackend.Selection> bindings = [];
        private int disposed;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal bool NetworkDemand;
        internal bool DiskDemand;
        internal bool GpuDemand;
        internal bool ResidencyDemand;
        public IReadWriteBackend Backend => owner.Backend;
        public SamplingPublicationSignal Publications => owner.Publications;
        internal void RequireActive() => ObjectDisposedException.ThrowIf(IsDisposed, this);
        private ImmutableArray<ProcessFieldReadWriteBackend.Selection> Select(ImmutableArray<FieldReference> fields)
        {
            RequireActive();
            if (fields.IsDefault) throw new ArgumentException("Supply an initialized field selection.", nameof(fields));
            lock (bindingGate)
            {
                // Binding metadata survives directory retirement; no observed values are retained here.
                var missing = fields.Where(field => !bindings.ContainsKey(field)).Distinct().ToImmutableArray();
                if (!missing.IsEmpty)
                    foreach (var item in ((ProcessFieldReadWriteBackend)Backend).Select(missing))
                        bindings.Add(item.Field, item);
                return [.. fields.Select(field => bindings[field])];
            }
        }
        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
            => [.. Select(fields).Select(static item => new FieldCollectionBinding(item.Field,
                [item.Definition.DatasetId], [item.Definition.DatasetId], FieldCollectionSourceKind.SourceQuery))];
        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields)
        {
            var selected = Select(activeFields);
            owner.SetBorrowDemand(this, selected.Any(static item =>
                item.Definition.DatasetId == ProcessMetricDatasetIds.ObservedNetworkTraffic), selected.Any(static item =>
                item.Definition.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo), selected.Any(static item =>
                item.Definition.DatasetId == ProcessMetricDatasetIds.GpuResidency), selected.Any(static item =>
                item.Definition.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency), false);
        }
        public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
            IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(permit);
            var selected = Select(fields);
            if (selected.IsEmpty) throw new ArgumentException("Select at least one field.", nameof(fields));
            var result = await owner.CollectCoreAsync(selected.Select(static item => item.Definition.Name).ToArray(),
                cancellationToken, permit, this);
            return new([.. selected.Select(item => ProcessFieldReadWriteBackend.Project(item, result.Snapshot))],
                result.Committed, fields);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.SetBorrowDemand(this, false, false, false, false, true);
        }
    }
}

