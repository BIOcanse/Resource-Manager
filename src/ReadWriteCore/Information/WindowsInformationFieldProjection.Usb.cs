using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    public static IReadOnlyList<DeviceInformationField> UsbFields(DeviceTopologyUsbInformation usb)
    {
        var fields = new List<DeviceInformationField> {
            Text(F.UsbAllParentPortActual, usb.ActualParentPortIdentity), Text(F.UsbAllManufacturerDeclared, usb.ManufacturerName),
            Text(F.UsbAllProductNameDeclared, usb.ProductName), Text(F.UsbAllSerialNumberDeclared, usb.SerialNumber) };
        if (!string.IsNullOrWhiteSpace(usb.ActualParentPortIdentity))
        {
            fields.AddRange([
                Number(F.UsbAllLinkSpeedNegotiated, usb.NegotiatedLinkSpeedMegabitsPerSecond),
                Number(F.UsbAllLinkSpeedCapability, usb.DeclaredMaximumLinkSpeedMegabitsPerSecond)]);
        }
        if (usb.HasHidCapabilities || usb.InputPollingIntervalMilliseconds is not null || usb.TheoreticalReportRateHz is not null)
            fields.AddRange([Number(F.HidAllPollIntervalTheoretical, usb.InputPollingIntervalMilliseconds),
                Number(F.HidAllReportRateTheoreticalHz, usb.TheoreticalReportRateHz)]);
        if (usb.CameraModes is not null)
        {
            var modes = usb.CameraModes.Where(mode => mode.Width > 0 && mode.Height > 0
                && double.IsFinite(mode.MaximumFrameRate) && mode.MaximumFrameRate > 0).ToArray();
            fields.Add(Value(F.CameraAllUvcModeCapability, modes.Length == 0 ? null : modes.Select(static mode =>
                new { mode.Width, mode.Height, mode.MaximumFrameRate, mode.PixelFormat }).ToArray()));
            // A legacy or invalid row must not be silently filtered out of a "complete" interval directory.
            var complete = modes.Length == usb.CameraModes.Count && modes.Length > 0;
            var identities = new HashSet<(byte Configuration, byte Interface, byte Alternate, byte Format, byte Frame)>();
            var copied = new List<object>();
            foreach (var mode in modes)
            {
                if (mode.FrameIntervalsDeclared is not { } intervals || !intervals.IsComplete()
                    || !identities.Add((intervals.ConfigurationValue, intervals.InterfaceNumber,
                        intervals.AlternateSetting, intervals.FormatIndex, intervals.FrameIndex)))
                {
                    complete = false;
                    break;
                }
                var copy = intervals with { DiscreteIntervals100Nanoseconds = intervals.DiscreteIntervals100Nanoseconds?.ToArray() };
                // Recheck the copy which is serialized; caller-owned collections are never retained.
                if (!copy.IsComplete()) { complete = false; break; }
                copied.Add(new { UsbDeviceIdentity = usb.DeviceIdentity, mode.Width, mode.Height,
                    mode.PixelFormat, FrameIntervalsDeclared = copy });
            }
            fields.Add(Value(F.CameraAllFrameIntervalCapability, complete ? copied.ToArray() : null));
        }
        return fields.AsReadOnly();
    }

    public static DeviceInformationSnapshot UsbDevices(IEnumerable<DeviceTopologyUsbInformation> devices, DateTimeOffset? observedAt)
        => Frame(devices.Where(device => !string.IsNullOrWhiteSpace(device.DeviceIdentity))
            .Select(device => new DeviceInformationObject(device.DeviceIdentity, "usb_device", UsbFields(device))), observedAt);
}
