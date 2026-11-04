using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class LastSuccessfulHardwareMetricSnapshot
{
    private static GpuMetrics EmptyGpu(HardwareMetricGpuDescriptor gpu) => new(gpu.Index, gpu.Name,
        0, 0, 0, 0, 0, 0, 0, 0,
        new(new(string.Empty, HardwareSensorProviderStates.NotRequested, null), null, null, null,
            null, null, null, null, null, null, null), gpu.UsageProvider, false, gpu.IdentityKey)
        { AdapterKey = gpu.AdapterKey, HasDedicatedMemoryMetrics = gpu.HasDedicatedMemoryMetrics };

    // A publication owns only the selected slot's payload. Independent updates
    // must not keep entire historical collection frames alive.
    private static HardwareMetricSnapshot Slice(HardwareMetricSnapshot snapshot, string id)
        => snapshot with
        {
            Items = snapshot.Items.Where(pair => MetricDatasetIds.ForSystemMetric(pair.Key)
                    .Equals(id, StringComparison.OrdinalIgnoreCase)).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Datasets = ImmutableDictionary<string, HardwareMetricDatasetObservation>.Empty,
            InvalidatedDatasetIds = ImmutableHashSet<string>.Empty,
            FieldBindings = ImmutableDictionary<string, MetricDeviceFieldBinding>.Empty,
            History = ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>>.Empty
        };

    private static HardwareMetricSnapshot Freeze(HardwareMetricSnapshot snapshot, IReadOnlySet<string>? selected = null)
        => snapshot with
        {
            Items = snapshot.Items.Where(pair => selected is null || selected.Contains(MetricDatasetIds.ForSystemMetric(pair.Key)))
                .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Datasets = snapshot.Datasets.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            Gpus = snapshot.Gpus.ToImmutableArray(),
            GpuInventory = snapshot.GpuInventory with { Adapters = snapshot.GpuInventory.Adapters.ToImmutableArray() },
            FieldBindings = snapshot.FieldBindings.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
        };
}
