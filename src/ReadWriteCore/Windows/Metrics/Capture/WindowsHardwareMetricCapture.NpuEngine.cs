using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    private NativeMetricSnapshotSourceCompletion CollectNpuEngine(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, NativeMetricSnapshotCatalogTopologySnapshot? topology, CancellationToken token,
        Action rejectPublication)
    {
        if (topology?.NpuEngineStatus != NativeMetricSnapshotCatalogTopologyStatus.Complete)
            return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        var requested = SelectedBindings(plan, sourcePlan.SourceHandle).Select(static binding => binding.DeviceIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var values = resources.ReadNpuEngines(plan.Catalog.NpuEngineIdentities, requested,
            plan.Catalog.NpuEngineEffectiveMaximumInstanceCount, token);
        token.ThrowIfCancellationRequested();
        if (values.Any(static value => value.RequiresTopologyRefresh))
        {
            requestTopologyRefresh();
            rejectPublication();
        }
        return CreateNpuEngineCompletion(plan, sourcePlan, identity, capturedAt, values);
    }

    internal static NativeMetricSnapshotSourceCompletion CreateNpuEngineCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<NpuEngineRead> values)
    {
        var byEngine = values.ToDictionary(static value => value.Engine.ExactIdentityKey, StringComparer.Ordinal);
        NpuEngineSample? Sample(NativeMetricSnapshotRuleCatalogBinding binding)
            => byEngine.TryGetValue(binding.DeviceIdentity, out var value)
                && ReferenceEquals(value.Engine, binding.NpuEngine) && !value.RequiresTopologyRefresh ? value.Sample : null;
        return CompleteWithValues(plan, sourcePlan, identity, capturedAt, _ => null,
            unsignedValue: binding => binding.FieldCode == MetricFieldCode.NpuEngineRunningTimeMicroseconds
                ? Sample(binding)?.RunningTimeMicroseconds : null,
            timedCounterValue: binding => binding.FieldCode == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent
                && Sample(binding) is { } sample
                    ? new NativeMetricSnapshotTimedCounter(sample.RunningTimeMicroseconds, sample.QueryReturnTicks, sample.TicksPerSecond) : null);
    }
}
