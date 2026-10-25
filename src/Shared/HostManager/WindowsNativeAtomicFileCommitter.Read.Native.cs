using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class WindowsNativeAtomicFileCommitter
{
    // NtCreateFile returns the open's own status, before managed exception construction can
    // overwrite thread-local last status with resource-DLL lookups. No RtlGetLastNtStatus is used.
    internal static unsafe int OpenReadNative(string nativePath, FileOptions options, out SafeFileHandle file)
    {
        if (nativePath.Length > 32767) throw new PathTooLongException();
        fixed (char* name = nativePath)
        {
            var unicodeName = new UnicodeString
            {
                Length = checked((ushort)(nativePath.Length * sizeof(char))),
                MaximumLength = checked((ushort)(nativePath.Length * sizeof(char))),
                Buffer = (IntPtr)name
            };
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                ObjectName = (IntPtr)(&unicodeName),
                Attributes = 0x40 // OBJ_CASE_INSENSITIVE
            };
            // NtCreateFile requires explicit SYNCHRONIZE for SYNCHRONOUS_IO_NONALERT.
            // GENERIC_READ alone returned C000000D in the synchronous-open regression cases.
            uint access = 0x80100000; // GENERIC_READ | SYNCHRONIZE
            uint createOptions = 0x40 | 0x400000; // NON_DIRECTORY_FILE | OPEN_NO_RECALL, as FileStream uses
            if ((options & FileOptions.Asynchronous) == 0) createOptions |= 0x20; // SYNCHRONOUS_IO_NONALERT
            if ((options & FileOptions.WriteThrough) != 0) createOptions |= 2;
            if ((options & FileOptions.SequentialScan) != 0) createOptions |= 4;
            if ((options & FileOptions.RandomAccess) != 0) createOptions |= 0x800;
            if ((options & FileOptions.DeleteOnClose) != 0)
            {
                access |= 0x10000; // DELETE
                createOptions |= 0x1000;
            }
            var status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero,
                0, (uint)CommittedReadShare, 1, createOptions, IntPtr.Zero, 0); // FILE_OPEN
            file = new SafeFileHandle(handle, ownsHandle: true);
            return status;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out IntPtr file, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock ioStatus, IntPtr allocationSize, uint fileAttributes, uint share,
        uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
}
