using System.Security.AccessControl;
using System.Security.Principal;

namespace ResourceManager.Shared.Packages;

/// <summary>Shared client leases and exclusive maintenance over one stable installed file.</summary>
public static class PackageUseGuard
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(30);

    public static string PathForPackage(string packageRoot)
        => Path.Combine(UpdateManagerPaths.InstalledDirectory(packageRoot), "package-use.guard");

    // Provisioning only. Never replace an existing guard, including while clients hold it.
    public static void Prepare(string packageRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User?.Equals(SystemSid) == true ? SystemSid
            : new WindowsPrincipal(identity).IsInRole(AdministratorsSid) ? AdministratorsSid
            : throw new UnauthorizedAccessException("Package use storage requires an administrator.");
        var path = PathForPackage(packageRoot);
        RejectReparse(path, allowMissing: true);
        if (File.Exists(path)) { Verify(packageRoot); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectReparse(path, allowMissing: true);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        security.AddAccessRule(new(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(UsersSid, FileSystemRights.Read, AccessControlType.Allow));
        try
        {
            using var created = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.Read, 1, FileOptions.None, security);
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 80 or 183)
        {
            // Another explicit provisioner won, or this is the existing installation.
        }
        Verify(packageRoot);
    }

    public static void Verify(string packageRoot)
    {
        var path = PathForPackage(packageRoot);
        RejectReparse(path, allowMissing: false);
        VerifySecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    public static FileStream Acquire(string packageRoot) => Open(packageRoot, FileShare.Read);

    public static FileStream? TryAcquireMaintenance(string packageRoot)
    {
        try { return Open(packageRoot, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xffff) == 32) { return null; }
    }

    public static async Task<IDisposable> AcquireMaintenanceAsync(string packageRoot, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < MaximumWait)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquireMaintenance(packageRoot) is { } lease)
            {
                try { token.ThrowIfCancellationRequested(); return lease; }
                catch { lease.Dispose(); throw; }
            }
            // Windows sharing violations have no waitable release notification.
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        throw new TimeoutException("Package maintenance could not acquire exclusive use.");
    }

    private static FileStream Open(string packageRoot, FileShare sharing)
    {
        var path = PathForPackage(packageRoot);
        RejectReparse(path, allowMissing: false);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, sharing);
        try
        {
            VerifySecurity(stream.GetAccessControl());
            RejectReparse(path, allowMissing: false);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void VerifySecurity(FileSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if ((!SystemSid.Equals(owner) && !AdministratorsSid.Equals(owner)) || !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("Package use storage has an invalid owner or ACL.");
        var system = false;
        var administrators = false;
        var users = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow
                || rule.InheritanceFlags != InheritanceFlags.None || rule.PropagationFlags != PropagationFlags.None)
                throw new UnauthorizedAccessException("Package use storage has an unsupported ACL entry.");
            if (rule.IdentityReference.Equals(SystemSid) && rule.FileSystemRights == FileSystemRights.FullControl) system = true;
            else if (rule.IdentityReference.Equals(AdministratorsSid) && rule.FileSystemRights == FileSystemRights.FullControl) administrators = true;
            else if (rule.IdentityReference.Equals(UsersSid)
                && rule.FileSystemRights == (FileSystemRights.Read | FileSystemRights.Synchronize)) users = true;
            else throw new UnauthorizedAccessException("Package use storage grants unexpected access.");
        }
        if (!system || !administrators || !users)
            throw new UnauthorizedAccessException("Package use storage ACL is incomplete.");
    }

    private static void RejectReparse(string path, bool allowMissing)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Package use path has no root.");
        var components = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length > 512) throw new InvalidDataException("Package use path exceeds its component bound.");
        var current = root;
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            try { ReleasePackageLayout.RejectReparse(current); }
            catch (FileNotFoundException) when (allowMissing) { return; }
            catch (DirectoryNotFoundException) when (allowMissing) { return; }
        }
    }
}
