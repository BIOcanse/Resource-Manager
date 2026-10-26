using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyTransportTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("stornvme", "nvme")]
    [InlineData("StorAHCI", "ahci")]
    [InlineData("hidi2c", "i2c_hid")]
    [InlineData("UcmUcsiAcpiClient", "ucsi")]
    [InlineData("BthEnum", "bluetooth")]
    [InlineData("BthLEEnum", "bluetooth")]
    [InlineData("HidBth", "bluetooth")]
    public void ExactBoundServiceRefinesGenericCarrier(string service, string expected)
    {
        var result = Resolve(Node("arbitrary-id", "ACPI", service));
        Assert.Equal(expected, result.Code);
        Assert.Null(result.Reason);
        Assert.Equal("service-" + service.ToLowerInvariant(), result.RuleId);
        Assert.Equal(service, Assert.Single(result.Evidence).Service);
    }

    [Theory]
    [InlineData("HDAUDIO", "hd_audio")]
    [InlineData("BTH", "bluetooth")]
    [InlineData("BTHENUM", "bluetooth")]
    [InlineData("BTHLE", "bluetooth")]
    [InlineData("BTHLEDEVICE", "bluetooth")]
    [InlineData("BTHHFENUM", "bluetooth")]
    [InlineData("USB", "usb")]
    [InlineData("PCI", "pci")]
    [InlineData("ACPI", "acpi")]
    [InlineData("USB4", "usb4")]
    public void ActualEnumeratorDecidesWithoutInspectingDeviceIdOrNames(string enumerator, string expected)
    {
        var result = Resolve(Node("USB\\MISLEADING_PCI_DEVICE", enumerator) with {
            FriendlyName = "NVMe Bluetooth PCI Express I2C HID", DeviceDescription = "AHCI HD Audio"
        });
        Assert.Equal(expected, result.Code);
        Assert.Equal(enumerator, Assert.Single(result.Evidence).EnumeratorName);
    }

    [Theory]
    [InlineData("c8ebdfb0-b510-11d0-80e5-00a0c92542e3", "pci")]
    [InlineData("9d7debbc-c85d-11d1-9eb4-006008c3a19a", "usb")]
    [InlineData("d7b46895-001a-4942-891f-a7d46610a843", "acpi")]
    public void BinaryBusTypeIsIndependentOfMissingEnumerator(string raw, string expected)
    {
        var guid = Guid.Parse(raw);
        Assert.Equal(guid, WindowsDeviceTopologyNativeReader.DecodeBusTypeGuid(guid.ToByteArray(), 3));
        var result = Resolve(Node("device") with { BusTypeGuid = guid });
        Assert.Equal(expected, result.Code);
        Assert.Equal(guid, Assert.Single(result.Evidence).BusTypeGuid);
        Assert.Equal(expected, Resolve(Node("device") with { BusTypeGuid = guid }, error: "unrelated row failed").Code);
    }

    [Fact]
    public void MissingOrMalformedGuidDoesNotInventABus()
    {
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeBusTypeGuid(new byte[16], 3));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeBusTypeGuid(new byte[15], 3));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeBusTypeGuid(Guid.NewGuid().ToByteArray(), 1));
        Assert.Null(Resolve(Node("PCI\\LooksLikePci"), error: null).Code);
    }

    [Theory]
    [InlineData("HID", "hidi2c", "i2c_hid")]
    [InlineData("SCSI", "stornvme", "nvme")]
    [InlineData("STORAGE", "storahci", "ahci")]
    public void TransparentChildUsesActualNearestParentProtocol(string enumerator, string service, string code)
    {
        var child = Node("child", enumerator, parent: "parent");
        var parent = Node("parent", "ACPI", service, "root");
        var result = DeviceTopologyTransportClassifier.Classify(child.DeviceId, Snapshot(child, parent, Node("root", "PCI")));
        Assert.Equal(code, result.Code);
        Assert.Equal(new[] { "child", "parent" }, result.Evidence.Select(static fact => fact.DeviceId));
        Assert.Equal(new[] { 0, 1 }, result.Evidence.Select(static fact => fact.Depth));
    }

    [Fact]
    public void HidUsbAndBluetoothStopBeforePciControllerAndUnknownWrappersDoNotInheritMachineBus()
    {
        foreach (var pair in new[] { ("USB", "usb"), ("BTHENUM", "bluetooth") })
        {
            var result = DeviceTopologyTransportClassifier.Classify("child", Snapshot(Node("child", "HID", parent: "parent"),
                Node("parent", pair.Item1, parent: "controller"), Node("controller", "PCI")));
            Assert.Equal(pair.Item2, result.Code);
            Assert.Equal(2, result.Evidence.Count);
        }
        Assert.Null(DeviceTopologyTransportClassifier.Classify("software", Snapshot(Node("software", "SWD", parent: "pci"), Node("pci", "PCI"))).Code);
    }

    [Fact]
    public void UsbStorageUsesRealParentBusInsteadOfParsingItsDiskIdentity()
    {
        var child = Node("opaque-disk-id", "USBSTOR", "disk", "usb-parent");
        var result = DeviceTopologyTransportClassifier.Classify(child.DeviceId, Snapshot(child, Node("usb-parent", "USB", "USBSTOR")));
        Assert.Equal("usb", result.Code);
        Assert.Equal("enumerator-usb", result.RuleId);
        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal("parent-unavailable", DeviceTopologyTransportClassifier.Classify(child.DeviceId, Snapshot(child)).Reason);
    }

    [Theory]
    [InlineData("umpass")]
    [InlineData("bth-unknown")]
    [InlineData("stornvme-fake")]
    [InlineData("UcmUcsiUnknownClient")]
    public void ServicePrefixesAndPresentationCannotCreateFacts(string service)
    {
        var result = Resolve(Node("HDAUDIO\\NVMe", "ROOT", service) with { FriendlyName = "Bluetooth", LocationInfo = "I2C" });
        Assert.Null(result.Code);
        Assert.Equal("unrecognized-source", result.Reason);
    }

    [Fact]
    public void UnavailableConflictingAndBrokenChainsCarryTheirReasonsAndAvailableFacts()
    {
        Assert.Equal("source-unavailable", DeviceTopologyTransportClassifier.Classify("missing", Snapshot() with { Error = "denied" }).Reason);
        Assert.Equal("no-device-record", DeviceTopologyTransportClassifier.Classify("missing", Snapshot()).Reason);
        Assert.Equal("properties-unavailable", Resolve(Node("x")).Reason);
        Assert.Equal("conflicting-evidence", Resolve(Node("x", "PCI") with { BusTypeGuid = DeviceTopologyTransportClassifier.UsbBus }).Reason);
        var child = Node("child", "HID", parent: "missing");
        var broken = DeviceTopologyTransportClassifier.Classify("child", Snapshot(child));
        Assert.Equal("parent-unavailable", broken.Reason);
        Assert.Single(broken.Evidence);
        Assert.Equal("parent-cycle", DeviceTopologyTransportClassifier.Classify("a", Snapshot(Node("a", "HID", parent: "b"), Node("b", "HID", parent: "a"))).Reason);
        var chain = Enumerable.Range(0, 33).Select(i => Node(i.ToString(), "HID", parent: (i + 1).ToString())).ToArray();
        var limited = DeviceTopologyTransportClassifier.Classify("0", Snapshot(chain));
        Assert.Equal("parent-depth-limit", limited.Reason);
        Assert.Equal(32, limited.Evidence.Count);
    }

    [Fact]
    public void EveryPortGetsAResultAndSyntheticConnectorNeedsRealUsbEvidence()
    {
        var port = DeviceInformationTestData.Port("unknown") with { BusKind = "usb", Protocol = "PCI Express" };
        Assert.Null(DeviceTopologyTransportClassifier.Apply(port, Snapshot()).Transport.Code);
        var usb = WindowsDeviceTopologyReader.CreateUsbConnection(DeviceTopologyUsbClassFactsTests.Port(null), false);
        var connected = DeviceTopologyTransportClassifier.Apply(port with { Usb = usb }, Snapshot());
        Assert.Equal("usb", connected.Transport.Code);
        Assert.Equal("usb-hub-ioctl", Assert.Single(connected.Transport.Evidence).Source);
        var withMissingNativeProperties = DeviceTopologyTransportClassifier.Apply(port with { Usb = usb }, Snapshot(Node("unknown")));
        Assert.Equal("usb", withMissingNativeProperties.Transport.Code);
        Assert.Equal("usb-hub-ioctl", Assert.Single(withMissingNativeProperties.Transport.Evidence).Source);
        var conflicting = Node("unknown", "PCI") with { BusTypeGuid = DeviceTopologyTransportClassifier.UsbBus };
        Assert.Equal("conflicting-evidence", DeviceTopologyTransportClassifier.Apply(port with { Usb = usb }, Snapshot(conflicting)).Transport.Reason);
        Assert.Equal("not-queried", port.Transport.Reason);
    }

    [Fact]
    public void TransportAndEvidenceArePersistedAndChangeExistingSemanticHash()
    {
        var known = Resolve(Node("device", "PCI"));
        var snapshot = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port("device") with { Transport = known });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var persisted = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot, options), options)!;
        Assert.Equal(known.Code, persisted.Ports[0].Transport.Code);
        Assert.Equal(known.Evidence, persisted.Ports[0].Transport.Evidence);
        var comparer = new DeviceTopologySemanticComparer();
        var richer = Resolve(Node("device", "PCI") with { BusTypeGuid = DeviceTopologyTransportClassifier.PciBus });
        Assert.NotEqual(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with { Ports = [snapshot.Ports[0] with { Transport = richer }] }));
        Assert.NotEqual(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with { Ports = [snapshot.Ports[0] with { Transport = DeviceTopologyTransport.Unqueried }] }));
        Assert.Equal(comparer.ComputeHash(snapshot), comparer.ComputeHash(persisted));
    }

    [Fact]
    public void RealMachineSmokePublishesNativeTransportEvidence()
    {
        var native = WindowsDeviceTopologyNativeReader.ReadSnapshot();
        var facts = native.Devices.Values.OrderBy(static device => device.DeviceId, StringComparer.Ordinal)
            .Select(device => new { device.DeviceId, Transport = DeviceTopologyTransportClassifier.Classify(device.DeviceId, native) }).ToArray();
        var summary = facts.GroupBy(static row => row.Transport.Code ?? "unknown")
            .ToDictionary(static group => group.Key, static group => group.Count());
        var json = JsonSerializer.Serialize(new { native.Error, Summary = summary, Devices = facts }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        output.WriteLine(json);
        Assert.NotEmpty(facts);
        Assert.All(facts, static row => Assert.True(row.Transport.Code is null ? row.Transport.Reason is not null : row.Transport.Reason is null));
    }

    internal static DeviceTopologyNativeDevice Node(string id, string? enumerator = null, string? service = null, string? parent = null)
        => new(id, null, null, null, service, null, null, null, enumerator, null, [], [], [], parent, null, null);
    internal static DeviceTopologyNativeSnapshot Snapshot(params DeviceTopologyNativeDevice[] nodes)
        => new(nodes.ToDictionary(static node => node.DeviceId, StringComparer.OrdinalIgnoreCase), null);
    private static DeviceTopologyTransport Resolve(DeviceTopologyNativeDevice node, string? error = null)
        => DeviceTopologyTransportClassifier.Classify(node.DeviceId, Snapshot(node) with { Error = error });
}
