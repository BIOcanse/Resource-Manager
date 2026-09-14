using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class WindowsNativeAtomicFileCommitter
{
    private static void CommitReplaceWithOpenReaders(string temporaryPath, string destinationPath)
    {
        var destination = ToExtendedLengthPath(destinationPath);
        using var file = OpenRenameSource(ToExtendedLengthPath(temporaryPath),
            0x40000000 | 0x00010000, 7, IntPtr.Zero, 3, 0x80000000, IntPtr.Zero);
        if (file.IsInvalid) ThrowRenameError("Cannot open the prepared file for atomic replacement.");
        if (!FlushFileBuffers(file)) ThrowRenameError("Cannot flush the prepared replacement file.");

        var name = Encoding.Unicode.GetBytes(destination);
        var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)).ToInt32();
        var size = checked(Marshal.SizeOf<RenameInformation>() + name.Length);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // POSIX rename retains old open handles while atomically changing the pathname.
            Marshal.StructureToPtr(new RenameInformation { Flags = 3, FileNameLength = checked((uint)name.Length) }, buffer, false);
            Marshal.Copy(name, 0, buffer + offset, name.Length);
            Marshal.WriteInt16(buffer, offset + name.Length, 0);
            if (!SetRenameInformation(file, 22, buffer, checked((uint)size)))
                ThrowRenameError("The atomic file replacement failed.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ThrowRenameError(string message) =>
        throw new IOException(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint Flags;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public ushort FileName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenRenameSource(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetRenameInformation(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
