using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class AdlxMetricsCollectorTests
{
    [Fact]
    public async Task ActualSamplerClearsHungAdlxWhilePublishingNewCpuObservations()
    {
        using var slots=new HostManagerSamplingSubscriptionTestFixture();
        var root=Path.Combine(Path.GetTempPath(),$"adlx-worker-{Guid.NewGuid():N}");
        var inventory=Inventory(); var inventoryZone=new WindowsGpuAdapterOrderMonitoringZone(()=>inventory);
        using var owner=new HostManagerMetricSnapshotOwner(slots.Provider,new HostManagerMetricSnapshotRuntime(slots.Provider,slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(()=>new(1,NativeMetricSnapshotCatalogTopologyStatus.Complete,
                RecordedGpuIdentities(Inventory()),
                NativeMetricSnapshotCatalogTopologyStatus.Complete,[],NativeMetricSnapshotCatalogTopologyStatus.Complete,[])),
            new Environment(root),NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None); var dashboard=new HardwareMetricCatalogState();
        using(var lease=owner.Acquire()) dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var device=dashboard.Current!.FieldBindings.Values.Single(binding=>binding.Field.Code==MetricFieldCode.AdlxActivity && binding.DeviceIdentity == RecordedGpuIdentities(Inventory()).Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey).DeviceIdentity;
        var row=Row(10,PnpA,AllFlags); row.VoltageMillivolts=1250; row.SampleTimestampMilliseconds=9007199254740993; row.NpuActivityLevelPercent=0; row.NpuFrequencyMhz=1800;
        var native=new Bridge { Metadata=[Row(10,PnpA),Row(20,PnpB)],Values=[row] };
        var zone=new AmdAdlxMonitoringZone(new AmdAdlxBridgeReader(native, TimeSpan.FromMilliseconds(200)));
        var registry=new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id=>id==zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory=new WindowsMemoryMonitoringZone(_=>throw new InvalidOperationException("No memory demand"),()=>throw new InvalidOperationException("No memory demand"));
        using var sampler=new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(),memory,null!,inventoryZone,
            null!,null!,null!,null!,null!,zone,null!,new AmdIntegratedGpuSensorRoute(),null!,owner,dashboard,registry,
            slots.Owner,slots.Provider,NullLogger<WindowsHardwareMetricSampler>.Instance);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var request = MetricSampleRequest.ForIds(MetricDeviceFields.CreateRequest(dashboard.Current!, device, Names).Ids
            .Concat(MetricDeviceFields.CreateRequest(dashboard.Current!, "static|cpu", ["cpu_all_usage_percent"]).Ids));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        DeviceMetricFieldValue Cpu(HardwareMetricSnapshot snapshot) => Assert.Single(Assert.Single(
            MetricDeviceFields.Read(snapshot, "static|cpu", new HashSet<string> { "cpu_all_usage_percent" })).Fields);
        async Task<HardwareMetricSnapshot> WaitFor(Func<HardwareMetricSnapshot, bool> ready)
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                var current = await sampler.GetSnapshotAsync(request, timeout.Token);
                if (ready(current)) return current;
                await Task.Delay(20, timeout.Token);
            }
            throw new TimeoutException("Original sampling worker did not publish the expected source transition.");
        }
        await sampler.StartAsync(timeout.Token);
        using var subscription = sampler.AcquireSubscription("adlx-cpu-fault-boundary", request, TimeSpan.FromMilliseconds(100));
        try
        {
            // Counter demand must be registered by the real worker, just as it
            // is for the frontend. A direct capture intentionally cannot invent it.
            var before = await WaitFor(snapshot => Cpu(snapshot).Status == SamplingObservationStatus.Current
                && Fields(snapshot, device)[Names[0]].NumericValue is not null);
            var beforeCpu = Cpu(before);
            native.BeforeSelected = () => { entered.Set(); try { Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } finally { finished.Set(); } };
            var after = await WaitFor(snapshot => entered.IsSet && Cpu(snapshot).ObservedAt > beforeCpu.ObservedAt
                && Fields(snapshot, device)[Names[0]].NumericValue is null);
            Assert.All(Fields(after, device).Values, field => { Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue); Assert.NotEqual(SamplingObservationStatus.Current, field.Status); });
            Assert.Equal(SamplingObservationStatus.Current, Cpu(after).Status);
            Assert.NotNull(Cpu(after).NumericValue);
            var reads = native.SelectedReads;
            var next = await WaitFor(snapshot => Cpu(snapshot).ObservedAt > Cpu(after).ObservedAt);
            Assert.Equal(reads, native.SelectedReads);
            Assert.Null(Fields(next, device)[Names[0]].NumericValue);
        }
        finally
        {
            release.Set();
            if (entered.IsSet) Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
            await sampler.StopAsync(CancellationToken.None);
        }
        // After stop this is an explicit inspection of the retained failed-source value.
        Assert.Null(Fields(sampler.ReadLatest(request)!, device)[Names[0]].NumericValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HungReadTimesOutWithoutQueueingOrPublishingLateValues(bool selected)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        void Block() { entered.Set(); try { Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } finally { finished.Set(); } }
        var bridge = new Bridge { Metadata = [Row(10, PnpA)], Values = [Row(10, PnpA, AllFlags)] };
        if (selected) bridge.BeforeSelected = Block; else bridge.AfterMetadata = Block;
        var reader = new AmdAdlxBridgeReader(bridge, TimeSpan.FromMilliseconds(200));
        var original = reader.ReadAsync(Request(9), Inventory(), CancellationToken.None);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Empty(await reader.ReadAsync(Request(9), Inventory(), CancellationToken.None));
            Assert.Empty(await original.WaitAsync(TimeSpan.FromSeconds(3)));
            // Topology shares the same boundary and still supplies OS identities.
            var declarations = reader.ReadDeclarations(Inventory());
            Assert.Equal(2, declarations!.Count);
            Assert.All(declarations, item => Assert.Null(item.VbiosVersion));
            Assert.Equal(1, bridge.MetadataReads);
            Assert.Equal(selected ? 1 : 0, bridge.SelectedReads);
        }
        finally { release.Set(); Assert.True(finished.Wait(TimeSpan.FromSeconds(2))); }
        Assert.Empty(await reader.ReadAsync(Request(9), Inventory(), CancellationToken.None));
        Assert.Equal(1, bridge.MetadataReads);
    }

    [Fact]
    public async Task SubscriberCancellationDoesNotRemoveTheNativeReadDeadline()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var bridge = new Bridge { Metadata = [Row(10, PnpA)], AfterMetadata = () =>
        { entered.Set(); try { Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } finally { finished.Set(); } } };
        var reader = new AmdAdlxBridgeReader(bridge, TimeSpan.FromMilliseconds(200));
        var original = reader.ReadAsync(Request(9), Inventory(), cancel.Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original);
            await Task.Delay(350);
            Assert.Empty(await reader.ReadAsync(Request(9), Inventory(), CancellationToken.None));
        }
        finally { release.Set(); Assert.True(finished.Wait(TimeSpan.FromSeconds(2))); }
        Assert.Empty(await reader.ReadAsync(Request(9), Inventory(), CancellationToken.None));
        Assert.Equal(1, bridge.MetadataReads);
        Assert.Equal(0, bridge.SelectedReads);
    }

    [Fact]
    public async Task NativeFaultRetiresReadBoundaryAndDoesNotCallVendorAgain()
    {
        var bridge = new Bridge { MetadataCount = -5 };
        var reader = new AmdAdlxBridgeReader(bridge);
        Assert.Empty(await reader.ReadAsync(Request(9), Inventory(), CancellationToken.None));
        Assert.Empty(await reader.ReadAsync(Request(9), Inventory(), CancellationToken.None));
        Assert.Equal(2, reader.ReadDeclarations(Inventory())!.Count);
        Assert.Equal(1, bridge.MetadataReads);
    }
}
