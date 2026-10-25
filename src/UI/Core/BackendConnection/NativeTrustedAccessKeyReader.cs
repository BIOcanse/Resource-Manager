using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.NativeUi;

// A request checks identity and change metadata, never an asynchronous notification alone.
// Only a stable, fully validated read can populate the cache; every failure drops it.
internal sealed class NativeTrustedAccessKeyReader
{
    private readonly object sync = new();
    private readonly string[] paths;
    private readonly FileStamp[] accepted;
    private readonly FileStamp[] current;
    private readonly Func<string?> validatedRead;
    private bool cached;
    private string? key;

    internal NativeTrustedAccessKeyReader(string packageRoot, Func<string?>? validatedRead = null)
    {
        var path = TrustedAccessKeyFile.PathForPackage(packageRoot);
        var components = new List<string>();
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            if (components.Count == 513)
                throw new UnauthorizedAccessException("Trusted access storage path exceeds its component bound.");
            components.Add(item);
        }
        paths = components.ToArray();
        accepted = new FileStamp[paths.Length];
        current = new FileStamp[paths.Length];
        this.validatedRead = validatedRead ?? (() => TrustedAccessKeyFile.Read(packageRoot));
    }

    internal string? Read()
    {
        lock (sync)
        {
            try
            {
                Capture(current);
                if (cached && current.AsSpan().SequenceEqual(accepted)) return key;
                cached = false;
                key = null;
                var value = validatedRead();
                Capture(accepted);
                if (!current.AsSpan().SequenceEqual(accepted))
                    throw new IOException("The default key storage changed during validation.");
                key = value;
                cached = true;
                return value;
            }
            catch
            {
                cached = false;
                key = null;
                throw;
            }
        }
    }

    private void Capture(FileStamp[] destination)
    {
        for (var index = 0; index < paths.Length; index++)
            destination[index] = ReadStamp(paths[index], includeChangeMetadata: index < 2);
    }

    private static FileStamp ReadStamp(string path, bool includeChangeMetadata)
    {
        // Opening the link itself makes each ancestor replacement/reparse visible.
        using var handle = CreateFileW(path, 0x80, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return default;
            throw new IOException("Cannot inspect default key storage.", new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var identity)
            || !GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>()))
            throw new IOException("Cannot inspect default key metadata.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((basic.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Trusted access storage cannot use reparse points in its path.");
        return new FileStamp(true, identity.Volume, identity.IndexHigh, identity.IndexLow,
            includeChangeMetadata ? basic.LastWriteTime : 0, includeChangeMetadata ? basic.ChangeTime : 0,
            identity.SizeHigh, identity.SizeLow, basic.Attributes);
    }

    private readonly record struct FileStamp(bool Exists, uint Volume, uint IndexHigh, uint IndexLow,
        long LastWriteTime, long ChangeTime, uint SizeHigh, uint SizeLow, FileAttributes Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        internal FileAttributes Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share,
        IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type,
        out BasicInformation information, uint size);
}
