using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.DeviceTopology;
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
        Task discovery = Task.CompletedTask;
        try
        {
            await RefreshCompositionAsync();
            discovery = ObserveDiscoveryAsync();
            await foreach (var committed in catalog.SubscribeChangesAsync(lifetime.Token))
                await writer.WriteAsync(id, new MetricCatalogWireSnapshot(committed.Revision,
                    committed.Fields, committed.Metrics), lifetime.Token);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await discovery; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        Task RefreshCompositionAsync() => catalog.RefreshCompositionAsync(
            services.GetRequiredService<IComponentManager>(),
            services.GetService<IDeviceTopologySnapshotProvider>(), lifetime.Token);

        async Task ObserveDiscoveryAsync()
        {
            try
            {
                await foreach (var _ in source.SubscribeAsync($"subscription:{id}",
                    MetricSampleRequest.CatalogProbe, interval, lifetime.Token))
                    await RefreshCompositionAsync();
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