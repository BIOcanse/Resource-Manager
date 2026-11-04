using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Buffers.Binary;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal static class WindowsStoragePropertyQuery
{
    // Two 4-byte enums, AdditionalParameters[1], and SDK alignment: sizeof(STORAGE_PROPERTY_QUERY)=12.
    internal static byte[] Create(uint propertyId)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(query, propertyId);
        return query; // PropertyStandardQuery=0, additional parameter and padding zero.
    }
}
