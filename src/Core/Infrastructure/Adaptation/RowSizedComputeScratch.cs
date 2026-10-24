using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Adaptation;

// The PDH high-water path: return/close the old region before creating the next
// identity. Refill of a reclaimed region continues to use the same identity.
internal sealed class RowSizedComputeScratch(Func<ulong, IComputeScratch> factory, string workArea) : IComputeScratch
{
    private IComputeScratch? scratch;
    private ulong capacity;
    private string capacityRows = "0";
    private bool bound;
    private bool disposed;
    private IDisposable? nativeCapacityRegistration;
    private Action? stopNativeGrowthObservation;

    internal void DescribeNative(Func<NativeWorkspaceCapacityDescription> read,
        Action<Action<NativeWorkspaceGrowthEvent>?>? observeGrowth = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (nativeCapacityRegistration is not null)
            throw new InvalidOperationException($"{workArea} native capacity is already registered.");
        if (factory.Target is IWorkspaceCapacityRegistry registry)
        {
            nativeCapacityRegistration = registry.RegisterNativeWorkspace(workArea + "-native", read);
            if (observeGrowth is not null)
            {
                stopNativeGrowthObservation = () => observeGrowth(null);
                observeGrowth(registry.LogNativeGrowth);
            }
        }
    }

    public ulong Length => bound ? capacity : 0;

    internal void Ensure(ulong length, string rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(length);
        if (bound && scratch is not null && length <= capacity) return;
        // A failed close keeps this reference and prevents a second registration.
        bound = false;
        scratch?.Dispose();
        scratch = null;
        var created = factory(length)
            ?? throw new InvalidOperationException($"{workArea} scratch factory returned no resource.");
        scratch = created; // Keep even a failed candidate reachable for explicit close.
        if (created.Length != length)
        {
            var mismatch = new InvalidOperationException($"{workArea} scratch factory returned the wrong length.");
            try { created.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(mismatch, cleanup); }
            scratch = null;
            throw mismatch;
        }
        if (created is ResourceManagerLedgerOwner.ComputeScratch registered)
            registered.DescribeGrowth(workArea, capacityRows, rows, capacity);
        else
            System.Diagnostics.Trace.TraceInformation(
                "Workspace {0} grew rows {1} -> {2}, bytes {3} -> {4}: actual row demand exceeded high water.",
                workArea, capacityRows, rows, capacity, length);
        capacity = length;
        capacityRows = rows;
        bound = true;
    }

    public ComputeScratchLease Acquire()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!bound) throw new InvalidOperationException($"{workArea} scratch has no validated layout.");
        return (scratch ?? throw new InvalidOperationException($"{workArea} scratch has no bound rows.")).Acquire();
    }

    public void Dispose()
    {
        if (disposed) return;
        scratch?.Dispose();
        stopNativeGrowthObservation?.Invoke();
        stopNativeGrowthObservation = null;
        nativeCapacityRegistration?.Dispose();
        nativeCapacityRegistration = null;
        scratch = null;
        bound = false;
        disposed = true;
    }
}
