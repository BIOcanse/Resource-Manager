using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsVolumeMetadataNativeCalls
{
    SafeFileHandle OpenVolume(string path);
    bool Extents(SafeFileHandle handle, byte[] buffer, out uint returnedBytes);
    bool MountPaths(string root, char[] buffer, out uint returnedCharacters);
    bool FileSystem(string root, out DeviceTopologyVolumeFileSystemInformation? value);
    int LastError();
    uint ThreadErrorMode();
    bool SetThreadErrorMode(uint mode, out uint previous);
}

internal sealed partial class WindowsVolumeMetadataInformationNativeApi(IWindowsVolumeMetadataNativeCalls calls)
    : IWindowsVolumeMetadataInformationApi
{
    internal const int MoreData = 234;
    internal const int MaximumAttempts = 8;
    internal const int MaximumPathCharacters = 65536;
    internal const uint FailCriticalErrors = 1;
    internal WindowsVolumeMetadataInformationNativeApi() : this(new NativeCalls()) { }

    public IReadOnlyList<DeviceTopologyVolumeDiskExtent>? ReadDiskExtents(string root)
    {
        if (!WindowsVolumeSpaceInformationReader.TryVolumeIdentity(root, out _, out var canonical)) return null;
        using var handle = calls.OpenVolume(canonical[..^1]);
        if (handle.IsInvalid || handle.IsClosed) return null;
        var size = WindowsVolumeMetadataInformationDecoder.ExtentOffset + WindowsVolumeMetadataInformationDecoder.ExtentSize;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var buffer = new byte[size];
            var success = calls.Extents(handle, buffer, out var returned);
            if (returned > buffer.Length) return null;
            if (success) return WindowsVolumeMetadataInformationDecoder.Extents(buffer.AsSpan(0, (int)returned));
            // This IOCTL documents a returned count when the first complete structure fits.
            if (calls.LastError() != MoreData || returned < 32) return null;
            var required = WindowsVolumeMetadataInformationDecoder.RequiredExtentBytes(buffer.AsSpan(0, (int)returned));
            if (required <= size) return null;
            size = required;
        }
        return null;
    }

    public IReadOnlyList<string>? ReadMountPaths(string root)
    {
        if (!WindowsVolumeSpaceInformationReader.TryVolumeIdentity(root, out _, out var canonical)) return null;
        var size = 512;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var buffer = new char[size];
            var success = calls.MountPaths(canonical, buffer, out var returned);
            if (success)
                return returned <= buffer.Length ? WindowsVolumeMetadataInformationDecoder.MountPaths(buffer.AsSpan(0, (int)returned)) : null;
            if (calls.LastError() != MoreData || returned <= size || returned > MaximumPathCharacters) return null;
            size = (int)returned;
        }
        return null;
    }

    public DeviceTopologyVolumeFileSystemInformation? ReadFileSystem(string root)
    {
        if (!WindowsVolumeSpaceInformationReader.TryVolumeIdentity(root, out _, out var canonical)) return null;
        if (!calls.SetThreadErrorMode(calls.ThreadErrorMode() | FailCriticalErrors, out var previous)) return null;
        DeviceTopologyVolumeFileSystemInformation? value = null;
        bool restored;
        try { if (!calls.FileSystem(canonical, out var reported)) return null; value = reported; }
        finally { restored = calls.SetThreadErrorMode(previous, out _); }
        return restored ? value : null;
    }
}
