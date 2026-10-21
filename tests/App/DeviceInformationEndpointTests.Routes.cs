using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task RouteHttpPublishesFullRecordsAndClearsOnlyFailedRoutesThenRecoversOrEmpties()
    {
        var api = new NetworkAddressInformationCollectorTests.Api();
        var collector = new WindowsNetworkInterfaceInformationReader(api);
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { NetworkInterfaceInformation = collector.Read() });
        using var fixture = new Fixture(reader);
        await using var app = await StartAsync(fixture.Provider);
        using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(0, api.RouteReads);
        for (var phase = 0; phase < 4; phase++)
        {
            api.Routes = phase == 1 ? null : phase == 3 ? new byte[8] : NetworkRouteInformationCollectorTests.Raw();
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            var current = fixture.Provider.ReadState().InformationFields;
            var count = api.RouteReads;
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var field = Field(result.RootElement, NetworkAddressInformationCollectorTests.IdentityA, "network_all_route_reported");
            var definition = field.GetProperty("definition");
            Assert.Equal("windows.ip-helper", definition.GetProperty("sourceId").GetString());
            Assert.Equal("array", definition.GetProperty("valueKind").GetString());
            Assert.Equal("route[]", definition.GetProperty("unit").GetString());
            var value = field.GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
            else if (phase == 3) Assert.Equal(0, value.GetArrayLength());
            else
            {
                Assert.Equal(3, value.GetArrayLength());
                Assert.Equal("0.0.0.0", value[0].GetProperty("destinationAddress").GetString());
                Assert.Equal(0, value[0].GetProperty("destinationPrefixLength").GetInt32());
                Assert.Equal("198.51.100.1", value[0].GetProperty("nextHopAddress").GetString());
                Assert.Equal(255, value[0].GetProperty("sitePrefixLengthRaw").GetInt32());
                Assert.Equal(uint.MaxValue, value[0].GetProperty("validLifetimeSeconds").GetUInt32());
                Assert.Equal(uint.MaxValue, value[0].GetProperty("routeMetricOffsetRaw").GetUInt32());
                Assert.Equal(0xf0000041U, value[0].GetProperty("protocolRaw").GetUInt32());
                Assert.Equal(0xf0000043U, value[0].GetProperty("originRaw").GetUInt32());
                Assert.True(value[0].GetProperty("loopback").GetBoolean());
                Assert.False(value[0].GetProperty("autoconfigureAddress").GetBoolean());
                Assert.Equal(55U, value[2].GetProperty("destinationSocketScopeId").GetUInt32());
                Assert.Equal(77U, value[2].GetProperty("nextHopSocketScopeId").GetUInt32());
            }
            Assert.Equal(2, Field(result.RootElement, NetworkAddressInformationCollectorTests.IdentityA, "network_all_unicast_address_reported").GetProperty("value").GetArrayLength());
            Assert.Equal("FEDCBA9876543210", Field(result.RootElement, NetworkAddressInformationCollectorTests.IdentityA, "network_all_interface_luid_reported").GetProperty("value").GetString());
            Assert.Equal(count, api.RouteReads); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        }
    }
}
