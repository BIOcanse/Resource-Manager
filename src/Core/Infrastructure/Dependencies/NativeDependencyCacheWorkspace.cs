using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.Dependencies;

internal delegate T NativeDependencyCacheRegionAction<T>(IntPtr region, ulong availableBytes);
internal delegate (T Value, ulong RequiredBytes) NativeDependencyCacheProbeAction<T>(IntPtr region, ulong availableBytes);

// One stable resource identity: native cache prefix, followed by the current probe's temporary arena.
// The managed roots below are cleanup metadata; only the native records select a held result.
internal sealed class NativeDependencyCacheWorkspace : IDisposable
{
    private readonly object gate = new();
    private readonly ResourceManagerLedgerOwner? ownedLedger;
    private readonly IComputeScratch scratch;
    private readonly ulong maximumWorkspaceBytes;
    private readonly ulong allocationUnit;
    private readonly HashSet<ulong> roots = [];
    private readonly ConditionalWeakTable<object, SourceIdentity> sources = new();
    private long nextSourceIdentity;
    private ulong allocationNumber;
    private bool initialized;
    private bool disposed;
    private bool closed;
    private ulong statusProbes;
    private ulong providerProbes;

    internal NativeDependencyCacheWorkspace() : this(new ResourceManagerLedgerOwner(), true) { }
    internal NativeDependencyCacheWorkspace(ResourceManagerLedgerOwner owner) : this(owner, false) { }

    private NativeDependencyCacheWorkspace(ResourceManagerLedgerOwner owner, bool ownsOwner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ownedLedger = ownsOwner ? owner : null;
        maximumWorkspaceBytes = DependencyNativeSettings.MaximumWorkspaceBytes;
        allocationUnit = owner.NativePageSize;
        try
        {
            if (maximumWorkspaceBytes < allocationUnit)
                throw new ArgumentOutOfRangeException(nameof(DependencyNativeSettings.MaximumWorkspaceBytes),
                    "The dependency workspace maximum must allow one Ledger page.");
            var registered = owner.CreateScratch(allocationUnit);
            registered.WorkArea = "dependencies";
            registered.Rows = "native cache and probe arena";
            scratch = registered;
        }
        catch
        {
            ownedLedger?.Dispose();
            throw;
        }
    }

