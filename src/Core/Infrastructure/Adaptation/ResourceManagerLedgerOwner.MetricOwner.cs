using System.Buffers;
using ResourceManager.Ledger;
using ResourceManager.Ledger.Identity;
using ResourceManager.ReadWrite.Windows.Metrics.Zig;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class ResourceManagerLedgerOwner
{
    /// <summary>
    /// Declares one identity once. The active resource is used in the authority's
    /// run scope; the work resource is used once per round. Neither constructor
    /// acquires a lease or pin. This migration factory is not wired into the host.
    /// </summary>
    internal IMetricOwnerMemoryResource CreateMetricOwnerMemoryResource(ulong byteCount, bool activeState)
    {
        ArgumentOutOfRangeException.ThrowIfZero(byteCount);
        var resource = CreateScratch(Math.Max(byteCount, NativePageSize));
        resource.WorkArea = activeState ? "hardware-metric-active" : "hardware-metric-work";
        return new MetricMemoryResource(this, resource);
    }

    internal (int ActiveStateUses, int WorkUses) ReadMetricOwnerMemoryDiagnostics()
    {
        lock (operationGate)
        {
            var active = 0; var work = 0;
            using var resources = computeScratch.GetEnumerator();
            while (resources.MoveNext())
            {
                var resource = resources.Current;
                if (resource.WorkArea == "hardware-metric-active") active = checked(active + resource.Active);
                else if (resource.WorkArea == "hardware-metric-work") work = checked(work + resource.Active);
            }
            return (active, work);
        }
    }

    private async ValueTask UseMetricMemoryAsync(ComputeScratch resource, MetricOwnerMemoryUse use,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(use);
        cancellationToken.ThrowIfCancellationRequested();
        LedgerLease<LedgerUid128> lease;
        lock (operationGate)
        {
            ThrowIfDisposed();
            if (closing || resource.Retiring || resource.Closed) throw new ObjectDisposedException(nameof(resource));
            var used = owner.Use(resource.Uid);
            if (used.Status is LedgerStatus.NotInMemory or LedgerStatus.Nonexistent)
            {
                var pushed = owner.Push(LedgerTarget<LedgerUid128>.ForTable(ComputeScratchTable),
                    resource.Uid, resource.Length, operationWorkspace);
                if (pushed.Status is not (LedgerStatus.NewlyAllocated or LedgerStatus.InMemory))
                    throw new LedgerException(pushed.Status, "allocate hardware metric memory");
                lease = pushed.Lease ?? throw new InvalidOperationException("Ledger returned no metric memory lease.");
            }
            else
            {
                if (used.Status != LedgerStatus.InMemory) throw new LedgerException(used.Status, "use hardware metric memory");
                lease = used.Lease ?? throw new InvalidOperationException("Ledger returned no metric memory lease.");
            }
            resource.Active++;
        }

        // This local is the acquired ownership. Work ends after the complete
        // callback, including native abort/detach and adapter drain. Active state
        // spans the actual authority run for catalog/counter continuity; it ends
        // when that run closes its admissions and unbinds. No field owns either.
        try
        {
            if (lease.Length != resource.Length) throw new InvalidOperationException("Metric memory extent changed.");
            using (var pin = lease.GetMemoryChunk(0, 1).Pin())
            {
                await use(BorrowMetricMemory(pin, lease.Length, lease.AllocationNumber), cancellationToken).ConfigureAwait(false);
            }
            if (lease.IsLoading) lease.PublishOnDispose();
        }
        finally
        {
            // Native borrows and pins have drained. A terminal cleanup failure
            // still returned this use; a pre-terminal error must abandon it here.
            try
            {
                try
                {
                    var ended = await lease.EndUseAsync().ConfigureAwait(false);
                    if (!ended.HasEnded || ended.CleanupFailed) throw new LedgerEndException(ended);
                }
                finally
                {
                    if (!lease.HasEnded) await lease.AbandonAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                SettleMetricMemoryEnd(lease, (Owner: this, Resource: resource),
                    static state => state.Owner.ScratchEnded(state.Resource));
            }
        }
    }

    // Claim before business cleanup: ScratchEnded decrements Active before its
    // possible deferred CloseScratch failure. A static callback avoids a round allocation.
    internal static void SettleMetricMemoryEnd<TState>(LedgerLease<LedgerUid128> lease,
        TState state, Action<TState> settle)
    {
        if (lease.TryTakeEndResult(out _)) settle(state);
    }

    private static unsafe MetricOwnerMemory BorrowMetricMemory(MemoryHandle pin, ulong length, ulong allocation)
    {
        if (pin.Pointer is null) throw new InvalidOperationException("Ledger returned no metric memory address.");
        return new((nint)pin.Pointer, length, allocation);
    }

    private sealed class MetricMemoryResource(ResourceManagerLedgerOwner owner, ComputeScratch resource)
        : IMetricOwnerMemoryResource
    {
        public ulong Length => resource.Length;
        public ValueTask UseAsync(MetricOwnerMemoryUse use, CancellationToken cancellationToken = default)
            => owner.UseMetricMemoryAsync(resource, use, cancellationToken);
        public void Dispose() => resource.Dispose();
    }
}
