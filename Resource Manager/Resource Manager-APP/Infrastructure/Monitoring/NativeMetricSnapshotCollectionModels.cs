using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal readonly record struct NativeMetricSnapshotSourceRuntimeMode(
    ulong SourceHandle,
    ulong CapabilityGeneration,
    NativeMetricSnapshotZoneMode ZoneMode,
    NativeMetricSnapshotSourceAvailability Availability);

internal sealed record NativeMetricSnapshotCollectionPlan(
    NativeMetricSnapshotPlanOutput Header,
    ImmutableArray<NativeMetricSnapshotSourcePlanOutput> Sources,
    ImmutableArray<NativeMetricSnapshotMetricPlanOutput> Metrics,
    NativeMetricSnapshotCatalogProjection Catalog)
{
    internal ImmutableArray<NativeMetricSnapshotMetricPlanOutput>
        MetricsForSource(ulong sourceHandle)
        => Metrics
            .Where(metric => metric.SourceHandle == sourceHandle)
            .ToImmutableArray();
}

internal sealed record NativeMetricSnapshotSourceCompletion(
    ulong SourceHandle,
    ulong SourceIncarnation,
    ulong SourceObservationSequence,
    DateTimeOffset CapturedAt,
    NativeMetricSnapshotSourceStatus Status,
    ImmutableArray<NativeMetricSnapshotObservationInput> Observations,
    NativeMetricSnapshotCpuCounterInput? CpuCounter,
    ImmutableArray<NativeMetricSnapshotGpuInventoryInput> GpuInventory,
    uint ExpectedGpuInventoryCount,
    uint OverflowCount);

internal sealed record NativeMetricSnapshotCommittedFrame(
    NativeMetricSnapshotSnapshotHeader Header,
    ImmutableArray<NativeMetricSnapshotSourceOutput> Sources,
    ImmutableArray<NativeMetricSnapshotMetricOutput> Metrics,
    ImmutableArray<NativeMetricSnapshotGpuInventoryOutput> GpuInventory,
    ImmutableArray<NativeMetricSnapshotRuleStateOutput> Rules,
    NativeMetricSnapshotCatalogProjection Catalog);