    internal NativeDependencyCacheWorkspace(IComputeScratch scratch, ulong maximumWorkspaceBytes)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        if (scratch.Length < 40 || scratch.Length > maximumWorkspaceBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumWorkspaceBytes));
        this.scratch = scratch;
        this.maximumWorkspaceBytes = maximumWorkspaceBytes;
        allocationUnit = 8; // Native cache ABI alignment, not a capacity limit.
    }

    internal int StatusProbeCount { get { lock (gate) return unchecked((int)statusProbes); } }
    internal int ProviderProbeCount { get { lock (gate) return unchecked((int)providerProbes); } }
    internal int RootedProjectionCount { get { lock (gate) return roots.Count; } }

    internal ulong SourceId(object source) => sources.GetValue(source,
        _ => new SourceIdentity(checked((ulong)Interlocked.Increment(ref nextSourceIdentity)))).Id;

    internal (object? Value, NativeDependencyCacheNative.Result Token) Lookup(uint kind, string key, ulong source)
        => WithLease(lease =>
        {
            var result = Invoke(lease, 1, kind, key, source, default, 0, out var status);
            Check(status);
            return (result.Projection == 0 ? null : ReadRoot(result.Projection).Value, result);
        });

    internal void Commit(uint kind, string key, ulong source, NativeDependencyCacheNative.Result token,
        object? sourceRoot, object? value)
        => WithLease(lease =>
        {
            var handle = GCHandle.Alloc(new ProjectionRoot(sourceRoot, value));
            var root = unchecked((ulong)(nuint)GCHandle.ToIntPtr(handle));
            var registered = false;
            try
            {
                roots.Add(root);
                registered = true;
                var result = Invoke(lease, 2, kind, key, source, token, root, out var status);
                if (status == 1 && Grow(lease, result.Required))
                    result = Invoke(lease, 2, kind, key, source, token, root, out status);
                if (status == 1) return 0; // Bounded native admission refusal; caller still receives its result.
                Check(status);
                if (result.Released != 0) FreeRoot(result.Released);
                if (result.Projection == root) root = 0; // Native record now owns the projection root.
                return 0;
            }
            finally
            {
                if (root != 0)
                {
                    if (registered) FreeRoot(root);
                    else handle.Free();
                }
            }
        });

    internal void Invalidate() => WithLease(lease =>
    {
        _ = Invoke(lease, 3, 0, "", 0, default, 0, out var status);
        Check(status);
        FreeRoots();
        return 0;
    });

    internal T UseTemporary<T>(ulong requiredBytes, NativeDependencyCacheRegionAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return WithLease(lease =>
        {
            var state = Invoke(lease, 4, 0, "", 0, default, 0, out var status);
            Check(status);
            var required = checked(state.Required + requiredBytes);
            if (!Grow(lease, required))
                throw new OutOfMemoryException("The dependency probe exceeds MaximumWorkspaceBytes.");
            using var view = new ComputeScratchView(lease);
            var region = view.AlignedRegion(state.Required, requiredBytes, 8);
            return action(region, lease.Length - state.Required);
        });
    }

    // A typed capacity result requests exact demand. No partial projection is published;
    // release the view before growing this same local lease and retrying the read-only probe.
    internal T UseProbe<T>(ulong inputBytes, NativeDependencyCacheProbeAction<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return WithLease(lease =>
        {
            var state = Invoke(lease, 4, 0, "", 0, default, 0, out var status);
            Check(status);
            var demand = inputBytes;
            while (true)
            {
                if (!Grow(lease, checked(state.Required + demand)))
                    throw new OutOfMemoryException("The dependency probe exceeds MaximumWorkspaceBytes.");
                (T Value, ulong RequiredBytes) attempt;
                var available = lease.Length - state.Required;
                using (var view = new ComputeScratchView(lease))
                    attempt = action(view.AlignedRegion(state.Required, available, 8), available);
                if (attempt.RequiredBytes == 0) return attempt.Value;
                if (attempt.RequiredBytes <= available)
                    throw new InvalidOperationException("Native dependency probe capacity demand did not increase.");
                demand = attempt.RequiredBytes;
            }
        });
    }

    private T WithLease<T>(Func<ComputeScratchLease, T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var lease = scratch.Acquire();
            try
            {
                if (!initialized || lease.IsLoading || lease.AllocationNumber != allocationNumber)
                {
                    FreeRoots();
                    using var view = new ComputeScratchView(lease);
                    Check(NativeDependencyCacheNative.Initialize(view.AlignedRegion(0, lease.Length, 8),
                        checked((nuint)lease.Length), lease.AllocationNumber, statusProbes, providerProbes));
                    initialized = true;
                    allocationNumber = lease.AllocationNumber;
                }
                return action(lease);
            }
            finally { lease.Complete(); }
        }
    }

    private NativeDependencyCacheNative.Result Invoke(ComputeScratchLease lease, uint operation, uint kind,
        string key, ulong source, NativeDependencyCacheNative.Result token, ulong root, out uint status)
    {
        using var view = new ComputeScratchView(lease);
        status = NativeDependencyCacheNative.Execute(view.AlignedRegion(0, lease.Length, 8), lease.Length,
            operation, kind, key, source, token.Incarnation, token.Generation, root, out var result);
        if (status != 2)
        {
            statusProbes = result.StatusProbes;
            providerProbes = result.ProviderProbes;
        }
        return result;
    }

    private bool Grow(ComputeScratchLease lease, ulong required)
    {
        if (required > maximumWorkspaceBytes) return false;
        if (required <= lease.Length) return true;
        var remainder = required % allocationUnit;
        var growth = remainder == 0 ? required
            : Math.Min(maximumWorkspaceBytes, checked(required + allocationUnit - remainder));
        if (lease.IsLoading) lease.PublishAndRetain();
        var previous = lease.Length;
        lease.Grow(growth);
        allocationNumber = lease.AllocationNumber;
        if (scratch is ResourceManagerLedgerOwner.ComputeScratch registered)
            registered.DescribeGrowth("dependencies", previous.ToString(), growth.ToString(), previous);
        return true;
    }

    private static void Check(uint status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Native dependency cache operation failed ({status}).");
    }

    private static ProjectionRoot ReadRoot(ulong root)
        => (ProjectionRoot)(GCHandle.FromIntPtr(unchecked((IntPtr)(nuint)root)).Target
            ?? throw new InvalidOperationException("Native dependency projection has no managed root."));

    private void FreeRoot(ulong root)
    {
        if (roots.Remove(root)) GCHandle.FromIntPtr(unchecked((IntPtr)(nuint)root)).Free();
    }

    private void FreeRoots()
    {
        using var entries = roots.GetEnumerator();
        while (entries.MoveNext())
            GCHandle.FromIntPtr(unchecked((IntPtr)(nuint)entries.Current)).Free();
        roots.Clear();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            disposed = true;
            FreeRoots();
            try { scratch?.Dispose(); }
            finally { ownedLedger?.Dispose(); }
            closed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeDependencyCacheWorkspace()
    {
        try { Dispose(); }
        catch { /* Finalization must not escape into the runtime. */ }
    }

    private sealed record SourceIdentity(ulong Id);
    private sealed record ProjectionRoot(object? Source, object? Value);
}
