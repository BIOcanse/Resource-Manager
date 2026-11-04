using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologyTransportClassifier
{
    internal static readonly Guid PciBus = new("c8ebdfb0-b510-11d0-80e5-00a0c92542e3");
    internal static readonly Guid UsbBus = new("9d7debbc-c85d-11d1-9eb4-006008c3a19a");
    internal static readonly Guid AcpiBus = new("d7b46895-001a-4942-891f-a7d46610a843");
    internal static readonly Guid HidBus = new("eeaf37d0-1963-47c4-aa48-72476db7cf49");

    private static readonly Dictionary<string, string> Services = new(StringComparer.OrdinalIgnoreCase) {
        ["stornvme"] = "nvme", ["storahci"] = "ahci", ["hidi2c"] = "i2c_hid",
        ["UcmUcsiAcpiClient"] = "ucsi", ["BthEnum"] = "bluetooth",
        ["BthLEEnum"] = "bluetooth", ["HidBth"] = "bluetooth"
    };
    private static readonly Dictionary<string, string> Enumerators = new(StringComparer.OrdinalIgnoreCase) {
        ["HDAUDIO"] = "hd_audio", ["BTH"] = "bluetooth", ["BTHENUM"] = "bluetooth", ["BTHLE"] = "bluetooth",
        ["BTHLEDEVICE"] = "bluetooth", ["BTHHFENUM"] = "bluetooth",
        ["USB"] = "usb", ["PCI"] = "pci", ["ACPI"] = "acpi", ["USB4"] = "usb4"
    };
    private static readonly Dictionary<Guid, string> Buses = new() {
        [PciBus] = "pci", [UsbBus] = "usb", [AcpiBus] = "acpi"
    };

    internal static DeviceTopologyTransport Classify(string deviceId, DeviceTopologyNativeSnapshot snapshot)
    {
        var evidence = new List<DeviceTopologyTransportEvidence>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentId = deviceId;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(currentId)) return Unknown("parent-cycle");
            if (!snapshot.Devices.TryGetValue(currentId, out var node))
                return Unknown(depth > 0 ? "parent-unavailable" : snapshot.Error is null ? "no-device-record" : "source-unavailable");
            evidence.Add(new("setupapi-cfgmgr32", node.DeviceId, depth, node.EnumeratorName,
                node.BusTypeGuid, node.Service, node.ParentDeviceId));
            var enumerator = node.EnumeratorName ?? string.Empty;
            var service = node.Service ?? string.Empty;
            Enumerators.TryGetValue(enumerator, out var enumerated);
            var bus = node.BusTypeGuid is { } guid && Buses.TryGetValue(guid, out var code) ? code : null;
            if ((enumerated is "pci" or "usb" or "acpi" or "usb4") && bus is not null && enumerated != bus)
                return Unknown("conflicting-evidence");
            if (Services.TryGetValue(service, out var protocol))
                return Known(protocol, "service-" + service.ToLowerInvariant());
            if (enumerated is not null) return Known(enumerated, "enumerator-" + enumerator.ToLowerInvariant());
            if (bus is not null) return Known(bus, "bus-guid-" + bus);
            var transparent = node.BusTypeGuid == HidBus
                || enumerator.Equals("HID", StringComparison.OrdinalIgnoreCase)
                || enumerator.Equals("SCSI", StringComparison.OrdinalIgnoreCase)
                || enumerator.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase)
                || enumerator.Equals("STORAGE", StringComparison.OrdinalIgnoreCase);
            if (!transparent)
                return Unknown(node.EnumeratorName is null && node.BusTypeGuid is null && node.Service is null
                    ? "properties-unavailable" : "unrecognized-source");
            if (string.IsNullOrWhiteSpace(node.ParentDeviceId)) return Unknown("parent-unavailable");
            currentId = node.ParentDeviceId;
        }
        return Unknown("parent-depth-limit");

        DeviceTopologyTransport Known(string code, string rule) => new(code, null, rule, evidence.ToArray());
        DeviceTopologyTransport Unknown(string reason) => new(null, reason, null, evidence.ToArray());
    }

    internal static DeviceTopologyPort Apply(DeviceTopologyPort port, DeviceTopologyNativeSnapshot snapshot)
    {
        var transport = Classify(port.DeviceId, snapshot);
        if (transport.Code is null && transport.Reason != "conflicting-evidence" && port.Usb is { } usb)
            transport = new("usb", null, "usb-hub-port", [new("usb-hub-ioctl", usb.HubDevicePath, 0, null, null, null, null)]);
        return port with { Transport = transport };
    }
}
