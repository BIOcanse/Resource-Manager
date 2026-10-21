using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.Installer;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class InstallationReplacementTests
{
    [Fact]
    public void ReinstallationCopiesDataAndRetiresIdentityWithoutDeletingPreviousFiles()
    {
        using var fixture = new Fixture();
        var original = fixture.OriginalDigests();
        var installed = false;
        fixture.Replacement.Run(fixture.Prepared, () =>
        {
            installed = true;
            Assert.False(fixture.Runtime.Registered);
            Assert.False(Directory.Exists(fixture.Plan.ManagerDirectory));
            Assert.Equal("settings", File.ReadAllText(Path.Combine(fixture.Prepared, "Config", "other.json")));
            Assert.Equal("data", File.ReadAllText(Path.Combine(fixture.Prepared, "UserData", "sample.txt")));
            Assert.Equal("installer", File.ReadAllText(Path.Combine(fixture.Prepared, "Misc", "component.zip")));
            Assert.Equal("control", File.ReadAllText(Path.Combine(fixture.Prepared, "UserData", "Control", "old-control.json")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Prepared, "Dependencies")));
        });
        Assert.True(installed);
        Assert.Equal(new[] { "stop", "remove" }, fixture.Runtime.Events);
        Assert.Equal(original, fixture.OriginalDigests());
        var preserved = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Plan.Root, "Internal"), "PreviousUpdateManager-*"));
        Assert.Equal("old-manager", File.ReadAllText(Path.Combine(preserved, "ResourceManager.UpdateManager.exe")));
        Assert.Empty(Directory.GetFiles(fixture.Plan.Root, "*.zip"));
    }

    [Fact]
    public void CopyFailureLeavesOriginalFilesAndRestoresServiceBeforeIdentityRemoval()
    {
        using var fixture = new Fixture();
        var original = fixture.OriginalDigests();
        using (var locked = new FileStream(Path.Combine(fixture.Plan.Root, "UserData", "sample.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => fixture.Replacement.Run(fixture.Prepared, () => Assert.Fail("Installation must not begin.")));
        Assert.Equal(new[] { "stop", "restore" }, fixture.Runtime.Events);
        Assert.True(fixture.Runtime.Registered);
        Assert.True(fixture.Runtime.Running);
        Assert.Equal(original, fixture.OriginalDigests());
        Assert.True(Directory.Exists(fixture.Plan.ManagerDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompatibleCopiedDataIsRejectedBeforeIdentityRemoval(bool database)
    {
        using var fixture = new Fixture();
        if (database)
        {
            var path = Path.Combine(fixture.Plan.Root, "UserData", "Database", "resource-manager.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 9;";
            command.ExecuteNonQuery();
        }
        else File.WriteAllText(Path.Combine(fixture.Plan.Root, "Config", "app-settings.json"), "{\"version\":\"9.0.0\"}");
        var original = fixture.OriginalDigests();
        Assert.Throws<InvalidDataException>(() => fixture.Replacement.Run(fixture.Prepared, () => Assert.Fail("Installation must not begin.")));
        Assert.Equal(new[] { "stop", "restore" }, fixture.Runtime.Events);
        Assert.True(fixture.Runtime.Registered);
        Assert.Equal(original, fixture.OriginalDigests());
    }

    [Fact]
    public void FailedNewRegistrationRestoresPreviousManagerAndIdentity()
    {
        using var fixture = new Fixture();
        var original = fixture.OriginalDigests();
        Assert.Throws<InvalidOperationException>(() => fixture.Replacement.Run(fixture.Prepared, () =>
        {
            Assert.False(fixture.Runtime.Registered);
            Assert.False(Directory.Exists(fixture.Plan.ManagerDirectory));
            throw new InvalidOperationException("New registration failed after its own rollback.");
        }));
        Assert.Equal(new[] { "stop", "remove", "restore" }, fixture.Runtime.Events);
        Assert.True(fixture.Runtime.Registered);
        Assert.True(fixture.Runtime.Running);
        Assert.Equal("old-manager", File.ReadAllText(Path.Combine(fixture.Plan.ManagerDirectory, "ResourceManager.UpdateManager.exe")));
        Assert.Equal(original, fixture.OriginalDigests());
    }

    [Fact]
    public void PartialIdentityRemovalIsRestored()
    {
        using var fixture = new Fixture();
        fixture.Runtime.FailRemoval = true;
        Assert.Throws<IOException>(() => fixture.Replacement.Run(fixture.Prepared, () => Assert.Fail("Installation must not begin.")));
        Assert.Equal(new[] { "stop", "remove", "restore" }, fixture.Runtime.Events);
        Assert.True(fixture.Runtime.Registered);
        Assert.True(Directory.Exists(fixture.Plan.ManagerDirectory));
    }

    [Fact]
    public void ForeignIdentityIsRejectedBeforeStoppingOrCopying()
    {
        using var fixture = new Fixture();
        fixture.Runtime.Owned = false;
        Assert.Throws<InvalidOperationException>(() => fixture.Replacement.Run(fixture.Prepared, () => Assert.Fail("Installation must not begin.")));
        Assert.Empty(fixture.Runtime.Events);
        Assert.False(Directory.Exists(Path.Combine(fixture.Prepared, "UserData")));
    }

    [Fact]
    public void InterruptedUpdateIsRejectedBeforeStopping()
    {
        using var fixture = new Fixture();
        var transaction = Path.Combine(fixture.Root, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        File.WriteAllText(Path.Combine(transaction, "update-transaction.json"), JsonSerializer.Serialize(new
        {
            InstallRoot = fixture.Plan.Root, stage = "verified", PreviousVersion = "0.0.1", targetVersion = "0.0.2", serviceWasRunning = true
        }));
        Assert.Throws<InvalidOperationException>(() => fixture.Replacement.Run(fixture.Prepared, () => Assert.Fail("Installation must not begin.")));
        Assert.Empty(fixture.Runtime.Events);
        Assert.True(Directory.Exists(fixture.Plan.ManagerDirectory));
    }

    [Fact]
    public void OccupiedDefaultDirectoryUsesASeparateVersionDirectoryAndRejectsOverlaps()
    {
        using var fixture = new Fixture();
        var source = PackageLayout.ForRoot(Path.Combine(fixture.Root, "release"), "0.0.2");
        var defaultRoot = Path.Combine(fixture.Root, "ResourceManager");
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(Path.Combine(defaultRoot, "keep.txt"), "keep");
        var target = InstallerWorkflow.SelectTarget(source, fixture.Root, fixture.Plan);
        Assert.Equal(Path.Combine(fixture.Root, "ResourceManager-0.0.2"), target.Root);
        InstallationReplacement.RequireSeparateDirectories(fixture.Plan, source, target);
        Directory.CreateDirectory(target.Root);
        Assert.Equal(target.Root + "-2", InstallerWorkflow.SelectTarget(source, fixture.Root, fixture.Plan).Root);
        Assert.Throws<InvalidOperationException>(() => InstallationReplacement.RequireSeparateDirectories(fixture.Plan, source,
            PackageLayout.ForRoot(fixture.Plan.Root, source.Version)));
        Assert.Throws<InvalidOperationException>(() => InstallationReplacement.RequireSeparateDirectories(fixture.Plan,
            PackageLayout.ForRoot(Path.Combine(fixture.Plan.Root, "release"), source.Version), target));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(defaultRoot, "keep.txt")));
    }

    [Fact]
    public void AllOfferedLanguagesRenderTheActualReinstallationConsent()
    {
        var target = PackageLayout.ForRoot("NEW-INSTALL-TOKEN", "0.0.2");
        var previous = new UninstallationPlan("OLD-INSTALL-TOKEN", "backend", "manager");
        var english = ToolText.For("en-US");
        foreach (var language in AppLanguage.SupportedIds)
        {
            var text = ToolText.For(language);
            var rendered = InstallationConsent.Build(text, target, previous);
            Assert.Contains("NEW-INSTALL-TOKEN", rendered);
            Assert.Contains("OLD-INSTALL-TOKEN", rendered);
            foreach (var value in new[] { text.ReinstallStop, text.ReinstallIdentity, text.ReinstallData, text.InstallConsent })
            {
                Assert.Contains(value, rendered);
                Assert.False(string.IsNullOrWhiteSpace(value));
            }
            Assert.Contains("HKLM", text.ReinstallIdentity);
            Assert.Contains("App Paths", text.ReinstallIdentity);
            Assert.Contains("LocalSystem", text.ReinstallIdentity);
            foreach (var name in new[] { "Config", "UserData", "Misc", "Internal" }) Assert.Contains(name, text.ReinstallData);
            if (language != "en-US")
            {
                Assert.NotEqual(english.ReinstallStop, text.ReinstallStop);
                Assert.NotEqual(english.ReinstallIdentity, text.ReinstallIdentity);
                Assert.NotEqual(english.ReinstallData, text.ReinstallData);
            }
            Assert.DoesNotContain("OLD-INSTALL-TOKEN", InstallationConsent.Build(text, target, null));
            Assert.DoesNotContain(text.ReinstallIdentity, InstallationConsent.Build(text, target, null));
        }
        Assert.DoesNotContain("复制", ToolText.For("zh-TW").ReinstallData);
    }

    [Fact]
    public void ReinstallationLeavesAnInsideRootManagerInThePreviousRoot()
    {
        using var fixture = new Fixture(inside: true);
        var original = fixture.OriginalDigests();
        fixture.Replacement.Run(fixture.Prepared, () =>
        {
            Assert.False(fixture.Runtime.Registered);
            Assert.True(Directory.Exists(fixture.Plan.ManagerDirectory));
        });
        Assert.Equal(original, fixture.OriginalDigests());
        Assert.Equal("old-manager", File.ReadAllText(Path.Combine(fixture.Plan.ManagerDirectory, "ResourceManager.UpdateManager.exe")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Plan.Root, "Internal"), "PreviousUpdateManager-*"));
    }

    private sealed class Runtime : IInstallationReplacementRuntime
    {
        public bool Owned = true, Registered = true, Running = true, FailRemoval;
        public List<string> Events { get; } = [];
        public void RequireOwned() { if (!Owned) throw new InvalidOperationException("Foreign registration."); }
        public void StopProduct() { Events.Add("stop"); Running = false; }
        public void RemoveIdentity()
        {
            Events.Add("remove"); Registered = false;
            if (FailRemoval) throw new IOException("Partial identity removal.");
        }
        public void RestoreIdentity() { Events.Add("restore"); Registered = true; Running = true; }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-reinstall-" + Guid.NewGuid().ToString("N"));
        public UninstallationPlan Plan { get; }
        public string Prepared { get; }
        public Runtime Runtime { get; } = new();
        public InstallationReplacement Replacement { get; }
        private readonly List<string> originalFiles = [];

        public Fixture(bool inside = false)
        {
            var old = Path.Combine(Root, "old");
            Plan = new(old, ReleasePackageLayout.ForRoot(old, "").Backend, inside ? UpdateManagerPaths.InstalledDirectory(old) : Path.Combine(Root, "ResourceManager.UpdateManager"));
            Prepared = Path.Combine(Root, "prepared");
            Replacement = new(Plan, Runtime);
            Directory.CreateDirectory(Prepared);
            File.WriteAllText(Path.Combine(Prepared, "release-manifest.json"), JsonSerializer.Serialize(new
            {
                updateCompatibility = new { minimumSettingsSchema = "0.0.1", maximumSettingsSchema = "0.0.2", maximumDatabaseSchema = 1 }
            }));
            Write("Config/other.json", "settings");
            Write("UserData/sample.txt", "data");
            Write("Misc/component.zip", "installer");
            Write("Dependencies/old-helper.exe", "helper");
            Write("Bin/ResourceManager/ResourceManager.exe", "old-program");
            Write("Bin/ResourceManager/UserData/Control/old-control.json", "control");
            Directory.CreateDirectory(Plan.ManagerDirectory);
            File.WriteAllText(Path.Combine(Plan.ManagerDirectory, "ResourceManager.UpdateManager.exe"), "old-manager");
        }

        private void Write(string relative, string text)
        {
            var path = Path.Combine(Plan.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            originalFiles.Add(path);
        }

        public string[] OriginalDigests() => originalFiles.Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(Root);
            OwnedPackageTree.Delete(Root);
        }
    }
}
