using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    [ResourceManager.ReadWrite.RuntimeReadSource(typeof(ResourceManager.App.Infrastructure.Monitoring.NativeMetricSnapshotSources), nameof(ResourceManager.App.Infrastructure.Monitoring.NativeMetricSnapshotSources.WindowsNpuMemory))]
    private NativeMetricSnapshotSourceCompletion CollectNpuMemory(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, NativeMetricSnapshotCatalogTopologySnapshot? topology, CancellationToken cancellationToken)
    {
        // A retained declaration is not fresh permission to sample an unavailable directory.
        if (topology?.NpuMemoryStatus != NativeMetricSnapshotCatalogTopologyStatus.Complete)
            return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        var requested = SelectedBindings(plan, sourcePlan.SourceHandle).GroupBy(static binding => binding.DeviceIdentity)
            .ToDictionary(static group => group.Key, static group => group.Aggregate(NpuMemoryFields.None,
                static (flags, binding) => flags | NpuField(binding.FieldCode)), StringComparer.Ordinal);
        var values = resources.ReadNpuMemory(plan.Catalog.NpuMemoryIdentities, requested,
            plan.Catalog.NpuMemoryEffectiveMaximumInstanceCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ObserveTopology(NativeMetricSnapshotSources.WindowsNpuMemory, "selected-identity-changed",
            values.Any(static value => value.RequiresTopologyRefresh));
        return CreateNpuMemoryCompletion(plan, sourcePlan, identity, capturedAt, values);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateNpuMemoryCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<NpuMemoryRead> values)
    {
        var devices = values.ToDictionary(static value => value.DeviceIdentity, StringComparer.Ordinal);
        return CompleteWithValues(plan, sourcePlan, identity, capturedAt, _ => null,
            unsignedValue: binding => devices.TryGetValue(binding.DeviceIdentity, out var device)
                && !device.RequiresTopologyRefresh ? binding.FieldCode switch
            {
                MetricFieldCode.NpuDedicatedMemoryCommitted => device.DedicatedCommitted,
                MetricFieldCode.NpuDedicatedMemoryResident => device.DedicatedResident,
                MetricFieldCode.NpuSharedMemoryCommitted => device.SharedCommitted,
                MetricFieldCode.NpuSharedMemoryResident => device.SharedResident,
                _ => null
            } : null);
    }

    private static NpuMemoryFields NpuField(MetricFieldCode code) => code switch
    {
        MetricFieldCode.NpuDedicatedMemoryCommitted => NpuMemoryFields.DedicatedCommitted,
        MetricFieldCode.NpuDedicatedMemoryResident => NpuMemoryFields.DedicatedResident,
        MetricFieldCode.NpuSharedMemoryCommitted => NpuMemoryFields.SharedCommitted,
        MetricFieldCode.NpuSharedMemoryResident => NpuMemoryFields.SharedResident,
        _ => NpuMemoryFields.None
    };
}
