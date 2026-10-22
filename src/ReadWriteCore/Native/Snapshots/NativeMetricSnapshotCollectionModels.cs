using System.Collections.Immutable;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace ResourceManager.App.Infrastructure.Monitoring;

public readonly record struct NativeMetricSnapshotSourceRuntimeMode(
    ulong SourceHandle,
    ulong CapabilityGeneration,
    NativeMetricSnapshotZoneMode ZoneMode,
    NativeMetricSnapshotSourceAvailability Availability);

public sealed record NativeMetricSnapshotCollectionPlan(
    NativeMetricSnapshotPlanOutput Header,
    ImmutableArray<NativeMetricSnapshotSourcePlanOutput> Sources,
    ImmutableArray<NativeMetricSnapshotMetricPlanOutput> Metrics,
    NativeMetricSnapshotCatalogProjection Catalog)
{
    public ImmutableArray<NativeMetricSnapshotMetricPlanOutput>
        MetricsForSource(ulong sourceHandle)
        => Metrics
            .Where(metric => metric.SourceHandle == sourceHandle)
            .ToImmutableArray();
}

public sealed record NativeMetricSnapshotSourceCompletion(
    ulong SourceHandle,
    ulong SourceIncarnation,
    ulong SourceObservationSequence,
    DateTimeOffset CapturedAt,
    NativeMetricSnapshotSourceStatus Status,
    ImmutableArray<NativeMetricSnapshotObservationInput> Observations,
    NativeMetricSnapshotCpuCounterInput? CpuCounter,
    ImmutableArray<NativeMetricSnapshotGpuInventoryInput> GpuInventory,
    uint ExpectedGpuInventoryCount,
    uint OverflowCount)
{
    public PdhGpuEngineUsageRead? PdhGpuEngineUsage { get; init; }
    // Positive sensor-presence facts are independent of a numeric value this frame.
    public ImmutableArray<ulong> SupportedMetricHandles { get; init; } = [];
    public ImmutableDictionary<ulong, string> ReadingQuantities { get; init; }
        = ImmutableDictionary<ulong, string>.Empty;
    public string? IntegratedGpuDeviceIdentity { get; init; }
}

public sealed record NativeMetricSnapshotCommittedFrame(
    NativeMetricSnapshotSnapshotHeader Header,
    ImmutableArray<NativeMetricSnapshotSourceOutput> Sources,
    ImmutableArray<NativeMetricSnapshotMetricOutput> Metrics,
    ImmutableArray<NativeMetricSnapshotGpuInventoryOutput> GpuInventory,
    ImmutableArray<NativeMetricSnapshotRuleStateOutput> Rules,
    NativeMetricSnapshotCatalogProjection Catalog);
