using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class MetricTopologyDeviceLinkTests
{
    [Fact]
    public void NetworkPortRetainsTheSameQueryGuidAndReportedDescriptionForMetricJoining()
    {
        const string id = @"PCI\VEN_8086&DEV_1234\MixedCase";
        const string guid = "26822419-2d18-4135-ab22-224b30376142";
        var adapter = WindowsNetworkAdapterTopologyReader.CreateAdapter(
            new Dictionary<string, object?>
            {
                ["PnPDeviceID"] = id,
                ["InterfaceGuid"] = "{" + guid + "}",
                ["InterfaceDescription"] = "  Hardware reported adapter  ",
                ["InterfaceName"] = "OS interface name"
            });
        Assert.NotNull(adapter);
        Assert.Equal(guid.ToUpperInvariant(), adapter.InterfaceGuid);
        Assert.Equal("Hardware reported adapter", adapter.InterfaceDescription);
        var connection = WindowsDeviceTopologyReader.CreateNetworkConnection(adapter);
        Assert.NotNull(connection);
        Assert.Equal(adapter.InterfaceGuid, connection.InterfaceGuid);
        Assert.Equal(adapter.InterfaceDescription, connection.InterfaceDescription);
        Assert.Equal("OS interface name", connection.InterfaceName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingOrInvalidNetworkGuidDoesNotInventAPortAssociation(string? guid)
    {
        var adapter = WindowsNetworkAdapterTopologyReader.CreateAdapter(
            new Dictionary<string, object?> { ["PnPDeviceID"] = @"PCI\fixture\network", ["InterfaceGuid"] = guid });
        Assert.NotNull(adapter);
        Assert.Null(adapter.InterfaceGuid);
        Assert.Null(adapter.InterfaceDescription);
        Assert.Null(WindowsDeviceTopologyReader.CreateNetworkConnection(adapter)!.InterfaceGuid);
    }
}
