using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>The live set of recordable items: application frames, the metric catalog and the resource
/// breakdown metrics and fields for the target software.</summary>
public interface ITargetedRecordingItemCatalog
{
    Task<IReadOnlyList<TargetedRecordingCatalogItem>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The catalog from the current metric publication, without refreshing the composition.</summary>
    IReadOnlyList<TargetedRecordingCatalogItem> ReadCurrent();
}

internal sealed class TargetedRecordingItemCatalog(
    HardwareMetricCatalogState catalogState,
    IComponentManager components,
    IServiceProvider services,
    IResourceBreakdownSampler breakdown) : ITargetedRecordingItemCatalog
{
    public async Task<IReadOnlyList<TargetedRecordingCatalogItem>> ReadAsync(CancellationToken cancellationToken)
    {
        // The same composition refresh the metric catalog GET performs before reading the publication.
        await catalogState.RefreshCompositionAsync(components,
            services.GetService<IDeviceTopologySnapshotProvider>(), cancellationToken);
        return ReadCurrent();
    }

    public IReadOnlyList<TargetedRecordingCatalogItem> ReadCurrent()
        => TargetedRecordingItemCatalogRules.Build(catalogState.ReadPublication()?.Metrics ?? [],
            ProcessMetricFieldRegistry.Definitions.Concat(breakdown.ResourceFactFields));
}

public static class TargetedRecordingItemCatalogRules
{
    /// <summary>The source name a breakdown metric (not a process field) reports.</summary>
    public const string ResourceBreakdownSource = "resource-breakdown";
    public const string FramesUnit = "ms";
    public const string FramesRateUnit = "FPS";
    // The target's total across every GPU: the sum of the per-GPU bars, usage capped at 100 %.
    public const string GpuUsageTotalId = "gpu.usage";
    public const string GpuVramTotalId = "gpu.vram";

    private static readonly (string Id, string Unit)[] BreakdownMetrics =
    [
        (ResourceBreakdownMetricIds.CpuUsage, "%"),
        (ResourceBreakdownMetricIds.MemoryUsage, "B"),
        (ResourceBreakdownMetricIds.VirtualMemoryUsage, "B"),
        (ResourceBreakdownMetricIds.DiskIo, "B/s"),
        (ResourceBreakdownMetricIds.DiskRead, "B/s"),
        (ResourceBreakdownMetricIds.DiskWrite, "B/s"),
        (ResourceBreakdownMetricIds.NetworkTraffic, "B/s"),
        (ResourceBreakdownMetricIds.NetworkReceive, "B/s"),
        (ResourceBreakdownMetricIds.NetworkSend, "B/s"),
        (ResourceBreakdownMetricIds.NetworkRawTraffic, "B/s"),
        (ResourceBreakdownMetricIds.NetworkRawReceive, "B/s"),
        (ResourceBreakdownMetricIds.NetworkRawSend, "B/s")
    ];

