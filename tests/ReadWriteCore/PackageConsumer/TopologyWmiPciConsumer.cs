using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Topology;

internal static class TopologyWmiPciConsumer
{
    internal static void RunRecorded()
    {
        var calls = 0;
        var reader = new WindowsDeviceTopologyWmiFactsReader(TimeSpan.FromMilliseconds(321), (scope, query, timeout) =>
        {
            Require(scope == @"root\CIMV2" && query.EndsWith("FROM Win32_BIOS", StringComparison.Ordinal)
                && timeout == TimeSpan.FromMilliseconds(321), "selected original WMI query and explicit timeout");
            calls++;
            return new([new() { ["Manufacturer"] = "Recorded vendor" }], null);
        });
        Require(calls == 0, "topology WMI construction is passive");
        Require(reader.Read(WindowsDeviceTopologyWmiFact.Bios).Rows.Single()["Manufacturer"] as string == "Recorded vendor"
            && calls == 1, "only the selected WMI source is queried");
        Require(new[] { typeof(WindowsDeviceTopologyWmiFactsReader), typeof(WindowsNetworkAdapterTopologyReader),
            typeof(PciDeviceIdentificationReader), typeof(DeviceTopologyWmiQueryResult) }
            .All(type => type.Assembly == typeof(ReadWriteCore).Assembly), "independent WMI and PCI implementation assembly");
        var declared = PciDeviceIdentificationReader.Read([@"PCI\VEN_1414&DEV_0000&REV_00"], [@"PCI\CC_030000"]);
        var fields = WindowsInformationFieldProjection.Pci(Port(@"PCI\RECORDED") with { PciIdentification = declared });
        Require(fields.Single(field => field.Definition.Name == "pci_all_device_id").Value!.Value.GetString() == "0000"
            && fields.Single(field => field.Definition == DeviceInformationFieldRegistry.PciAllProgrammingInterfaceDeclaredCode).Value!.Value.GetString() == "00",
            "canonical PCI preserves zero declarations");
        Require(fields.Single(field => field.Definition == DeviceInformationFieldRegistry.PciAllResourceRange).Value is null,
            "unavailable PCI allocation is not an empty successful list");
        Console.WriteLine("Topology WMI/PCI recorded package consumer passed: selected query, explicit timeout and canonical declarations.");
    }

    [SupportedOSPlatform("windows")]
    internal static void RunWindows()
    {
        var bios = new WindowsDeviceTopologyWmiFactsReader(TimeSpan.FromSeconds(2)).Read(WindowsDeviceTopologyWmiFact.Bios);
        var network = WindowsNetworkAdapterTopologyReader.ReadSnapshot(TimeSpan.FromSeconds(2));
        Require(bios.Error is null && network.Error is null, "actual original BIOS and network-adapter WMI queries available");
        Console.WriteLine($"Topology WMI Windows package consumer passed: BIOS rows {bios.Rows.Count}, network adapters {network.Adapters.Count}; selected original sources only.");
    }

    internal static void ProjectNative(DeviceTopologyNativeSnapshot snapshot)
    {
        var devices = snapshot.Devices.Values.Where(device => device.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)).ToArray();
        var values = 0;
        foreach (var device in devices)
        {
            var fields = WindowsInformationFieldProjection.Pci(Port(device.DeviceId) with {
                NativeParentDeviceId = device.ParentDeviceId,
                PciIdentification = PciDeviceIdentificationReader.ReadForDevice(device.DeviceId, device),
                PciResourceRanges = device.PciResources.Ranges, PciInterrupts = device.PciResources.Interrupts,
                PciDmaChannels = device.PciResources.DmaChannels });
            Require(fields.All(field => field.Definition.Name.StartsWith("pci_", StringComparison.Ordinal)), "native PCI canonical names");
            values += fields.Count(field => field.Value is not null);
        }
        Console.WriteLine($"Topology PCI native package consumer completed: devices {devices.Length}, source available {snapshot.Error is null}, canonical values {values}; original shared native capture.");
    }

    private static DeviceTopologyPort Port(string id)
    {
        var message = new BackendMessage(0, 0, []);
        return new(Id: "port:" + id, IsPhysicalConnector: false, DisplayName: null, ConnectorKind: "generic",
            BusKind: "pci", HardwareKind: null, Protocol: null, Speed: null, DeviceId: id, PnpClass: null,
            Manufacturer: null, Service: null, Status: null, Confidence: message, Source: message,
            UpstreamDeviceId: null, UpstreamDisplayName: null, TopologyPath: message, NativeParentDeviceId: null,
            NativeParentDisplayName: null, LocationInfo: null, LocationPaths: [], ClassGuid: null, Display: null,
            Network: null, IdResolution: null, AdvancedInterconnect: null, Usb: null, HardwareIds: [], CompatibleIds: []);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
