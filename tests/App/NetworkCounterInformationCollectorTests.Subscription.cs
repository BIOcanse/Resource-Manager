using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class NetworkCounterInformationCollectorTests
{
    [Fact]
    public async Task OriginalWorkerUsesOnlySelectedInterfaceAndCancellingDemandPreservesCache()
    {
        var api = new Api(); long ticks=0; ulong octets=0;
        api.Transform=row=>{
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(1208),octets+=200);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(1240),ulong.MaxValue);
            return row;
        };
        var zone=new IpHelperNetworkMonitoringZone(api,()=>ticks+=1000,1000);
        var identities=zone.ReadInterfaceIdentities().Identities;
        var target=identities.Single(row=>row.ExactIdentityKey==NetworkAddressInformationCollectorTests.IdentityA);
        using var slots=new HostManagerSamplingSubscriptionTestFixture();
        using var owner=new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider,slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(()=>new(1,NativeMetricSnapshotCatalogTopologyStatus.Complete,[],
                NativeMetricSnapshotCatalogTopologyStatus.Complete,[],NativeMetricSnapshotCatalogTopologyStatus.Complete,[])
                { NetworkInterfaceStatus=NativeMetricSnapshotCatalogTopologyStatus.Complete,NetworkInterfaces=identities }),
            new NetworkEnvironment(Path.Combine(Path.GetTempPath(),$"network-worker-{Guid.NewGuid():N}")),NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None);
        var dashboard=new DashboardMonitoringCatalogState();
        using(var lease=owner.Acquire())dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(lease.Workspace.ReadCommitted(),"CPU","RAM",lease.WorkspaceIdentity));
        var registry=new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id=>id==zone.DisplayName?zone:new MonitoringSourceZone(id)).ToArray());
        var memory=new WindowsMemoryMonitoringZone(_=>throw new InvalidOperationException("Network demand cannot read NUMA"),()=>throw new InvalidOperationException("Network demand cannot read global memory"));
        using var sampler=new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(),memory,null!,null!,null!,null!,null!,null!,null!,null!,null!,new AmdIntegratedGpuSensorRoute(),null!,owner,dashboard,registry,slots.Owner,slots.Provider,NullLogger<WindowsHardwareMetricSampler>.Instance,networkZone:zone);
        var request=MetricDeviceFields.CreateRequest(dashboard.Current!,target.ExactIdentityKey,["network_all_rx_errors_actual","network_all_rx_bytes_per_second_actual"]);
        Assert.Equal(2,request.Ids.Count);
        await sampler.GetSnapshotAsync(request,CancellationToken.None);Assert.Empty(api.Reads);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using(var stream=sampler.SubscribeAsync("network-first",request,TimeSpan.FromMilliseconds(100),timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync()); Check(stream.Current,null);
                Assert.True(await stream.MoveNextAsync()); Check(stream.Current,200);
            }
            await Task.Delay(150,timeout.Token);var reads=api.Reads.Count;
            Check(await sampler.GetSnapshotAsync(request,timeout.Token),200);sampler.ReadLatest(request);
            await Task.Delay(150,timeout.Token);Assert.Equal(reads,api.Reads.Count);
            await using(var stream=sampler.SubscribeAsync("network-second",request,TimeSpan.FromMilliseconds(100),timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync());Check(stream.Current,null);
                Assert.True(await stream.MoveNextAsync());Check(stream.Current,200);
                await sampler.StopAsync(CancellationToken.None);var stopped=api.Reads.Count;
                Check(await sampler.GetSnapshotAsync(request,timeout.Token),200);Assert.Equal(stopped,api.Reads.Count);
            }
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
        Assert.All(api.Reads,luid=>Assert.Equal(target.Luid,luid));Assert.Equal(1,api.DirectoryReads);
        void Check(HardwareMetricSnapshot snapshot,double? rate)
        {
            var fields=MetricDeviceFields.Read(snapshot,target.ExactIdentityKey).Single().Fields;
            Assert.Equal("18446744073709551615",fields.Single(f=>f.Name=="network_all_rx_errors_actual").IntegerValue);
            var speed=fields.Single(f=>f.Name=="network_all_rx_bytes_per_second_actual");Assert.Equal(rate,speed.NumericValue);
            Assert.Equal(rate is null?(ulong?)null:1000UL,speed.SampleDurationMilliseconds);
        }
    }

    private sealed class NetworkEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; }="Tests";
        public string ApplicationName { get; set; }="ResourceManager";
        public string ContentRootPath { get; set; }=root;
        public IFileProvider ContentRootFileProvider { get; set; }=new NullFileProvider();
    }
}
