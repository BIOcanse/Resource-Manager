using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed partial class MetricCatalogueSubscriptionTests
{
    [Fact]
    public async Task HostDiskPerformanceUsesOriginalStorageZoneAndCurrent()
    {
        const string identity = "recorded-physical-disk";
        using var fixture = new Fixture(Topology([new(7, identity, 1) { SupportsAtaSmart = true }]));
        var reads = 0;
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var zone = new WindowsStorageSensorsMonitoringZone((targets, token) =>
        {
            token.ThrowIfCancellationRequested();
            var target = Assert.Single(targets);
            Assert.Equal(7, target.DisplayIndex);
            Assert.Equal(identity, target.IdentityKey);
            Assert.True(target.SupportsAtaSmart);
            reads++;
            observedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Task.FromResult(new WindowsDiskPerformanceCaptureResult(
                targets.ToDictionary(static target => target, _ => (WindowsDiskPerformanceMetrics?)
                    new WindowsDiskPerformanceMetrics(4096, 2048, 1, 9, 50, 7, 10) { ObservedAt = observedAt })));
        });
        using var notebook = new NotebookOemFanMonitoringZone();
        var platform = new WindowsPlatformSensorReader(zone, new HardwareMonitorWmiMonitoringZone(), notebook);
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(),
            new WindowsMemoryMonitoringZone(), null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new AmdIntegratedGpuSensorRoute(), platform, fixture.Owner, fixture.Catalog,
 fixture.Slots.Owner, fixture.Slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        using (var lease = fixture.Owner.Acquire())
            fixture.Catalog.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        using var client = new HttpClient();
        var controls = new NoControls();
        var backend = new HostReadWriteBackend(sampler, sampler, fixture.Catalog, controls,
            new ControlWriteLayer(controls, [], new NormalAccess()));
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var field = core.Catalog.Fields.Single(item =>
            item.Definition.Name == "storage_all_read_bytes_per_second_actual").Field;

        Assert.Equal(MonitoringSourceZoneIds.WindowsStorageSensors,
            NativeMetricSnapshotSourceCatalog.ResolveZoneId(NativeMetricSnapshotSources.WindowsDiskPerformance));
        var diskCapabilities = core.Catalog.Fields.Where(item =>
            item.ReadSource == NativeMetricSnapshotSources.WindowsDiskPerformance).ToArray();
        Assert.Equal(7, diskCapabilities.Length);
        Assert.All(diskCapabilities, capability =>
        {
            var queried = core.Query(capability.Field);
            Assert.True(queried.Read.Supported);
            Assert.Empty(queried.Read.Dependencies);
            Assert.False(queried.Write.Supported);
        });
        Assert.Equal(0, reads);
        Assert.Null(core.ReadCached(field).Value);
        var captured = Assert.Single(await core.CollectAsync([field]));
        Assert.Equal(FieldObservationStatus.Current, captured.Status);
        Assert.Equal(4096d, captured.Value!.Number);
        Assert.Equal(observedAt, captured.ObservedAt);
        Assert.Equal(captured, core.ReadCached(field));
        Assert.Equal(1, reads);
        Assert.Null(sampler.CaptureSamplingWorkerTask());

        var recovered = Assert.Single(await core.CollectAsync([field]));
        Assert.Equal(FieldObservationStatus.Current, recovered.Status);
        Assert.Equal(4096d, recovered.Value!.Number);
        Assert.Equal(recovered, core.ReadCached(field));
        Assert.Equal(2, reads);
    }
}
