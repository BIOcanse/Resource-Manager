using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
    [Theory]
    [InlineData("HDAUDIO", null)] [InlineData("BTH", null)] [InlineData("BTHENUM", null)]
    [InlineData("ACPI", "UcmUcsiAcpiClient")] [InlineData("PCI", "stornvme")]
    [InlineData("PCI", "storahci")] [InlineData("ACPI", "hidi2c")]
    [InlineData("ACPI", null)] [InlineData("PCI", null)] [InlineData("USB", null)]
    [InlineData("USB4", null)] [InlineData("ROOT", null)]
    public async Task AuthoritativeTransportAndRawEvidenceSurviveProductionDecoder(string enumerator, string? service)
    {
        var node = DeviceTopologyTransportTests.Node("not-a-bus-prefix", enumerator, service) with {
            BusTypeGuid = enumerator == "PCI" ? DeviceTopologyTransportClassifier.PciBus : null
        };
        var port = DeviceTopologyTransportClassifier.Apply(DeviceInformationTestData.Port(node.DeviceId), DeviceTopologyTransportTests.Snapshot(node));
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot(port) };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("topologySnapshot")
            .GetProperty("ports")[0].GetProperty("transport");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(port.Transport, WebJson), decoded));
    }

    [Fact]
    public async Task ParentChainAndIndependentUsbPortEvidenceSurviveProductionDecoder()
    {
        var child = DeviceTopologyTransportTests.Node("child", "HID", parent: "parent");
        var parent = DeviceTopologyTransportTests.Node("parent", "ACPI", "hidi2c") with {
            BusTypeGuid = DeviceTopologyTransportClassifier.AcpiBus
        };
        var native = DeviceTopologyTransportTests.Snapshot(child, parent, DeviceTopologyTransportTests.Node("usb-device"));
        var hidPort = DeviceTopologyTransportClassifier.Apply(DeviceInformationTestData.Port("child"), native);
        var usb = WindowsDeviceTopologyReader.CreateUsbConnection(DeviceTopologyUsbClassFactsTests.Port(null), false);
        var usbPort = DeviceTopologyTransportClassifier.Apply(DeviceInformationTestData.Port("usb-device") with { Usb = usb }, native);
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot(hidPort, usbPort) };
        var ports = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("topologySnapshot").GetProperty("ports");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(hidPort.Transport, WebJson), ports[0].GetProperty("transport")));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(usbPort.Transport, WebJson), ports[1].GetProperty("transport")));
    }
}
