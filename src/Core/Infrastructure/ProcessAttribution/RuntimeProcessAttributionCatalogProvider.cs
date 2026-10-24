using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

public sealed class RuntimeProcessAttributionCatalogProvider(
    ISoftwareRegistryView softwareRegistry,
    IAdapterSoftwareRegistry adapterRegistry,
    IControlledSoftwareRegistry controlledRegistry,
    IRuntimePackageIdentityResolver packageIdentityResolver,
    IRuntimeServiceIdentityResolver serviceIdentityResolver,
    IRuntimeRootIdentityResolver rootIdentityResolver,
    IRuntimeSystemProcessClassifier systemProcessClassifier,
    ISoftwareIdentityCatalog softwareIdentityCatalog,
    HostManagerSoftwareIdentityOwner softwareIdentityOwner,
    IWindowsGameListSource windowsGameListSource) : IRuntimeProcessAttributionCatalogProvider, IDisposable
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object lifecycleGate = new();
    private readonly Dictionary<string, CatalogScopeState> scopes = new(StringComparer.Ordinal);
    private long softwareSnapshotGeneration;
    private bool disposed;

    public long SoftwareSnapshotGeneration => Volatile.Read(ref softwareSnapshotGeneration);

    public Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(CancellationToken cancellationToken)
        => AcquireCatalogAsync(null, cancellationToken);

    public async Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(string? windowsSid, CancellationToken cancellationToken)
    {
        var scope = RuntimeProcessAttributionCapture.CatalogScope(windowsSid);
        var software = await GetSoftwareAsync(scope, cancellationToken);
        var adapted = await adapterRegistry.GetAllAsync(cancellationToken);
        var controlled = controlledRegistry.GetAll();
        var retired = new List<CatalogHolder>();
        RuntimeProcessAttributionCatalogLease result;
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var state = GetScope(scope);
            var previous = state.Current?.Catalog;
            var next = RuntimeProcessAttributionCatalog.CreateOrReuse(
                previous,
                software.Software,
                adapted,
                controlled,
                packageIdentityResolver,
                serviceIdentityResolver,
                rootIdentityResolver,
                systemProcessClassifier,
                softwareIdentityCatalog,
                softwareIdentityOwner,
                software.WindowsGamePaths);
            if (cancellationToken.IsCancellationRequested)
            {
                if (!ReferenceEquals(previous, next)) next.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (ReferenceEquals(previous, next))
                result = state.Current!.Acquire();
            else
            {
                CatalogHolder holder;
                try
                {
                    holder = new CatalogHolder(next);
                    result = holder.Acquire();
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
                if (state.Current is { } old) retired.Add(old);
                state.Current = holder;
            }
            state.LastAcquiredAt = DateTimeOffset.UtcNow;
            // Idle owner catalogs do not accumulate for the lifetime of the service.
            foreach (var idle in scopes.Where(pair => pair.Key != scope
                && state.LastAcquiredAt - pair.Value.LastAcquiredAt >= SnapshotLifetime).ToArray())
            {
                if (idle.Value.Current is { } holder) retired.Add(holder);
                scopes.Remove(idle.Key);
            }
        }
        try
        {
            foreach (var holder in retired) holder.Retire();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public async Task InvalidateSoftwareSnapshotAsync()
    {
        await refreshGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                foreach (var state in scopes.Values)
                {
                    state.CachedSoftware = null;
                    state.CachedSoftwareAt = default;
                }
                softwareSnapshotGeneration = checked(softwareSnapshotGeneration + 1);
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void Dispose()
    {
        CatalogHolder[] retired;
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            retired = scopes.Values.Where(state => state.Current is not null).Select(state => state.Current!).ToArray();
            scopes.Clear();
        }
        foreach (var holder in retired) holder.Retire();
    }

    private async Task<SoftwareSnapshot> GetSoftwareAsync(string scope, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (TryGetCachedSoftware(scope, now, out var software))
        {
            return software;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (TryGetCachedSoftware(scope, now, out software))
            {
                return software;
            }

            var records = await softwareRegistry.GetSoftwareAsync(scope, cancellationToken);
            software = new SoftwareSnapshot(records,
                WindowsGameListPaths.Normalize(windowsGameListSource.ReadExecutablePaths()));
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var state = GetScope(scope);
                state.CachedSoftware = software;
                state.CachedSoftwareAt = DateTimeOffset.UtcNow;
            }
            return software;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private bool TryGetCachedSoftware(
        string scope,
        DateTimeOffset now,
        out SoftwareSnapshot software)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (scopes.TryGetValue(scope, out var state) && state.CachedSoftware is not null
                && now - state.CachedSoftwareAt < SnapshotLifetime)
            {
                software = state.CachedSoftware;
                return true;
            }
        }

        software = null!;
        return false;
    }

    private sealed record SoftwareSnapshot(
        IReadOnlyList<SoftwareRecord> Software,
        IReadOnlySet<string> WindowsGamePaths);

    // Accessed only under lifecycleGate.
    private CatalogScopeState GetScope(string scope)
    {
        if (!scopes.TryGetValue(scope, out var state)) scopes.Add(scope, state = new());
        return state;
    }

    private sealed class CatalogScopeState
    {
        internal CatalogHolder? Current;
        internal SoftwareSnapshot? CachedSoftware;
        internal DateTimeOffset CachedSoftwareAt;
        internal DateTimeOffset LastAcquiredAt;
    }

    // Acquire is only called under lifecycleGate while this is the current holder.
    // Releases never acquire lifecycleGate; Native disposal occurs after retirement
    // and the last reader, outside the provider's publication lock.
    private sealed class CatalogHolder(RuntimeProcessAttributionCatalog catalog)
    {
        private int references = 1;
        private int retired;
        internal RuntimeProcessAttributionCatalog Catalog { get; } = catalog;

        internal RuntimeProcessAttributionCatalogLease Acquire()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref retired) != 0, this);
            var lease = new RuntimeProcessAttributionCatalogLease(Catalog, Release);
            Interlocked.Increment(ref references);
            return lease;
        }

        internal void Retire()
        {
            if (Interlocked.Exchange(ref retired, 1) == 0) Release();
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref references) == 0) Catalog.Dispose();
        }
    }
}
