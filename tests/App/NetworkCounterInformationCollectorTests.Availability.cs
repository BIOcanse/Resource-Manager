using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace Resource_Manager_APP.Tests;

public sealed partial class NetworkCounterInformationCollectorTests
{
    [Fact]
    public void EnumeratedButUnsubscribedInterfacesAreRequestableAndRetainSourcesAfterReadFailure()
    {
        var api = new Api();
        var zone = new IpHelperNetworkMonitoringZone(api);
        var interfaces = zone.ReadInterfaceIdentities().Identities;
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"network-catalog-availability-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [],
            workspace.CreateCatalogHandleMap(), networkInterfaces: interfaces);
        var now = DateTimeOffset.UtcNow;
        workspace.ReplaceCatalog(catalog, now);
        var tracker = new MetricReadingSourceTracker();
        var frame = workspace.ReadCommitted();
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame, 1, tracker.Select(frame, 1));
        var definitions = MetricCatalog.FromSnapshot(snapshot)
            .Where(item => item.SourceId == "windows.network-interface").ToArray();
        Assert.Equal(interfaces.Count * 19, definitions.Length);
        Assert.Empty(api.Reads);
        Assert.All(definitions, definition =>
        {
            Assert.True(definition.Selectable);
            Assert.Null(definition.DisabledReason);
            Assert.Equal("unverified", Assert.Single(definition.CandidateSources).SourceAvailability);
        });

        var binding = catalog.MetricBindings.Values.First(item => item.SourceId == "windows.network-interface"
            && item.FieldCode == MetricFieldCode.NetworkRxOctets);
        var modes = catalog.Sources.Select(source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var plan = workspace.PlanCollection([binding.MetricHandle], modes, false, now);
        var sourcePlan = Assert.Single(plan.Sources);
        api.Transform = _ => null;
        var read = zone.ReadInterfaces(interfaces, Request(interfaces.Single(item => item.ExactIdentityKey == binding.DeviceIdentity),
            MetricFieldCode.NetworkRxOctets));
        var completion = WindowsHardwareMetricCapture.CreateNetworkInterfaceCompletion(plan, sourcePlan,
            new(sourcePlan.SourceHandle, 1, 1), now, read);
        workspace.CompleteSource(plan, completion, now);
        frame = workspace.ReadCommitted();
        tracker.Observe(frame, 1, [completion]);
        snapshot = NativeMetricSnapshotCommittedProjection.Create(frame, 1, tracker.Select(frame, 1));
        var failed = MetricCatalog.FromSnapshot(snapshot).Single(item => item.Id == binding.MetricId);
        Assert.True(failed.Selectable);
        Assert.Equal("unverified", Assert.Single(failed.CandidateSources).SourceAvailability);
        Assert.Null(failed.DisabledReason);
        Assert.Null(snapshot.Items[binding.MetricId].IntegerValue);
    }
}
