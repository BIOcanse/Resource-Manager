using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Usb;
using System.Runtime.Versioning;

static class WindowsDisplayUsbConsumer
{
    [SupportedOSPlatform("windows")]
    public static void Run()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Require(new[] { typeof(WindowsDisplayPathTopologyReader), typeof(WindowsMonitorEdidReader),
            typeof(WindowsDxgiDisplayCapabilityReader), typeof(EdidParser), typeof(WindowsUsbHubIoctlReader),
            typeof(UsbDescriptorParser), typeof(UsbEndpointTiming) }.All(type => type.Assembly == owner), "independent display/USB readers");
        var edidBytes = new byte[128];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(edidBytes, 0);
        edidBytes[18] = 1; edidBytes[19] = 4; edidBytes[20] = 0xB5; edidBytes[21] = 60; edidBytes[22] = 34;
        edidBytes[127] = unchecked((byte)(-edidBytes.Sum(value => (int)value)));
        var edid = EdidParser.Parse(edidBytes)!;
        Require(edid.IsDigital && edid.BitsPerColorChannel == 10 && edid.DigitalInterface == DeviceEdidDigitalInterfaces.DisplayPort,
            "recorded EDID declares a digital interface without product message codes");
        var declared = new DeviceTopologyDisplayInformation("recorded-display", false, true, EdidRawDeclaredBase64: "AA==",
            CurrentWidth: 1920, CurrentHeight: 1080, ClockwiseRotationDegrees: 0, HdrSupportedReported: false, HdrUserEnabledActual: false);
        var fields = WindowsInformationFieldProjection.DisplayFields(declared);
        Require(fields.Single(field => field.Definition == DeviceInformationFieldRegistry.DisplayAllResolutionActual).Value is null,
            "inactive paths have no current resolution");
        Require(!fields.Single(field => field.Definition == DeviceInformationFieldRegistry.DisplayAllHdrCapability).Value!.Value.GetBoolean(),
            "reported false remains available while inactive");
        var at = DateTimeOffset.UtcNow;
        var descriptor = new DeviceTopologyUsbDescriptor("USB 2.0", "1.0", "HID", "Vendor", "Product", "Serial", [],
            [new(0, 0, 3, 1, 2, 0x81, 3, 64, 1)], [new(0, 0, 1, 2, 0x0111, 0, null)], []);
        var port = new DeviceTopologyUsbPort("hub", 7, true, 1, "HighSpeed", 1, 2, 3, false, "driver", null, null,
            descriptor, NegotiatedSpeedCode: 2);
        var device = new DeviceTopologyNativeDevice(@"USB\RECORDED", null, null, null, null, "driver", null, null,
            null, null, [], [], [], null, null, null);
        var capture = new DeviceTopologyUsbPortSnapshot([port], new Dictionary<string, DeviceTopologyUsbPort>(StringComparer.OrdinalIgnoreCase) {
            [WindowsUsbHubIoctlReader.NormalizeDriverKey("driver")] = port }, null);
        var usbFields = WindowsUsbHubIoctlReader.ProjectFields(capture, [device], at).Devices.Single().Fields;
        Require(usbFields.Single(field => field.Definition == DeviceInformationFieldRegistry.UsbAllParentPortActual).Value!.Value.GetString() == "HUB|7",
            "direct attached parent uses the captured port, not a connector representative");
        Require(usbFields.Single(field => field.Definition == DeviceInformationFieldRegistry.HidAllPollIntervalTheoretical).Value!.Value.GetDouble() == 0.125,
            "original theoretical service interval uses milliseconds");
        Require(usbFields.Single(field => field.Definition == DeviceInformationFieldRegistry.HidAllReportRateTheoreticalHz).Value!.Value.GetDouble() == 8000,
            "original theoretical endpoint rate");
        Require(WindowsUsbHubIoctlReader.ProjectFields(capture, [device with { DriverKey = "foreign" }], at).Devices.Count == 0,
            "a foreign driver key has no parent-port association");

        var paths = WindowsDisplayPathTopologyReader.ReadSnapshot();
        var displayFields = WindowsDisplayPathTopologyReader.ReadFields(path =>
            $"consumer-display|{path.AdapterHighPart:X8}:{path.AdapterLowPart:X8}|{path.TargetId}|{path.MonitorDevicePath}");
        var topology = WindowsDeviceTopologyNativeReader.ReadSnapshot();
        TopologyWmiPciConsumer.ProjectNative(topology);
        var ports = WindowsUsbHubIoctlReader.ReadSnapshot();
        var actualUsb = WindowsUsbHubIoctlReader.ReadFields(topology.Devices.Values.ToArray());
        Require(displayFields.Devices.SelectMany(item => item.Fields).All(field => field.Definition.Name.StartsWith("display_", StringComparison.Ordinal)),
            "actual display frame uses registered fields");
        Console.WriteLine($"ReadWriteCore display/USB consumer passed: display paths {paths.Paths.Count}, display path capture available {paths.Error is null}, display field objects {displayFields.Devices.Count}, display field capture available {displayFields.ObservedAt is not null}, USB ports {ports.Ports.Count}, USB capture available {ports.Error is null}, matched USB field objects {actualUsb.Devices.Count}, USB field capture available {actualUsb.ObservedAt is not null}; recorded declarations, inactive current values, direct parent and theoretical endpoint units passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
