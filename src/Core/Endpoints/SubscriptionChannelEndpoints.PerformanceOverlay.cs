using ResourceManager.App.Application.FrameTiming;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static CompiledFrontendSubscription CompilePerformanceOverlaySubscription(
        ParsedFrontendSubscription subscription,
        IServiceProvider services,
        TimeSpan interval)
    {
        if (!subscription.Query.TryGetValue("intervalMs", out var rawInterval)
            || !int.TryParse(rawInterval.FirstOrDefault(), out var milliseconds))
            interval = TimeSpan.FromMilliseconds(500);
        var store = services.GetRequiredService<IPerformanceOverlaySettingsStore>();
        var processSource = services.GetRequiredService<ISchedulingProcessFactObservationSource>();
        var resourceSource = services.GetRequiredService<IResourceBreakdownObservationSource>();
        var metricSource = services.GetRequiredService<IMetricSnapshotObservationSource>();
        var frameSource = services.GetRequiredService<IFrameTimingObservationSource>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            IDisposable? processLease = null;
            IDisposable? resourceLease = null;
            IDisposable? metricLease = null;
            IDisposable? frameLease = null;
            string? resourceKey = null;
            string? metricKey = null;
            try
            {
                using var timer = new PeriodicTimer(interval);
                do
                {
                    var document = await store.GetAsync(cancellationToken);
                    var enabled = document.Software.Where(static item => item.Enabled).ToArray();
                    if (enabled.Length > 0 && processLease is null)
                        processLease = processSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:processes",
                            SchedulingProcessMetricMask.CpuUsage,
                            interval);
                    else if (enabled.Length == 0)
                    {
                        processLease?.Dispose();
                        processLease = null;
                    }

                    var systemIds = enabled.SelectMany(static item => item.Metrics)
                        .Where(static id => !id.StartsWith("target.", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var needsGpu = enabled.Any(static item => item.Metrics.Any(static id =>
                        id is PerformanceOverlayMetricIds.Gpu or PerformanceOverlayMetricIds.Vram));
                    var metricRequest = needsGpu
                        ? MetricSampleRequest.ForIdsAndAllGpuCoreMetrics(systemIds)
                        : MetricSampleRequest.ForIds(systemIds);
                    if (metricKey != metricRequest.CacheKey)
                    {
                        metricLease?.Dispose();
                        metricLease = metricRequest.IsEmpty ? null : metricSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:system",
                            metricRequest, interval);
                        metricKey = metricRequest.CacheKey;
                    }
                    var hardware = metricLease is null ? null : metricSource.ReadLatest(metricRequest);

                    var resourceIds = new List<string>();
                    if (enabled.Any(static item => item.Metrics.Contains(PerformanceOverlayMetricIds.Memory)))
                        resourceIds.Add(ResourceBreakdownMetricIds.MemoryUsage);
                    if (needsGpu)
                    {
                        var indexes = hardware?.Gpus.Select(static gpu => gpu.Index).ToArray() ?? [];
                        foreach (var index in indexes)
                        {
                            resourceIds.Add($"gpu.{index}.usage");
                            resourceIds.Add($"gpu.{index}.vram");
                        }
                    }
                    var resourceRequest = new ResourceBreakdownSampleRequest(
                        resourceIds,
                        new Dictionary<string, string>(),
                        ProcessSampleDetailLevel.ResourceTableBasic);
                    if (resourceKey != resourceRequest.CacheKey)
                    {
                        resourceLease?.Dispose();
                        resourceLease = resourceIds.Count == 0 ? null : resourceSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:resource",
                            resourceRequest, interval);
                        resourceKey = resourceRequest.CacheKey;
                    }
                    var breakdown = resourceLease is null ? null : resourceSource.ReadLatest(resourceRequest);

                    var processes = processLease is null ? null : processSource.ReadLatest(
                        new SchedulingProcessFactRequest(
                            SchedulingProcessMetricMask.CpuUsage,
                            null,
                            hardware?.GpuInventory ?? new SchedulingGpuInventorySnapshot(
                                SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
                    var running = PerformanceOverlayProjection.SelectTargets(enabled, processes);
                    if (running.Count > 0 && frameLease is null)
                        frameLease = frameSource.AcquireSubscription();
                    else if (running.Count == 0)
                    {
                        frameLease?.Dispose();
                        frameLease = null;
                    }
                    var frames = frameLease is null ? null : frameSource.Read(TimeSpan.FromSeconds(1));
                    await writer.WriteAsync(subscription.Id, PerformanceOverlayProjection.Create(
                        enabled, processes, breakdown, hardware, frames, DateTimeOffset.UtcNow),
                        cancellationToken);
                }
                while (await timer.WaitForNextTickAsync(cancellationToken));
            }
            finally
            {
                frameLease?.Dispose();
                resourceLease?.Dispose();
                metricLease?.Dispose();
                processLease?.Dispose();
            }
        });
    }
}
