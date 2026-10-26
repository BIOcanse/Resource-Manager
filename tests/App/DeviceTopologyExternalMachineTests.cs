using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyExternalMachineTests(ITestOutputHelper output)
{
    [Fact]
    public void ConnectedDocumentedInterdomainPeerIsIndependentWorkstationWithOneUnknownSoc()
    {
        var peer = Assert.Single(DeviceTopologyExternalMachineProjector.Project(Native(Device()), Network(1))!);
        Assert.Equal(LinkId, peer.LocalLinkDeviceId);
        Assert.Null(peer.PeerIdentity);
        Assert.Null(peer.Model);
        Assert.Equal("workstation", peer.FormFactor);
        var soc = Assert.Single(peer.Components);
        Assert.Equal("soc", soc.Component);
        Assert.Null(soc.Model);
        Assert.Equal(new("windows-usb4net", @"USB4\USB4NET", 1, "windows-peer-properties-unavailable"), peer.Evidence);
        var snapshot = DeviceInformationTestData.Snapshot();
        var updated = snapshot with { ExternalMachines = [peer] };
        Assert.Equal(snapshot.MachineIdentity, updated.MachineIdentity);
        Assert.Equal(snapshot.GpuInformation, updated.GpuInformation);
    }

    [Theory]
    [InlineData("Mac Studio", "USB4\\ROUTER")]
    [InlineData("Thunderbolt P2P Network Adapter", "PCI\\CC_0200")]
    [InlineData("USB4 P2P Network Adapter", "USB4\\USB4NET_FAKE")]
    [InlineData("NVIDIA RTX PRO 6000", "USB4\\ROUTER")]
    public void DeviceAndAdapterNamesCannotEstablishHostPeer(string name, string compatibleId)
    {
        Assert.Empty(DeviceTopologyExternalMachineProjector.Project(Native(Device() with {
            FriendlyName = name, CompatibleIds = [compatibleId] }), Network(1, name))!);
    }

    [Theory]
    [InlineData(0u)] [InlineData(null)] [InlineData(3u)]
    public void UnknownLinkDoesNotInventConnectedPeer(uint? state)
        => Assert.Null(DeviceTopologyExternalMachineProjector.Project(Native(Device()), Network(state)));

    [Fact]
    public void DisconnectedOrMissingAdapterCannotEstablishCurrentPeer()
    {
        Assert.Empty(DeviceTopologyExternalMachineProjector.Project(Native(Device()), Network(2))!);
        Assert.Null(DeviceTopologyExternalMachineProjector.Project(Native(Device()), new(
            new Dictionary<string, DeviceTopologyNetworkAdapter>(), null)));
    }

    [Fact]
    public void SourceFailureIsUnavailableAndIrrelevantNetworkFailureDoesNotHideKnownAbsence()
    {
        Assert.Null(DeviceTopologyExternalMachineProjector.Project(Native() with { Error = "failed" }, Network(1)));
        Assert.Null(DeviceTopologyExternalMachineProjector.Project(Native(Device()), Network(0) with { Error = "failed" }));
        Assert.Empty(DeviceTopologyExternalMachineProjector.Project(Native(), Network(1) with { Error = "failed" })!);
    }

    [Fact]
    public void CompatibleIdAndPnpJoinAreCaseInsensitiveAndDoNotCopyLocalDescriptions()
    {
        var device = Device() with { CompatibleIds = [@"usb4\usb4net"] };
        var peer = Assert.Single(DeviceTopologyExternalMachineProjector.Project(Native(device), Network(1, "Mac Studio"))!);
        Assert.Null(peer.Model);
        Assert.Null(peer.PeerIdentity);
        Assert.Null(Assert.Single(peer.Components).Model);
    }

    [Fact]
    public void MissingOrPartialEvidenceDoesNotHideOtherConfirmedPeers()
    {
        var first = Device();
        var second = Device() with { DeviceId = @"USB4\LOCAL_LINK\2" };
        var peers = DeviceTopologyExternalMachineProjector.Project(Native(first, second), Network(1));
        Assert.Equal(LinkId, Assert.Single(peers!).LocalLinkDeviceId);
        peers = DeviceTopologyExternalMachineProjector.Project(Native(first) with { Error = "partial" },
            Network(1) with { Error = "partial" });
        Assert.Equal(LinkId, Assert.Single(peers!).LocalLinkDeviceId);
    }

    [Fact]
    public void SourceOnlyMachineSmokePublishesCurrentPeerEvidence()
    {
        var native = WindowsDeviceTopologyNativeReader.ReadSnapshot();
        var network = WindowsNetworkAdapterTopologyReader.ReadSnapshot();
        var peers = DeviceTopologyExternalMachineProjector.Project(native, network);
        output.WriteLine(JsonSerializer.Serialize(new { native.Error, NetworkError = network.Error, ExternalMachines = peers },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (peers is null) return; // An unavailable source is an explicit permitted result, never confirmed absence.
        foreach (var peer in peers)
        {
            Assert.Contains(DeviceTopologyExternalMachineProjector.Usb4NetCompatibleId,
                native.Devices[peer.LocalLinkDeviceId].CompatibleIds, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1u, network.Adapters[peer.LocalLinkDeviceId].MediaConnectState);
            Assert.Null(peer.PeerIdentity);
            Assert.Null(peer.Model);
        }
    }

    private const string LinkId = @"USB4\LOCAL_LINK\1";
    private static DeviceTopologyNativeDevice Device() => new(LinkId, "Local adapter", "Local adapter", "Microsoft",
        null, null, "Net", null, "USB4", null, [], [], [@"USB4\USB4NET"], null, 0x8, 0);
    private static DeviceTopologyNativeSnapshot Native(params DeviceTopologyNativeDevice[] devices)
        => new(devices.ToDictionary(static device => device.DeviceId, StringComparer.OrdinalIgnoreCase), null);
    private static DeviceTopologyNetworkAdapterSnapshot Network(uint? state, string? name = null)
        => new(new Dictionary<string, DeviceTopologyNetworkAdapter>(StringComparer.OrdinalIgnoreCase) {
            [LinkId.ToLowerInvariant()] = new(LinkId, name, state, null, null, null, null, null, null)
        }, null);
}
