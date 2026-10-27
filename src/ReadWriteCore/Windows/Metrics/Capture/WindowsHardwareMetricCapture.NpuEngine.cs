using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed partial class WindowsHardwareMetricCapture
{
    [ResourceManager.ReadWrite.RuntimeReadSource(typeof(ResourceManager.App.Infrastructure.Monitoring.NativeMetricSnapshotSources), nameof(ResourceManager.App.Infrastructure.Monitoring.NativeMetricSnapshotSources.WindowsNpuEngine))]
    private NativeMetricSnapshotSourceCompletion CollectNpuEngine(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, NativeMetricSnapshotCatalogTopologySnapshot? topology, CancellationToken token)
    {
        if (topology?.NpuEngineStatus != NativeMetricSnapshotCatalogTopologyStatus.Complete)
            return Unavailable(sourcePlan.SourceHandle, identity, capturedAt);
        var requested = SelectedBindings(plan, sourcePlan.SourceHandle)
            .Where(static binding => binding.NpuEngine is not null)
            .Select(static binding => binding.NpuEngine!.ExactIdentityKey)
            .ToHashSet(StringComparer.Ordinal);
        var values = resources.ReadNpuEngines(plan.Catalog.NpuEngineIdentities, requested,
            plan.Catalog.NpuEngineEffectiveMaximumInstanceCount, token);
        token.ThrowIfCancellationRequested();
        ObserveTopology(NativeMetricSnapshotSources.WindowsNpuEngine, "selected-identity-changed",
            values.Any(static value => value.RequiresTopologyRefresh));
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
