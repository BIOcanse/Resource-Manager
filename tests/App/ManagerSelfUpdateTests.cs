using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class ManagerSelfUpdateTests
{
    [Fact]
    public async Task MissingInstalledManagerCanBeScheduledAndRestoredFromVerifiedPayload()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        File.Delete(fixture.OldManager);
        var launched = false;
        Assert.True(ManagerSelfUpdate.Schedule(ReleasePackageLayout.ForRoot(fixture.Download, "0.3.0"),
            fixture.Installed, _ => launched = true));
        Assert.True(launched);
        await ManagerSelfUpdate.ReplaceVerifiedAsync(UpdateManagerPaths.PackagedExecutable(fixture.Installed),
            fixture.Installed, 123, fixture.OldManager, () => { }, default,
            waitForExit: (_, _, _, _) => Task.CompletedTask);
        Assert.Equal(File.ReadAllBytes(UpdateManagerPaths.PackagedExecutable(fixture.Installed)), File.ReadAllBytes(fixture.OldManager));
    }

    [Fact]
    public async Task SuccessfulReplacementsKeepOnlyCurrentRecoveryExecutables()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        var directory = Path.GetDirectoryName(fixture.OldManager)!;
        var unrelated = Path.Combine(directory, "previous-manual.exe");
        File.WriteAllText(unrelated, "user");
        for (var i = 0; i < 4; i++)
        {
            File.WriteAllText(fixture.OldManager, "old-" + i);
            await ManagerSelfUpdate.ReplaceVerifiedAsync(UpdateManagerPaths.PackagedExecutable(fixture.Installed),
                fixture.Installed, 123, fixture.OldManager, () => { }, default,
                waitForExit: (_, _, _, _) => Task.CompletedTask);
            var backup = Assert.Single(Directory.EnumerateFiles(directory, "previous-*.exe"), path => path != unrelated);
            Assert.Equal("old-" + i, File.ReadAllText(backup));
            Assert.Equal("user", File.ReadAllText(unrelated));
        }
    }

    [Fact]
    public void ExistingUnreadableManagerIsNotMistakenForMissing()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        using var locked = new FileStream(fixture.OldManager, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Throws<IOException>(() => ManagerSelfUpdate.Schedule(ReleasePackageLayout.ForRoot(fixture.Download, "0.3.0"),
            fixture.Installed, _ => throw new Xunit.Sdk.XunitException("Unexpected launch")));
    }
    [Fact]
    public async Task VerifiedPackageManagerCanApplyButReplacementStillComparesTheOldInstalledManager()
    {
        using var fixture = new Fixture();
        var package = ReleasePackageLayout.Verify(fixture.Download);
        var plan = new UpdatePlan(package, fixture.Installed, "0.2.0", false);
        var executable = UpdateManagerPaths.PackagedExecutable(fixture.Download);
        UpdateManagerCommand.RequireApplyManager(executable, plan, ToolText.For("en-US"));
        await PackageSwitchTransaction.ApplyAsync(plan, new Runtime(), CancellationToken.None);
        ProcessStartInfo? launched = null;
        Assert.True(ManagerSelfUpdate.Schedule(package, fixture.Installed, start => launched = start));
        Assert.NotNull(launched);
        Assert.Equal(UpdateManagerPaths.PackagedExecutable(fixture.Installed), launched.FileName);
        Assert.NotEqual(executable, launched.FileName);
        Assert.Equal("old-manager", File.ReadAllText(fixture.OldManager));
    }

    [Theory]
    [InlineData("other-path")]
    [InlineData("changed-payload")]
    public void ArbitraryOrTamperedPackageManagerCannotApply(string failure)
    {
        using var fixture = new Fixture();
        var package = ReleasePackageLayout.Verify(fixture.Download);
        var plan = new UpdatePlan(package, fixture.Installed, "0.2.0", false);
        var executable = UpdateManagerPaths.PackagedExecutable(fixture.Download);
        if (failure == "other-path") executable = Path.Combine(fixture.Download, "ResourceManager.UpdateManager.exe");
        else File.AppendAllText(executable, "changed-after-preflight");
        Assert.ThrowsAny<Exception>(() => UpdateManagerCommand.RequireApplyManager(executable, plan, ToolText.For("en-US")));
        Assert.Equal("old-manager", File.ReadAllText(fixture.OldManager));
    }

    [Fact]
    public async Task CommittedCopyIsLaunchedEvenIfUserPackageChangesAfterSwitch()
    {
        using var fixture = new Fixture();
        var package = ReleasePackageLayout.Verify(fixture.Download);
        await PackageSwitchTransaction.ApplyAsync(new UpdatePlan(package, fixture.Installed, "0.2.0", false),
            new Runtime(), CancellationToken.None);
        File.WriteAllText(UpdateManagerPaths.PackagedExecutable(fixture.Download), "tampered-user-file");
        File.WriteAllText(Path.Combine(fixture.Installed, "Config", "app-settings.json"), "user-settings");

        ProcessStartInfo? launched = null;
        Assert.True(ManagerSelfUpdate.Schedule(package, fixture.Installed, start =>
        {
            launched = start;
            Assert.Equal("fixture:Internal/UpdateManager/ResourceManager.UpdateManager.exe", File.ReadAllText(start.FileName));
            Assert.Throws<IOException>(() => File.WriteAllText(start.FileName, "replace-between-check-and-launch"));
            Assert.Throws<IOException>(() => File.Delete(start.FileName));
        }));
        Assert.NotNull(launched);
        Assert.Equal(UpdateManagerPaths.PackagedExecutable(fixture.Installed), launched.FileName);
        Assert.Equal(Path.GetDirectoryName(launched.FileName), launched.WorkingDirectory);
        Assert.False(launched.UseShellExecute);
        Assert.Equal(new[] { "--replace-manager", fixture.Installed, Environment.ProcessId.ToString() }, launched.ArgumentList);
        // The lock belongs to this operation and is released after dispatch.
        File.WriteAllText(launched.FileName, "writable-after-dispatch");
    }

    [Theory]
    [InlineData("content")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("undeclared")]
    public void InvalidCommittedPayloadCannotDispatchAProcess(string failure)
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        var source = UpdateManagerPaths.PackagedExecutable(fixture.Installed);
        switch (failure)
        {
            case "content": File.WriteAllText(source, "corrupt"); break;
            case "missing": File.Delete(source); break;
            case "version": fixture.WriteInstalledManifest("0.4.0"); break;
            case "duplicate": fixture.WriteInstalledManifest("0.3.0", copies: 2); break;
            case "undeclared": fixture.WriteInstalledManifest("0.3.0", copies: 0); break;
        }
        var launched = false;
        Action dispatch = () => ManagerSelfUpdate.Schedule(
            ReleasePackageLayout.ForRoot(fixture.Download, "0.3.0"), fixture.Installed,
            _ => launched = true);
        if (failure == "missing") Assert.Throws<FileNotFoundException>(dispatch);
        else Assert.Throws<InvalidDataException>(dispatch);
        Assert.False(launched);
        // Failed validation also closes its stream.
        File.WriteAllText(source, "after-failed-validation");
    }

    [Fact]
    public void MatchingInstalledManagerDoesNotStartAHelper()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        File.Copy(UpdateManagerPaths.PackagedExecutable(fixture.Installed), fixture.OldManager, overwrite: true);
        Assert.False(ManagerSelfUpdate.Schedule(ReleasePackageLayout.ForRoot(fixture.Download, "0.3.0"),
            fixture.Installed, _ => throw new Xunit.Sdk.XunitException("Unexpected launch")));
    }

    [Fact]
    public void LaunchFailureReleasesVerifiedPayload()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        Assert.Throws<InvalidOperationException>(() => ManagerSelfUpdate.Schedule(
            ReleasePackageLayout.ForRoot(fixture.Download, "0.3.0"), fixture.Installed,
            _ => throw new InvalidOperationException("injected dispatch failure")));
        File.Delete(UpdateManagerPaths.PackagedExecutable(fixture.Installed));
    }

    [Fact]
    public void PayloadValidationUsesEveryInstalledLanguagePreference()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        fixture.WriteInstalledManifest("0.4.0");
        var english = ToolText.For("en-US");
        foreach (var language in AppLanguage.SupportedIds)
        {
            File.WriteAllText(Path.Combine(fixture.Installed, "Config", "app-settings.json"),
                JsonSerializer.Serialize(new { appearance = new { language } }));
            var text = ToolText.For(language);
            var error = Assert.Throws<InvalidDataException>(() => ManagerSelfUpdate.Schedule(
                ReleasePackageLayout.ForRoot(fixture.Download, "0.3.0"), fixture.Installed,
                _ => throw new Xunit.Sdk.XunitException("Invalid payload was dispatched")));
            Assert.Equal(text.ManagerPayloadInvalid, error.Message);
            Assert.Contains("BACKUP-TOKEN", text.Format(text.ManagerSelfUpdatedBackupFormat, "BACKUP-TOKEN"));
            if (language != "en-US")
            {
                Assert.NotEqual(english.ManagerPayloadInvalid, error.Message);
                Assert.NotEqual(english.ManagerSelfUpdated, text.ManagerSelfUpdated);
            }
        }
        Assert.Contains("載荷", ToolText.For("zh-TW").ManagerPayloadInvalid);
        Assert.DoesNotContain("载荷", ToolText.For("zh-TW").ManagerPayloadInvalid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public028DownloadHelperRelocatesOnlyCommittedBytesAndPreservesExternalFiles(bool interruptedRegistration)
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        foreach (var name in new[] { "Start.exe", UninstallationPlan.ExecutableName })
            File.Copy(Path.Combine(fixture.Download, name), Path.Combine(fixture.Installed, name));
        fixture.WriteInstalledManifest("0.3.0", allEntries: true);
        var old = Path.Combine(Path.GetDirectoryName(fixture.Installed)!, "ResourceManager.UpdateManager", UpdateManagerPaths.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "public-0.2.8-manager");
        File.Delete(fixture.OldManager);
        Directory.Delete(Path.GetDirectoryName(fixture.OldManager)!);
        var writes = 0;
        Task Run() => ManagerSelfUpdate.ReplaceVerifiedAsync(UpdateManagerPaths.PackagedExecutable(fixture.Download),
            fixture.Installed, 123, old, () =>
            {
                writes++;
                Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Installed, "Start.exe")),
                    File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(fixture.OldManager)!, "Start.exe")));
                if (interruptedRegistration && writes == 1) throw new IOException("injected registration failure");
            }, CancellationToken.None, waitForExit: (path, pid, _, _) =>
            { Assert.Equal(old, path); Assert.Equal(123, pid); return Task.CompletedTask; });
        if (interruptedRegistration)
        {
            await Assert.ThrowsAsync<IOException>(Run);
            Assert.NotNull(ManagerSelfUpdate.ReadLastFailure(fixture.Installed));
        }
        await Run();
        Assert.Null(ManagerSelfUpdate.ReadLastFailure(fixture.Installed));
        Assert.Equal(interruptedRegistration ? 2 : 1, writes);
        Assert.Equal("public-0.2.8-manager", File.ReadAllText(old));
        Assert.Equal(File.ReadAllBytes(UpdateManagerPaths.PackagedExecutable(fixture.Installed)), File.ReadAllBytes(fixture.OldManager));
    }

    [Fact]
    public async Task MismatchedDownloadHelperWritesFailureWithoutReplacingManagerOrRegistration()
    {
        using var fixture = new Fixture();
        fixture.CopyPayloadToInstalled();
        File.WriteAllText(UpdateManagerPaths.PackagedExecutable(fixture.Download), "foreign-download");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManagerSelfUpdate.ReplaceVerifiedAsync(
            UpdateManagerPaths.PackagedExecutable(fixture.Download), fixture.Installed, 123, fixture.OldManager,
            () => throw new Xunit.Sdk.XunitException("Unexpected registration"), CancellationToken.None,
            waitForExit: (_, _, _, _) => throw new Xunit.Sdk.XunitException("Unexpected wait")));
        Assert.Equal("old-manager", File.ReadAllText(fixture.OldManager));
        Assert.NotNull(ManagerSelfUpdate.ReadLastFailure(fixture.Installed));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-manager-test-" + Guid.NewGuid().ToString("N"));
        public string Download => Path.Combine(root, "download");
        public string Installed => Path.Combine(root, "ResourceManager");
        public string OldManager => UpdateManagerPaths.InstalledExecutable(Installed);
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Download, "Config"));
            Directory.CreateDirectory(Path.Combine(Installed, "Config"));
            Directory.CreateDirectory(Path.GetDirectoryName(OldManager)!);
            File.WriteAllText(OldManager, "old-manager");
            var paths = new[] { "Install.exe", "Start.exe", "UninstallResourceManager.exe", "Bin/ResourceManager/ResourceManager.exe",
                "Bin/ResourceManager/wwwroot/index.html", "Bin/ResourceManagerNativeUi/ResourceManager.NativeUi.exe",
                "Bin/ResourceManagerLauncher/ResourceManager.Launcher.exe", "Bin/ResourceManagerSessionAgent/ResourceManager.SessionAgent.exe",
                "runtime-modules.json", "Internal/UpdateManager/ResourceManager.UpdateManager.exe" };
            var files = paths.Select(path =>
            {
                var full = Path.Combine(Download, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, path == "runtime-modules.json" ? "{\"version\":1,\"modules\":[]}" : "fixture:" + path);
                return Entry(path, full);
            }).ToArray();
            File.WriteAllText(Path.Combine(Download, "release-manifest.json"),
                JsonSerializer.Serialize(new { version = "0.3.0", sourceCommit = "fixture", files }));
        }
        public void CopyPayloadToInstalled()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateManagerPaths.PackagedExecutable(Installed))!);
            File.Copy(UpdateManagerPaths.PackagedExecutable(Download), UpdateManagerPaths.PackagedExecutable(Installed));
            WriteInstalledManifest("0.3.0");
        }
        public void WriteInstalledManifest(string version, int copies = 1, bool allEntries = false)
        {
            var entry = Entry("Internal/UpdateManager/ResourceManager.UpdateManager.exe", UpdateManagerPaths.PackagedExecutable(Installed));
            File.WriteAllText(Path.Combine(Installed, "release-manifest.json"), JsonSerializer.Serialize(new
                { version, files = Enumerable.Repeat(entry, copies).Concat(allEntries
                    ? new[] { Entry("Start.exe", Path.Combine(Installed, "Start.exe")),
                        Entry(UninstallationPlan.ExecutableName, Path.Combine(Installed, UninstallationPlan.ExecutableName)) }
                    : []).ToArray() }));
        }
        private static object Entry(string path, string full) => new { path, length = new FileInfo(full).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) };
        public void Dispose()
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(root);
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class Runtime : IUpdateRuntime
    {
        public Task<IDisposable> AcquirePackageMaintenanceAsync(string installRoot, CancellationToken cancellationToken) => Task.FromResult<IDisposable>(new MemoryStream());
        public void StopService(string installRoot) { }
        public void InitializeTrustedAccess(string installRoot) { }
        public void StartService(string installRoot) { }
        public Task WaitForHealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void WriteInstalledVersion(string installRoot, string version) { }
    }
}
