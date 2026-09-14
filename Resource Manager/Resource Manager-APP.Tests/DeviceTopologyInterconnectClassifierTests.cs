using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyInterconnectClassifierTests
{
    [Theory]
    [InlineData("UcmUcsiAcpiClient", DeviceInterconnectKinds.UcsiConnectorManager, "UCSI")]
    [InlineData("Usb4HostRouter", DeviceInterconnectKinds.Usb4HostRouter, "USB4")]
    [InlineData("usb4devicerouter.sys", DeviceInterconnectKinds.Usb4DeviceRouter, "USB4 / Thunderbolt 3")]
    [InlineData("USB4P2PNETADAPTER", DeviceInterconnectKinds.Usb4P2PNetwork, "USB4NET")]
    public void Classify_UsesExactWindowsServiceRole(string service, string expectedKind, string expectedTechnology)
    {
        var result = DeviceTopologyInterconnectClassifier.Classify(service);

        Assert.NotNull(result);
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedTechnology, result.Technology);
        Assert.Contains("Windows PnP 服务", result.Evidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UcmUcsiCx")]
    [InlineData("VendorUsb4Controller")]
    [InlineData("Usb4HostRouterFilter")]
    public void Classify_DoesNotInferUnknownOrPartialServiceNames(string? service)
    {
        Assert.Null(DeviceTopologyInterconnectClassifier.Classify(service));
    }

}
