using System.Collections.Frozen;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Domain.Metrics;

public sealed record HardwareMetricItemDescriptor(string Id, string Unit, MetricFieldCode FieldCode,
    string? FieldName, string? DeviceIdentity, string? ValueKind, string? SourceId);
public sealed record HardwareMetricCpuDescriptor(string Name, int MaxFrequencyMhz, string FrequencySource);
public sealed record HardwareMetricGpuDescriptor(int Index, string Name, string? IdentityKey,
    ulong? AdapterKey, string UsageProvider)
{
    public bool? HasDedicatedMemoryMetrics { get; init; }
}

/// <summary>Discovered device and field facts. No sampled values, observation times or history are retained.</summary>
public sealed record HardwareMetricCatalog(
    HardwareMetricCpuDescriptor Cpu,
    IReadOnlyList<HardwareMetricGpuDescriptor> Gpus,
    IReadOnlyDictionary<string, HardwareMetricItemDescriptor> Items,
    IReadOnlyDictionary<string, MetricDeviceFieldBinding> FieldBindings,
    ulong WorkspaceIdentity, ulong ConfigurationGeneration, ulong CatalogGeneration)
{
    public static HardwareMetricCatalog FromSnapshot(HardwareMetricSnapshot snapshot)
        => new(new(snapshot.Cpu.Name, snapshot.Cpu.MaxFrequencyMhz, snapshot.Cpu.FrequencySource),
            snapshot.Gpus.Select(static gpu => new HardwareMetricGpuDescriptor(gpu.Index, gpu.Name,
                gpu.IdentityKey, gpu.AdapterKey, gpu.UsageProvider)
                { HasDedicatedMemoryMetrics = gpu.HasDedicatedMemoryMetrics }).ToArray(),
            snapshot.Items.ToFrozenDictionary(static pair => pair.Key, static pair => new HardwareMetricItemDescriptor(
                pair.Key, pair.Value.Unit, pair.Value.FieldCode, pair.Value.FieldName, pair.Value.DeviceIdentity,
                pair.Value.ValueKind, pair.Value.SourceId), StringComparer.OrdinalIgnoreCase),
            snapshot.FieldBindings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            snapshot.WorkspaceIdentity, snapshot.ConfigurationGeneration, snapshot.CatalogGeneration);

    /// <summary>Materializes an empty value carrier for APIs that consume discovery snapshots.</summary>
    public HardwareMetricSnapshot CreateSnapshot()
    {
        var provider = new HardwareSensorProviderState(string.Empty, HardwareSensorProviderStates.Unavailable, null);
        return new(default,
            new(Cpu.Name, 0, false, CpuMetricObservationStatus.NotRequested, 0, 0, 0,
                Cpu.MaxFrequencyMhz, 0, Cpu.FrequencySource, new(provider, null, null, null, null)),
            new(0, 0, 0, false), new(0, 0, 0, false),
            Gpus.Select(gpu => new GpuMetrics(gpu.Index, gpu.Name, 0, 0, 0, 0, 0, 0, 0, 0,
                new(provider, null, null, null, null, null, null, null, null, null, null),
                gpu.UsageProvider, false, gpu.IdentityKey) { AdapterKey = gpu.AdapterKey,
                    HasDedicatedMemoryMetrics = gpu.HasDedicatedMemoryMetrics }).ToArray(),
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []),
            Items.ToDictionary(static pair => pair.Key, static pair => new MetricReadingValue(pair.Key, null, pair.Value.Unit)
            {
                FieldCode = pair.Value.FieldCode, FieldName = pair.Value.FieldName,
                DeviceIdentity = pair.Value.DeviceIdentity, ValueKind = pair.Value.ValueKind,
                SourceId = pair.Value.SourceId, Status = SamplingObservationStatus.NotRequested
            }, StringComparer.OrdinalIgnoreCase))
        {
            FieldBindings = FieldBindings, WorkspaceIdentity = WorkspaceIdentity,
            ConfigurationGeneration = ConfigurationGeneration, CatalogGeneration = CatalogGeneration
        };
    }
}
