using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Adaptation;

// The PDH high-water path: return/close the old region before creating the next
// identity. Refill of a reclaimed region continues to use the same identity.
// A per-round owner calls Release when its round ends; the next Ensure then
// creates an identity of exactly that round's length.
internal sealed class RowSizedComputeScratch(Func<ulong, IComputeScratch> factory, string workArea) : IComputeScratch
{
    private IComputeScratch? scratch;
    private ulong capacity;
    // The largest length created so far; growth is logged only above it.
    private ulong peak;
    private string peakRows = "0";
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
        var registered = created as ResourceManagerLedgerOwner.ComputeScratch;
        if (length <= peak)
        {
            // A round within the logged peak names its region without a growth event.
            if (registered is not null)
            {
                registered.WorkArea = workArea;
                registered.Rows = rows;
            }
        }
        else
        {
            if (registered is not null)
                registered.DescribeGrowth(workArea, peakRows, rows, peak);
            else
                System.Diagnostics.Trace.TraceInformation(
                    "Workspace {0} grew rows {1} -> {2}, bytes {3} -> {4}: actual row demand exceeded high water.",
                    workArea, peakRows, rows, peak, length);
            peak = length;
            peakRows = rows;
        }
        capacity = length;
        bound = true;
    }

    // Returns the region at the end of a round. A failed close keeps the
    // reference so Ensure or Dispose closes it before any new identity.
    internal void Release()
    {
        if (disposed) return; // Dispose already closed the region.
        bound = false;
        scratch?.Dispose();
        scratch = null;
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
