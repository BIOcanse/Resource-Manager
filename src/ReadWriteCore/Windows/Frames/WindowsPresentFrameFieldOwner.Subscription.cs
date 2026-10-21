using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Frames;

public sealed partial class WindowsPresentFrameFieldOwner
{
    private readonly HashSet<FrameFieldCollectionBorrow> borrows = [];

    /// <summary>Borrow the original owner's current and source; this creates no worker or ledger.</summary>
    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        CaptureGate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var borrow = new FrameFieldCollectionBorrow(this);
            borrows.Add(borrow);
            return borrow;
        }
        finally { CaptureGate.Release(); TryDisposeRetiredGate(); }
    }

    private void SetBorrowDemand(FrameFieldCollectionBorrow borrow, bool active, bool remove)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        CaptureGate.Wait();
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (remove)
            {
                borrows.Remove(borrow);
                borrow.ReleaseSourceLease();
            }
            else
            {
                borrow.RequireActive();
                if (!active) borrow.ReleaseSourceLease();
                // Active demand is acquired by CollectAsync inside the same
                // failure and publication boundary as the actual source read.
            }
        }
        finally { CaptureGate.Release(); TryDisposeRetiredGate(); }
    }

    private void EnsureBorrowLease(FrameFieldCollectionBorrow borrow)
    {
        borrow.RequireActive();
        if (!borrow.HasSourceLease) borrow.AcquireSourceLease(source.AcquireSubscription());
    }

    private async ValueTask<FieldCollectionResult> CollectBorrowedAsync(
        ImmutableArray<InformationFieldSelection> selection,
        IMetricPublicationPermit permit,
        CancellationToken cancellationToken,
        FrameFieldCollectionBorrow borrow)
    {
        var result = await CaptureSelectedCoreAsync(selection, cancellationToken, permit, borrow).ConfigureAwait(false);
        ImmutableArray<FieldReference> queried = [.. selection.Where(static item => FrameFieldRegistry.IsSupported(item.Definition)
                && item.Definition.Name != FrameFieldRegistry.WindowDurationConfigured)
            .Select(static item => item.Field)];
        return new(InformationReadWriteBackend.Read(selection, ReadInformation()), result.Committed, queried);
    }

    private void ReleaseBorrowResources()
    {
        foreach (var borrow in borrows) borrow.ReleaseSourceLease();
        borrows.Clear();
    }

    private sealed class FrameFieldCollectionBorrow(WindowsPresentFrameFieldOwner owner) : IReadWriteCollectionBorrow
    {
        private readonly object bindingGate = new();
        private readonly Dictionary<FieldReference, InformationFieldSelection> bindings = [];
        private IDisposable? sourceLease;
        private int disposed;

        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal bool HasSourceLease => sourceLease is not null;
        public IReadWriteBackend Backend => owner.CreateBackend();
        public ResourceManager.ReadWrite.Publications.SamplingPublicationSignal Publications => owner.Publications;

        internal void RequireActive()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner.disposed) != 0, owner);
        }

        private ImmutableArray<InformationFieldSelection> Select(ImmutableArray<FieldReference> fields)
        {
            if (fields.IsDefault) throw new ArgumentException("Supply an initialized field selection.", nameof(fields));
            lock (bindingGate)
            {
                RequireActive();
                foreach (var field in fields)
                {
                    if (bindings.ContainsKey(field)) continue;
                    var definition = FrameFieldRegistry.Find(field.FieldName)
                        ?? throw new ArgumentException("Unknown canonical frame field.", nameof(fields));
                    if (!owner.Query.Matches(field, definition))
                        throw new ArgumentException("Field reference does not match this fixed Present query.", nameof(fields));
                    bindings.Add(field, new(field, definition));
                }
                return [.. fields.Select(field => bindings[field])];
            }
        }

        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
        {
            _ = Select(fields);
            return [.. fields.Select(field => new FieldCollectionBinding(field,
                [CollectionItemId], [CollectionItemId],
                FrameFieldRegistry.Find(field.FieldName) is { } definition
                    && FrameFieldRegistry.IsSupported(definition)
                    && definition.Name != FrameFieldRegistry.WindowDurationConfigured
                        ? FieldCollectionSourceKind.SourceQuery
                        : FieldCollectionSourceKind.CurrentValue))];
        }

        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields)
        {
            var selected = Select(activeFields);
            var needsSource = selected.Any(static item => FrameFieldRegistry.IsSupported(item.Definition)
                && item.Definition.Name != FrameFieldRegistry.WindowDurationConfigured);
            owner.SetBorrowDemand(this, needsSource, false);
        }

        public ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
            IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(permit);
            cancellationToken.ThrowIfCancellationRequested();
            var selected = Select(fields);
            return owner.CollectBorrowedAsync(selected, permit, cancellationToken, this);
        }

        internal void AcquireSourceLease(IDisposable lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            if (sourceLease is null) sourceLease = lease;
            else lease.Dispose();
        }

        internal void ReleaseSourceLease()
        {
            sourceLease?.Dispose();
            sourceLease = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            owner.SetBorrowDemand(this, false, true);
        }
    }
}
