using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsDeviceTopologyNativeReader
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegMultiSz = 7;
    private const int CrSuccess = 0;

    private static readonly nint InvalidHandleValue = new(-1);

    public static DeviceTopologyNativeSnapshot ReadSnapshot()
    {
        var deviceInfoSet = SetupDiGetClassDevs(
            nint.Zero,
            null,
            nint.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            return new DeviceTopologyNativeSnapshot(
                new Dictionary<string, DeviceTopologyNativeDevice>(StringComparer.OrdinalIgnoreCase),
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        try
        {
            var devices = new Dictionary<string, DeviceTopologyNativeDevice>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                var info = new SpDevInfoData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDevInfoData>()
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    return new DeviceTopologyNativeSnapshot(devices, new Win32Exception(error).Message);
                }

                var deviceId = ReadDeviceInstanceId(deviceInfoSet, ref info);
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    continue;
                }

                var normalizedDeviceId = NormalizeDeviceId(deviceId);
                var devNodeState = ReadDevNodeState(info.DevInst);
                devices[normalizedDeviceId] = new DeviceTopologyNativeDevice(
                    normalizedDeviceId,
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.FriendlyName),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.DeviceDescription),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.Manufacturer),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.Service),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.Driver),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.Class),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.ClassGuid),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.EnumeratorName),
                    ReadStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.LocationInformation),
                    ReadMultiStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.LocationPaths),
                    ReadMultiStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.HardwareId),
                    ReadMultiStringProperty(deviceInfoSet, ref info, SetupDiRegistryProperty.CompatibleIds),
                    ReadParentDeviceId(info.DevInst),
                    devNodeState?.Status,
                    devNodeState?.ProblemCode);
            }

            return new DeviceTopologyNativeSnapshot(devices, null);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? ReadDeviceInstanceId(nint deviceInfoSet, ref SpDevInfoData info)
    {
        var builder = new StringBuilder(256);
        if (SetupDiGetDeviceInstanceId(deviceInfoSet, ref info, builder, (uint)builder.Capacity, out var requiredSize))
        {
            return Clean(builder.ToString());
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || requiredSize == 0)
        {
            return null;
        }

        builder = new StringBuilder((int)requiredSize);
        return SetupDiGetDeviceInstanceId(deviceInfoSet, ref info, builder, requiredSize, out _)
            ? Clean(builder.ToString())
            : null;
    }

    private static string? ReadStringProperty(
        nint deviceInfoSet,
        ref SpDevInfoData info,
        SetupDiRegistryProperty property)
    {
        var data = ReadRegistryProperty(deviceInfoSet, ref info, property);
        if (data is null)
        {
            return null;
        }

        var value = DecodeRegistryString(data.Value.Buffer, data.Value.Type)
            .FirstOrDefault();
        return Clean(value);
    }

    private static IReadOnlyList<string> ReadMultiStringProperty(
        nint deviceInfoSet,
        ref SpDevInfoData info,
        SetupDiRegistryProperty property)
    {
        var data = ReadRegistryProperty(deviceInfoSet, ref info, property);
        return data is null ? [] : DecodeRegistryString(data.Value.Buffer, data.Value.Type);
    }

    private static RegistryPropertyData? ReadRegistryProperty(
        nint deviceInfoSet,
        ref SpDevInfoData info,
        SetupDiRegistryProperty property)
    {
        var buffer = new byte[4096];
        if (SetupDiGetDeviceRegistryProperty(
                deviceInfoSet,
                ref info,
                (uint)property,
                out var propertyType,
                buffer,
                (uint)buffer.Length,
                out var requiredSize))
        {
            return new RegistryPropertyData(propertyType, SliceBuffer(buffer, requiredSize));
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer || requiredSize == 0)
        {
            return null;
        }

        buffer = new byte[(int)requiredSize];
        return SetupDiGetDeviceRegistryProperty(
            deviceInfoSet,
            ref info,
            (uint)property,
            out propertyType,
            buffer,
            requiredSize,
            out requiredSize)
            ? new RegistryPropertyData(propertyType, SliceBuffer(buffer, requiredSize))
            : null;
    }

    private static IReadOnlyList<string> DecodeRegistryString(byte[] buffer, uint propertyType)
    {
        if (buffer.Length == 0 || propertyType is not (RegSz or RegExpandSz or RegMultiSz))
        {
            return [];
        }

        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static byte[] SliceBuffer(byte[] buffer, uint requiredSize)
    {
        var length = requiredSize > 0 && requiredSize <= buffer.Length ? (int)requiredSize : buffer.Length;
        var result = new byte[length];
        Array.Copy(buffer, result, length);
        return result;
    }

    private static string? ReadParentDeviceId(uint devInst)
    {
        if (CM_Get_Parent(out var parentDevInst, devInst, 0) != CrSuccess)
        {
            return null;
        }

        var bufferLength = 0u;
        if (CM_Get_Device_ID_Size(out bufferLength, parentDevInst, 0) != CrSuccess)
        {
            bufferLength = 512;
        }
        else
        {
            bufferLength++;
        }

        var builder = new StringBuilder((int)Math.Max(bufferLength, 2u));
        return CM_Get_Device_ID(parentDevInst, builder, builder.Capacity, 0) == CrSuccess
            ? NormalizeDeviceId(builder.ToString())
            : null;
    }

    private static DevNodeState? ReadDevNodeState(uint devInst)
    {
        return CM_Get_DevNode_Status(out var status, out var problemCode, devInst, 0) == CrSuccess
            ? new DevNodeState(status, problemCode)
            : null;
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId)
            ? string.Empty
            : deviceId.Trim().Replace(@"\\", @"\", StringComparison.Ordinal).ToUpperInvariant();
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint SetupDiGetClassDevs(
        nint classGuid,
        string? enumerator,
        nint hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceId(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static extern int CM_Get_Device_ID_Size(out uint bufferLength, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(
        uint devInst,
        StringBuilder buffer,
        int bufferLength,
        uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    private static extern int CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint devInst,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    private readonly record struct RegistryPropertyData(uint Type, byte[] Buffer);

    private readonly record struct DevNodeState(uint Status, uint ProblemCode);

    private enum SetupDiRegistryProperty : uint
    {
        DeviceDescription = 0x00000000,
        HardwareId = 0x00000001,
        CompatibleIds = 0x00000002,
        Service = 0x00000004,
        Class = 0x00000007,
        ClassGuid = 0x00000008,
        Driver = 0x00000009,
        Manufacturer = 0x0000000B,
        FriendlyName = 0x0000000C,
        LocationInformation = 0x0000000D,
        EnumeratorName = 0x00000016,
        LocationPaths = 0x00000023
    }
}
