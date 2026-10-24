using System.Threading.Channels;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static async Task RunMetricFieldSubscriptionAsync(
        string id,
        IMetricSnapshotPushSource source,
        DashboardMonitoringCatalogState catalog,
        string? device,
        IReadOnlySet<string> fields,
        TimeSpan interval,
        IServiceProvider services,
        FrontendSubscriptionChannelWriter writer,
        CancellationToken cancellationToken)
    {
        // Keep the symbolic selection for this channel's lifetime. A directory publication
        // retires the concrete sampler lease, including when no devices currently match.
        var names = fields.Select(MetricFieldRegistry.NormalizePublicName).ToHashSet(StringComparer.Ordinal);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        void Changed() => changes.Writer.TryWrite(true);
        catalog.Changed += Changed;
        Changed();
        try
        {
            while (await changes.Reader.WaitToReadAsync(cancellationToken))
            {
                while (changes.Reader.TryRead(out _)) { }
                // Compilation already validated the selection against a published catalog.
                var request = MetricDeviceFields.SelectBound(catalog.Current!, device, names);
                if (request.IsEmpty)
                {
                    await writer.WriteAsync(id, Array.Empty<DeviceMetricFieldSnapshot>(), cancellationToken);
                    continue;
                }

                using var bindingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await using var snapshots = source.SubscribeAsync($"frontend-channel:{id}", request, interval,
                    bindingCancellation.Token).GetAsyncEnumerator(bindingCancellation.Token);
                var changed = changes.Reader.WaitToReadAsync(bindingCancellation.Token).AsTask();
                try
                {
                    while (true)
                    {
                        var next = snapshots.MoveNextAsync().AsTask();
                        await Task.WhenAny(next, changed);
                        if (changed.IsCompletedSuccessfully)
                        {
                            await bindingCancellation.CancelAsync();
                            try { await next; }
                            catch (OperationCanceledException) when (bindingCancellation.IsCancellationRequested) { }
                            break;
                        }
                        if (!await next) break;
                        await writer.WriteAsync(id, ReadMetricFields(snapshots.Current, device, fields, services), cancellationToken);
                    }
                }
                finally
                {
                    await bindingCancellation.CancelAsync();
                    try { await changed; }
                    catch (OperationCanceledException) when (bindingCancellation.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            catalog.Changed -= Changed;
        }
    }
}
