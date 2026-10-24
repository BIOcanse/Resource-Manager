using System.Security.AccessControl;
using System.Security.Principal;

namespace ResourceManager.Shared.Packages;

public static class PackageUserDirectoryAccess
{
    // Runtime settings and tokens remain service-owned. Only user-created UI cache/diagnostic children are delegated.
    public static void Prepare(string packageRoot)
    {
        var root = Path.GetFullPath(packageRoot);
        foreach (var relative in new[] { "Config/WebView2", "Config/Diagnostics/NativeUiStartup" })
        {
            var directory = root;
            ReleasePackageLayout.RejectReparse(directory);
            foreach (var part in relative.Split('/'))
            {
                directory = Path.Combine(directory, part);
                Directory.CreateDirectory(directory);
                ReleasePackageLayout.RejectReparse(directory);
            }
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
    }
    public static void PrepareRestoredUserCache(string packageRoot)
    {
        foreach (var relative in new[] { "Config/WebView2", "Config/Diagnostics/NativeUiStartup" })
        {
            var parent = Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(parent)) continue;
            ReleasePackageLayout.RejectReparse(parent);
            var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                if (++count > 100_000) throw new IOException("Restored user cache count exceeds its entry bound.");
                ReleasePackageLayout.RejectReparse(directory);
                var user = RequireCacheUser(Path.GetFileName(directory));
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                             new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    security.AddAccessRule(new(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
            }
        }
    }

    internal static SecurityIdentifier RequireCacheUser(string directoryName)
    {
        SecurityIdentifier user;
        try { user = new SecurityIdentifier(directoryName); }
        catch (ArgumentException exception) { throw new InvalidDataException("Invalid restored user cache SID.", exception); }
        if (!user.IsAccountSid() || user.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || !string.Equals(user.Value, directoryName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid restored user cache SID.");
        return user;
    }

}
