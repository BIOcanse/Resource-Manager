using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaPerformanceStateCollectorTests
{
    private const string Name = "gpu_nvidia_performance_state";
    private static readonly HashSet<MetricFieldCode> Requested = [MetricFieldCode.NvmlPerformanceState];
    public static IEnumerable<object[]> ValidStates => Enumerable.Range(0, 16).Select(static state => new object[] { (uint)state });

    [Theory]
    [MemberData(nameof(ValidStates))]
    public void ExactDriverEnumIncludesZeroAndAllPstates(uint state)
    {
        var calls = 0;
        var result = NvidiaNvmlReader.ReadAdditionalMetrics((IntPtr)42, Requested, Query);
        Assert.Equal(1, calls);
        Assert.Equal((ulong)state, result.Integers[MetricFieldCode.NvmlPerformanceState]);
        Assert.Null(result.Values[MetricFieldCode.NvmlPerformanceState]);
        Assert.Empty(result.ObservedAt);
        int Query(IntPtr device, out uint value) { Assert.Equal((IntPtr)42, device); calls++; value = state; return 0; }
    }

    [Theory]
    [InlineData(0, 16U)]
    [InlineData(0, 32U)]
    [InlineData(0, uint.MaxValue)]
    [InlineData(3, 0U)]
    [InlineData(15, 15U)]
    public void UnknownReservedOrFailedQueriesHaveNoCurrentInteger(int status, uint state)
    {
        var result = NvidiaNvmlReader.ReadAdditionalMetrics((IntPtr)42, Requested, Query);
        Assert.Empty(result.Integers); Assert.Null(result.Values[MetricFieldCode.NvmlPerformanceState]);
        int Query(IntPtr device, out uint value) { value = state; return status; }
    }

    [Fact]
    public void NoDemandNeverCallsAndMissingExportPublishesEmpty()
    {
        var result = NvidiaNvmlReader.ReadAdditionalMetrics((IntPtr)42, new HashSet<MetricFieldCode>(), Missing);
        Assert.Empty(result.Integers); Assert.Empty(result.Values);
        result = NvidiaNvmlReader.ReadAdditionalMetrics((IntPtr)42, Requested, Missing);
        Assert.Empty(result.Integers); Assert.Null(result.Values[MetricFieldCode.NvmlPerformanceState]);
        Assert.Contains(MetricFieldCode.NvmlPerformanceState, NvidiaNvmlReadRequest.All.AdditionalMetrics);
        int Missing(IntPtr device, out uint value) { throw new EntryPointNotFoundException(); }
    }

    [Fact]
    public void OriginalNvmlCompletionPublishesIndependentDevicesAndClearsFailure()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,
            Path.Combine(Path.GetTempPath(), $"pstate-native-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9,"NVIDIA A","gpu-a",0xA1,1,1,true), new(3,"NVIDIA B","gpu-b",0xB1,1,1,true),
             new(7,"AMD","gpu-amd",0xC1,1,2,true)], [], [], workspace.CreateCatalogHandleMap());
        var selected = catalog.MetricBindings.Values.Where(static binding => binding.FieldCode == MetricFieldCode.NvmlPerformanceState).ToArray();
        Assert.Equal(2, selected.Length);
        Assert.DoesNotContain(selected, static binding => binding.DisplayIndex == 7);
        Assert.All(selected, static binding => { Assert.Equal("nvidia.nvml", binding.SourceId); Assert.Equal("unsigned64", binding.Field!.ValueKind); });
        Assert.InRange(workspace.Capacity.ResidentByteCount,1UL,64UL * 1024 * 1024);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,1,
            NativeMetricSnapshotZoneMode.Normal,NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        for (var phase = 0; phase < 3; phase++)
        {
            var at = now.AddSeconds(phase + 1);
            var plan = workspace.PlanCollection(selected.Select(static binding => binding.MetricHandle).ToArray(), modes, false, at);
            var source = Assert.Single(plan.Sources);
            var completion = WindowsHardwareMetricSampler.CreateNvmlCompletion(plan,source,new(source.SourceHandle,1,(ulong)phase+1),at,
                [Gpu(9,phase == 1 ? null : 0), Gpu(3,15)]);
            workspace.CompleteSource(plan,completion,at);
            var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
            foreach (var binding in selected)
            {
                var value = Field(snapshot, binding.DeviceIdentity);
                var expected = binding.DisplayIndex == 3 ? "15" : phase == 1 ? null : "0";
                Assert.Equal(expected is null ? null : MetricInteger.Parse(expected), value.IntegerValue);
                Assert.Null(value.NumericValue); Assert.Null(value.BooleanValue); Assert.Null(value.BytesValue);
                Assert.Equal("enum",value.Unit); Assert.Equal("unsigned64",value.ValueKind);
                Assert.Equal(expected is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,value.Status);
                var json = JsonSerializer.Serialize(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Contains(expected is null ? "\"integerValue\":null" : $"\"integerValue\":\"{expected}\"",json);
                Assert.Equal([binding.MetricId],MetricDeviceFields.CreateRequest(snapshot,binding.DeviceIdentity,[Name]).Ids);
            }
        }
    }

    [Fact]
    public async Task OriginalWorkerReadsOnlyDemandAndGetUsesCacheAfterCancelAndStop()
    {
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        var root = Path.Combine(Path.GetTempPath(),$"pstate-worker-{Guid.NewGuid():N}");
        var adapters = new WindowsGpuAdapter[] { NvidiaAdapter(9,1,0xA1,PnpA), NvidiaAdapter(3,2,0xB1,PnpB) };
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current,1,DateTimeOffset.UtcNow.UtcTicks,2,0,0,1,adapters);
        var inventoryZone = new WindowsGpuAdapterOrderMonitoringZone(() => inventory);
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider,slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => new(1,NativeMetricSnapshotCatalogTopologyStatus.Complete,
                RecordedGpuIdentities(inventory),
                NativeMetricSnapshotCatalogTopologyStatus.Complete,[],NativeMetricSnapshotCatalogTopologyStatus.Complete,[])),
            new Environment(root),NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None);
        var dashboard = new DashboardMonitoringCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var device = dashboard.Current!.FieldBindings.Values.Single(binding => binding.Field.Code == MetricFieldCode.NvmlPerformanceState
            && binding.RequestAddress.Contains(Name)
            && binding.DeviceIdentity == RecordedGpuIdentities(inventory).Single(static gpu => gpu.DisplayIndex == 9).ExactIdentityKey).DeviceIdentity;
        var reads = 0;
        var zone = new NvidiaNvmlMonitoringZone((request,current,token) => {
            Assert.Equal(inventory,current); Assert.Equal([9],request.DisplayIndexes!.Order().ToArray());
            Assert.Equal(Requested,request.AdditionalMetrics.ToHashSet());
            Assert.False(request.IncludeUsage || request.IncludeGraphicsClocks || request.IncludeMemory || request.IncludeMemoryClock
                || request.IncludePowerUsage || request.IncludePowerLimit || request.IncludeTemperature || request.IncludeFan || request.IncludeElectricalState);
            Interlocked.Increment(ref reads); return Task.FromResult<IReadOnlyList<GpuMetrics>>([Gpu(9,0)]);
        });
        var memory = new WindowsMemoryMonitoringZone(_ => throw new InvalidOperationException("No NUMA demand"),
            () => throw new InvalidOperationException("No memory demand"));
        var registry = new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id =>
            id == zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(),memory,null!,inventoryZone,
            null!,null!,null!,zone,null!,null!,null!,new AmdIntegratedGpuSensorRoute(),null!,owner,dashboard,registry,
            slots.Owner,slots.Provider,NullLogger<WindowsHardwareMetricSampler>.Instance);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!,device,[Name]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Null(Field(await sampler.GetSnapshotAsync(request,timeout.Token),device).IntegerValue); Assert.Equal(0,reads);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using (var stream = sampler.SubscribeAsync("pstate-consumer",request,TimeSpan.FromMilliseconds(100),timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync()); Assert.Equal(MetricInteger.Parse("0"), Field(stream.Current,device).IntegerValue);
                Assert.Equal(MetricInteger.Parse("0"), Field(sampler.ReadLatest(request)!,device).IntegerValue);
            }
            await Task.Delay(150,timeout.Token); var stoppedReads = Volatile.Read(ref reads);
            Assert.True(stoppedReads > 0);
            Assert.Equal(MetricInteger.Parse("0"), Field(await sampler.GetSnapshotAsync(request,timeout.Token),device).IntegerValue);
            await Task.Delay(150,timeout.Token); Assert.Equal(stoppedReads,Volatile.Read(ref reads));
            await sampler.StopAsync(CancellationToken.None);
            Assert.Equal(MetricInteger.Parse("0"), Field(await sampler.GetSnapshotAsync(request,timeout.Token),device).IntegerValue);
            Assert.Equal(stoppedReads,Volatile.Read(ref reads));
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    // The live GPU check uses the topology owner's physical identity projection, so the recorded catalog
    // is derived from the same inventory instead of hand-written identities.
    private const string PnpA = @"PCI\VEN_10DE&DEV_0001&SUBSYS_00000000&REV_A1\4&1&0&0008";
    private const string PnpB = @"PCI\VEN_10DE&DEV_0002&SUBSYS_00000000&REV_A1\4&2&0&0010";
    private static IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> RecordedGpuIdentities(WindowsGpuAdapterInventoryRead inventory)
    {
        var captured = ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsHardwareMetricTopologySource.CaptureGpuIdentities(inventory);
        Assert.Null(captured.FailureCode);
        return captured.Rows;
    }
    private static WindowsGpuAdapter NvidiaAdapter(int index,uint device,uint luid,string pnp) =>
        new(index,index == 9 ? "NVIDIA A" : "NVIDIA B",0x10DE,device,0,new() { LowPart=luid },false,8UL<<30,WindowsGpuAdapterKind.Dedicated)
        {
            PhysicalIdentityStatus=SamplingObservationStatus.Current,
            PhysicalIdentities=[new(0,pnp,WindowsGpuPnpKind.Pci,true,false,0,(uint)index,0,0,0x10DE,device,0,0,1,
                WindowsGpuIdentityEvidence.KmtLuidOpen|WindowsGpuIdentityEvidence.KmtHardwarePnpKey|WindowsGpuIdentityEvidence.CmExactDevNode|
                WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip|WindowsGpuIdentityEvidence.KmtDeviceIds|WindowsGpuIdentityEvidence.CmPciAddress,
                WindowsGpuPhysicalIdentityStatus.Bound,0)]
        };

    private static GpuMetrics Gpu(int index,ulong? state) => new(index,"NVIDIA",0,0,0,0,0,0,0,0,null!) {
        AdditionalIntegerReadings = state is { } value ? new Dictionary<MetricFieldCode,ulong> { [MetricFieldCode.NvmlPerformanceState]=value } : new(),
        AdditionalReadings = new Dictionary<MetricFieldCode,double?> { [MetricFieldCode.NvmlPerformanceState]=null } };
    private static DeviceMetricFieldValue Field(HardwareMetricSnapshot snapshot,string device) =>
        Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot,device,new HashSet<string> { Name })).Fields);
    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
