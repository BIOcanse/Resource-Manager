using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class WindowsNativeAtomicFileCommitter
{
    internal delegate int RenameCall(SafeFileHandle file, IntPtr information, uint size);

    // Per-operation native boundary seam; production callers always use RenameNative.
    internal static void CommitReplace(string temporaryPath, string destinationPath, RenameCall rename)
        => CommitReplaceWithOpenReaders(temporaryPath, destinationPath, rename);

    private static void CommitReplaceWithOpenReaders(string temporaryPath, string destinationPath,
        RenameCall? rename = null)
    {
        var destination = ToExtendedLengthPath(destinationPath);
        using var file = OpenRenameSource(ToExtendedLengthPath(temporaryPath),
            0x40000000 | 0x00010000, 7, IntPtr.Zero, 3, 0x80000000, IntPtr.Zero);
        if (file.IsInvalid) ThrowRenameError("Cannot open the prepared file for atomic replacement.");
        if (!FlushFileBuffers(file)) ThrowRenameError("Cannot flush the prepared replacement file.");

        var name = Encoding.Unicode.GetBytes(ToNtPath(destination));
        var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileName)).ToInt32();
        var size = checked(Marshal.SizeOf<RenameInformation>() + name.Length);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // POSIX rename retains old handles; every attempt uses this same flushed handle and buffer.
            Marshal.StructureToPtr(new RenameInformation { Flags = 3, FileNameLength = checked((uint)name.Length) }, buffer, false);
            Marshal.Copy(name, 0, buffer + offset, name.Length);
            Marshal.WriteInt16(buffer, offset + name.Length, 0);
            rename ??= RenameNative;
            for (var attempt = 1; ; attempt++)
            {
                var status = rename(file, buffer, checked((uint)size));
                if (status >= 0)
                {
                    if (attempt > 1) LogTransient("rename", status, destinationPath, attempt, "recovered", temporaryPath);
                    return;
                }

                var attributes = status == StatusAccessDenied ? GetFileAttributes(destination) : uint.MaxValue;
                var readOnly = attributes != uint.MaxValue && (attributes & (uint)FileAttributes.ReadOnly) != 0;
                var retry = status == StatusAccessDenied && !readOnly && attempt < TransientAttemptLimit;
                LogTransient("rename", status, destinationPath, attempt,
                    retry ? "retry" : readOnly ? "readonly" : status == StatusAccessDenied ? "exhausted" : "failed", temporaryPath);
                if (!retry)
                    throw new IOException($"The atomic file replacement failed (NTSTATUS=0x{status:X8}).",
                        new Win32Exception(unchecked((int)RtlNtStatusToDosError(status))));
                Thread.Sleep(TransientRetryDelayMilliseconds);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static int RenameNative(SafeFileHandle file, IntPtr information, uint size)
        => NtSetInformationFile(file, out _, information, size, 65); // FileRenameInformationEx

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

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileAttributes(string path);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus,
        IntPtr information, uint size, int informationClass);
}
