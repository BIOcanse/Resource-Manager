using System.Buffers.Binary;

namespace ResourceManager.ReadWrite.Windows.Devices;

// Shared selected read-only property query for both physical matcher and opened PnP disk capture.
internal static class WindowsStorageTrimQuery
{
    internal const uint PropertyId = 8;
    internal const int DescriptorSize = 12, MaximumBytes = 4096;
    internal static bool? Read(Func<byte[], int, byte[]?> query)
    {
        var response = query(WindowsStoragePropertyQuery.Create(PropertyId), MaximumBytes);
        return response is null ? null : Decode(response);
    }
    internal static bool? Decode(ReadOnlySpan<byte> response)
    {
        if (response.Length < DescriptorSize || response.Length > MaximumBytes) return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(response);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(response[4..]);
        if (version < DescriptorSize || version > size || size > response.Length || response[8] > 1) return null;
        return response[8] != 0; // DEVICE_TRIM_DESCRIPTOR.TrimEnabled; never send a TRIM request.
    }
}
