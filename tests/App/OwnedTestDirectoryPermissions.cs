using System.Security.AccessControl;
using System.Security.Principal;

namespace Resource_Manager_APP.Tests;

internal static class OwnedTestDirectoryPermissions
{
    internal static void RestoreOwnerAccessForCleanup(string root)
    {
        var full = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("rm-", Path.GetFileName(full), StringComparison.Ordinal);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User!;
        var pending = new Queue<string>(); pending.Enqueue(full);
        while (pending.TryDequeue(out var directory))
        {
            Assert.False(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint));
            var access = new DirectoryInfo(directory).GetAccessControl();
            access.AddAccessRule(new(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(access);
            foreach (var child in Directory.GetDirectories(directory)) pending.Enqueue(child);
        }
    }
}
