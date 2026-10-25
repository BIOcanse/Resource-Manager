using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Security.AccessControl;
using System.Security.Principal;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class PackageSwitchTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedClientsBlockStopAndMaintenanceCoversHealthCommitAndRollback(bool failHealth)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true
            && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-update-guard-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(sandbox, "installed");
        var package = Path.Combine(sandbox, "package");
        Directory.CreateDirectory(Path.Combine(installed, "Config"));
        try
        {
            CreatePackage(package, "0.3.0");
            File.WriteAllText(Path.Combine(installed, "Config", "value"), "original");
            PackageUseGuard.Prepare(installed);
            using var first = PackageUseGuard.Acquire(installed);
            using var second = PackageUseGuard.Acquire(installed);
            var runtime = new FakeUpdateRuntime(failHealth)
            {
                AcquireOverride = PackageUseGuard.AcquireMaintenanceAsync,
                HealthObserved = () => Assert.Throws<IOException>(() => PackageUseGuard.Acquire(installed))
            };
            var plan = new UpdatePlan(ReleasePackageLayout.Verify(package), installed, "0.2.0", true);
            var apply = PackageSwitchTransaction.ApplyAsync(plan, runtime, CancellationToken.None);
            Assert.False(apply.IsCompleted);
            Assert.Equal(0, runtime.StopCount);
            Assert.Equal("original", File.ReadAllText(Path.Combine(installed, "Config", "value")));
            first.Dispose();
            Assert.Null(PackageUseGuard.TryAcquireMaintenance(installed));
            second.Dispose();
            if (failHealth)
                await Assert.ThrowsAsync<InvalidOperationException>(() => apply.WaitAsync(TimeSpan.FromSeconds(15)));
            else await apply.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(runtime.MaintenanceActive);
            Assert.Equal("original", File.ReadAllText(Path.Combine(installed, "Config", "value")));
            using var resumed = PackageUseGuard.Acquire(installed);
            Assert.NotNull(resumed);
        }
        finally
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(sandbox);
            OwnedPackageTree.Delete(sandbox);
        }
    }

    [Fact]
    public async Task CancelledMaintenanceWaitNeverStopsOrChangesInstalledPayload()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-update-cancel-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(sandbox, "installed");
        var package = Path.Combine(sandbox, "package");
        Directory.CreateDirectory(installed);
        try
        {
            CreatePackage(package, "0.3.0");
            File.WriteAllText(Path.Combine(installed, "original"), "retained");
            var waiting = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runtime = new FakeUpdateRuntime(false)
            {
                AcquireOverride = (_, token) => waiting.Task.WaitAsync(token)
            };
            using var cancellation = new CancellationTokenSource();
            var plan = new UpdatePlan(ReleasePackageLayout.Verify(package), installed, "0.2.0", true);
            var apply = PackageSwitchTransaction.ApplyAsync(plan, runtime, cancellation.Token);
            Assert.False(apply.IsCompleted);
            cancellation.Cancel();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
            Assert.Equal(0, runtime.StopCount);
            Assert.Equal(0, runtime.StartCount);
            Assert.Empty(runtime.WrittenVersions);
            Assert.Equal("retained", File.ReadAllText(Path.Combine(installed, "original")));
        }
        finally { OwnedPackageTree.Delete(sandbox); }
    }

    [Fact]
    public void CompatibilityPreflightReleasesDatabaseBeforeDirectorySwitch()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "rm-compat-test-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(sandbox, "package");
        var installed = Path.Combine(sandbox, "installed");
        var moved = Path.Combine(sandbox, "previous");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(Path.Combine(installed, "Config"));
        var databaseDirectory = Path.Combine(installed, "UserData", "Database");
        Directory.CreateDirectory(databaseDirectory);
        try
        {
            File.WriteAllText(Path.Combine(package, "release-manifest.json"), JsonSerializer.Serialize(new
            {
                updateCompatibility = new
                {
                    minimumSettingsSchema = "1.0.17", maximumSettingsSchema = "1.0.27", maximumDatabaseSchema = 11
                }
            }));
            File.WriteAllText(Path.Combine(installed, "Config", "app-settings.json"), "{\"version\":\"1.0.24\"}");
            var database = Path.Combine(databaseDirectory, "resource-manager.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 11;";
                command.ExecuteNonQuery();
            }

            UpdatePlan.CheckCompatibility(package, installed);
            Directory.Move(installed, moved);
            Assert.True(File.Exists(Path.Combine(moved, "UserData", "Database", "resource-manager.db")));
        }
        finally { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(sandbox); Directory.Delete(sandbox, recursive: true); }
    }

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
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Test requires a Windows identity.");
            var configDirectory = new DirectoryInfo(Path.Combine(installed, "Config"));
            var configSecurity = configDirectory.GetAccessControl(AccessControlSections.Access);
            configSecurity.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            configDirectory.SetAccessControl(configSecurity);
            PackageUserDirectoryAccess.Prepare(installed);
            using var cacheToken = PackageUserDirectoryTokenTests.CreateUserToken();
            var cacheProfile = Path.Combine(installed, "Config", "WebView2", user.Value, "Profile");
            WindowsIdentity.RunImpersonated(cacheToken, () =>
            {
                Directory.CreateDirectory(cacheProfile);
                File.WriteAllText(Path.Combine(cacheProfile, "cache"), "old-private-cache");
            });
            File.WriteAllText(Path.Combine(installed, "UserData", "Database", "resource-manager.db"), "old-database");
            File.WriteAllText(Path.Combine(installed, "release-manifest.json"), "old-manifest");
            var legacyControl = Path.Combine(installed, "Bin", "ResourceManager", "UserData", "Control");
            Directory.CreateDirectory(legacyControl);
            File.WriteAllText(Path.Combine(legacyControl, "cpu-power-baseline.txt"), "45");
            File.WriteAllText(Path.Combine(legacyControl, "desired-state.json"), "owned-control-state");
            var verified = ReleasePackageLayout.Verify(package);
            var runtime = new FakeUpdateRuntime(failHealth);
            var plan = new UpdatePlan(verified, installed, "0.2.0", ServiceWasRunning: true);

            var managerDirectory = UpdateManagerPaths.InstalledDirectory(installed);
            Directory.CreateDirectory(managerDirectory);
            var managerPath = UpdateManagerPaths.InstalledExecutable(installed);
            File.WriteAllText(managerPath, "stable-manager");
            using var managerLock = new FileStream(managerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var operationLock = PackageOperationLock.Acquire(installed);
            if (failHealth)
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PackageSwitchTransaction.ApplyAsync(plan, runtime, CancellationToken.None));
            else
                await PackageSwitchTransaction.ApplyAsync(plan, runtime, CancellationToken.None);

            var transaction = Directory.GetDirectories(sandbox, "ResourceManager-update-*").Single();
            using (var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(transaction, "update-transaction.json"))))
            {
                Assert.Equal(verified.Root, journal.RootElement.GetProperty("sourcePackageRoot").GetString());
                Assert.Equal(failHealth ? "rolledBack" : "committed", journal.RootElement.GetProperty("stage").GetString());
            }

            Assert.Equal("old-settings", File.ReadAllText(Path.Combine(installed, "Config", "app-settings.json")));
            WindowsIdentity.RunImpersonated(cacheToken, () =>
            {
                Assert.Equal("old-private-cache", File.ReadAllText(Path.Combine(cacheProfile, "cache")));
                File.WriteAllText(Path.Combine(cacheProfile, "cache"), "cache-still-writable");
            });
            if (!failHealth)
                Assert.Contains(new DirectoryInfo(cacheProfile).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(), rule => !rule.IsInherited && rule.IdentityReference.Equals(user)
                        && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
            Assert.Equal("old-database", File.ReadAllText(Path.Combine(installed, "UserData", "Database", "resource-manager.db")));
            var installedManifest = File.ReadAllText(Path.Combine(installed, "release-manifest.json"));
            if (failHealth) Assert.Equal("old-manifest", installedManifest);
            else
            {
                Assert.Equal("45", File.ReadAllText(Path.Combine(installed, "UserData", "Control", "cpu-power-baseline.txt")));
                Assert.Equal("owned-control-state", File.ReadAllText(Path.Combine(installed, "UserData", "Control", "desired-state.json")));
                using var document = JsonDocument.Parse(installedManifest);
                Assert.Equal("0.3.0", document.RootElement.GetProperty("version").GetString());
                var copied = new DirectoryInfo(Path.Combine(installed, "Config"))
                    .GetAccessControl(AccessControlSections.Access);
                Assert.Contains(copied.GetAccessRules(true, false, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>(), rule => rule.IdentityReference == user
                    && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
            }
            Assert.Equal("stable-manager", File.ReadAllText(managerPath));
            Assert.True(File.Exists(Path.Combine(managerDirectory, "operation.lock")));
            Assert.Equal(failHealth ? 2 : 1, runtime.StartCount);
            Assert.Equal(failHealth ? 2 : 1, runtime.StopCount);
            Assert.Equal(failHealth ? 0 : 1, runtime.WrittenVersions.Count);
            if (!failHealth) Assert.Equal("0.3.0", runtime.WrittenVersions.Single());
        }
        finally { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(sandbox); Directory.Delete(sandbox, recursive: true); }
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
                serviceWasRunning = true,
                sourcePackageRoot = Path.Combine(sandbox, "selected-package")
            }));
            var runtime = new FakeUpdateRuntime(false);
            var recovered = await PackageSwitchTransaction.RecoverPendingAsync(
                installed, runtime, CancellationToken.None);
            Assert.Single(recovered);
            Assert.Equal("original-data", File.ReadAllText(Path.Combine(installed, "Config", "app-settings.json")));
            Assert.Equal("0.2.0", Assert.Single(runtime.WrittenVersions));
            Assert.Equal(1, runtime.StartCount);
            Assert.Empty(PackageSwitchTransaction.ListPending(installed));
            using (var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace, "update-transaction.json"))))
                Assert.Equal(Path.Combine(sandbox, "selected-package"),
                    journal.RootElement.GetProperty("sourcePackageRoot").GetString());
            if (nextWasPromoted)
                Assert.True(File.Exists(Path.Combine(workspace, "failed", "Config", "app-settings.json")));
        }
        finally { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(sandbox); Directory.Delete(sandbox, recursive: true); }
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
        finally { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(sandbox); Directory.Delete(sandbox, recursive: true); }
    }

    internal static void CreatePackage(string root, string version)
    {
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        var paths = new[]
        {
            "Install.exe", "Start.exe", "UninstallResourceManager.exe", "Bin/ResourceManager/ResourceManager.exe",
            "Bin/ResourceManager/wwwroot/index.html",
            "Bin/ResourceManagerNativeUi/ResourceManager.NativeUi.exe",
            "Bin/ResourceManagerLauncher/ResourceManager.Launcher.exe",
            "Bin/ResourceManagerSessionAgent/ResourceManager.SessionAgent.exe", "runtime-modules.json",
            "Internal/UpdateManager/ResourceManager.UpdateManager.exe"
        };
        var files = paths.Select(path =>
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, path == "runtime-modules.json" ? "{\"version\":1,\"modules\":[]}" : "fixture:" + path);
            return new { path, length = new FileInfo(full).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) };
        }).ToArray();
        File.WriteAllText(Path.Combine(root, "release-manifest.json"),
            JsonSerializer.Serialize(new { version, sourceCommit = "fixture", files }));
    }

    private sealed class FakeUpdateRuntime(bool failHealth) : IUpdateRuntime
    {
        public bool MaintenanceActive { get; private set; }
        public Func<string, CancellationToken, Task<IDisposable>>? AcquireOverride { get; init; }
        public Action? HealthObserved { get; init; }
        public int StopCount { get; private set; }
        public int StartCount { get; private set; }
        public List<string> WrittenVersions { get; } = [];
        public async Task<IDisposable> AcquirePackageMaintenanceAsync(string installRoot, CancellationToken cancellationToken)
        {
            var inner = AcquireOverride is { } acquire ? await acquire(installRoot, cancellationToken) : new MemoryStream();
            Assert.False(MaintenanceActive);
            MaintenanceActive = true;
            return new Lease(inner, () => MaintenanceActive = false);
        }
        public void StopService(string installRoot) { Assert.True(MaintenanceActive); StopCount++; }
        public void StartService(string installRoot) { Assert.True(MaintenanceActive); StartCount++; }
        public Task WaitForHealthAsync(CancellationToken cancellationToken)
        {
            Assert.True(MaintenanceActive);
            HealthObserved?.Invoke();
            return failHealth ? Task.FromException(new InvalidOperationException("Injected health failure")) : Task.CompletedTask;
        }
        public void WriteInstalledVersion(string root, string version) { Assert.True(MaintenanceActive); WrittenVersions.Add(version); }

        private sealed class Lease(IDisposable inner, Action released) : IDisposable
        {
            public void Dispose() { inner.Dispose(); released(); }
        }
    }
}
