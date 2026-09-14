using ResourceManager.App.Infrastructure.DeviceTopology;
using System.Runtime.InteropServices;

namespace Resource_Manager_APP.Tests;

public sealed class UsbDescriptorParserTests
{
    [Theory]
    [InlineData(0x0200, "USB 2.0 (bcdUSB 0x0200)")]
    [InlineData(0x0210, "USB 2.1 (bcdUSB 0x0210)")]
    [InlineData(0x0320, "USB 3.2 (bcdUSB 0x0320)")]
    public void FormatUsbSpecification_FormatsBcdVersion(ushort value, string expected)
    {
        Assert.Equal(expected, UsbDescriptorParser.FormatUsbSpecification(value));
    }

    [Fact]
    public void ParseInterfaceProtocols_DeduplicatesRepeatedInterfaceTriples()
    {
        byte[] descriptor =
        [
            9, 2, 27, 0, 2, 1, 0, 0x80, 50,
            9, 4, 0, 0, 1, 3, 1, 1, 0,
            9, 4, 1, 0, 1, 3, 1, 1, 0
        ];

        var protocols = UsbDescriptorParser.ParseInterfaceProtocols(descriptor);

        Assert.Equal(["HID Boot Keyboard [03/01/01]"], protocols);
    }

    [Fact]
    public void ParseInterfaceProtocols_StopsAtZeroLengthDescriptor()
    {
        byte[] descriptor =
        [
            9, 2, 20, 0, 1, 1, 0, 0x80, 50,
            0, 4, 0, 0, 1, 3, 1, 1, 0, 0, 0
        ];

        var protocols = UsbDescriptorParser.ParseInterfaceProtocols(descriptor);

        Assert.Empty(protocols);
    }

    [Fact]
    public void ParseStringDescriptor_RejectsOddAndOversizedLengths()
    {
        Assert.Null(UsbDescriptorParser.ParseStringDescriptor([5, 3, 65, 0, 66]));
        Assert.Null(UsbDescriptorParser.ParseStringDescriptor([8, 3, 65, 0]));
    }

    [Fact]
    public void SelectLanguageId_PrefersCurrentCultureThenEnglish()
    {
        ushort[] languageIds = [0x0411, 0x0409, 0x0804];

        Assert.Equal((ushort)0x0804, UsbDescriptorParser.SelectLanguageId(languageIds, 0x0804));
        Assert.Equal((ushort)0x0409, UsbDescriptorParser.SelectLanguageId(languageIds, 0x0407));
    }

    [Fact]
    public void ParseEndpointsAndHidDescriptors_PreserveInterfaceAndPollingFacts()
    {
        byte[] descriptor =
        [
            9, 2, 34, 0, 1, 1, 0, 0x80, 50,
            9, 4, 0, 0, 1, 3, 1, 2, 0,
            9, 0x21, 0x11, 0x01, 0, 1, 0x22, 0x34, 0,
            7, 5, 0x81, 3, 0x40, 0, 1
        ];

        var endpoint = Assert.Single(UsbDescriptorParser.ParseEndpoints(descriptor));
        var hid = Assert.Single(UsbDescriptorParser.ParseHidDescriptors(descriptor));

        Assert.Equal(0, endpoint.InterfaceNumber);
        Assert.Equal(0x81, endpoint.EndpointAddress);
        Assert.Equal(3, endpoint.TransferType);
        Assert.Equal(64, endpoint.MaximumPacketSize);
        Assert.Equal(1, endpoint.Interval);
        Assert.Equal(2, hid.InterfaceProtocol);
        Assert.Equal((ushort)0x0111, hid.HidVersionBcd);
        Assert.Equal((ushort?)0x34, hid.ReportDescriptorLength);
    }

    [Fact]
    public void ParseUvcCameraModes_ReadsNativeResolutionAndFrameInterval()
    {
        var descriptor = CreateUvcDescriptor(1920, 1080, 333_333);

        var mode = Assert.Single(UsbDescriptorParser.ParseUvcCameraModes(descriptor));

        Assert.Equal(1920u, mode.Width);
        Assert.Equal(1080u, mode.Height);
        Assert.Equal("YUY2", mode.PixelFormat);
        Assert.InRange(mode.MaximumFrameRate, 29.99, 30.01);
    }

    [Theory]
    [InlineData(1, 3, 1, 1000d)]
    [InlineData(2, 3, 1, 125d)]
    [InlineData(3, 3, 4, 1000d)]
    [InlineData(3, 2, 1, null)]
    public void ServiceInterval_UsesNegotiatedUsbSpeedAndTransferType(
        byte speed,
        byte transferType,
        byte interval,
        double? expected)
    {
        Assert.Equal(
            expected,
            DeviceTopologySpecializedCapabilityProjector.CalculateServiceIntervalMicroseconds(
                speed,
                transferType,
                interval));
    }

    [Fact]
    public void UsbNodeConnectionInformationEx_UsesWindowsNativeAlignment()
    {
        var type = typeof(WindowsDeviceTopologyReader).Assembly.GetType(
            "ResourceManager.App.Infrastructure.DeviceTopology.WindowsUsbHubIoctlReader+UsbNodeConnectionInformationEx",
            throwOnError: true)!;
        var pipeType = typeof(WindowsDeviceTopologyReader).Assembly.GetType(
            "ResourceManager.App.Infrastructure.DeviceTopology.WindowsUsbHubIoctlReader+UsbPipeInfo",
            throwOnError: true)!;
        var capabilityType = typeof(WindowsDeviceTopologyReader).Assembly.GetType(
            "ResourceManager.App.Infrastructure.DeviceTopology.WindowsUsbPortCapabilityReader+UsbNodeConnectionInformationExV2",
            throwOnError: true)!;

        Assert.Equal(35, Marshal.SizeOf(type));
        Assert.Equal(11, Marshal.SizeOf(pipeType));
        Assert.Equal(1, capabilityType.StructLayoutAttribute?.Pack);
    }

    private static byte[] CreateUvcDescriptor(ushort width, ushort height, uint frameInterval)
    {
        var bytes = new List<byte>
        {
            9, 2, 75, 0, 1, 1, 0, 0x80, 50,
            9, 4, 0, 0, 1, 0x0E, 0x02, 0, 0,
            27, 0x24, 0x04, 1, 1,
            (byte)'Y', (byte)'U', (byte)'Y', (byte)'2'
        };
        bytes.AddRange(new byte[12]);
        bytes.AddRange([16, 1, 0, 0, 0, 0]);
        bytes.AddRange([
            30, 0x24, 0x05, 1, 0,
            (byte)width, (byte)(width >> 8),
            (byte)height, (byte)(height >> 8)
        ]);
        bytes.AddRange(new byte[12]);
        bytes.AddRange([
            (byte)frameInterval,
            (byte)(frameInterval >> 8),
            (byte)(frameInterval >> 16),
            (byte)(frameInterval >> 24),
            1,
            (byte)frameInterval,
            (byte)(frameInterval >> 8),
            (byte)(frameInterval >> 16),
            (byte)(frameInterval >> 24)
        ]);
        Assert.Equal(75, bytes.Count);
        return bytes.ToArray();
    }

}
