using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Usb;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace Resource_Manager_APP.Tests;

public sealed partial class BackendFrontendWireAcceptanceTests
{
    [Theory]
    [InlineData(10, true, "mobile_workstation")]
    [InlineData(7, false, "workstation")]
    [InlineData(35, false, "desktop")]
    [InlineData(2, false, "unknown")]
    public async Task TopologyMachineIdentitySurvivesProductionDecoder(byte code, bool battery, string expected)
    {
        var identity = DeviceTopologyMachineIdentityTests.Resolve([code], battery,
            gpus: code is 10 or 7 ? ["NVIDIA RTX PRO 6000"] : null);
        var snapshot = DeviceInformationTestData.Snapshot() with { MachineIdentity = identity };
        var state = DeviceTopologySnapshotState.Warming with {
            State = DeviceTopologySnapshotStatus.Ready, Snapshot = snapshot,
            ContentGeneration = 1, StateRevision = 1
        };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("machineIdentity");
        Assert.Equal(expected, decoded.GetProperty("formFactor").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(identity, WebJson), decoded));
    }

    [Fact]
    public async Task MissingMachineEvidenceSurvivesProductionDecoderAsTypedNullWithReason()
    {
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot() with { MachineIdentity = DeviceTopologyMachineIdentityTests.Resolve(null, null) } };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("machineIdentity");
        Assert.Equal("unknown", decoded.GetProperty("formFactor").GetString());
        var chassis = decoded.GetProperty("evidence").GetProperty("chassisTypes");
        Assert.Equal(JsonValueKind.Null, chassis.GetProperty("value").ValueKind);
        Assert.Equal("source-unavailable", chassis.GetProperty("reason").GetString());
    }

    private static JsonElement ProductionTopologyWire(DeviceTopologySnapshotState state)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        return JsonSerializer.SerializeToElement(state, options);
    }

    [Fact]
    public async Task GraphicsDockEvidenceSurvivesWireAndCannotMakePortableMachineWorkstation()
    {
        const string name = "NVIDIA RTX PRO 6000";
        var evidence = DeviceTopologyMachineIdentityTests.Resolve([10], true, gpus: [name]).Evidence with {
            GpuAttachments = new([new(name, true, 0x400)], null)
        };
        var gpu = new DeviceTopologyGpuInformation("PCI\\GPU", "0000000000000001", name, "10DE", "1234", null)
            { AdapterTypeFlags = 0x400 };
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot() with {
                MachineIdentity = DeviceTopologyMachineClassifier.Default.Classify(evidence), GpuInformation = [gpu]
            } };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("topologySnapshot");
        Assert.Equal("laptop", decoded.GetProperty("machineIdentity").GetProperty("formFactor").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new[] { gpu }, WebJson), decoded.GetProperty("gpuInformation")));
        Assert.Equal("graphics_dock", decoded.GetProperty("gpuInformation")[0].GetProperty("attachmentKind").GetString());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task WlanSourceStandardsSurviveProductionDecoderAsFactsOrNull(bool available)
    {
        var wireless = available ? new DeviceTopologyWirelessConnection("wlan-api", 1, [7, 8, 10],
            ["802.11n", "802.11ac", "802.11ax"], 8, "802.11ac") : null;
        var network = new DeviceTopologyNetworkConnection("Misleading Wi-Fi 7 Name", "connected", null, null,
            null, null, true, null) { Wireless = wireless };
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port("NET") with { Network = network }) };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("topologySnapshot")
            .GetProperty("ports")[0].GetProperty("network").GetProperty("wireless");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(wireless, WebJson), decoded));
    }

    [Fact]
    public async Task IndependentHostPeerSurvivesWireWithoutBecomingLocalCpuGpuOrDock()
    {
        var identity = DeviceTopologyMachineIdentityTests.Resolve([10], true);
        var peer = new DeviceTopologyExternalMachine("USB4\\LINK", null, "workstation", null,
            [new("soc", null)], new("windows-usb4net", @"USB4\USB4NET", 1, "windows-peer-properties-unavailable"));
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot() with { MachineIdentity = identity, ExternalMachines = [peer] } };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state))).GetProperty("topologySnapshot");
        Assert.Equal("laptop", decoded.GetProperty("machineIdentity").GetProperty("formFactor").GetString());
        var machines = decoded.GetProperty("externalMachines");
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(new[] { peer }, WebJson), machines));
        Assert.Equal("soc", machines[0].GetProperty("components")[0].GetProperty("component").GetString());
        Assert.Equal(JsonValueKind.Null, machines[0].GetProperty("model").ValueKind);
    }

    [Fact]
    public async Task UsbRawClassCodesSurviveProductionDecoderWithoutReadingEnglishSummaries()
    {
        var usb = WindowsDeviceTopologyReader.CreateUsbConnection(DeviceTopologyUsbClassFactsTests.Port(
            [new DeviceTopologyUsbInterfaceDescriptor(0, 1, 255, 128, 254)]), false)!;
        var state = DeviceTopologySnapshotState.Warming with { State = DeviceTopologySnapshotStatus.Ready,
            Snapshot = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port("USB\\RAW") with { Usb = usb }) };
        var decoded = (await DecodeAsync("device-topology", ProductionTopologyWire(state)))
            .GetProperty("topologySnapshot").GetProperty("ports")[0].GetProperty("usb");
        Assert.Equal(239, decoded.GetProperty("deviceClassCode").GetInt32());
        Assert.Equal(2, decoded.GetProperty("deviceSubClassCode").GetInt32());
        Assert.Equal(1, decoded.GetProperty("deviceProtocolCode").GetInt32());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(usb.Interfaces, WebJson), decoded.GetProperty("interfaces")));
    }
}
