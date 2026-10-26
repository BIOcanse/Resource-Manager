using System.Threading.Channels;
using ResourceManager.App.Application.Components;
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
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        void Changed() => changes.Writer.TryWrite(true);
        catalog.Changed += Changed;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The existing probe keeps discovery active; metadata publications also wake this
        // channel directly, without requiring any ordinary metric-value subscription.
        var discovery = ObserveDiscoveryAsync();
        Changed();
        try
        {
            while (await changes.Reader.WaitToReadAsync(lifetime.Token))
            {
                while (changes.Reader.TryRead(out _)) { }
                var committed = catalog.ReadCommitted();
                if (committed.Snapshot is not { } snapshot) continue;
                var installed = await InstalledComponentIdsAsync(
                    services.GetRequiredService<IComponentManager>(), lifetime.Token);
                await writer.WriteAsync(id, new MetricCatalogWireSnapshot(committed.Revision,
                    ReadMetricFieldCatalog(snapshot, services),
                    MetricCatalog.WithInstalledComponents(MetricCatalog.FromSnapshot(snapshot), installed)), lifetime.Token);
            }
        }
        finally
        {
            catalog.Changed -= Changed;
            await lifetime.CancelAsync();
            try { await discovery; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        async Task ObserveDiscoveryAsync()
        {
            try
            {
                await foreach (var _ in source.SubscribeAsync($"frontend-channel:{id}",
                    MetricSampleRequest.CatalogProbe, interval, lifetime.Token)) Changed();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception) { changes.Writer.TryComplete(exception); }
        }
    }
}
