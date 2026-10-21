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
    HostManagerSoftwareIdentityOwner softwareIdentityOwner) : IRuntimeProcessAttributionCatalogProvider, IDisposable
{
    private static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object lifecycleGate = new();
    private CatalogHolder? current;
    private IReadOnlyList<SoftwareRecord>? cachedSoftware;
    private DateTimeOffset cachedSoftwareAt;
    private long softwareSnapshotGeneration;
    private bool disposed;

    public long SoftwareSnapshotGeneration => Volatile.Read(ref softwareSnapshotGeneration);

    public async Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(CancellationToken cancellationToken)
    {
        var software = await GetSoftwareAsync(cancellationToken);
        var adapted = await adapterRegistry.GetAllAsync(cancellationToken);
        var controlled = controlledRegistry.GetAll();
        CatalogHolder? retired = null;
        RuntimeProcessAttributionCatalogLease result;
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = current?.Catalog;
            var next = RuntimeProcessAttributionCatalog.CreateOrReuse(
                previous,
                software,
                adapted,
                controlled,
                packageIdentityResolver,
                serviceIdentityResolver,
                rootIdentityResolver,
                systemProcessClassifier,
                softwareIdentityCatalog,
                softwareIdentityOwner);
            if (cancellationToken.IsCancellationRequested)
            {
                if (!ReferenceEquals(previous, next)) next.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (ReferenceEquals(previous, next))
                result = current!.Acquire();
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
                retired = current;
                current = holder;
            }
        }
        try
        {
            retired?.Retire();
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
                cachedSoftware = null;
                cachedSoftwareAt = default;
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
        CatalogHolder? retired;
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            retired = current;
            current = null;
        }
        retired?.Retire();
    }

    private async Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (TryGetCachedSoftware(now, out var software))
        {
            return software;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (TryGetCachedSoftware(now, out software))
            {
                return software;
            }

            software = await softwareRegistry.GetSoftwareAsync(cancellationToken);
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cachedSoftware = software;
                cachedSoftwareAt = DateTimeOffset.UtcNow;
            }
            return software;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private bool TryGetCachedSoftware(
        DateTimeOffset now,
        out IReadOnlyList<SoftwareRecord> software)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cachedSoftware is not null && now - cachedSoftwareAt < SnapshotLifetime)
            {
                software = cachedSoftware;
                return true;
            }
        }

        software = [];
        return false;
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
