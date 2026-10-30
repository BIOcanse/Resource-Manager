using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class BatteryInformationCollectorTests
{
    [Fact]
    public void NativePlanPublishesApplicableFieldsWithoutMixingDevicesAndCacheClearsFailure()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"battery-{Guid.NewGuid():N}.bin"));
        var batteries = new[] { Device(0), Device(1, true) };
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), batteries: batteries);
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var bindings = catalog.MetricBindings.Values.Where(binding => binding.SourceId == NativeMetricSnapshotSourceCatalog.WindowsBattery).ToArray();
        Assert.Equal(20, bindings.Length);
        var plan = workspace.PlanCollection(bindings.Select(static b => b.MetricHandle).ToArray(),
            catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle, 1,
                NativeMetricSnapshotSourceAvailability.Available)).ToArray(), false, now);
        var source = Assert.Single(plan.Sources);
        var values = batteries.Select(battery => WindowsBatteryReader.Project(battery.ExactIdentityKey, battery.Information,
            Status(5, battery.DisplayIndex == 0 ? 25000U : 10000U), 3600, AllFields)).ToArray();
        workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateBatteryCompletion(plan, source,
            new(source.SourceHandle, 1, 1), now, values), now);
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
        var first = Assert.Single(MetricDeviceFields.Read(snapshot, batteries[0].ExactIdentityKey)).Fields;
        var second = Assert.Single(MetricDeviceFields.Read(snapshot, batteries[1].ExactIdentityKey)).Fields;
        Assert.All(first.Concat(second), field => Assert.Equal(SamplingObservationStatus.Current, field.Status));
        Assert.Equal(50, first.Single(f => f.Name == "battery_all_charge_percent_derived").NumericValue);
        Assert.Equal(20, second.Single(f => f.Name == "battery_all_charge_percent_derived").NumericValue);
        Assert.Equal("mWh", first.Single(f => f.Name == "battery_all_remaining_capacity_actual_mwh").Unit);
        Assert.Equal("relative-unit", second.Single(f => f.Name == "battery_all_remaining_capacity_actual_relative_units").Unit);
        Assert.Equal(MetricInteger.Parse("5"), first.Single(f => f.Name == "battery_all_charge_state_actual").IntegerValue);
        Assert.Null(first.Single(f => f.Name == "battery_all_charge_state_actual").NumericValue);
        using var cache = new LastSuccessfulHardwareMetricSnapshot();
        var request = MetricSampleRequest.ForIds(bindings.Select(static b => b.MetricId));
        cache.Publish(snapshot, request);
        cache.RecordFailure(request, now.AddSeconds(1), "fixture-failure");
        Assert.All(MetricDeviceFields.Read(cache.Read()!).SelectMany(static device => device.Fields)
            .Where(static field => field.Name.StartsWith("battery_")), field =>
        { Assert.Equal(SamplingObservationStatus.Unavailable, field.Status); Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue); });
        cache.Publish(snapshot, request);
        Assert.Equal(50, Assert.Single(MetricDeviceFields.Read(cache.Read()!, batteries[0].ExactIdentityKey))
            .Fields.Single(f => f.Name == "battery_all_charge_percent_derived").NumericValue);
    }

    [Fact]
    public void DirectoryFailureRetainsDeclarationsSuccessfulRemovalAndUnitChangeReplaceThem()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, batteries: [Device(0)]);
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, []);
        Assert.Single(HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(topology, catalog).Batteries);
        Assert.Empty(HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(topology with
            { BatteryStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete }, catalog).Batteries);
        var changed = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], catalog.HandleMap, batteries: [Device(0, true)]);
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(catalog, changed));
        Assert.DoesNotContain(changed.MetricBindings.Values, b => b.FieldCode == MetricFieldCode.BatteryRemainingCapacityAbsolute);
        Assert.Contains(changed.MetricBindings.Values, b => b.FieldCode == MetricFieldCode.BatteryRemainingCapacityRelative);
    }
}
