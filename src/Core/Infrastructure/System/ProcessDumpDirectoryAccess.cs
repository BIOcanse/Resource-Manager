using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.LocalSystem;

internal static class ProcessDumpDirectoryAccess
{
    internal static string Prepare(string packageRoot, string callerSid)
    {
        var sid = new SecurityIdentifier(callerSid);
        var directory = Path.GetFullPath(packageRoot);
        ReleasePackageLayout.RejectReparse(directory);
        foreach (var part in new[] { "Misc", "ProcessDumps" })
        {
            directory = Path.Combine(directory, part);
            Directory.CreateDirectory(directory);
            ReleasePackageLayout.RejectReparse(directory);
        }

        // Old releases did not record a requesting user. Keep those dumps private to administrators.
        var oldCount = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.dmp", SearchOption.TopDirectoryOnly))
        {
            if (++oldCount > 50_000) throw new IOException("Too many legacy process dumps.");
            ReleasePackageLayout.RejectReparse(path);
            var access = new FileSecurity();
            access.SetAccessRuleProtection(true, false);
            foreach (var administrator in AdministratorSids())
                access.AddAccessRule(new(administrator, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(access);
        }

        var dumpRoot = directory;
        directory = Path.Combine(dumpRoot, sid.Value);
        Directory.CreateDirectory(directory);
        ReleasePackageLayout.RejectReparse(directory);
        var userAccess = AdministratorsOnly();
        userAccess.AddAccessRule(new(sid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(userAccess);
        var rootAccess = AdministratorsOnly();
        rootAccess.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Traverse | FileSystemRights.ReadAttributes,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dumpRoot).SetAccessControl(rootAccess);
        return directory;
    }

    private static DirectorySecurity AdministratorsOnly()
    {
        var access = new DirectorySecurity();
        access.SetAccessRuleProtection(true, false);
        foreach (var sid in AdministratorSids())
            access.AddAccessRule(new(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return access;
    }

    private static SecurityIdentifier[] AdministratorSids()
        => [new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null)];
}
