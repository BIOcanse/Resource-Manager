using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal enum WindowsNativeFileDeleteResult : byte
{
    Deleted = 1,
    NotFound = 2
}

internal static partial class WindowsNativeAtomicFileCommitter
{
    private const string DevicePathPrefix = @"\\.\";
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPathPrefix = @"\\?\UNC\";
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint MoveFileWriteThrough = 0x0000_0008;

    public static void CommitNew(string temporaryPath, string destinationPath)
        => Commit(temporaryPath, destinationPath, replaceExisting: false);

    public static void CommitReplace(string temporaryPath, string destinationPath)
        => Commit(temporaryPath, destinationPath, replaceExisting: true);

    public static WindowsNativeFileDeleteResult DeleteExact(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The durable file delete requires Windows DeleteFileW.");
        }

        if (DeleteFile(ToExtendedLengthPath(path)))
        {
            return WindowsNativeFileDeleteResult.Deleted;
        }

        var nativeError = Marshal.GetLastPInvokeError();
        if (nativeError is ErrorFileNotFound or ErrorPathNotFound)
        {
            return WindowsNativeFileDeleteResult.NotFound;
        }

        throw new IOException(
            "The durable file namespace delete failed.",
            new Win32Exception(nativeError));
    }

    private static void Commit(
        string temporaryPath,
        string destinationPath,
        bool replaceExisting)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The durable file commit requires Windows MoveFileExW.");
        }

        var flags = MoveFileWriteThrough;
        if (replaceExisting)
        {
            CommitReplaceWithOpenReaders(temporaryPath, destinationPath);
            return;
        }

        if (!MoveFileEx(
                ToExtendedLengthPath(temporaryPath),
                ToExtendedLengthPath(destinationPath),
                flags))
        {
            var nativeError = Marshal.GetLastPInvokeError();
            throw new IOException(
                "The durable file namespace commit failed.",
                new Win32Exception(nativeError));
        }
    }

    private static string ToExtendedLengthPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        if (path.StartsWith(DevicePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Durable file operations do not accept the Win32 device namespace.",
                nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? $@"{ExtendedUncPathPrefix}{fullPath[2..]}"
            : $@"{ExtendedPathPrefix}{fullPath}";
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "DeleteFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteFile(string fileName);
}
