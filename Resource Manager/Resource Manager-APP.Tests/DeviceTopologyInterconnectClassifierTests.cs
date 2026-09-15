using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyInterconnectClassifierTests
{
    [Theory]
    [InlineData(
        "UcmUcsiAcpiClient",
        DeviceInterconnectKinds.UcsiConnectorManager,
        "UCSI",
        BackendMessageCodes.DeviceTopology.RoleUcsiConnectorManager,
        "UcmUcsiAcpiClient")]
    [InlineData(
        "Usb4HostRouter",
        DeviceInterconnectKinds.Usb4HostRouter,
        "USB4",
        BackendMessageCodes.DeviceTopology.RoleUsb4HostRouter,
        "Usb4HostRouter")]
    [InlineData(
        "usb4devicerouter.sys",
        DeviceInterconnectKinds.Usb4DeviceRouter,
        "USB4 / Thunderbolt 3",
        BackendMessageCodes.DeviceTopology.RoleUsb4DeviceRouter,
        "Usb4DeviceRouter")]
    [InlineData(
        "USB4P2PNETADAPTER",
        DeviceInterconnectKinds.Usb4P2PNetwork,
        "USB4NET",
        BackendMessageCodes.DeviceTopology.RoleUsb4P2PNetwork,
        "Usb4P2PNetAdapter")]
    public void Classify_UsesExactWindowsServiceRole(
        string service,
        string expectedKind,
        string expectedTechnology,
        byte expectedRoleCode,
        string expectedServiceName)
    {
        var result = DeviceTopologyInterconnectClassifier.Classify(service);

        Assert.NotNull(result);
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedTechnology, result.Technology);
        Assert.Equal(BackendMessageDomains.DeviceTopology, result.Role.Domain);
        Assert.Equal(expectedRoleCode, result.Role.Code);
        Assert.Equal(
            BackendMessageCodes.DeviceTopology.InterconnectEvidencePnpService,
            result.Evidence.Code);
        Assert.Equal(expectedServiceName, Assert.Single(result.Evidence.Args));
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
