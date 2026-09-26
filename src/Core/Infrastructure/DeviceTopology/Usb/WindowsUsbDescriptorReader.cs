using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsUsbDescriptorReader
{
    private const uint IoctlUsbGetDescriptorFromNodeConnection = 0x00220410;
    private const int DescriptorRequestHeaderLength = 12;
    private const int ConfigurationDescriptorHeaderLength = 9;
    private const ushort MaximumStringDescriptorLength = 255;
    private const int MaximumConfigurationCount = 16;
    private const byte ConfigurationDescriptorType = 0x02;
    private const byte StringDescriptorType = 0x03;

    public static DeviceTopologyUsbDescriptor Read(
        SafeFileHandle hub,
        uint portNumber,
        DeviceTopologyUsbDescriptorHeader header)
    {
        var languageId = ReadPreferredLanguageId(hub, portNumber);
        var configuration = ReadCurrentConfiguration(hub, portNumber, header);

        var endpoints = configuration is null ? [] : UsbDescriptorParser.ParseEndpoints(configuration);
        var hidDescriptors = configuration is null ? [] : UsbDescriptorParser.ParseHidDescriptors(configuration);
        var cameraModes = configuration is null ? [] : UsbDescriptorParser.ParseUvcCameraModes(configuration);
        return new DeviceTopologyUsbDescriptor(
            UsbDescriptorParser.FormatUsbSpecification(header.UsbVersionBcd),
            UsbDescriptorParser.FormatDeviceRevision(header.DeviceRevisionBcd),
            UsbDescriptorParser.DescribeClass(header.DeviceClass, header.DeviceSubClass, header.DeviceProtocol),
            ReadString(hub, portNumber, header.ManufacturerIndex, languageId),
            ReadString(hub, portNumber, header.ProductIndex, languageId),
            ReadString(hub, portNumber, header.SerialNumberIndex, languageId),
            configuration is null
                ? []
                : UsbDescriptorParser.ParseInterfaceProtocols(configuration),
            endpoints,
            hidDescriptors,
            cameraModes);
    }

    private static ushort? ReadPreferredLanguageId(SafeFileHandle hub, uint portNumber)
    {
        var descriptor = ReadRawDescriptor(
            hub,
            portNumber,
            StringDescriptorType,
            descriptorIndex: 0,
            languageId: 0,
            MaximumStringDescriptorLength);
        if (descriptor is null)
        {
            return null;
        }

        var languageIds = UsbDescriptorParser.ParseLanguageIds(descriptor);
        return UsbDescriptorParser.SelectLanguageId(languageIds, CultureInfo.CurrentUICulture.LCID);
    }

    private static string? ReadString(
        SafeFileHandle hub,
        uint portNumber,
        byte descriptorIndex,
        ushort? languageId)
    {
        if (descriptorIndex == 0 || languageId is null)
        {
            return null;
        }

        var descriptor = ReadRawDescriptor(
            hub,
            portNumber,
            StringDescriptorType,
            descriptorIndex,
            languageId.Value,
            MaximumStringDescriptorLength);
        return descriptor is null ? null : UsbDescriptorParser.ParseStringDescriptor(descriptor);
    }

    private static byte[]? ReadCurrentConfiguration(
        SafeFileHandle hub,
        uint portNumber,
        DeviceTopologyUsbDescriptorHeader header)
    {
        if (header.CurrentConfigurationValue == 0 || header.ConfigurationCount == 0)
        {
            return null;
        }

        var count = Math.Min((int)header.ConfigurationCount, MaximumConfigurationCount);
        for (byte descriptorIndex = 0; descriptorIndex < count; descriptorIndex++)
        {
            var descriptor = ReadConfiguration(hub, portNumber, descriptorIndex);
            if (descriptor is null
                || !UsbDescriptorParser.TryReadConfigurationHeader(descriptor, out _, out var configurationValue)
                || configurationValue != header.CurrentConfigurationValue)
            {
                continue;
            }

            return descriptor;
        }

        return null;
    }

    private static byte[]? ReadConfiguration(
        SafeFileHandle hub,
        uint portNumber,
        byte descriptorIndex)
    {
        var header = ReadRawDescriptor(
            hub,
            portNumber,
            ConfigurationDescriptorType,
            descriptorIndex,
            languageId: 0,
            ConfigurationDescriptorHeaderLength);
        if (header is null
            || !UsbDescriptorParser.TryReadConfigurationHeader(header, out var totalLength, out _))
        {
            return null;
        }

        var descriptor = ReadRawDescriptor(
            hub,
            portNumber,
            ConfigurationDescriptorType,
            descriptorIndex,
            languageId: 0,
            totalLength);
        if (descriptor is null
            || descriptor.Length < totalLength
            || !UsbDescriptorParser.TryReadConfigurationHeader(descriptor, out var returnedLength, out _)
            || returnedLength != totalLength)
        {
            return null;
        }

        return descriptor.Length == totalLength ? descriptor : descriptor[..totalLength];
    }

    private static byte[]? ReadRawDescriptor(
        SafeFileHandle hub,
        uint portNumber,
        byte descriptorType,
        byte descriptorIndex,
        ushort languageId,
        ushort requestedLength)
    {
        if (requestedLength < 2)
        {
            return null;
        }

        var buffer = new byte[DescriptorRequestHeaderLength + requestedLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)), portNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(6, sizeof(ushort)),
            (ushort)((descriptorType << 8) | descriptorIndex));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, sizeof(ushort)), languageId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, sizeof(ushort)), requestedLength);

        ref var start = ref MemoryMarshal.GetArrayDataReference(buffer);
        if (!DeviceIoControl(
                hub,
                IoctlUsbGetDescriptorFromNodeConnection,
                ref start,
                (uint)buffer.Length,
                ref start,
                (uint)buffer.Length,
                out var bytesReturned,
                nint.Zero)
            || bytesReturned <= DescriptorRequestHeaderLength
            || bytesReturned > (uint)buffer.Length)
        {
            return null;
        }

        return buffer.AsSpan(
            DescriptorRequestHeaderLength,
            (int)bytesReturned - DescriptorRequestHeaderLength).ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        ref byte inBuffer,
        uint inBufferSize,
        ref byte outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
