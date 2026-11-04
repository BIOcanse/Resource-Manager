using ResourceManager.App.Domain.Messages;
using System.Globalization;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed partial class WindowsDeviceTopologyReader
{
    internal static BackendMessage DescribeUsbConnectionStatus(uint status)
    {
        return status switch
        {
            0 => Status(BackendMessageCodes.DeviceTopology.UsbNotConnected),
            1 => Status(BackendMessageCodes.DeviceTopology.UsbConnected),
            2 => Status(BackendMessageCodes.DeviceTopology.UsbEnumerationFailed),
            3 => Status(BackendMessageCodes.DeviceTopology.UsbDeviceGeneralFailure),
            4 => Status(BackendMessageCodes.DeviceTopology.UsbDeviceCausedOvercurrent),
            5 => Status(BackendMessageCodes.DeviceTopology.UsbInsufficientPower),
            6 => Status(BackendMessageCodes.DeviceTopology.UsbInsufficientBandwidth),
            7 => Status(BackendMessageCodes.DeviceTopology.UsbHubNestedTooDeep),
            8 => Status(BackendMessageCodes.DeviceTopology.UsbDeviceInLegacyHub),
            9 => Status(BackendMessageCodes.DeviceTopology.UsbEnumerating),
            10 => Status(BackendMessageCodes.DeviceTopology.UsbResetting),
            _ => BackendMessage.Create(
                BackendMessageDomains.DeviceTopology,
                BackendMessageCodes.DeviceTopology.UsbUnknownStatus,
                status.ToString(CultureInfo.InvariantCulture))
        };
    }

    private static BackendMessage Status(byte code)
        => BackendMessage.Create(BackendMessageDomains.DeviceTopology, code);

    internal static BackendMessage? DescribeEdidDisplayTechnology(DeviceTopologyEdidCapabilities? edid)
        => edid is null ? null : edid.IsDigital
            ? BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.DisplayTechnologyDigital, edid.DigitalInterface)
            : BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.DisplayTechnologyAnalog);
}
