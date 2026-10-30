using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaFanHostIntegrationTests
{
    [Fact]
    public void ProductionHostTopologyConstructorBorrowsTheSameZoneFanDirectory()
    {
        var fixture = new NvidiaFanFixture(); var reads = 0;
        var zone = new NvidiaNvmlMonitoringZone((_, _, _) => throw new InvalidOperationException(),
            readFanIdentities: inventory => { Assert.Same(fixture.Inventory, inventory); reads++; return fixture.Fans; }, readFans: fixture.Read);
        var topology = new HostManagerMetricSnapshotCatalogTopologySource(new WindowsGpuAdapterOrderMonitoringZone(() => fixture.Inventory),
            new WindowsBatteryMonitoringZone(), nvidiaZone: zone);
        var source = typeof(HostManagerMetricSnapshotCatalogTopologySource).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(topology)!;
        var resources = (WindowsMetricTopologyResources)source.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(static field => field.FieldType == typeof(WindowsMetricTopologyResources)).GetValue(source)!;
        Assert.Same(fixture.Fans, resources.ReadNvmlFanIdentities(fixture.Inventory)); Assert.Equal(1, reads);
        Assert.Same(fixture.Fans, resources.ReadNvmlFanIdentities(fixture.Inventory)); Assert.Equal(2, reads);
    }

    [Fact]
    public async Task OriginalHostWorkerAndSingleFetchUseSelectedFanAndRetireOldCatalogPublication()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var fixture = new NvidiaFanFixture(); var topology = fixture.Topology();
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => topology.Capture()),
            new TestEnvironment(Path.Combine(Path.GetTempPath(), $"nvml-fan-host-{Guid.NewGuid():N}")), NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        using var workspaceHolding = owner.Hold();
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var fan = new NvidiaNvmlFanIdentity(NvidiaFanFixture.ParentA, 1);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, fan.ExactIdentityKey, [NvidiaFanFixture.Percent]);
        var zone = new NvidiaNvmlMonitoringZone((_, _, _) => throw new InvalidOperationException("No GPU telemetry was selected."),
            readFanIdentities: _ => fixture.Fans, readFans: fixture.Read);
        using var sampler = new WindowsHardwareMetricSampler(null!, null!, null!, new WindowsGpuAdapterOrderMonitoringZone(() => fixture.Inventory),
            null!, null!, null!, zone, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner, dashboard,
 slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Empty(fixture.Targets);
        _ = sampler.ReadLatest(request); Assert.Empty(fixture.Targets);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("nvml-fan-recording", request, TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync());
                var value = Assert.Single(Assert.Single(MetricDeviceFields.Read(stream.Current, fan.ExactIdentityKey,
                    new HashSet<string> { NvidiaFanFixture.Percent })).Fields);
                Assert.Equal(105d, value.NumericValue); Assert.Equal(SamplingObservationStatus.Current, value.Status);
            }
            await Task.Delay(150, timeout.Token);
            var count = fixture.Targets.Count; Assert.True(count > 0);
            Assert.All(fixture.Targets, targets =>
            {
                var target = Assert.Single(targets); Assert.Equal(fan, target.Fan);
                Assert.True(target.IncludePercent); Assert.False(target.IncludeRpm);
            });
            var cached = sampler.ReadLatest(request);
            Assert.Equal(cached?.CommittedGeneration, sampler.ReadLatest(request)?.CommittedGeneration);
            var fresh = await sampler.GetSnapshotAsync(request, timeout.Token);
            Assert.True(fixture.Targets.Count > count);
            Assert.Equal(105d, Assert.Single(Assert.Single(MetricDeviceFields.Read(fresh, fan.ExactIdentityKey,
                new HashSet<string> { NvidiaFanFixture.Percent })).Fields).NumericValue);
            var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
                .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(20, timeout.Token);
            var gate = (SemaphoreSlim)typeof(WindowsHardwareMetricSampler).GetField("sampleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
            await gate.WaitAsync(timeout.Token); gate.Release();
            var afterFetch = fixture.Targets.Count;
            await Task.Delay(150, timeout.Token); Assert.Equal(afterFetch, fixture.Targets.Count);
            using var retired = owner.Acquire();
            fixture.Fans = [new(NvidiaFanFixture.ParentB, 0)]; owner.RequestTopologyRefresh();
            Assert.NotNull(owner.RefreshTopologyIfRequested());
            Assert.NotEqual(retired.WorkspaceIdentity, owner.CurrentWorkspaceIdentity);
            var published = false;
            Assert.False(retired.TryPublish(_ => { published = true; return true; }, out _)); Assert.False(published);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
