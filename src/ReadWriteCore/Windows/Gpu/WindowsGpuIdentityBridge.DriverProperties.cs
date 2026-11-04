using ResourceManager.ReadWrite.Windows.Devices.Drivers;

namespace ResourceManager.ReadWrite.Windows.Gpu;

public sealed partial class WindowsGpuIdentityBridge
{
    internal delegate uint DriverPropertyQuery(uint devInst, in NativeMethods.DevPropKey key,
        out uint propertyType, byte[] buffer, ref uint size, uint flags);

    internal static string? ReadDriverVersion(uint devInst, DriverPropertyQuery query)
        => WindowsDeviceDriverPropertyReader.ReadString(devInst,
            new(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), 3),
            (uint node, in WindowsDeviceDriverPropertyReader.PropertyKey key, out uint type,
                byte[] buffer, ref uint size, uint flags) =>
            {
                var originalKey = new NativeMethods.DevPropKey(key.FormatId, key.PropertyId);
                return query(node, in originalKey, out type, buffer, ref size, flags);
            });

    internal static string? DecodeDriverVersion(uint type, byte[] buffer, uint size)
        => WindowsDeviceDriverPropertyReader.DecodeString(type, buffer, size);
}
