using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

public static partial class WindowsUsbHubIoctlReader
{
    /// <summary>Explicitly reads USB hubs and associates an attached port by the original unique direct DriverKey.</summary>
    public static DeviceInformationSnapshot ReadFields(IReadOnlyList<DeviceTopologyNativeDevice> devices)
    {
        var capture = ReadSnapshot();
        return ProjectFields(capture, devices, capture.Error is null ? DateTimeOffset.UtcNow : null);
    }

    public static DeviceInformationSnapshot ProjectFields(DeviceTopologyUsbPortSnapshot capture,
        IReadOnlyList<DeviceTopologyNativeDevice> devices, DateTimeOffset? observedAt)
    {
        var information = new List<DeviceTopologyUsbInformation>();
        foreach (var device in devices)
        {
            var key = NormalizeDriverKey(device.DriverKey);
            if (key.Length == 0 || !capture.PortsByDriverKey.TryGetValue(key, out var port) || !IsAttachedPort(port)) continue;
            var intervals = port.Descriptor.Endpoints.Where(endpoint => endpoint.TransferType == 3 && (endpoint.EndpointAddress & 0x80) != 0)
                .Select(endpoint => UsbEndpointTiming.CalculateServiceIntervalMicroseconds(port.NegotiatedSpeedCode, endpoint.TransferType, endpoint.Interval))
                .Where(interval => interval is > 0).ToArray();
            var interval = port.Descriptor.HidDescriptors.Count > 0 && intervals.Length > 0 ? intervals.Min() : null;
            information.Add(new("windows-pnp|" + device.DeviceId.Trim().ToUpperInvariant(),
                DeviceTopologyUsbConnectorGrouper.CreatePortIdentity(port.HubDevicePath, port.PortNumber),
                port.Descriptor.ManufacturerName, port.Descriptor.ProductName, port.Descriptor.SerialNumber,
                interval / 1000d, interval is > 0 ? 1_000_000d / interval : null,
                port.Descriptor.CameraModes.Count > 0 ? port.Descriptor.CameraModes : null,
                HasHidCapabilities: port.Descriptor.HidDescriptors.Count > 0,
                NegotiatedLinkSpeedMegabitsPerSecond: port.NegotiatedLinkSpeedMegabitsPerSecond,
                DeclaredMaximumLinkSpeedMegabitsPerSecond: port.DeclaredMaximumLinkSpeedMegabitsPerSecond));
        }
        return WindowsInformationFieldProjection.UsbDevices(information, observedAt);
    }
}
