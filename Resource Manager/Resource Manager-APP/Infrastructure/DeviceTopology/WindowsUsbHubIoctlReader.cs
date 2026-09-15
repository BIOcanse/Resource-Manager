using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Messages;
using System.Buffers.Binary;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsUsbHubIoctlReader
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlUsbGetNodeInformation = 0x00220408;
    private const uint IoctlUsbGetNodeConnectionName = 0x00220414;
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x00220448;
    private const uint IoctlUsbGetNodeConnectionDriverKeyName = 0x00220420;
    private const int MaximumUsbPipeCount = 30;
    private const int MaximumHubPathBytes = 64 * 1024;

    private static readonly Guid UsbHubInterfaceGuid = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    public static DeviceTopologyUsbPortSnapshot ReadSnapshot()
    {
        var hubPaths = EnumerateHubDevicePaths(out var enumerateError);
        var ports = new List<DeviceTopologyUsbPort>();
        string? firstError = enumerateError;

        foreach (var hubPath in hubPaths)
        {
            using var hub = OpenHub(hubPath);
            if (hub.IsInvalid)
            {
                firstError ??= $"Opening the USB hub failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                continue;
            }

            var portCount = ReadHubPortCount(hub);
            if (portCount == 0)
            {
                continue;
            }

            for (uint portNumber = 1; portNumber <= portCount; portNumber++)
            {
                var port = ReadPort(hub, hubPath, portNumber);
                if (port is not null)
                {
                    ports.Add(port);
                }
            }
        }

        var byDriverKey = ports
            .Where(static port => !string.IsNullOrWhiteSpace(port.DriverKeyName))
            .GroupBy(static port => NormalizeDriverKey(port.DriverKeyName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new DeviceTopologyUsbPortSnapshot(ports, byDriverKey, firstError);
    }

    private static IReadOnlyList<string> EnumerateHubDevicePaths(out string? error)
    {
        error = null;
        var interfaceGuid = UsbHubInterfaceGuid;
        var deviceInfoSet = SetupDiGetClassDevs(
            ref interfaceGuid,
            null,
            nint.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new nint(-1))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return [];
        }

        try
        {
            var paths = new List<string>();
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, nint.Zero, ref interfaceGuid, index, ref data))
                {
                    var lastError = Marshal.GetLastWin32Error();
                    if (lastError == ErrorNoMoreItems)
                    {
                        break;
                    }

                    error ??= new Win32Exception(lastError).Message;
                    break;
                }

                var path = ReadDeviceInterfacePath(deviceInfoSet, ref data, ref error);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? ReadDeviceInterfacePath(
        nint deviceInfoSet,
        ref SpDeviceInterfaceData data,
        ref string? error)
    {
        SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref data,
            nint.Zero,
            0,
            out var requiredSize,
            nint.Zero);
        var lastError = Marshal.GetLastWin32Error();
        if (lastError != ErrorInsufficientBuffer || requiredSize == 0)
        {
            error ??= new Win32Exception(lastError).Message;
            return null;
        }

        var detailData = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailData, nint.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref data,
                    detailData,
                    requiredSize,
                    out _,
                    nint.Zero))
            {
                error ??= new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            return Marshal.PtrToStringUni(nint.Add(detailData, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detailData);
        }
    }

    private static SafeFileHandle OpenHub(string hubPath)
    {
        return CreateFile(
            hubPath,
            0,
            FileShareRead | FileShareWrite,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero);
    }

    private static byte ReadHubPortCount(SafeFileHandle hub)
    {
        Span<byte> nodeInformation = stackalloc byte[80];
        MemoryMarshal.Write(nodeInformation, 0);
        return DeviceIoControl(
            hub,
            IoctlUsbGetNodeInformation,
            ref MemoryMarshal.GetReference(nodeInformation),
            (uint)nodeInformation.Length,
            ref MemoryMarshal.GetReference(nodeInformation),
            (uint)nodeInformation.Length,
            out _,
            nint.Zero)
            ? nodeInformation[6]
            : (byte)0;
    }

    private static DeviceTopologyUsbPort? ReadPort(SafeFileHandle hub, string hubPath, uint portNumber)
    {
        var infoSize = Marshal.SizeOf<UsbNodeConnectionInformationEx>();
        var buffer = new byte[infoSize + (Marshal.SizeOf<UsbPipeInfo>() * MaximumUsbPipeCount)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)), portNumber);
        ref var start = ref MemoryMarshal.GetArrayDataReference(buffer);
        if (!DeviceIoControl(
                hub,
                IoctlUsbGetNodeConnectionInformationEx,
                ref start,
                (uint)buffer.Length,
                ref start,
                (uint)buffer.Length,
                out var bytesReturned,
                nint.Zero)
            || bytesReturned < (uint)infoSize)
        {
            return null;
        }

        var info = MemoryMarshal.Read<UsbNodeConnectionInformationEx>(buffer);

        var connectorProperties = WindowsUsbPortConnectorPropertiesReader.Read(hub, portNumber);
        var capability = WindowsUsbPortCapabilityReader.Read(hub, portNumber);
        var descriptorHeader = new DeviceTopologyUsbDescriptorHeader(
            info.DeviceDescriptor.BcdUsb,
            info.DeviceDescriptor.BcdDevice,
            info.DeviceDescriptor.BDeviceClass,
            info.DeviceDescriptor.BDeviceSubClass,
            info.DeviceDescriptor.BDeviceProtocol,
            info.DeviceDescriptor.IManufacturer,
            info.DeviceDescriptor.IProduct,
            info.DeviceDescriptor.ISerialNumber,
            info.DeviceDescriptor.BNumConfigurations,
            info.CurrentConfigurationValue);
        var descriptor = info.ConnectionStatus == 1
            ? WindowsUsbDescriptorReader.Read(hub, portNumber, descriptorHeader)
            : CreateBaseDescriptor(descriptorHeader);

        return new DeviceTopologyUsbPort(
            hubPath,
            portNumber,
            info.ConnectionStatus == 1,
            DescribeConnectionStatus(info.ConnectionStatus),
            DescribeNegotiatedUsbSpeed(info.ConnectionStatus, info.Speed, capability),
            info.DeviceDescriptor.IdVendor,
            info.DeviceDescriptor.IdProduct,
            info.DeviceAddress,
            info.DeviceIsHub != 0,
            ReadDriverKeyName(hub, portNumber),
            connectorProperties,
            capability,
            descriptor,
            info.DeviceIsHub != 0 ? ReadDownstreamHubDevicePath(hub, portNumber) : null,
            info.Speed);
    }

    private static DeviceTopologyUsbDescriptor CreateBaseDescriptor(DeviceTopologyUsbDescriptorHeader header)
    {
        return new DeviceTopologyUsbDescriptor(
            UsbDescriptorParser.FormatUsbSpecification(header.UsbVersionBcd),
            UsbDescriptorParser.FormatDeviceRevision(header.DeviceRevisionBcd),
            UsbDescriptorParser.DescribeClass(header.DeviceClass, header.DeviceSubClass, header.DeviceProtocol),
            null,
            null,
            null,
            [],
            [],
            [],
            []);
    }

    private static string? ReadDriverKeyName(SafeFileHandle hub, uint portNumber)
    {
        var buffer = new byte[512];
        MemoryMarshal.Write(buffer.AsSpan(0, sizeof(uint)), portNumber);
        if (!DeviceIoControl(
                hub,
                IoctlUsbGetNodeConnectionDriverKeyName,
                buffer,
                (uint)buffer.Length,
                buffer,
                (uint)buffer.Length,
                out _,
                nint.Zero))
        {
            return null;
        }

        var actualLength = MemoryMarshal.Read<uint>(buffer.AsSpan(sizeof(uint), sizeof(uint)));
        var stringByteLength = actualLength > 8 && actualLength <= buffer.Length
            ? (int)actualLength - 8
            : buffer.Length - 8;
        if (stringByteLength <= 0)
        {
            return null;
        }

        var key = Encoding.Unicode.GetString(buffer, 8, stringByteLength).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private static string? ReadDownstreamHubDevicePath(SafeFileHandle hub, uint portNumber)
    {
        var buffer = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)), portNumber);
        var succeeded = DeviceIoControl(
            hub,
            IoctlUsbGetNodeConnectionName,
            buffer,
            (uint)buffer.Length,
            buffer,
            (uint)buffer.Length,
            out var bytesReturned,
            nint.Zero);
        var actualLength = buffer.Length >= 8
            ? BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sizeof(uint), sizeof(uint)))
            : 0;

        if (actualLength > buffer.Length && actualLength <= MaximumHubPathBytes)
        {
            buffer = new byte[actualLength];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)), portNumber);
            succeeded = DeviceIoControl(
                hub,
                IoctlUsbGetNodeConnectionName,
                buffer,
                (uint)buffer.Length,
                buffer,
                (uint)buffer.Length,
                out bytesReturned,
                nint.Zero);
            actualLength = buffer.Length >= 8
                ? BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sizeof(uint), sizeof(uint)))
                : 0;
        }

        if (!succeeded || actualLength <= 8)
        {
            return null;
        }

        var availableLength = Math.Min(
            buffer.Length,
            bytesReturned > 0 ? checked((int)bytesReturned) : buffer.Length);
        var stringByteLength = Math.Min(checked((int)actualLength), availableLength) - 8;
        if (stringByteLength <= 0)
        {
            return null;
        }

        var path = Encoding.Unicode.GetString(buffer, 8, stringByteLength).TrimEnd('\0').Trim();
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = @"\\?\" + path[4..];
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            path = @"\\?\" + path[4..];
        }
        else if (!path.StartsWith('\\') && path.Contains('#'))
        {
            path = @"\\?\" + path;
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>没有设备或还没协商出速率时返回 null，由前端出占位文案。</summary>
    internal static string? DescribeNegotiatedUsbSpeed(
        uint connectionStatus,
        byte speed,
        DeviceTopologyUsbPortCapability? capability)
    {
        if (connectionStatus != 1)
        {
            return null;
        }

        if (capability?.OperatingAtSuperSpeedPlusOrHigher == true)
        {
            return "USB SuperSpeedPlus / 10Gbps+";
        }

        if (capability?.OperatingAtSuperSpeedOrHigher == true)
        {
            return "USB 3.x SuperSpeed / 5Gbps+";
        }

        return speed switch
        {
            0 => "USB Low-Speed / 1.5Mbps",
            1 => "USB Full-Speed / 12Mbps",
            2 => "USB 2.0 High-Speed / 480Mbps",
            3 => "USB 3.x SuperSpeed / 5Gbps+",
            _ => $"USB speed {speed}"
        };
    }

    /// <summary>没有可判定的能力时返回 null。</summary>
    internal static string? DescribeMaximumUsbSpeed(
        IEnumerable<DeviceTopologyUsbPortCapability?> capabilities)
    {
        var values = capabilities.Where(static capability => capability is not null).ToArray();
        if (values.Any(static capability => capability?.SuperSpeedPlusCapableOrHigher == true))
        {
            return "USB SuperSpeedPlus / 10Gbps+";
        }

        if (values.Any(static capability => capability?.SuperSpeedCapableOrHigher == true
            || capability?.SupportsUsb30 == true))
        {
            return "USB 3.x SuperSpeed / 5Gbps+";
        }

        if (values.Any(static capability => capability?.SupportsUsb20 == true))
        {
            return "USB 2.0 High-Speed / 480Mbps";
        }

        if (values.Any(static capability => capability?.SupportsUsb11 == true))
        {
            return "USB Full-Speed / 12Mbps";
        }

        return null;
    }

    private static BackendMessage DescribeConnectionStatus(uint status)
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

    public static string NormalizeDriverKey(string? driverKey)
    {
        return string.IsNullOrWhiteSpace(driverKey) ? string.Empty : driverKey.Trim().ToUpperInvariant();
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        nint hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inBuffer,
        uint inBufferSize,
        byte[] outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbDeviceDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public ushort BcdUsb;
        public byte BDeviceClass;
        public byte BDeviceSubClass;
        public byte BDeviceProtocol;
        public byte BMaxPacketSize0;
        public ushort IdVendor;
        public ushort IdProduct;
        public ushort BcdDevice;
        public byte IManufacturer;
        public byte IProduct;
        public byte ISerialNumber;
        public byte BNumConfigurations;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbNodeConnectionInformationEx
    {
        public uint ConnectionIndex;
        public UsbDeviceDescriptor DeviceDescriptor;
        public byte CurrentConfigurationValue;
        public byte Speed;
        public byte DeviceIsHub;
        public ushort DeviceAddress;
        public uint NumberOfOpenPipes;
        public uint ConnectionStatus;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbEndpointDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public byte BEndpointAddress;
        public byte BmAttributes;
        public ushort WMaxPacketSize;
        public byte BInterval;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbPipeInfo
    {
        public UsbEndpointDescriptor EndpointDescriptor;
        public uint ScheduleOffset;
    }
}
