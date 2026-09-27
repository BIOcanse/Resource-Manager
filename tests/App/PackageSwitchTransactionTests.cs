using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class PackageSwitchTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedSwitchPreservesDataAndRestoresOldTreeWhenHealthFails(bool failHealth)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-update-test-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(sandbox, "package");
        var installed = Path.Combine(sandbox, "ResourceManager");
        Directory.CreateDirectory(sandbox);
        try
        {
            CreatePackage(package, "0.3.0");
            Directory.CreateDirectory(Path.Combine(installed, "Config"));
            Directory.CreateDirectory(Path.Combine(installed, "UserData", "Database"));
            File.WriteAllText(Path.Combine(installed, "Config", "app-settings.json"), "old-settings");
            File.WriteAllText(Path.Combine(installed, "UserData", "Database", "resource-manager.db"), "old-database");
            File.WriteAllText(Path.Combine(installed, "release-manifest.json"), "old-manifest");
            var verified = ReleasePackageLayout.Verify(package);
            var runtime = new FakeUpdateRuntime(failHealth);
            var plan = new UpdatePlan(verified, installed, "0.2.0", ServiceWasRunning: true);

            if (failHealth)
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PackageSwitchTransaction.ApplyAsync(plan, runtime, CancellationToken.None));
            else
                await PackageSwitchTransaction.ApplyAsync(plan, runtime, CancellationToken.None);

            Assert.Equal("old-settings", File.ReadAllText(Path.Combine(installed, "Config", "app-settings.json")));
            Assert.Equal("old-database", File.ReadAllText(Path.Combine(installed, "UserData", "Database", "resource-manager.db")));
            var installedManifest = File.ReadAllText(Path.Combine(installed, "release-manifest.json"));
            if (failHealth) Assert.Equal("old-manifest", installedManifest);
            else
            {
                using var document = JsonDocument.Parse(installedManifest);
                Assert.Equal("0.3.0", document.RootElement.GetProperty("version").GetString());
            }
            Assert.Equal(failHealth ? 2 : 1, runtime.StartCount);
            Assert.Equal(failHealth ? 2 : 1, runtime.StopCount);
            Assert.Equal(failHealth ? 0 : 1, runtime.WrittenVersions.Count);
            if (!failHealth) Assert.Equal("0.3.0", runtime.WrittenVersions.Single());
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSwitchCanRestorePreviousTree(bool nextWasPromoted)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-recover-test-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(sandbox, "ResourceManager");
        var workspace = Path.Combine(sandbox, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(workspace, "previous");
        Directory.CreateDirectory(Path.Combine(previous, "Config"));
        try
        {
            File.WriteAllText(Path.Combine(previous, "Config", "app-settings.json"), "original-data");
            if (nextWasPromoted)
            {
                Directory.CreateDirectory(Path.Combine(installed, "Config"));
                File.WriteAllText(Path.Combine(installed, "Config", "app-settings.json"), "changed-data");
            }
            File.WriteAllText(Path.Combine(workspace, "update-transaction.json"), JsonSerializer.Serialize(new
            {
                stage = nextWasPromoted ? "nextPromoted" : "previousMoved",
                InstallRoot = installed,
                PreviousVersion = "0.2.0",
                targetVersion = "0.3.0",
                serviceWasRunning = true
            }));
            var runtime = new FakeUpdateRuntime(false);
            var recovered = await PackageSwitchTransaction.RecoverPendingAsync(
                installed, runtime, CancellationToken.None);
            Assert.Single(recovered);
            Assert.Equal("original-data", File.ReadAllText(Path.Combine(installed, "Config", "app-settings.json")));
            Assert.Equal("0.2.0", Assert.Single(runtime.WrittenVersions));
            Assert.Equal(1, runtime.StartCount);
            Assert.Empty(PackageSwitchTransaction.ListPending(installed));
            if (nextWasPromoted)
                Assert.True(File.Exists(Path.Combine(workspace, "failed", "Config", "app-settings.json")));
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    [Fact]
    public async Task InterruptedBeforeDirectoryMoveKeepsOldTreeAndRestartsService()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-recover-test-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(sandbox, "ResourceManager");
        var workspace = Path.Combine(sandbox, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(installed, "old-file"), "original");
            File.WriteAllText(Path.Combine(workspace, "update-transaction.json"), JsonSerializer.Serialize(new
            {
                stage = "dataCopied", InstallRoot = installed, PreviousVersion = "0.2.0",
                targetVersion = "0.3.0", serviceWasRunning = true
            }));
            var runtime = new FakeUpdateRuntime(false);
            await PackageSwitchTransaction.RecoverPendingAsync(installed, runtime, CancellationToken.None);
            Assert.Equal("original", File.ReadAllText(Path.Combine(installed, "old-file")));
            Assert.Equal("0.2.0", Assert.Single(runtime.WrittenVersions));
            Assert.Equal(1, runtime.StartCount);
            Assert.Empty(PackageSwitchTransaction.ListPending(installed));
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    private static void CreatePackage(string root, string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        var paths = new[]
        {
            "Install.exe", "Start.exe", "Bin/ResourceManager/ResourceManager.exe",
            "Bin/ResourceManager/wwwroot/index.html",
            "Bin/ResourceManagerNativeUi/ResourceManager.NativeUi.exe",
            "Bin/ResourceManagerLauncher/ResourceManager.Launcher.exe",
            "Internal/UpdateManager/ResourceManager.UpdateManager.exe"
        };
        var files = paths.Select(path =>
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "fixture:" + path);
            return new { path, length = new FileInfo(full).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) };
        }).ToArray();
        File.WriteAllText(Path.Combine(root, "release-manifest.json"),
            JsonSerializer.Serialize(new { version, sourceCommit = "fixture", files }));
    }

    private sealed class FakeUpdateRuntime(bool failHealth) : IUpdateRuntime
    {
        public int StopCount { get; private set; }
        public int StartCount { get; private set; }
        public List<string> WrittenVersions { get; } = [];
        public Task WaitForNativeUiExitAsync(string installRoot, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public void StopService(string installRoot) => StopCount++;
        public void StartService(string installRoot) => StartCount++;
        public Task WaitForHealthAsync(CancellationToken cancellationToken)
            => failHealth ? Task.FromException(new InvalidOperationException("Injected health failure")) : Task.CompletedTask;
        public void WriteInstalledVersion(string root, string version) => WrittenVersions.Add(version);
    }
}
