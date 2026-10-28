using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class BatteryInformationCollectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSamplingWorkerCollectsOnlySubscribedBatteryAndCacheInspectionNeedsNoWorker(bool cancelFetchBeforeStart)
    {
        var root = Path.Combine(Path.GetTempPath(), $"battery-owner-{Guid.NewGuid():N}");
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var devices = new[] { Device(0), Device(1, true) };
        var topology = new NativeMetricSnapshotCatalogTopologySnapshot(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            NativeMetricSnapshotCatalogTopologyStatus.Complete, [], NativeMetricSnapshotCatalogTopologyStatus.Complete, [])
            { BatteryStatus = NativeMetricSnapshotCatalogTopologyStatus.Complete, Batteries = devices };
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => topology), new BatteryEnvironment(root),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        using var workspaceHolding = owner.Hold();
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var opened = new List<string>();
        var battery = new WindowsBatteryMonitoringZone(new WindowsBatteryReader(enumerate: null, open: path =>
            { opened.Add(path); return new DeviceQuery { Relative = path == "path-1" }; }));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == battery.DisplayName ? battery : new MonitoringSourceZone(id)).ToArray());
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), new WindowsMemoryMonitoringZone(),
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner,
            dashboard, registry, slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance, battery);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, devices[1].ExactIdentityKey,
            ["battery_all_remaining_capacity_actual_relative_units"]);
        Assert.Empty(opened);
        // Explicit cache inspection: GetSnapshotAsync acquires a single-fetch demand and waits for sampling.
        _ = sampler.ReadLatest(request);
        Assert.Empty(opened);
        if (cancelFetchBeforeStart)
        {
            // A single fetch before Start legitimately waits; canceling it must
            // withdraw its demand so the later worker sees only battery-consumer.
            using var fetchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var fetch = sampler.GetSnapshotAsync(request, fetchCancellation.Token);
            Assert.False(fetch.IsCompleted);
            Assert.Empty(opened);
            fetchCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
            Assert.Empty(opened);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sampler.StartAsync(timeout.Token);
        try
        {
            using var demand = sampler.AcquireSubscription("battery-consumer", request, TimeSpan.FromMinutes(10));
            var current = await HardwareMetricCurrentTestWait.ReadAsync(sampler, request, timeout.Token);
            var value = Assert.Single(Assert.Single(MetricDeviceFields.Read(current, devices[1].ExactIdentityKey,
                new HashSet<string> { "battery_all_remaining_capacity_actual_relative_units" })).Fields);
            Assert.Equal(SamplingObservationStatus.Current, value.Status);
            Assert.Equal(25000, value.NumericValue);
            Assert.Equal(["path-1"], opened);
            _ = sampler.ReadLatest(request);
            sampler.ReadLatest(request);
            Assert.Single(opened);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            var count = opened.Count;
            _ = sampler.ReadLatest(request);
            Assert.Equal(count, opened.Count);
        }
    }

    private sealed class BatteryEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
