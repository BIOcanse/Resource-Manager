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
    // Explicit cached-current inspection for startup and post-cancellation assertions.
    private static string ReadPublishedNetworkFields(DeviceTopologySnapshotProvider provider, Microsoft.AspNetCore.Builder.WebApplication app)
        => JsonSerializer.Serialize(provider.ReadNetworkEndpointInformation(), ResponseJson(app));
    [Fact]
    public async Task EndpointHttpKeepsRawPidExactCreationAndFamilyFailuresThenRecoversOrEmpties()
    {
        var api=new NetworkEndpointInformationCollectorTests.Api();
        var collector=new WindowsNetworkEndpointInformationReader(api);
        var reader=new Reader(DeviceInformationTestData.Snapshot());
        using var fixture=new Fixture(reader, networkEndpoints: collector);await using var app=await StartAsync(fixture.Provider);using var client=Client(app);
        using var initial=JsonDocument.Parse(ReadPublishedNetworkFields(fixture.Provider, app));
        Assert.Equal(0,api.Reads);Assert.Equal(0,api.Opens);
        for(var phase=0;phase<4;phase++)
        {
            api.ReadTable=(kind,_)=>phase==3?new byte[4]:phase==1&&kind==WindowsNetworkEndpointTableKind.UdpIpv6?null:NetworkEndpointInformationCollectorTests.Raw(kind);
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/network-endpoints/fields");var reads=api.Reads;var opens=api.Opens;
            var current=fixture.Provider.ReadNetworkEndpointInformation();
            using var result=JsonDocument.Parse(sampledHttpFields1);
            var tcp4=Field(result.RootElement,"machine","network_all_tcp_ipv4_endpoint_reported");
            Assert.Equal("windows.ip-helper",tcp4.GetProperty("definition").GetProperty("sourceId").GetString());
            Assert.Equal("array",tcp4.GetProperty("definition").GetProperty("valueKind").GetString());
            Assert.Equal("endpoint[]",tcp4.GetProperty("definition").GetProperty("unit").GetString());
            if(phase==3) Assert.Equal(0,tcp4.GetProperty("value").GetArrayLength());
            else {
                var listen=Assert.Single(tcp4.GetProperty("value").EnumerateArray(),static row=>row.GetProperty("stateRaw").GetUInt32()==2);
                Assert.Equal(JsonValueKind.Null,listen.GetProperty("remote").ValueKind);
                Assert.Equal(101U,listen.GetProperty("owningProcessIdReported").GetUInt32());
                Assert.Equal(55535,listen.GetProperty("local").GetProperty("port").GetInt32());
                Assert.Equal("18446744073709551598",listen.GetProperty("processIdentity").GetProperty("creationWindowsFileTime100Ns").GetString());
                var denied=Assert.Single(tcp4.GetProperty("value").EnumerateArray(),static row=>row.GetProperty("owningProcessIdReported").GetUInt32()==uint.MaxValue);
                Assert.Equal(JsonValueKind.Null,denied.GetProperty("processIdentity").ValueKind);
            }
            var udp6=Field(result.RootElement,"machine","network_all_udp_ipv6_endpoint_reported").GetProperty("value");
            if(phase==1) Assert.Equal(JsonValueKind.Null,udp6.ValueKind);else Assert.Equal(phase==3?0:3,udp6.GetArrayLength());
            if(phase!=3) {
                var udp4=Field(result.RootElement,"machine","network_all_udp_ipv4_endpoint_reported").GetProperty("value");
                Assert.Equal(3,udp4.GetArrayLength());Assert.All(udp4.EnumerateArray(),static row=> {
                    Assert.False(row.TryGetProperty("stateRaw",out _));Assert.False(row.TryGetProperty("remote",out _)); });
                var tcp6=Field(result.RootElement,"machine","network_all_tcp_ipv6_endpoint_reported").GetProperty("value");
                var scoped=Assert.Single(tcp6.EnumerateArray(),static row=>row.GetProperty("owningProcessIdReported").GetUInt32()==202);
                Assert.Equal(0x11223344U,scoped.GetProperty("local").GetProperty("scopeIdRaw").GetUInt32());
                Assert.Equal(0xf1234567U,scoped.GetProperty("remote").GetProperty("scopeIdRaw").GetUInt32());
            }
            Assert.Equal(reads,api.Reads);Assert.Equal(opens,api.Opens);Assert.Same(current,fixture.Provider.ReadNetworkEndpointInformation());
        }
    }

    [Fact]
    public async Task PersistedTopologyDoesNotSupplyTheNetworkItemUntilItsOwnFreshCapture()
    {
        var snapshot=DeviceInformationTestData.Snapshot() with { NetworkEndpointInformation=new WindowsNetworkEndpointInformationReader(new NetworkEndpointInformationCollectorTests.Api()).Read() };
        var persisted=new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion,"test",9,new DeviceTopologySemanticComparer().ComputeHash(snapshot),snapshot);
        var collector=new WindowsNetworkEndpointInformationReader(new NetworkEndpointInformationCollectorTests.Api());
        var reader=new Reader(snapshot);using var fixture=new Fixture(reader,persisted, networkEndpoints: collector);
        await using var app=await StartAsync(fixture.Provider);using var client=Client(app);
        using var cached=JsonDocument.Parse(ReadPublishedNetworkFields(fixture.Provider, app));
        string[] names=["network_all_tcp_ipv4_endpoint_reported","network_all_tcp_ipv6_endpoint_reported","network_all_udp_ipv4_endpoint_reported","network_all_udp_ipv6_endpoint_reported"];
        Assert.Equal(0, cached.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(0,reader.ReadCount);var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/network-endpoints/fields");
        using var current=JsonDocument.Parse(sampledHttpFields2);
        foreach(var name in names) Assert.Equal(3,Field(current.RootElement,"machine",name).GetProperty("value").GetArrayLength());
    }

    [Fact]
    public async Task OriginalWorkerAndUnifiedStreamCollectEndpointsOnlyWithDemandAndCancellationKeepsCurrentDisplay()
    {
        var api=new NetworkEndpointInformationCollectorTests.Api();var collector=new WindowsNetworkEndpointInformationReader(api);
        var reader=new Reader(DeviceInformationTestData.Snapshot());
        using var fixture=new Fixture(reader, networkEndpoints: collector);await using var app=await StartAsync(fixture.Provider);using var client=Client(app);
        using var initial=JsonDocument.Parse(ReadPublishedNetworkFields(fixture.Provider, app));
        Assert.Equal(0,api.Reads);Assert.Equal(0,api.Opens);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream") { Content=JsonContent.Create(new { version=1,subscriptions=new[] {
            new { id="endpoint-worker",path="/api/device-topology/network-endpoints/fields/subscribe?intervalMs=1000" } } }) };
        using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var stream=new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message=JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var value=Field(message.RootElement.GetProperty("value"),"machine","network_all_tcp_ipv4_endpoint_reported").GetProperty("value");
        Assert.Equal(3,value.GetArrayLength());Assert.True(api.Reads>=8);Assert.True(api.Opens>=4);
        Assert.All(api.Leases,static lease=>Assert.Equal(1,lease.Disposals));
        await timeout.CancelAsync();response.Dispose();await fixture.WaitForNoDemandAndIdleAsync();
        var reads=api.Reads;var opens=api.Opens;var current=fixture.Provider.ReadNetworkEndpointInformation();
        using var cached=JsonDocument.Parse(ReadPublishedNetworkFields(fixture.Provider, app));
        Assert.Equal(value.GetRawText(),Field(cached.RootElement,"machine","network_all_tcp_ipv4_endpoint_reported").GetProperty("value").GetRawText());
        Assert.Equal(reads,api.Reads);Assert.Equal(opens,api.Opens);Assert.Same(current,fixture.Provider.ReadNetworkEndpointInformation());
        await app.StopAsync();
    }
}
