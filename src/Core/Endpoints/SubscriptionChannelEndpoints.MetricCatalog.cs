using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static async Task RunMetricCatalogSubscriptionAsync(
        string id, IMetricSnapshotPushSource source, HardwareMetricCatalogState catalog,
        TimeSpan interval, IServiceProvider services, FrontendSubscriptionChannelWriter writer,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var components = services.GetRequiredService<IComponentManager>();
        var topology = services.GetService<IDeviceTopologySnapshotProvider>();
        // The installed component set is a component manager fact: read it at subscription start and
        // on each component revision. Without a revision owner it is read at subscription start only.
        var componentRevisions = services.GetService<IManagementInventoryRevisionOwner>();
        Task discovery = Task.CompletedTask;
        Task componentChanges = Task.CompletedTask;
        try
        {
            var observedRevision = componentRevisions?.Current.ComponentRevision;
            await catalog.RefreshCompositionAsync(components, topology, lifetime.Token);
            if (componentRevisions is not null)
                componentChanges = ObserveComponentRevisionsAsync(componentRevisions, observedRevision!);
            discovery = ObserveDiscoveryAsync();
            await foreach (var committed in catalog.SubscribeChangesAsync(lifetime.Token))
                await writer.WriteAsync(id, new MetricCatalogWireSnapshot(committed.Revision,
                    committed.Fields, committed.Metrics), lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(discovery, componentChanges); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        // Each catalog probe only recommits the hardware part (current snapshot and topology ports).
        async Task ObserveDiscoveryAsync()
        {
            try
            {
                await foreach (var _ in source.SubscribeAsync($"subscription:{id}",
                    MetricSampleRequest.CatalogProbe, interval, lifetime.Token))
                    await catalog.RecommitCompositionAsync(topology, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }

        // A revision observed during our own read may be caused by that read; refreshing once more is
        // idempotent and never loses a change that landed in the same window.
        async Task ObserveComponentRevisionsAsync(IManagementInventoryRevisionOwner revisions, string seen)
        {
            try
            {
                await foreach (var snapshot in revisions.SubscribeAsync(lifetime.Token))
                {
                    if (string.Equals(snapshot.ComponentRevision, seen, StringComparison.Ordinal)) continue;
                    seen = snapshot.ComponentRevision;
                    await catalog.RefreshCompositionAsync(components, topology, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }
    }
}
