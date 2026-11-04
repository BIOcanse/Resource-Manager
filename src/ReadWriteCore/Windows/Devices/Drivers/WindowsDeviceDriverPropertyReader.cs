using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Drivers;

/// <summary>Reads declarations on the exact existing CM devInst; does not enumerate INF packages.</summary>
internal static class WindowsDeviceDriverPropertyReader
{
    internal const uint StringType = 0x12;
    internal const uint FileTimeType = 0x10;
    private const uint BufferSmall = 26;
    private const int MaximumStringBytes = 4096;
    private static readonly Guid DriverPropertySet = new("A8B865DD-2E3D-4094-AD97-E593A70C75D6");
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PropertyKey(Guid FormatId, uint PropertyId);
    internal delegate uint PropertyQuery(uint devInst, in PropertyKey key, out uint type, byte[] buffer, ref uint size, uint flags);

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]
    internal static DeviceTopologyDriverInformation Read(uint devInst, string deviceInstanceId)
        => Read(devInst, deviceInstanceId, QueryNative);

    /// <summary>Reads the exact CM devnode's declared driver version; the caller must first locate
    /// the devnode from a proven device relationship. This does not enumerate driver packages.</summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]
    internal static string? ReadDriverVersion(uint devInst)
        => ReadString(devInst, new(DriverPropertySet, 3), QueryNative);

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows device-bound driver package changed")]
    internal static DeviceTopologyDriverInformation Read(uint devInst, string deviceInstanceId, PropertyQuery query)
    {
        var provider = ReadString(devInst, new(DriverPropertySet, 9), query);
        var key = new PropertyKey(DriverPropertySet, 2);
        var buffer = new byte[8];
        uint size = 8;
        var status = query(devInst, in key, out var type, buffer, ref size, 0);
        var date = status == 0 ? DecodeDate(type, buffer, size) : null;
        var version = ReadString(devInst, new(DriverPropertySet, 3), query);
        return new(deviceInstanceId, provider, date) { VersionDeclared = version };
    }

    internal static string? ReadString(uint devInst, PropertyKey key, PropertyQuery query)
    {
        var buffer = new byte[512];
        uint size = (uint)buffer.Length;
        var status = query(devInst, in key, out var type, buffer, ref size, 0);
        if (status == BufferSmall && size > buffer.Length && size <= MaximumStringBytes && size % 2 == 0)
        {
            buffer = new byte[size];
            status = query(devInst, in key, out type, buffer, ref size, 0);
        }
        return status == 0 ? DecodeString(type, buffer, size) : null;
    }

    internal static string? DecodeString(uint type, byte[] buffer, uint size)
    {
        if (type != StringType || size < 2 || size > buffer.Length || size > MaximumStringBytes
            || size % 2 != 0 || buffer[size - 2] != 0 || buffer[size - 1] != 0) return null;
        try
        {
            var value = StrictUnicode.GetString(buffer, 0, checked((int)size - 2));
            return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? null : value;
        }
        catch (DecoderFallbackException) { return null; }
    }

    internal static string? DecodeDate(uint type, byte[] buffer, uint size)
    {
        if (type != FileTimeType || size != 8 || buffer.Length < 8) return null;
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        // Zero/negative cannot represent a meaningful INF DriverVer declaration.
        if (fileTime <= 0) return null;
        try { return DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static extern uint QueryNative(uint devInst, in PropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint size, uint flags);
}
