using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class UserDataArchiveTests
{
    [Fact]
    public void KeepUninstallLeavesOnlyVerifiedZipAndImportsIntoFreshInstallation()
    {
        var fixture = CreateFixture();
        try
        {
            var root = Path.Combine(fixture, "ResourceManager");
            Seed(root);
            var manager = Directory.CreateDirectory(Path.Combine(fixture, "ResourceManager.UpdateManager")).FullName;
            File.WriteAllText(Path.Combine(manager, "UninstallResourceManager.exe"), "owned helper");
            var runtime = new Runtime();
            var archive = UninstallationWorkflow.Run(new(root, Path.Combine(root, "backend"), manager), true, runtime)!;
            Assert.Equal([archive], Directory.GetFileSystemEntries(root));
            Assert.False(Directory.Exists(manager));
            Assert.Equal(["owned", "stop", "protect", "service", "registration"], runtime.Events);
            UserDataArchive.Verify(archive);
            using (var retained = ZipFile.OpenRead(archive))
                Assert.DoesNotContain(retained.Entries, entry => entry.FullName.StartsWith("UserData/Security/", StringComparison.OrdinalIgnoreCase));
            var next = Path.Combine(fixture, "NextInstallation");
            Directory.CreateDirectory(next);
            Directory.CreateDirectory(Path.Combine(next, "Config"));
            File.WriteAllText(Path.Combine(next, "Config", "app-settings.json"), "replaced settings");
            File.WriteAllText(Path.Combine(next, "Start.exe"), "program remains");
            UserDataArchive.Import(next, archive);
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(next, "Config", "app-settings.json")));
            Assert.Equal("private data", File.ReadAllText(Path.Combine(next, "UserData", "Profiles", "profile")));
            Assert.Equal("root-level user data", File.ReadAllText(Path.Combine(next, "UserData", "root-level.txt")));
            Assert.True(Directory.Exists(Path.Combine(next, "UserData", "Empty")));
            Assert.False(Directory.Exists(Path.Combine(next, "UserData", "Security")));
            Assert.Equal("program remains", File.ReadAllText(Path.Combine(next, "Start.exe")));
            Assert.False(Directory.Exists(Path.Combine(next, "Dependencies")));
        }
        finally { Cleanup(fixture); }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("protect")]
    [InlineData("service")]
    public void FailureBeforeDeletingDataRetainsOriginal(string failure)
    {
        var fixture = CreateFixture();
        try
        {
            var root = Path.Combine(fixture, "ResourceManager");
            Seed(root);
            Assert.Throws<IOException>(() => UninstallationWorkflow.Run(new(root, "backend", Path.Combine(fixture, "manager")), true, new Runtime(failure)));
            Assert.Equal("private data", File.ReadAllText(Path.Combine(root, "UserData", "Profiles", "profile")));
            Assert.True(File.Exists(Path.Combine(root, "Start.exe")));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void DeleteUninstallRemovesRootWithoutArchive()
    {
        var fixture = CreateFixture();
        try
        {
            var root = Path.Combine(fixture, "ResourceManager"); Seed(root);
            Assert.Null(UninstallationWorkflow.Run(new(root, "backend", Path.Combine(fixture, "manager")), false, new Runtime()));
            Assert.False(Directory.Exists(root));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void IncompatibleImportDoesNotChangeCurrentData()
    {
        var fixture = CreateFixture();
        try
        {
            var source = Path.Combine(fixture, "source"); Seed(source);
            var zip = Path.Combine(fixture, "data.zip"); UserDataArchive.Create(source, zip);
            var target = Path.Combine(fixture, "target"); Seed(target);
            File.WriteAllText(Path.Combine(target, "Config", "app-settings.json"), "current");
            Assert.Throws<InvalidDataException>(() => UserDataArchive.Import(target, zip, _ => throw new InvalidDataException("schema")));
            Assert.Equal("current", File.ReadAllText(Path.Combine(target, "Config", "app-settings.json")));
        }
        finally { Cleanup(fixture); }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Bin/program.exe")]
    [InlineData("UserData/../outside")]
    [InlineData("UserData/file:stream")]
    public void UnsafeManifestIsRejectedBeforeImport(string path)
    {
        var fixture = CreateFixture();
        try
        {
            var zip = Path.Combine(fixture, "data.zip");
            using (var file = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var output = file.CreateEntry("user-data-manifest.json").Open())
                JsonSerializer.Serialize(output, new { Contract = "resource-manager-user-data-v1", Directories = new[] { path }, Files = Array.Empty<object>() });
            var target = Path.Combine(fixture, "target"); Seed(target);
            Assert.Throws<InvalidDataException>(() => UserDataArchive.Import(target, zip));
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(target, "Config", "app-settings.json")));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void VerifyRejectsRetainedArchivesContainingTrustedAccessStorage()
    {
        var fixture = CreateFixture();
        try
        {
            var archive = Path.Combine(fixture, "credentials.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var output = zip.CreateEntry("user-data-manifest.json").Open())
            {
                JsonSerializer.Serialize(output, new
                {
                    Contract = "resource-manager-user-data-v1",
                    Directories = new[] { "UserData/Security" },
                    Files = Array.Empty<object>()
                });
            }

            Assert.Throws<InvalidDataException>(() => UserDataArchive.Verify(archive));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void CreateArchivesBusinessDataWithoutLegacyAiCredentialsOrDatabaseSidecars()
    {
        var fixture = CreateFixture();
        try
        {
            var root = Path.Combine(fixture, "source");
            var databasePath = Path.Combine(root, "UserData", "Database", "resource-manager.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            CreateLegacyBusinessDatabase(databasePath);
            File.WriteAllText(databasePath + "-shm", "legacy secret hash sidecar");
            var databaseHashBefore = SHA256.HashData(File.ReadAllBytes(databasePath));
            var archive = Path.Combine(fixture, "safe.zip");

            UserDataArchive.Create(root, archive, CreateCredentialFreeDatabaseCopyForTest);

            Assert.Equal(databaseHashBefore, SHA256.HashData(File.ReadAllBytes(databasePath)));
            using var zip = ZipFile.OpenRead(archive);
            Assert.Null(zip.GetEntry("UserData/Database/resource-manager.db-shm"));
            Assert.Null(zip.GetEntry("UserData/Security/default-key"));
            var databaseEntry = Assert.IsType<ZipArchiveEntry>(zip.GetEntry("UserData/Database/resource-manager.db"));
            var restoredDatabase = Path.Combine(fixture, "archive-copy.db");
            using (var input = databaseEntry.Open())
            using (var output = new FileStream(restoredDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = restoredDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            Assert.Equal(0L, ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM ai_gateway_credentials;"));
            Assert.Equal(1L, ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM retained_application_data;"));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void ImportPreservesTheInstalledSecurityDirectoryInsteadOfReplacingItFromArchive()
    {
        var fixture = CreateFixture();
        try
        {
            var source = Path.Combine(fixture, "source");
            Seed(source);
            var archive = Path.Combine(fixture, "safe.zip");
            UserDataArchive.Create(source, archive);

            var target = Path.Combine(fixture, "target");
            Seed(target);
            var defaultKeyPath = Path.Combine(target, "UserData", "Security", "default-key");
            var databasePath = Path.Combine(target, "UserData", "Security", "access-keys.db");
            File.WriteAllText(defaultKeyPath, "rm_sk_" + new string('c', 64));
            File.WriteAllText(databasePath, "current key store bytes");

            UserDataArchive.Import(target, archive);

            Assert.Equal("rm_sk_" + new string('c', 64), File.ReadAllText(defaultKeyPath));
            Assert.Equal("current key store bytes", File.ReadAllText(databasePath));
            Assert.DoesNotContain(Directory.EnumerateFiles(target, "default-key", SearchOption.AllDirectories),
                path => !path.Equals(defaultKeyPath, StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void ImportSanitizesLegacyCredentialRowsBeforePromotingTheBusinessDatabase()
    {
        var fixture = CreateFixture();
        try
        {
            var databasePath = Path.Combine(fixture, "legacy.db");
            CreateLegacyBusinessDatabase(databasePath);
            var originalHash = SHA256.HashData(File.ReadAllBytes(databasePath));
            var archive = Path.Combine(fixture, "legacy-with-credentials.zip");
            CreateLegacyArchiveWithBusinessDatabase(archive, databasePath);
            var target = Path.Combine(fixture, "target");
            Directory.CreateDirectory(target);

            UserDataArchive.Import(
                target,
                archive,
                createCredentialFreeLegacyDatabaseCopy: (source, destination) =>
                {
                    var rawDatabase = Path.Combine(fixture, "legacy-import-source.db");
                    using (var output = new FileStream(rawDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        source.CopyTo(output);
                    try
                    {
                        CreateCredentialFreeDatabaseCopyForTest(rawDatabase, destination);
                    }
                    finally { File.Delete(rawDatabase); }
                });

            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(databasePath)));
            var importedDatabase = Path.Combine(target, "UserData", "Database", "resource-manager.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = importedDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            Assert.Equal(0L, ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM ai_gateway_credentials;"));
            Assert.Equal(1L, ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM retained_application_data;"));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void ImportRollbackRestoresRootLevelFilesAndProtectedSecurityDirectory()
    {
        var fixture = CreateFixture();
        try
        {
            var source = Path.Combine(fixture, "source");
            Seed(source);
            File.WriteAllText(Path.Combine(source, "Config", "WebView2"), "invalid cache shape");
            var archive = Path.Combine(fixture, "invalid-cache-shape.zip");
            UserDataArchive.Create(source, archive);

            var target = Path.Combine(fixture, "target");
            Seed(target);
            var defaultKeyPath = Path.Combine(target, "UserData", "Security", "default-key");
            var databasePath = Path.Combine(target, "UserData", "Security", "access-keys.db");
            File.WriteAllText(defaultKeyPath, "rm_sk_" + new string('d', 64));
            File.WriteAllText(databasePath, "active key store");

            Assert.ThrowsAny<IOException>(() => UserDataArchive.Import(target, archive));

            Assert.Equal("root-level user data", File.ReadAllText(Path.Combine(target, "UserData", "root-level.txt")));
            Assert.Equal("rm_sk_" + new string('d', 64), File.ReadAllText(defaultKeyPath));
            Assert.Equal("active key store", File.ReadAllText(databasePath));
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(target, "Config", "app-settings.json")));
        }
        finally { Cleanup(fixture); }
    }

    [Fact]
    public void TamperedArchiveCannotReplaceExistingData()
    {
        var fixture = CreateFixture();
        try
        {
            var root = Path.Combine(fixture, "source"); Seed(root);
            var zip = Path.Combine(fixture, "data.zip"); UserDataArchive.Create(root, zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            {
                archive.GetEntry("Config/app-settings.json")!.Delete();
                using var output = new StreamWriter(archive.CreateEntry("Config/app-settings.json").Open());
                output.Write("corrupt data");
            }
            Assert.Throws<InvalidDataException>(() => UserDataArchive.Import(root, zip));
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(root, "Config", "app-settings.json")));
        }
        finally { Cleanup(fixture); }
    }

    private static string CreateFixture() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-uninstall-test-" + Guid.NewGuid().ToString("N"))).FullName;
    private static void Seed(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        Directory.CreateDirectory(Path.Combine(root, "UserData", "Profiles"));
        Directory.CreateDirectory(Path.Combine(root, "UserData", "Empty"));
        Directory.CreateDirectory(Path.Combine(root, "UserData", "Security"));
        Directory.CreateDirectory(Path.Combine(root, "Dependencies"));
        File.WriteAllText(Path.Combine(root, "Config", "app-settings.json"), "user settings");
        File.WriteAllText(Path.Combine(root, "UserData", "Profiles", "profile"), "private data");
        File.WriteAllText(Path.Combine(root, "UserData", "root-level.txt"), "root-level user data");
        File.WriteAllText(Path.Combine(root, "UserData", "Security", "default-key"), "rm_sk_" + new string('a', 64));
        File.WriteAllText(Path.Combine(root, "UserData", "Security", "access-keys.db"), "private credential hashes");
        File.WriteAllText(Path.Combine(root, "Dependencies", "helper.exe"), "excluded dependency");
        File.WriteAllText(Path.Combine(root, "Start.exe"), "program");
    }

    private static void CreateLegacyBusinessDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE retained_application_data (value TEXT NOT NULL);
            INSERT INTO retained_application_data(value) VALUES ('keep this data');
            CREATE TABLE ai_gateway_credentials (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                compatibility_profile TEXT NOT NULL,
                secret_hash BLOB NOT NULL,
                created_utc_ticks INTEGER NOT NULL);
            INSERT INTO ai_gateway_credentials(id, display_name, compatibility_profile, secret_hash, created_utc_ticks)
                VALUES ('old-key', 'old client', 'openai', zeroblob(32), 1);
            """;
        command.ExecuteNonQuery();
    }

    private static void CreateLegacyArchiveWithBusinessDatabase(string archivePath, string databasePath)
    {
        var length = new FileInfo(databasePath).Length;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(databasePath)));
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        using (var source = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var destination = zip.CreateEntry("UserData/Database/resource-manager.db").Open())
            source.CopyTo(destination);
        using var manifest = zip.CreateEntry("user-data-manifest.json").Open();
        JsonSerializer.Serialize(manifest, new
        {
            Contract = "resource-manager-user-data-v1",
            Directories = new[] { "UserData", "UserData/Database" },
            Files = new[]
            {
                new { Path = "UserData/Database/resource-manager.db", Length = length, Sha256 = hash }
            }
        });
    }

    private static void CreateCredentialFreeDatabaseCopyForTest(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        source.Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        using var command = destination.CreateCommand();
        command.CommandText = "PRAGMA secure_delete=ON; DELETE FROM ai_gateway_credentials; VACUUM;";
        command.ExecuteNonQuery();
    }

    private static long ExecuteScalarInt64(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static void Cleanup(string root) { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(root); Directory.Delete(root, true); }
    private sealed class Runtime(string? failure = null) : IUninstallationRuntime
    {
        public List<string> Events { get; } = [];
        private void Record(string step) { Events.Add(step); if (failure == step) throw new IOException(step); }
        public void RequireOwned(UninstallationPlan plan) => Record("owned");
        public void StopProduct(UninstallationPlan plan) => Record("stop");
        public void RemoveLaunchInterception(UninstallationPlan plan) { }
        public void RemoveFrontendLogonTasks(UninstallationPlan plan) { }
        public void RemoveService(UninstallationPlan plan) => Record("service");
        public void RemoveRegistration(UninstallationPlan plan) => Record("registration");
        public void ProtectArchive(string path) => Record("protect");
    }
}
