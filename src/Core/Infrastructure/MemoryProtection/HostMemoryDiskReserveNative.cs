using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

internal static class HostMemoryDiskReserveNative
{
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    internal static IOException Error(string operation) =>
        new($"{operation}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    internal static FileStream OpenFile(string path, FileMode mode, FileShare share, FileAccess access = FileAccess.ReadWrite)
    {
        var desiredAccess = access == FileAccess.Read ? 0x80000000u : 0x80000000u | 0x40000000u;
        var handle = CreateFile(path, desiredAccess, share, 0,
            mode == FileMode.CreateNew ? 1u : mode == FileMode.OpenOrCreate ? 4u : 3u, OpenReparsePoint, 0);
        if (handle.IsInvalid) { handle.Dispose(); throw Error("open-reserve-file"); }
        try
        {
            RejectReparse(handle);
            var information = Information(handle);
            if (information.Directory != 0 || information.NumberOfLinks != 1)
                throw new IOException("reserve-file-must-be-a-regular-single-link-file");
            return new FileStream(handle, access, 1);
        }
        catch { handle.Dispose(); throw; }
    }

    // Retaining each directory handle prevents rename/replacement while paths are in use.
    // Check before creating each child, rather than following an existing junction in CreateDirectory.
    internal static List<SafeFileHandle> HoldDirectories(string location)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var root = Path.GetPathRoot(location)!;
            var current = root;
            var parts = location[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var index = -1; index < parts.Length; index++)
            {
                if (index >= 0)
                {
                    current = Path.Combine(current, parts[index]);
                    Directory.CreateDirectory(current);
                }
                var handle = CreateFile(current, 0, FileShare.ReadWrite, 0, 3, BackupSemantics | OpenReparsePoint, 0);
                if (handle.IsInvalid) { handle.Dispose(); throw Error("open-reserve-directory"); }
                handles.Add(handle);
                RejectReparse(handle);
            }
            return handles;
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    internal static void RejectReparse(SafeFileHandle handle)
    {
        if (!GetAttributes(handle, 9, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw Error("query-reserve-attributes");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("reserve-reparse-points-are-not-supported");
    }

    internal static void DisableCompression(FileStream stream)
    {
        if (!GetAttributes(stream.SafeFileHandle, 9, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw Error("query-reserve-attributes");
        if ((info.Attributes & (uint)FileAttributes.SparseFile) != 0)
            throw new IOException("sparse-reserve-files-are-not-supported");
        if ((info.Attributes & (uint)FileAttributes.Compressed) != 0)
        {
            ushort format = 0; // COMPRESSION_FORMAT_NONE; do not change the user's directory compression policy.
            if (!DeviceIoControl(stream.SafeFileHandle, 0x0009C040, ref format, 2, 0, 0, out _, 0))
                throw Error("disable-reserve-compression");
        }
        if (!GetAttributes(stream.SafeFileHandle, 9, out info, (uint)Marshal.SizeOf<AttributeTagInfo>()) ||
            (info.Attributes & (uint)(FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
            throw new IOException("reserve-file-is-compressed-or-sparse");
    }

    internal static StandardInfo Information(SafeFileHandle handle)
    {
        if (!GetStandardInfo(handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>()))
            throw Error("query-reserve-allocation");
        return info;
    }

    // FileAllocationInfo (5) actually allocates/frees clusters, independently of logical EOF.
    // https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_allocation_info
    internal static void Allocate(FileStream stream, long bytes)
    {
        if (!SetFileInformationByHandle(stream.SafeFileHandle, 5, ref bytes, sizeof(long)))
            throw Error("set-reserve-allocation");
    }

    internal static ulong Verify(FileStream stream, string path)
    {
        DisableCompression(stream);
        var info = Information(stream.SafeFileHandle);
        if (info.AllocationSize < info.EndOfFile || info.NumberOfLinks != 1)
            throw new IOException("reserve-physical-allocation-is-incomplete");
        Marshal.SetLastPInvokeError(0);
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
            throw Error("verify-reserve-disk-size");
        // For a normal uncompressed file this API reports EOF, not rounded allocation size.
        // FileStandardInfo supplies allocated clusters; both checks are required.
        if ((((ulong)high << 32) | low) < (ulong)info.EndOfFile)
            throw new IOException("reserve-disk-size-is-incomplete");
        return checked((ulong)info.AllocationSize);
    }

    internal static (ulong Cluster, ulong Available) Volume(string location)
    {
        var volumePath = new System.Text.StringBuilder(32768);
        if (!GetVolumePathName(location, volumePath, volumePath.Capacity)) throw Error("query-reserve-volume");
        if (!GetDiskFreeSpace(volumePath.ToString(), out var sectors, out var bytes, out _, out _) ||
            !GetDiskFreeSpaceEx(location, out var available, out _, out _)) throw Error("query-reserve-free-space");
        var cluster = (ulong)sectors * bytes;
        if (cluster == 0) throw new IOException("invalid-reserve-allocation-unit");
        return (cluster, available);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { internal uint Attributes; internal uint ReparseTag; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle file, int kind, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle file, int kind, out AttributeTagInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref long info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, ref ushort input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, System.Text.StringBuilder volumePath, int length);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint clusters);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
