using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

public sealed partial class RuntimeProcessAttributionCatalogProvider(
    ISoftwareRegistryView softwareRegistry,
    IAdapterSoftwareRegistry adapterRegistry,
    IControlledSoftwareRegistry controlledRegistry,
    IRuntimePackageIdentityResolver packageIdentityResolver,
    IRuntimeServiceIdentityResolver serviceIdentityResolver,
    IRuntimeRootIdentityResolver rootIdentityResolver,
    IRuntimeSystemProcessClassifier systemProcessClassifier,
    ISoftwareIdentityCatalog softwareIdentityCatalog,
    HostManagerSoftwareIdentityOwner softwareIdentityOwner,
    IWindowsGameListSource windowsGameListSource,
    IManagementInventoryRevisionOwner? inventoryRevisionOwner = null, TimeProvider? clock = null,
    Microsoft.Extensions.Options.IOptions<RuntimeProcessAttributionCatalogAvailabilityOptions>? availabilityOptions = null) : IRuntimeProcessAttributionCatalogProvider, IDisposable
{
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
        var adapted = await adapterRegistry.GetAllAsync(cancellationToken);
        var controlled = controlledRegistry.GetAll();
        var adaptedVersion = RuntimeProcessAttributionCatalogVersionFactory.CreateAdaptedVersion(adapted);
        var controlledVersion = RuntimeProcessAttributionCatalogVersionFactory.CreateControlledVersion(controlled);
        var software = await GetSoftwareAsync(scope, adaptedVersion, controlledVersion, cancellationToken);
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
                software.WindowsGamePaths,
                state.LastRevision);
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
                    holder = new CatalogHolder(this, scope, next);
                    result = holder.Acquire();
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
                state.Current = holder;
                state.LastRevision = next.Revision;
            }
        }
        return result;
    }

    public Task InvalidateSoftwareSnapshotAsync() => InvalidateSoftwareSnapshotAsync(CancellationToken.None);

    private async Task InvalidateSoftwareSnapshotAsync(CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var state in scopes.Values)
                {
                    state.CachedSoftware = null;
                    state.CachedSoftwareRevision = null;
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
        RuntimeProcessAttributionCatalogAvailability? availabilityToDispose;
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            availabilityToDispose = availability;
            scopes.Clear();
        }
        availabilityToDispose?.Dispose();
    }

    [ResourceManager.ReadWrite.RuntimeReadCache]
    private async Task<SoftwareSnapshot> GetSoftwareAsync(string scope, RuntimeProcessAttributionSourceVersion adapted,
        RuntimeProcessAttributionSourceVersion controlled, CancellationToken cancellationToken)
    {
        var revision = inventoryRevisionOwner?.Current.SoftwareRevision;
        if (TryGetCachedSoftware(scope, revision, adapted, controlled, out var software))
        {
            return software;
        }

        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            revision = inventoryRevisionOwner?.Current.SoftwareRevision;
            if (TryGetCachedSoftware(scope, revision, adapted, controlled, out software))
            {
                return software;
            }

            software = await ReadSoftwareSnapshotAsync(scope, adapted, controlled, cancellationToken);
            lock (lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var state = GetScope(scope);
                state.CachedSoftware = software;
                state.CachedSoftwareRevision = revision;
            }
            return software;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "Management software revision published or InvalidateSoftwareSnapshotAsync")]
    private async Task<SoftwareSnapshot> ReadSoftwareSnapshotAsync(string scope, RuntimeProcessAttributionSourceVersion adapted,
        RuntimeProcessAttributionSourceVersion controlled, CancellationToken cancellationToken)
    {
        var bundledVersion = softwareIdentityCatalog.Version;
        var records = await softwareRegistry.GetSoftwareAsync(scope, cancellationToken);
        return new(records, WindowsGameListPaths.Normalize(windowsGameListSource.ReadExecutablePaths()),
            adapted, controlled, bundledVersion);
    }

    private bool TryGetCachedSoftware(
        string scope,
        string? revision, RuntimeProcessAttributionSourceVersion adapted,
        RuntimeProcessAttributionSourceVersion controlled, out SoftwareSnapshot software)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (scopes.TryGetValue(scope, out var state) && state.CachedSoftware is not null
                && StringComparer.Ordinal.Equals(state.CachedSoftwareRevision, revision)
                && state.CachedSoftware.Adapted == adapted && state.CachedSoftware.Controlled == controlled
                && StringComparer.Ordinal.Equals(state.CachedSoftware.BundledVersion, softwareIdentityCatalog.Version))
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
        IReadOnlySet<string> WindowsGamePaths,
        RuntimeProcessAttributionSourceVersion Adapted,
        RuntimeProcessAttributionSourceVersion Controlled,
        string BundledVersion);

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
        internal string? CachedSoftwareRevision;
        internal RuntimeProcessAttributionCatalogRevision? LastRevision;
    }

    // Only readers own the native pipeline. Publication and the last-reader detach
    // share one lock; native destruction happens outside that lock.
    private sealed class CatalogHolder(RuntimeProcessAttributionCatalogProvider owner, string scope,
        RuntimeProcessAttributionCatalog catalog)
    {
        private int references;
        internal RuntimeProcessAttributionCatalog Catalog { get; } = catalog;

        internal RuntimeProcessAttributionCatalogLease Acquire()
        {
            var lease = new RuntimeProcessAttributionCatalogLease(Catalog, Release);
            references++;
            return lease;
        }

        private void Release()
        {
            lock (owner.lifecycleGate)
            {
                if (--references != 0) return;
                if (owner.scopes.TryGetValue(scope, out var state) && ReferenceEquals(state.Current, this))
                    state.Current = null;
            }
            Catalog.Dispose();
        }
    }
}
