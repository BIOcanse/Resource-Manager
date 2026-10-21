using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task DiskLayoutHttpHasExactPartitionsAndRelationsFailureIsolationAndCompleteDirectoryRecovery()
    {
        var api=new DiskLayoutInformationCollectorTests.Api();var collector=new WindowsDiskLayoutInformationReader(api);
        var volume=new DeviceTopologyVolumeSpaceInformation(VolumeSpaceInformationCollectorTests.IdA){DiskExtents=[new(2,4096,8192),new(5,4096,8192)]};
        var reader=new Reader(()=>DeviceInformationTestData.Snapshot() with {DiskLayoutInformation=collector.Read(),VolumeSpaceInformation=[volume]});
        using var fixture=new Fixture(reader);await using var app=await StartAsync(fixture.Provider);using var client=Client(app);
        _=await client.GetStringAsync("/api/device-topology/fields");Assert.Empty(api.Reads);
        for(var phase=0;phase<5;phase++)
        {
            api.FailFirst=phase==1;api.Directory=phase==2?null:phase==3?[]:[DiskLayoutInformationCollectorTests.DiskA,DiskLayoutInformationCollectorTests.DiskB];
            await fixture.Provider.SampleNowAsync(CancellationToken.None);var reads=api.Reads.Count;
            using var document=JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));var root=document.RootElement;
            var disks=root.GetProperty("devices").EnumerateArray().Where(item=>item.GetProperty("objectType").GetString()=="os_disk").ToArray();
            Assert.Equal(phase==3?0:2,disks.Length);
            if(phase==2) {AssertDiskLayoutEmpty(root,DiskLayoutInformationCollectorTests.IdA);AssertDiskLayoutEmpty(root,DiskLayoutInformationCollectorTests.IdB);}
            else if(phase!=3)
            {
                if(phase==1)AssertDiskLayoutEmpty(root,DiskLayoutInformationCollectorTests.IdA);
                else
                {
                    var entries=Field(root,DiskLayoutInformationCollectorTests.IdA,"storage_all_partition_layout_reported").GetProperty("value");
                    Assert.Equal("9007199254740993",entries[1].GetProperty("startingOffsetBytes").GetString());
                    Assert.Equal("18446744073709551615",entries[1].GetProperty("gpt").GetProperty("attributes").GetString());
                    var relation=Field(root,DiskLayoutInformationCollectorTests.IdA,"storage_all_partition_relation_actual").GetProperty("value");
                    Assert.True(Assert.Single(relation.EnumerateArray()).GetProperty("rangesEqual").GetBoolean());
                }
                Assert.Equal("5",Field(root,DiskLayoutInformationCollectorTests.IdB,"storage_all_os_disk_number_reported").GetProperty("value").GetString());
            }
            Assert.Equal(reads,api.Reads.Count);Assert.Equal("Machine Model",Field(root,"machine","machine_all_model_declared").GetProperty("value").GetString());
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PersistedDiskLayoutsStartEmptyAndWholeCaptureFailureDoesNotShowHistoricalDiskNumbers()
    {
        var disk=new DeviceTopologyDiskLayoutInformation(DiskLayoutInformationCollectorTests.IdA){OsDiskNumberReported=2,Layout=WindowsDiskLayoutInformationDecoder.Layout(DiskLayoutInformationCollectorTests.Frame("gpt"))};
        var raw=DeviceInformationTestData.Snapshot() with {DiskLayoutInformation=[disk],VolumeSpaceInformation=[]};
        var persisted=new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion,"test",11,new DeviceTopologySemanticComparer().ComputeHash(raw),raw);
        var reader=new Reader(raw,new IOException("failed"),raw);using var fixture=new Fixture(reader,persisted);await using var app=await StartAsync(fixture.Provider);using var client=Client(app);
        using var initial=JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));AssertDiskLayoutEmpty(initial.RootElement,disk.DeviceIdentity);Assert.Equal(0,reader.ReadCount);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);using var fresh=JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("2",Field(fresh.RootElement,disk.DeviceIdentity,"storage_all_os_disk_number_reported").GetProperty("value").GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);using var failed=JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));AssertDiskLayoutEmpty(failed.RootElement,disk.DeviceIdentity);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);using var recovered=JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("1",Field(recovered.RootElement,disk.DeviceIdentity,"storage_all_partition_style_reported").GetProperty("value").GetString());await app.StopAsync();
    }

    [Fact]
    public async Task UnifiedDiskLayoutStreamSamplesOriginalOwnerAndCancellationRetainsOnlyCachedDisplay()
    {
        var api=new DiskLayoutInformationCollectorTests.Api();var collector=new WindowsDiskLayoutInformationReader(api);
        var reader=new Reader(()=>DeviceInformationTestData.Snapshot() with {DiskLayoutInformation=collector.Read(),VolumeSpaceInformation=[]});
        using var fixture=new Fixture(reader);await using var app=await StartAsync(fixture.Provider);using var client=Client(app);
        _=await client.GetStringAsync("/api/device-topology/fields");Assert.Empty(api.Reads);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));using var request=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream")
        {Content=JsonContent.Create(new{version=1,subscriptions=new[]{new{id="disk-layout",path="/api/device-topology/fields/subscribe?intervalMs=1000"}}})};
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var stream=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));using var message=JsonDocument.Parse((await stream.ReadLineAsync(timeout.Token))!);
        var value=message.RootElement.GetProperty("value");var entry=Field(value,DiskLayoutInformationCollectorTests.IdA,"storage_all_partition_layout_reported");
        Assert.Equal("windows.disk-layout",entry.GetProperty("definition").GetProperty("sourceId").GetString());Assert.Equal("9007199254740993",entry.GetProperty("value")[1].GetProperty("startingOffsetBytes").GetString());
        Assert.True(api.Reads.Count>=2);await timeout.CancelAsync();response.Dispose();await Task.Delay(250);var reads=api.Reads.Count;var cached=fixture.Provider.ReadState().InformationFields;
        using var displayed=JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));Assert.Equal("2",Field(displayed.RootElement,DiskLayoutInformationCollectorTests.IdA,"storage_all_os_disk_number_reported").GetProperty("value").GetString());
        await Task.Delay(1300);Assert.Equal(reads,api.Reads.Count);Assert.Same(cached,fixture.Provider.ReadState().InformationFields);await app.StopAsync();
    }

    private static void AssertDiskLayoutEmpty(JsonElement root,string identity)
    {
        var disk=Assert.Single(root.GetProperty("devices").EnumerateArray(),item=>item.GetProperty("deviceIdentity").GetString()==identity);
        var fields=disk.GetProperty("fields").EnumerateArray().Where(item=>item.GetProperty("definition").GetProperty("sourceId").GetString()=="windows.disk-layout").ToArray();
        Assert.Equal(11,fields.Length);Assert.All(fields,item=>Assert.Equal(JsonValueKind.Null,item.GetProperty("value").ValueKind));
    }
}
