using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static CompiledFrontendSubscription CompilePerformanceOverlaySubscription(
        ParsedFrontendSubscription subscription, IServiceProvider services, TimeSpan interval)
    {
        if (!subscription.Query.TryGetValue("softwareId", out var softwareIds)
            || softwareIds.Count != 1 || string.IsNullOrWhiteSpace(softwareIds[0]))
            throw new ArgumentException("An overlay subscription requires a software ID.");
        var softwareId = softwareIds[0]!;
        var ids = subscription.Query.TryGetValue("ids", out var requested)
            ? requested.Select(static id => id ?? string.Empty).ToArray() : [];
        if (ids.Length is 0 or > 64 || ids.Any(static id => string.IsNullOrWhiteSpace(id)
                || id.Length > 128 || id != id.Trim())
            || ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ids.Length)
            throw new ArgumentException("An overlay subscription requires explicit unique metric IDs.");
        var processMask = ids.Contains(PerformanceOverlayMetricIds.Cpu)
            ? SchedulingProcessMetricMask.CpuUsage : SchedulingProcessMetricMask.None;
        var needsFrames = ids.Any(static id => id is PerformanceOverlayMetricIds.Fps
            or PerformanceOverlayMetricIds.FrameTime or PerformanceOverlayMetricIds.OnePercentLow
            or PerformanceOverlayMetricIds.PointOnePercentLow);
        var needsGpu = ids.Contains(PerformanceOverlayMetricIds.Gpu);
        var needsVram = ids.Contains(PerformanceOverlayMetricIds.Vram);
        var systemIds = ids.Where(static id => !id.StartsWith("target.", StringComparison.Ordinal)).ToArray();
        var store = services.GetRequiredService<IPerformanceOverlaySettingsStore>();
        var processSource = services.GetRequiredService<ISchedulingProcessFactObservationSource>();
        var resourceSource = services.GetRequiredService<IResourceBreakdownObservationSource>();
        var metricSource = services.GetRequiredService<IMetricSnapshotObservationSource>();
        var frameSource = services.GetRequiredService<IFrameTimingObservationSource>();
        return subscription.Compile(async (writer, cancellationToken) =>
        {
            IDisposable? identityLease = null, processLease = null, resourceLease = null, metricLease = null, frameLease = null;
            string? resourceKey = null, metricKey = null;
            try
            {
                using var timer = new PeriodicTimer(interval);
                do
                {
                    var setting = await store.GetSoftwareAsync(softwareId, cancellationToken);
                    PerformanceOverlaySettings[] enabled = setting.Enabled ? [setting] : [];
                    if (setting.Enabled && identityLease is null)
                        identityLease = resourceSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:identity", new ResourceBreakdownSampleRequest(
                                [], new Dictionary<string, string>(), ProcessSampleDetailLevel.SmartSchedulingLite)
                            { PublicationDatasetIds = [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution] }, interval);
                    else if (!setting.Enabled) { identityLease?.Dispose(); identityLease = null; }
                    var inventory = new SchedulingGpuInventorySnapshot(
                        SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []);
                    var processes = identityLease is null ? null : processSource.ReadLatest(new(
                        SchedulingProcessMetricMask.None, inventory));
                    var running = PerformanceOverlayProjection.SelectTargets(enabled, processes);
                    var hasTargets = running.Count > 0;
                    if (hasTargets && processMask != SchedulingProcessMetricMask.None && processLease is null)
                        processLease = processSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:processes", processMask, interval);
                    else if (!hasTargets) { processLease?.Dispose(); processLease = null; }
                    if (processLease is not null && processes is not null)
                        processes = processes with { Processes = processSource.ReadLatest(new(processMask, inventory))?.Processes ?? [] };
                    var metricRequest = MetricSampleRequest.ForIds(hasTargets ? systemIds : []);
                    // Device inventory is a dependency of per-device process resource facts.
                    if (hasTargets && (needsGpu || needsVram)) metricRequest = metricRequest.WithCatalog();
                    if (metricKey != metricRequest.CacheKey)
                    {
                        metricLease?.Dispose();
                        metricLease = metricRequest.IsEmpty ? null : metricSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:system", metricRequest, interval);
                        metricKey = metricRequest.CacheKey;
                    }
                    var hardware = metricLease is null ? null : metricSource.ReadLatest(metricRequest);
                    var resourceIds = new List<string>();
                    if (hasTargets && ids.Contains(PerformanceOverlayMetricIds.Memory))
                        resourceIds.Add(ResourceBreakdownMetricIds.MemoryUsage);
                    if (hasTargets && (needsGpu || needsVram))
                        foreach (var gpu in hardware?.Gpus ?? [])
                        {
                            if (needsGpu) resourceIds.Add($"gpu.{gpu.Index}.usage");
                            if (needsVram) resourceIds.Add($"gpu.{gpu.Index}.vram");
                        }
                    var resourceRequest = new ResourceBreakdownSampleRequest(resourceIds,
                        new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic);
                    if (resourceKey != resourceRequest.CacheKey)
                    {
                        resourceLease?.Dispose();
                        resourceLease = resourceIds.Count == 0 ? null : resourceSource.AcquireSubscription(
                            $"performance-overlay:{subscription.Id}:resource", resourceRequest, interval);
                        resourceKey = resourceRequest.CacheKey;
                    }
                    var breakdown = resourceLease is null ? null : resourceSource.ReadLatest(resourceRequest);
                    if (hasTargets && needsFrames && frameLease is null) frameLease = frameSource.AcquireSubscription();
                    else if (!hasTargets || !needsFrames) { frameLease?.Dispose(); frameLease = null; }
                    var frames = frameLease is null ? null : frameSource.Read(TimeSpan.FromSeconds(1));
                    await writer.WriteAsync(subscription.Id, PerformanceOverlayProjection.Create(
                        enabled, ids, processes, breakdown, hardware, frames, DateTimeOffset.UtcNow), cancellationToken);
                } while (await timer.WaitForNextTickAsync(cancellationToken));
            }
            finally
            {
                frameLease?.Dispose(); resourceLease?.Dispose(); metricLease?.Dispose(); processLease?.Dispose(); identityLease?.Dispose();
            }
        });
    }
}
