using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed partial class WindowsDiskLayoutInformationNativeApi
{
    internal const int IdentityPropertyCapacity = 4096; // Same bounded descriptors used by the existing Identify source.

    private byte[]? ReadDeviceDescriptor(SafeFileHandle handle)
    {
        var descriptor = ReadIdentityProperty(handle, 0);
        if (descriptor is null || descriptor.Length < 36) return null;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
        if (version < 36 || version > descriptor.Length) return null;
        return descriptor;
    }

    private DiskHardwareIdentification? ReadHardwareIdentification(SafeFileHandle handle, byte[]? descriptor)
    {
        if (descriptor is null) return null;
        var protocol = StorageIdentifyProtocol.Ata;
        var serial = WindowsAtaSmartReader.ParseDeviceSerial(descriptor);
        if (serial is null)
        {
            protocol = StorageIdentifyProtocol.NvmeController;
            serial = WindowsNvmeHealthReader.ParseDeviceSerial(descriptor);
        }
        if (serial is null) return null;
        byte[]? identifiers;
        try { identifiers = ReadIdentityProperty(handle, 2); }
        catch (Exception error) when (WindowsVolumeSpaceInformationReader.IsSourceFailure(error)) { identifiers = null; }
        return new(protocol, serial, identifiers);
    }

    private byte[]? ReadIdentityProperty(SafeFileHandle handle, uint property)
    {
        var output = new byte[IdentityPropertyCapacity];
        var query = WindowsStoragePropertyQuery.Create(property);
        if (!calls.DeviceIdentity(handle, query, output, out var returned) || returned > output.Length || returned < 8) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4));
        if (size < 8 || size > returned) return null;
        return output[..(int)size];
    }
}
