using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologySpecializedCapabilityProjector
{
    public static IReadOnlyList<DeviceTopologyUsbEndpoint> CreateUsbEndpoints(DeviceTopologyUsbPort? port)
    {
        if (port is null || port.Descriptor.Endpoints.Count == 0)
        {
            return [];
        }

        return port.Descriptor.Endpoints
            .Select(endpoint =>
            {
                var serviceInterval = CalculateServiceIntervalMicroseconds(
                    port.NegotiatedSpeedCode,
                    endpoint.TransferType,
                    endpoint.Interval);
                var inputInterrupt = endpoint.TransferType == 3
                    && (endpoint.EndpointAddress & 0x80) != 0;
                return new DeviceTopologyUsbEndpoint(
                    endpoint.InterfaceNumber,
                    endpoint.AlternateSetting,
                    UsbDescriptorParser.DescribeClass(
                        endpoint.InterfaceClass,
                        endpoint.InterfaceSubClass,
                        endpoint.InterfaceProtocol),
                    endpoint.EndpointAddress,
                    (endpoint.EndpointAddress & 0x80) != 0
                        ? DeviceEndpointDirections.Input
                        : DeviceEndpointDirections.Output,
                    DescribeTransferType(endpoint.TransferType),
                    endpoint.MaximumPacketSize,
                    endpoint.Interval,
                    serviceInterval,
                    inputInterrupt && serviceInterval is > 0
                        ? 1_000_000d / serviceInterval.Value
                        : null);
            })
            .ToArray();
    }

    public static DeviceTopologyHidCapabilities? CreateHid(
        DeviceTopologyUsbPort? port,
        IReadOnlyList<DeviceTopologyUsbEndpoint> endpoints)
    {
        if (port is null || port.Descriptor.HidDescriptors.Count == 0)
        {
            return null;
        }

        var descriptors = port.Descriptor.HidDescriptors;
        var type = descriptors.Any(static value => value.InterfaceProtocol == 1)
            ? DeviceHidTypes.Keyboard
            : descriptors.Any(static value => value.InterfaceProtocol == 2)
                ? DeviceHidTypes.Mouse
                : DeviceHidTypes.InputDevice;
        var fastestInput = endpoints
            .Where(static endpoint => endpoint.Direction == DeviceEndpointDirections.Input
                && endpoint.TransferType == DeviceEndpointTransferTypes.Interrupt
                && endpoint.ServiceIntervalMicroseconds is > 0)
            .OrderBy(static endpoint => endpoint.ServiceIntervalMicroseconds)
            .FirstOrDefault();
        var specification = descriptors
            .Select(static value => value.HidVersionBcd)
            .Where(static value => value != 0)
            .Distinct()
            .OrderDescending()
            .Select(static value => $"HID {UsbDescriptorParser.FormatBcdVersion(value)}")
            .FirstOrDefault();

        return new DeviceTopologyHidCapabilities(
            type,
            specification,
            fastestInput?.ServiceIntervalMicroseconds,
            fastestInput?.TheoreticalReportRateHz,
            ReportedDpi: null,
            ReportedScanRateHz: null,
            StandardCapabilitySource: Source(BackendMessageCodes.DeviceTopology.SourceUsbHidEndpointDescriptors),
            VendorCapabilitySource: null);
    }

    public static DeviceTopologyCameraCapabilities? CreateCamera(DeviceTopologyUsbPort? port)
    {
        if (port is null || port.Descriptor.CameraModes.Count == 0)
        {
            return null;
        }

        var modes = port.Descriptor.CameraModes
            .Select(static mode => new DeviceTopologyCameraMode(
                mode.Width,
                mode.Height,
                Math.Round(mode.MaximumFrameRate, 2),
                mode.PixelFormat))
            .ToArray();
        return new DeviceTopologyCameraCapabilities(
            Source(BackendMessageCodes.DeviceTopology.SourceUsbVideoClassDescriptors),
            modes.FirstOrDefault(),
            modes);
    }

    public static DeviceTopologySmartDeviceCapabilities? CreateSmartDevice(
        DeviceTopologyUsbPort? port,
        string displayName,
        string? modelName,
        string? pnpClass,
        string? service,
        string? manufacturer)
    {
        var protocols = port?.Descriptor.InterfaceProtocols ?? [];
        var descriptorPortableDevice = protocols.Any(static value =>
                value.Contains("Still Image", StringComparison.OrdinalIgnoreCase)
                || value.Contains("MTP", StringComparison.OrdinalIgnoreCase));
        var wpdMtpDevice = string.Equals(pnpClass, "WPD", StringComparison.OrdinalIgnoreCase)
            && service?.Contains("WpdMtp", StringComparison.OrdinalIgnoreCase) == true;
        var isPortableDevice = descriptorPortableDevice || wpdMtpDevice;
        if (!isPortableDevice)
        {
            return null;
        }

        var deviceType = ResolvePortableDeviceType($"{displayName} {modelName}");
        var protocol = protocols.Any(static value => value.Contains("Still Image", StringComparison.OrdinalIgnoreCase))
            ? "MTP / PTP"
            : service?.Contains("WpdMtp", StringComparison.OrdinalIgnoreCase) == true
                ? "MTP"
                : protocols.FirstOrDefault();
        return new DeviceTopologySmartDeviceCapabilities(
            deviceType,
            port?.Descriptor.ManufacturerName ?? manufacturer,
            CleanModelName(modelName) ?? port?.Descriptor.ProductName ?? displayName,
            port?.Descriptor.SerialNumber,
            FirmwareVersion: null,
            protocol,
            port?.NegotiatedSpeed ?? (wpdMtpDevice ? "USB / WPD" : null),
            BatteryPercent: null,
            Storages: [],
            Source: descriptorPortableDevice
                ? Source(BackendMessageCodes.DeviceTopology.SourceUsbDescriptorsAndWindows)
                : Source(BackendMessageCodes.DeviceTopology.SourceWindowsWpdPnp));
    }

    internal static double? CalculateServiceIntervalMicroseconds(
        byte? negotiatedSpeedCode,
        byte transferType,
        byte interval)
    {
        if (interval == 0 || transferType is not (1 or 3))
        {
            return null;
        }

        return negotiatedSpeedCode is >= 2
            ? interval <= 16 ? 125d * Math.Pow(2, interval - 1) : null
            : interval * 1_000d;
    }

    private static string DescribeTransferType(byte transferType)
    {
        return transferType switch
        {
            0 => DeviceEndpointTransferTypes.Control,
            1 => DeviceEndpointTransferTypes.Isochronous,
            2 => DeviceEndpointTransferTypes.Bulk,
            3 => DeviceEndpointTransferTypes.Interrupt,
            _ => DeviceEndpointTransferTypes.Unknown
        };
    }

    private static BackendMessage Source(byte code)
        => BackendMessage.Create(BackendMessageDomains.DeviceTopology, code);

    private static string ResolvePortableDeviceType(string displayName)
    {
        if (displayName.Contains("phone", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("android", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("iphone", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("手机", StringComparison.OrdinalIgnoreCase))
        {
            return DevicePortableDeviceTypes.Phone;
        }

        if (displayName.Contains("tablet", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("ipad", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("平板", StringComparison.OrdinalIgnoreCase))
        {
            return DevicePortableDeviceTypes.Tablet;
        }

        if (displayName.Contains("camera", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("相机", StringComparison.OrdinalIgnoreCase))
        {
            return DevicePortableDeviceTypes.Camera;
        }

        return DevicePortableDeviceTypes.SmartDevice;
    }

    private static string? CleanModelName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("portable device", StringComparison.OrdinalIgnoreCase)
            || value.Contains("便携设备", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim();
    }
}
