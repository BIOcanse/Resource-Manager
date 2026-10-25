using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ResourceManager.Shared.Runtime.TrustedAccess;

public static class TrustedAccessKeyFile
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaximumPathComponents = 512;
    private const int MaximumKeyFileBytes = 72;
    private const string KeyPrefix = "rm_sk_";

    public static string PathForPackage(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        return Path.Combine(Path.GetFullPath(packageRoot), "UserData", "Security", "default-key");
    }

    public static string? Read(string packageRoot)
    {
        var path = PathForPackage(packageRoot);
        var directory = Path.GetDirectoryName(path)!;
        if (!TryGetAttributes(directory, out _))
        {
            return null;
        }

        VerifyDirectory(directory);
        if (!TryGetAttributes(path, out _))
        {
            return null;
        }

        RejectExistingPathReparsePoints(path);
        ValidateKeyFileLength(new FileInfo(path).Length);
        VerifyFile(path, allowLocalUsersRead: true);

        var value = File.ReadAllText(path, StrictUtf8).TrimEnd('\r', '\n');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!IsApiKey(value))
        {
            throw new InvalidDataException("The default key file does not contain a valid API key.");
        }

        return value;
    }

    public static void WriteAtomic(string packageRoot, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 0 && !IsApiKey(key))
        {
            throw new ArgumentException("The default key must be empty or a valid API key.", nameof(key));
        }

        var path = PathForPackage(packageRoot);
        var directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);
        VerifyDirectory(directory);

        if (TryGetAttributes(path, out _))
        {
            VerifyFile(path, allowLocalUsersRead: true);
        }

        var temporaryPath = Path.Combine(directory, $"default-key.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileInfo(temporaryPath).Create(
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                4096,
                FileOptions.WriteThrough,
                BuildKeyFileSecurity()))
            {
                var bytes = StrictUtf8.GetBytes(key);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            VerifyFile(temporaryPath, allowLocalUsersRead: true);
            File.Move(temporaryPath, path, overwrite: true);
            VerifyFile(path, allowLocalUsersRead: true);
        }
        finally
        {
            if (TryGetAttributes(temporaryPath, out _))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void EnsureSecurityDirectoryForPackage(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        EnsureDirectory(Path.GetDirectoryName(PathForPackage(packageRoot))!);
    }

    internal static void VerifySecurityDirectoryForPackage(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        VerifyDirectory(Path.GetDirectoryName(PathForPackage(packageRoot))!);
    }

    internal static void VerifyNoReparsePoints(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectExistingPathReparsePoints(path);
    }

    internal static void CreatePrivateFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        VerifyDirectory(directory);
        using (new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            4096,
            FileOptions.WriteThrough,
            BuildPrivateFileSecurity()))
        {
        }

        VerifyPrivateFile(path);
    }

    internal static void VerifyPrivateFile(string path)
    {
        RejectExistingPathReparsePoints(path);
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        VerifyOwner(security);
        VerifyProtectedRules(
            security,
            allowLocalUsersRead: false,
            isDirectory: false,
            requireProtected: false,
            allowInherited: true);
    }

    internal static void ValidateKeyFileLength(long length)
    {
        if (length > MaximumKeyFileBytes)
        {
            throw new InvalidDataException("The default key file exceeds its supported size.");
        }
    }

    private static void EnsureDirectory(string directory)
    {
        RejectExistingPathReparsePoints(directory);
        if (TryGetAttributes(directory, out _))
        {
            return;
        }

        new DirectoryInfo(directory).Create(BuildDirectorySecurity());
        VerifyDirectory(directory);
    }

    private static void VerifyDirectory(string directory)
    {
        RejectExistingPathReparsePoints(directory);
        var security = new DirectoryInfo(directory).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        VerifyOwner(security);
        VerifyProtectedRules(
            security,
            allowLocalUsersRead: false,
            isDirectory: true,
            requireProtected: true,
            allowInherited: false);
    }

    private static void VerifyFile(string path, bool allowLocalUsersRead)
    {
        RejectExistingPathReparsePoints(path);
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        VerifyOwner(security);
        VerifyProtectedRules(
            security,
            allowLocalUsersRead,
            isDirectory: false,
            requireProtected: true,
            allowInherited: false);
    }

    private static void VerifyOwner(ObjectSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier sid
            || (!sid.Equals(SystemSid) && !sid.Equals(AdministratorsSid)))
        {
            throw new UnauthorizedAccessException("Trusted access storage must be owned by SYSTEM or Administrators.");
        }
    }

    private static void VerifyProtectedRules(
        CommonObjectSecurity security,
        bool allowLocalUsersRead,
        bool isDirectory,
        bool requireProtected,
        bool allowInherited)
    {
        if (requireProtected && !security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException("Trusted access storage must have a protected ACL.");
        }

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        var systemFull = false;
        var administratorsFull = false;
        var usersRead = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if ((!allowInherited && rule.IsInherited) || rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference is not SecurityIdentifier sid)
            {
                throw new UnauthorizedAccessException("Trusted access storage has an unsupported ACL entry.");
            }

            var rights = rule.FileSystemRights;
            if (sid.Equals(SystemSid))
            {
                systemFull |= rights == FileSystemRights.FullControl;
                if (rights != FileSystemRights.FullControl
                    || (isDirectory && rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    || rule.PropagationFlags != PropagationFlags.None)
                {
                    throw new UnauthorizedAccessException("SYSTEM must have full control of trusted access storage.");
                }
            }
            else if (sid.Equals(AdministratorsSid))
            {
                administratorsFull |= rights == FileSystemRights.FullControl;
                if (rights != FileSystemRights.FullControl
                    || (isDirectory && rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                    || rule.PropagationFlags != PropagationFlags.None)
                {
                    throw new UnauthorizedAccessException("Administrators must have full control of trusted access storage.");
                }
            }
            else if (allowLocalUsersRead && !isDirectory && sid.Equals(UsersSid))
            {
                usersRead = true;
                if (rights != (FileSystemRights.Read | FileSystemRights.Synchronize)
                    || rule.InheritanceFlags != InheritanceFlags.None
                    || rule.PropagationFlags != PropagationFlags.None)
                {
                    throw new UnauthorizedAccessException("The default key file must be read-only for local users.");
                }
            }
            else if (isDirectory && sid.Equals(UsersSid))
            {
                var allowedDirectoryRead = FileSystemRights.Traverse
                    | FileSystemRights.ReadAttributes
                    | FileSystemRights.ReadPermissions
                    | FileSystemRights.Synchronize;
                if (rights != allowedDirectoryRead
                    || rule.InheritanceFlags != InheritanceFlags.None
                    || rule.PropagationFlags != PropagationFlags.None)
                {
                    throw new UnauthorizedAccessException("Local users may only traverse and inspect ACL metadata on the trusted access directory.");
                }
            }
            else
            {
                throw new UnauthorizedAccessException("Trusted access storage grants access to an unexpected principal.");
            }
        }

        if (!systemFull || !administratorsFull || (allowLocalUsersRead && !usersRead))
        {
            throw new UnauthorizedAccessException("Trusted access storage ACL is incomplete.");
        }
    }

    private static DirectorySecurity BuildDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(GetTrustedOwnerSid());
        const InheritanceFlags storageInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid,
            FileSystemRights.FullControl,
            storageInheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid,
            FileSystemRights.FullControl,
            storageInheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            UsersSid,
            FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions,
            AccessControlType.Allow));
        return security;
    }

    private static FileSecurity BuildKeyFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(GetTrustedOwnerSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.Read, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity BuildPrivateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(GetTrustedOwnerSid());
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static SecurityIdentifier GetTrustedOwnerSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Equals(SystemSid) == true)
        {
            return SystemSid;
        }

        if (new WindowsPrincipal(identity).IsInRole(AdministratorsSid))
        {
            return AdministratorsSid;
        }

        throw new UnauthorizedAccessException("Trusted access storage can only be initialized by SYSTEM or an administrator.");
    }

    private static void RejectExistingPathReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw new UnauthorizedAccessException("Trusted access storage path has no root.");
        }

        var components = fullPath[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (components.Length > MaximumPathComponents)
        {
            throw new UnauthorizedAccessException("Trusted access storage path exceeds its component bound.");
        }

        var current = pathRoot;
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            if (!TryGetAttributes(current, out var attributes))
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Trusted access storage cannot use reparse points in its path.");
            }
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    internal static bool IsApiKey(string value)
    {
        if (value.Length != 70 || !value.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = KeyPrefix.Length; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
