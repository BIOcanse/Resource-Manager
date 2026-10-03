using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Overlay;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Application.Overlay;

public sealed record PerformanceOverlayMetricItem(
    string MetricId, double? Value, string Unit, string State, string? Reason,
    IReadOnlyDictionary<string, string> Labels);

public sealed record PerformanceOverlayFrameStream(
    string Source, ulong SwapChain, int PresentCount,
    bool Selected, DateTimeOffset LastPresentAt, FrameIntervalStatistics Statistics);

public sealed record PerformanceOverlayTarget(
    string SoftwareId, string DisplayName, int ProcessId, ulong ProcessStartKey,
    PerformanceOverlaySettings Settings,
    IReadOnlyList<PerformanceOverlayMetricItem> Metrics,
    IReadOnlyList<PerformanceOverlayFrameStream> FrameStreams);

public sealed record PerformanceOverlaySnapshot(
    int Version, DateTimeOffset CapturedAt, IReadOnlyList<PerformanceOverlayTarget> Targets);

public static class PerformanceOverlayProjection
{
    public static IReadOnlyList<SchedulingProcessFact> SelectTargets(
        IReadOnlyList<PerformanceOverlaySettings> settings,
        SchedulingProcessFactSnapshot? processes)
    {
        if (processes is null || processes.InventoryStatus != SamplingObservationStatus.Current)
        {
            return [];
        }

        var enabled = settings.Where(static item => item.Enabled)
            .Select(static item => item.SoftwareId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attributedProcesses = processes.HasSeparateFoundationPayloads
            ? processes.AttributionProcesses : processes.Processes;
        return attributedProcesses
            .Where(process => process.ProcessId > 0 && process.ProcessStartKey > 0
                && enabled.Contains(process.SoftwareId))
            .GroupBy(static process => (process.ProcessId, process.ProcessStartKey))
            .Select(static group => group.First())
            .OrderBy(static process => process.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static process => process.ProcessId)
            .ToArray();
    }

    public static PerformanceOverlaySnapshot Create(
        IReadOnlyList<PerformanceOverlaySettings> settings,
        SchedulingProcessFactSnapshot? processes,
        ResourceBreakdownSnapshot? breakdown,
        HardwareMetricSnapshot? hardware,
        FrameTimingSnapshot? frames,
        DateTimeOffset capturedAt)
    {
        var settingsById = settings.Where(static item => item.Enabled)
            .ToDictionary(static item => item.SoftwareId, StringComparer.OrdinalIgnoreCase);
        var systemDefinitions = hardware is null
            ? new Dictionary<string, MetricDefinition>(StringComparer.OrdinalIgnoreCase)
            : MetricCatalog.ForPerformanceOverlay(hardware).ToDictionary(
                static item => item.Id, StringComparer.OrdinalIgnoreCase);
        var targets = SelectTargets(settings, processes).Select(process =>
        {
            var selectedSettings = settingsById[process.SoftwareId];
            var metricProcess = processes?.Processes.FirstOrDefault(item =>
                item.ProcessId == process.ProcessId
                && item.ProcessStartKey == process.ProcessStartKey);
            var frameProcess = frames?.Processes.FirstOrDefault(item =>
                item.ProcessId == process.ProcessId
                && item.ProcessStartKey > 0
                && checked((ulong)item.ProcessStartKey) == process.ProcessStartKey);
            var streams = (frameProcess?.Streams ?? [])
                .OrderByDescending(static item => item.PresentCount)
                .ThenBy(static item => item.Source)
                .ThenBy(static item => item.SwapChain)
                .ToArray();
            var selectedStream = streams.FirstOrDefault();
            var streamRows = streams.Select(item => new PerformanceOverlayFrameStream(
                item.Source switch
                {
                    FramePresentSource.Dxgi => "dxgi",
                    FramePresentSource.Direct3D9 => "direct3D9",
                    _ => "graphicsKernel"
                },
                item.SwapChain, item.PresentCount,
                ReferenceEquals(item, selectedStream), item.LastPresentAt, item.Statistics)).ToArray();
            var metrics = selectedSettings.Metrics.Select(metricId => ProjectMetric(
                metricId, process, metricProcess, breakdown, hardware, frames, selectedStream,
                systemDefinitions)).ToArray();
            return new PerformanceOverlayTarget(
                process.SoftwareId,
                string.IsNullOrWhiteSpace(process.SoftwareName)
                    ? process.ProcessName : process.SoftwareName,
                process.ProcessId,
                process.ProcessStartKey,
                selectedSettings,
                metrics,
                streamRows);
        }).ToArray();
        return new PerformanceOverlaySnapshot(1, capturedAt, targets);
    }

    private static PerformanceOverlayMetricItem ProjectMetric(
        string metricId,
        SchedulingProcessFact process,
        SchedulingProcessFact? metricProcess,
        ResourceBreakdownSnapshot? breakdown,
        HardwareMetricSnapshot? hardware,
        FrameTimingSnapshot? frames,
        FrameTimingStream? stream,
        IReadOnlyDictionary<string, MetricDefinition> systemDefinitions)
    {
        if (metricId is PerformanceOverlayMetricIds.Fps
            or PerformanceOverlayMetricIds.FrameTime
            or PerformanceOverlayMetricIds.OnePercentLow
            or PerformanceOverlayMetricIds.PointOnePercentLow)
        {
            var unit = metricId == PerformanceOverlayMetricIds.FrameTime ? "ms" : "FPS";
            if (frames is { Complete: false }) return Missing(metricId, unit, "frame-events-lost");
            if (stream is null) return Missing(metricId, unit, "no-frame-data");
            var statistics = stream.Statistics;
            var value = metricId switch
            {
                PerformanceOverlayMetricIds.Fps => statistics.AverageFps,
                PerformanceOverlayMetricIds.FrameTime => statistics.DurationMs / statistics.IntervalCount,
                PerformanceOverlayMetricIds.OnePercentLow => statistics.OnePercentLowFps,
                _ => statistics.PointOnePercentLowFps
            };
            return Current(metricId, value, unit);
        }

        if (metricId == PerformanceOverlayMetricIds.Cpu)
        {
            return metricProcess?.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage) == true
                ? Current(metricId, metricProcess.CpuUsagePercent, "%")
                : Missing(metricId, "%", "process-cpu-unavailable");
        }

        if (metricId is PerformanceOverlayMetricIds.Gpu
            or PerformanceOverlayMetricIds.Memory
            or PerformanceOverlayMetricIds.Vram)
        {
            var sourceId = metricId == PerformanceOverlayMetricIds.Memory
                ? "memory.usage"
                : metricId == PerformanceOverlayMetricIds.Gpu ? "usage" : "vram";
            var bars = breakdown?.Bars.Where(bar => metricId == PerformanceOverlayMetricIds.Memory
                ? bar.MetricId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)
                : bar.MetricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)
                  && bar.MetricId.EndsWith($".{sourceId}", StringComparison.OrdinalIgnoreCase))
                ?? [];
            var values = bars.Where(static bar => bar.ObservationStatus == SamplingObservationStatus.Current)
                .SelectMany(static bar => bar.Software)
                .Where(software => software.SoftwareId.Equals(process.SoftwareId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(static software => software.Processes)
                .Where(item => item.ProcessId == process.ProcessId && item.ProcessStartKey is > 0
                    && checked((ulong)item.ProcessStartKey.Value) == process.ProcessStartKey)
                .Select(static item => item.Value)
                .ToArray();
            var unit = metricId == PerformanceOverlayMetricIds.Gpu ? "%" : MetricUnits.Bytes;
            return values.Length > 0
                ? Current(metricId, metricId == PerformanceOverlayMetricIds.Gpu
                    ? Math.Min(100, values.Sum()) : values.Sum(), unit)
                : Missing(metricId, unit, "process-metric-unavailable");
        }

        if (!systemDefinitions.TryGetValue(metricId, out var definition))
            return Missing(metricId, string.Empty, "unknown-metric");
        if (!definition.Selectable)
            return Missing(metricId, definition.Unit, "metric-unavailable");
        var item = hardware?.Items.GetValueOrDefault(metricId);
        return item?.NumericValue is { } number && double.IsFinite(number)
            ? Current(metricId, number, definition.Unit)
            : Missing(metricId, definition.Unit, "metric-unavailable");
    }

    private static PerformanceOverlayMetricItem Current(string id, double value, string unit) =>
        double.IsFinite(value)
            ? new(id, value, unit, "current", null, PerformanceOverlayMetricLabels.ForMetric(id))
            : Missing(id, unit, "metric-unavailable");

    private static PerformanceOverlayMetricItem Missing(string id, string unit, string reason) =>
        new(id, null, unit, "unavailable", reason, PerformanceOverlayMetricLabels.ForMetric(id));
}
