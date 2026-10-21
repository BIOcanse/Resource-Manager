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
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Importing user identity is unavailable.");
        if (user.IsWellKnown(WellKnownSidType.LocalSystemSid)) return;
        foreach (var relative in new[] { "Config/WebView2", "Config/Diagnostics/NativeUiStartup" })
        {
            var directory = Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar), user.Value);
            if (!Directory.Exists(directory)) continue;
            ReleasePackageLayout.RejectReparse(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
    }

}
