using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

/// <summary>Removes legacy AI key hashes from a retained copy without changing its source database.</summary>
public static class LegacyAiCredentialArchiveSanitizer
{
    private const string CredentialTable = "ai_gateway_credentials";
    private const long MaximumInputBytes = 256L * 1024 * 1024 * 1024;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void CreateCredentialFreeCopy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ReleasePackageLayout.RejectReparse(sourcePath);

        var destination = Path.GetFullPath(destinationPath);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(destination)!;
        ValidatePrivateTempDirectory(directory, tempRoot);
        CreateProtectedDirectory(directory);
        try
        {
            CreateProtectedFile(destination);
            SanitizeDatabase(sourcePath, destination);
        }
        catch
        {
            DeletePrivateDatabaseFamily(destination);
            throw;
        }
    }

    public static void CreateCredentialFreeCopy(Stream source, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"ResourceManager-archive-db-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(tempDirectory, "source.db");
        var sanitizedPath = Path.Combine(tempDirectory, "sanitized.db");
        CreateProtectedDirectory(tempDirectory);
        try
        {
            CreateProtectedFile(sourcePath);
            CreateProtectedFile(sanitizedPath);
            using (var output = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                CopyBounded(source, output);
                output.Flush(flushToDisk: true);
            }

            SanitizeDatabase(sourcePath, sanitizedPath);
            var destination = Path.GetFullPath(destinationPath);
            ReleasePackageLayout.RejectReparse(Path.GetDirectoryName(destination)!);
            File.Copy(sanitizedPath, destination, overwrite: false);
        }
        finally
        {
            DeleteDatabaseFiles(sourcePath);
            DeleteDatabaseFiles(sanitizedPath);
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: false);
        }
    }

    private static void SanitizeDatabase(string sourcePath, string destinationPath)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        source.Open();
        VerifyIntegrity(source);
        using var destination = new SqliteConnection(destinationConnectionString);
        destination.Open();
        source.BackupDatabase(destination);
        Execute(destination, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON;");
        if (TableExists(destination, CredentialTable))
        {
            Execute(destination, "DELETE FROM ai_gateway_credentials;");
            using var count = destination.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM ai_gateway_credentials;";
            if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("Legacy AI credentials remain in the retained database copy.");
        }

        Execute(destination, "VACUUM;");
        VerifyIntegrity(destination);
    }

    private static void ValidatePrivateTempDirectory(string directory, string tempRoot)
    {
        var relativeDirectory = Path.GetRelativePath(tempRoot, directory);
        if (Path.IsPathRooted(relativeDirectory)
            || relativeDirectory.Contains(Path.DirectorySeparatorChar)
            || relativeDirectory.Contains(Path.AltDirectorySeparatorChar)
            || !Path.GetFileName(directory).StartsWith("ResourceManager-archive-db-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The sanitized database must use the bounded private temporary location.");
        }
    }

    private static void CreateProtectedDirectory(string path)
    {
        ReleasePackageLayout.RejectReparse(Path.GetDirectoryName(path)!);
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("The private archive database directory already exists.");

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(AdministratorsSid))
            throw new UnauthorizedAccessException("Credential-safe archive creation requires an elevated administrator.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit,
            PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, inherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        VerifyProtectedRules(new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access), isDirectory: true);
    }

    private static void CreateProtectedFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(AdministratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
        using (new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            4096, FileOptions.WriteThrough, security))
        {
        }
        VerifyProtectedRules(new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access), isDirectory: false);
    }

    private static void VerifyProtectedRules(CommonObjectSecurity security, bool isDirectory)
    {
        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(AdministratorsSid))
        {
            throw new UnauthorizedAccessException("The archive database copy is not protected for administrators.");
        }

        var systemFull = false;
        var administratorsFull = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited
                || rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference is not SecurityIdentifier sid
                || rule.FileSystemRights != FileSystemRights.FullControl
                || rule.PropagationFlags != PropagationFlags.None
                || (isDirectory && rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)))
            {
                throw new UnauthorizedAccessException("The archive database copy has an unexpected ACL entry.");
            }

            systemFull |= sid.Equals(SystemSid);
            administratorsFull |= sid.Equals(AdministratorsSid);
        }

        if (!systemFull || !administratorsFull)
            throw new UnauthorizedAccessException("The archive database copy ACL is incomplete.");
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture),
                "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The legacy business database failed its integrity check.");
        }
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void CopyBounded(Stream source, Stream destination)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            total = checked(total + read);
            if (total > MaximumInputBytes)
                throw new InvalidDataException("The legacy business database exceeds its supported archive size.");
            destination.Write(buffer, 0, read);
        }
    }

    private static void DeletePrivateDatabaseFamily(string path)
    {
        DeleteDatabaseFiles(path);
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: false);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var member in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
            if (File.Exists(member)) File.Delete(member);
    }
}
