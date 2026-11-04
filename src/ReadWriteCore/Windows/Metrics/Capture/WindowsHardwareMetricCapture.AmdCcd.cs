using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    private NativeMetricSnapshotSourceCompletion CollectAmdCcd(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, NativeMetricSnapshotCatalogTopologySnapshot? topology)
    {
        // Retained declarations are not evidence that the old PCI target remains valid.
        if (topology?.AmdCcdStatus != NativeMetricSnapshotCatalogTopologyStatus.Complete)
            return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        var requested = SelectedBindings(plan, sourcePlan.SourceHandle)
            .Where(static binding => binding.AmdCcd is not null)
            .Select(static binding => binding.DeviceIdentity).ToHashSet(StringComparer.Ordinal);
        IReadOnlyDictionary<string, double?> values = requested.Count == 0
            ? new Dictionary<string, double?>(StringComparer.Ordinal)
            : resources.ReadAmdCcd(plan.Catalog.AmdCcdIdentities, requested);
        return CreateAmdCcdCompletion(plan, sourcePlan, identity, capturedAt, values);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateAmdCcdCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyDictionary<string, double?> values)
        => CompleteWithValues(plan, sourcePlan, identity, capturedAt,
            binding => values.TryGetValue(binding.DeviceIdentity, out var value) ? value : null);
}
