using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyUsbClassFactsTests
{
    [Fact]
    public void RawInterfacesRetainAlternateSettingsAndZeroEndpointInterfaces()
    {
        byte[] bytes = [9, 2, 36, 0, 2, 1, 0, 0x80, 50,
            9, 4, 0, 0, 0, 14, 1, 0, 0,
            9, 4, 1, 0, 0, 255, 128, 254, 0,
            9, 4, 1, 1, 0, 3, 1, 2, 0];
        var interfaces = UsbDescriptorParser.ParseInterfaces(bytes);
        Assert.Equal([new(0, 0, 14, 1, 0), new(1, 0, 255, 128, 254), new(1, 1, 3, 1, 2)], interfaces);
        Assert.Empty(UsbDescriptorParser.ParseEndpoints(bytes));
        var connection = WindowsDeviceTopologyReader.CreateUsbConnection(Port(interfaces), false)!;
        Assert.Equal((byte)239, connection.DeviceClassCode);
        Assert.Equal((byte)2, connection.DeviceSubClassCode);
        Assert.Equal((byte)1, connection.DeviceProtocolCode);
        Assert.Equal(interfaces, connection.Interfaces);
        Assert.Equal("unrelated legacy summary", connection.DeviceClass);
    }

    [Fact]
    public void MissingTruncatedAndMalformedDescriptorsNeverBecomeEmptyCapability()
    {
        Assert.Null(UsbDescriptorParser.ParseInterfaces([]));
        Assert.Null(UsbDescriptorParser.ParseInterfaces([9, 2, 18, 0, 1, 1, 0, 0x80, 50]));
        Assert.Null(UsbDescriptorParser.ParseInterfaces([9, 2, 11, 0, 0, 1, 0, 0x80, 50, 0, 4]));
        Assert.Null(UsbDescriptorParser.ParseInterfaces([9, 2, 9, 0, 1, 1, 0, 0x80, 50]));
        Assert.Empty(UsbDescriptorParser.ParseInterfaces([9, 2, 9, 0, 0, 1, 0, 0x80, 50])!);
        Assert.Null(WindowsDeviceTopologyReader.CreateUsbConnection(Port(null), false)!.Interfaces);
        var disconnected = WindowsDeviceTopologyReader.CreateUsbConnection(Port([]) with { DeviceConnected = false }, false)!;
        Assert.Null(disconnected.DeviceClassCode);
        Assert.Null(disconnected.DeviceSubClassCode);
        Assert.Null(disconnected.DeviceProtocolCode);
        Assert.Null(disconnected.Interfaces);
    }

    internal static DeviceTopologyUsbPort Port(IReadOnlyList<DeviceTopologyUsbInterfaceDescriptor>? interfaces) => new(
        "hub", 1, true, 1, null, 0, 0, 1, false, null, null, null,
        new("USB", "1", "unrelated legacy summary", null, null, null, [], [], [], [],
            DeviceClassCode: 239, DeviceSubClassCode: 2, DeviceProtocolCode: 1, Interfaces: interfaces));
}
