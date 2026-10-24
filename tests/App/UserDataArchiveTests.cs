using System.IO.Compression;
using System.Text.Json;
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
            var next = Path.Combine(fixture, "NextInstallation");
            Directory.CreateDirectory(next);
            Directory.CreateDirectory(Path.Combine(next, "Config"));
            File.WriteAllText(Path.Combine(next, "Config", "app-settings.json"), "replaced settings");
            File.WriteAllText(Path.Combine(next, "Start.exe"), "program remains");
            UserDataArchive.Import(next, archive);
            Assert.Equal("user settings", File.ReadAllText(Path.Combine(next, "Config", "app-settings.json")));
            Assert.Equal("private data", File.ReadAllText(Path.Combine(next, "UserData", "Profiles", "profile")));
            Assert.True(Directory.Exists(Path.Combine(next, "UserData", "Empty")));
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
        Directory.CreateDirectory(Path.Combine(root, "Dependencies"));
        File.WriteAllText(Path.Combine(root, "Config", "app-settings.json"), "user settings");
        File.WriteAllText(Path.Combine(root, "UserData", "Profiles", "profile"), "private data");
        File.WriteAllText(Path.Combine(root, "Dependencies", "helper.exe"), "excluded dependency");
        File.WriteAllText(Path.Combine(root, "Start.exe"), "program");
    }
    private static void Cleanup(string root) { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(root); Directory.Delete(root, true); }
    private sealed class Runtime(string? failure = null) : IUninstallationRuntime
    {
        public List<string> Events { get; } = [];
        private void Record(string step) { Events.Add(step); if (failure == step) throw new IOException(step); }
        public void RequireOwned(UninstallationPlan plan) => Record("owned");
        public void StopProduct(UninstallationPlan plan) => Record("stop");
        public void RemoveLaunchInterception(UninstallationPlan plan) { }
        public void RemoveService(UninstallationPlan plan) => Record("service");
        public void RemoveRegistration(UninstallationPlan plan) => Record("registration");
        public void ProtectArchive(string path) => Record("protect");
    }
}