    /// <summary>Union of frames, every metric catalog id (concrete sources are the catalog's candidate
    /// fieldName@deviceIdentity ids) and the software breakdown metrics and fields. No catalog publishes a
    /// natural interval today, so NaturalIntervalMs is null and the profile default applies.</summary>
    public static IReadOnlyList<TargetedRecordingCatalogItem> Build(IReadOnlyList<MetricDefinition> metrics,
        IEnumerable<ProcessMetricFieldDefinition> fields)
    {
        var items = new List<TargetedRecordingCatalogItem>
        {
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesItemId, FramesUnit, [], null,
                TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesFpsItemId, FramesRateUnit, [], null,
                TargetedRecordingItemUses.OverlayOnly),
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesOnePercentLowItemId, FramesRateUnit, [],
                null, TargetedRecordingItemUses.OverlayOnly),
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesPointOnePercentLowItemId, FramesRateUnit,
                [], null, TargetedRecordingItemUses.OverlayOnly)
        };
        var seen = new HashSet<(string, string)>();
        foreach (var metric in metrics)
        {
            if (!seen.Add((TargetedRecordingItemKinds.Metric, metric.Id.ToLowerInvariant()))) continue;
            items.Add(new(TargetedRecordingItemKinds.Metric, metric.Id, metric.Unit,
                metric.CandidateSources.Select(static source => $"{source.FieldName}@{source.DeviceIdentity}")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), null, TargetedRecordingItemUses.Both));
        }
        foreach (var (id, unit) in BreakdownMetrics)
            Add(items, seen, id, unit, ResourceBreakdownSource);
        foreach (var metric in metrics)
        {
            // Per-GPU breakdown bars exist for the GPUs the metric catalog knows.
            if (!metric.Id.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (metric.Id.EndsWith(".usage", StringComparison.OrdinalIgnoreCase) && ResourceBreakdownMetricIds.IsSupported(metric.Id))
                Add(items, seen, metric.Id, "%", ResourceBreakdownSource);
            else if (metric.Id.EndsWith(".vram", StringComparison.OrdinalIgnoreCase) && ResourceBreakdownMetricIds.IsSupported(metric.Id))
                Add(items, seen, metric.Id, "B", ResourceBreakdownSource);
        }
        if (items.Any(static item => IsGpuPart(item, ".usage"))) Add(items, seen, GpuUsageTotalId, "%", ResourceBreakdownSource);
        if (items.Any(static item => IsGpuPart(item, ".vram"))) Add(items, seen, GpuVramTotalId, "B", ResourceBreakdownSource);
        foreach (var field in fields)
            Add(items, seen, field.Name, field.Unit, field.SourceId);
        return items;
    }

    /// <summary>The per-GPU breakdown ids a GPU total sums, or null when the id is not a GPU total.</summary>
    public static IReadOnlyList<string>? TotalParts(string id, IReadOnlyList<TargetedRecordingCatalogItem> catalog)
    {
        var suffix = string.Equals(id, GpuUsageTotalId, StringComparison.OrdinalIgnoreCase) ? ".usage"
            : string.Equals(id, GpuVramTotalId, StringComparison.OrdinalIgnoreCase) ? ".vram" : null;
        return suffix is null ? null
            : catalog.Where(item => IsGpuPart(item, suffix)).Select(static item => item.Id).ToArray();
    }

    private static bool IsGpuPart(TargetedRecordingCatalogItem item, string suffix)
        => item.Kind == TargetedRecordingItemKinds.Software
            && item.Id.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase)
            && item.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && ResourceBreakdownMetricIds.IsSupported(item.Id);

    /// <summary>Shape, catalog membership for the given use (recording or overlay) and source ownership of one item.</summary>
    public static string? Validate(TargetedRecordingItem item, IReadOnlyList<TargetedRecordingCatalogItem> catalog,
        string use = TargetedRecordingItemUses.Recording)
    {
        if (TargetedRecordingProfileShape.ValidateItem(item) is { } shape) return shape;
        var entry = Find(catalog, item.Kind, item.Id);
        if (entry is null || !entry.Uses.Contains(use, StringComparer.Ordinal)) return "targeted-recording-item-unknown";
        return string.Equals(item.Source, TargetedRecordingProfileDefaults.AutoSource, StringComparison.Ordinal)
            || entry.Sources.Contains(item.Source, StringComparer.OrdinalIgnoreCase)
            ? null : "targeted-recording-item-source-invalid";
    }

    public static TargetedRecordingCatalogItem? Find(IReadOnlyList<TargetedRecordingCatalogItem> catalog,
        string kind, string id)
        => catalog.FirstOrDefault(item => item.Kind == kind && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private static void Add(List<TargetedRecordingCatalogItem> items, HashSet<(string, string)> seen,
        string id, string unit, string source)
    {
        if (seen.Add((TargetedRecordingItemKinds.Software, id.ToLowerInvariant())))
            items.Add(new(TargetedRecordingItemKinds.Software, id, unit, [source], null, TargetedRecordingItemUses.Both));
    }
}
