using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.Dependencies;

internal static class DependencyInstallPaths
{
    internal const int MaximumEntries = 100_000;

    internal static bool IsRegularFile(string path)
    {
        if (!File.Exists(path)) return false;
        try { RejectLinks(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }

    // Check every existing ancestor, including custom/legacy package roots.
    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var cursor = full;
        while (!string.IsNullOrEmpty(cursor))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(cursor); }
            catch (FileNotFoundException) { cursor = Path.GetDirectoryName(cursor); continue; }
            catch (DirectoryNotFoundException) { cursor = Path.GetDirectoryName(cursor); continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Component installation rejects a reparse point: {cursor}");
            if (cursor == full && (attributes & FileAttributes.Directory) == 0)
            {
                using var handle = File.OpenHandle(cursor, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!GetFileInformationByHandle(handle, out var information))
                    throw new IOException($"Cannot validate the component file identity: {cursor}", new Win32Exception(Marshal.GetLastWin32Error()));
                if (information.NumberOfLinks != 1)
                    throw new IOException($"Component installation rejects a hard-linked file: {cursor}");
            }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    internal static string Relative(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or ".."
                || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Invalid component archive path: {name}");
        return normalized;
    }

    internal static string InRoot(string root, string relative)
    {
        var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, Relative(relative)));
        if (!target.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Component path escapes its root.");
        return target;
    }

    // Only staging/backup directories created by the payload writer use this bounded cleanup.
    internal static void ValidateTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumEntries) throw new IOException("Component path validation exceeded its entry bound.");
                RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
    }

    internal static void DeleteOwnedTree(string root)
    {
        if (!Directory.Exists(root)) return;
        RejectLinks(root);
        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory);
            directories.Add(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumEntries) throw new IOException("Component cleanup exceeded its entry bound.");
                RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path);
                else File.Delete(path);
            }
        }
        for (var index = directories.Count - 1; index >= 0; index--) Directory.Delete(directories[index]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
