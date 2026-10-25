using System.Text.Json;
using System.Security.AccessControl;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class InsideRootUpdateManagerTests
{
    [Fact]
    public async Task PartialPreviousMoveRestoresMovedFilesWhileManagerIsLocked()
    {
        using var fixture = new Fixture();
        var package = Path.Combine(fixture.Parent, "package");
        PackageSwitchTransactionTests.CreatePackage(package, "0.3.0");
        File.WriteAllText(Path.Combine(fixture.Root, "a.txt"), "old-a");
        File.WriteAllText(Path.Combine(fixture.Root, "z.txt"), "old-z");
        using var manager = fixture.LockManager();
        using var operation = PackageOperationLock.Acquire(fixture.Root);
        using var locked = new FileStream(Path.Combine(fixture.Root, "z.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PackageSwitchTransaction.ApplyAsync(
            new(ReleasePackageLayout.Verify(package), fixture.Root, "0.2.0", true), new Runtime(), CancellationToken.None));
        Assert.Equal("old-a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal("stable-manager", File.ReadAllText(fixture.Manager));
        Assert.Empty(PackageSwitchTransaction.ListPending(fixture.Root));
    }

    [Theory]
    [InlineData("previousMoving")]
    [InlineData("previousMoved")]
    [InlineData("nextPromoting")]
    [InlineData("nextPromoted")]
    [InlineData("removingNew")]
    [InlineData("restoringPrevious")]
    public async Task RecoveryPreservesMaintenanceDirectoryAcrossPartialPhases(string stage)
    {
        using var fixture = new Fixture();
        var workspace = fixture.Journal(stage);
        var previous = Path.Combine(workspace, "previous");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "a.txt"), "old-a");
        var oldAuthority = Path.Combine(previous, "UserData", "HostManager", "Validation");
        Directory.CreateDirectory(oldAuthority);
        File.WriteAllText(Path.Combine(oldAuthority, "old-scope"), "old-authority");
        if (stage is "previousMoving" or "restoringPrevious")
            File.WriteAllText(Path.Combine(fixture.Root, "b.txt"), "old-b");
        else
            File.WriteAllText(Path.Combine(previous, "b.txt"), "old-b");
        if (stage is "nextPromoting" or "nextPromoted" or "removingNew")
            File.WriteAllText(Path.Combine(fixture.Root, "a.txt"), "new-a");
        if (stage == "removingNew")
        {
            Directory.CreateDirectory(Path.Combine(workspace, "failed"));
            File.WriteAllText(Path.Combine(workspace, "failed", "new.txt"), "already-moved-new");
        }
        using var manager = fixture.LockManager();
        using var operation = PackageOperationLock.Acquire(fixture.Root);
        Assert.Single(await PackageSwitchTransaction.RecoverPendingAsync(fixture.Root, new Runtime(), CancellationToken.None));
        Assert.Equal("old-a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal("old-b", File.ReadAllText(Path.Combine(fixture.Root, "b.txt")));
        Assert.Equal("old-authority", File.ReadAllText(Path.Combine(fixture.Root, "UserData", "HostManager", "Validation", "old-scope")));
        Assert.Equal("stable-manager", File.ReadAllText(fixture.Manager));
        Assert.Empty(PackageSwitchTransaction.ListPending(fixture.Root));
    }

    [Fact]
    public async Task RecoveryRetryDoesNotDiscardAlreadyRestoredPreviousFiles()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Journal("nextPromoting");
        var previous = Directory.CreateDirectory(Path.Combine(workspace, "previous")).FullName;
        File.WriteAllText(Path.Combine(previous, "a.txt"), "old-a");
        File.WriteAllText(Path.Combine(previous, "z.txt"), "old-z");
        File.WriteAllText(Path.Combine(fixture.Root, "a.txt"), "new-a");
        using var manager = fixture.LockManager();
        using var operation = PackageOperationLock.Acquire(fixture.Root);
        using (var locked = new FileStream(Path.Combine(previous, "z.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PackageSwitchTransaction.RecoverPendingAsync(fixture.Root, new Runtime(), CancellationToken.None));
        Assert.Equal("restoringPrevious", Assert.Single(PackageSwitchTransaction.ListPending(fixture.Root)).Stage);
        Assert.Equal("old-a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        await PackageSwitchTransaction.RecoverPendingAsync(fixture.Root, new Runtime(), CancellationToken.None);
        Assert.Equal("old-a", File.ReadAllText(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal("old-z", File.ReadAllText(Path.Combine(fixture.Root, "z.txt")));
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(workspace, "failed", "a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAndRecoveryKeepServiceAuthorityAtItsOriginalPathAndSecurity(bool failHealth)
    {
        using var fixture = new Fixture();
        var package = Path.Combine(fixture.Parent, "package");
        PackageSwitchTransactionTests.CreatePackage(package, "0.3.0");
        var state = Path.Combine(fixture.Root, "UserData", "HostManager", "Validation", "generation-00000001", "scope.json");
        var authority = Path.Combine(fixture.Root, ".resource-manager-validation-authority", "scope.json");
        using var scopeDirectory = WindowsProcessEffectValidationScopeStorage.AcquireDirectory(state, true);
        using var authorityDirectory = WindowsProcessEffectValidationScopeStorage.AcquireDirectory(authority, true);
        File.WriteAllText(state, "owned-scope"); File.WriteAllText(authority, "owned-authority");
        var scopeAcl = new DirectoryInfo(Path.GetDirectoryName(state)!).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var authorityAcl = new DirectoryInfo(Path.GetDirectoryName(authority)!).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        using var lockedScope = new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var lockedAuthority = new FileStream(authority, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var operation = PackageOperationLock.Acquire(fixture.Root);
        var apply = PackageSwitchTransaction.ApplyAsync(new(ReleasePackageLayout.Verify(package), fixture.Root, "0.2.0", true),
            new Runtime { FailHealth = failHealth }, CancellationToken.None);
        if (failHealth) await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
        else await apply;
        Assert.Equal("owned-scope", File.ReadAllText(state)); Assert.Equal("owned-authority", File.ReadAllText(authority));
        Assert.Equal(scopeAcl, new DirectoryInfo(Path.GetDirectoryName(state)!).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        Assert.Equal(authorityAcl, new DirectoryInfo(Path.GetDirectoryName(authority)!).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        Assert.Empty(PackageSwitchTransaction.ListPending(fixture.Root));
    }

    [Fact]
    public void RegisteredOwnershipAcceptsOnlyCurrentOrKnownLegacyManager()
    {
        using var fixture = new Fixture();
        Assert.Equal(fixture.Manager, UpdateManagerPaths.RequireOwnedManager(fixture.Root, fixture.Manager));
        var legacy = Path.Combine(fixture.Parent, "ResourceManager.UpdateManager", UpdateManagerPaths.ExecutableName);
        Assert.Equal(legacy, UpdateManagerPaths.RequireOwnedManager(fixture.Root, legacy));
        Assert.Throws<InvalidDataException>(() => UpdateManagerPaths.RequireOwnedManager(fixture.Root,
            Path.Combine(fixture.Parent, "Other", UpdateManagerPaths.ExecutableName)));
        Assert.Throws<InvalidDataException>(() => UpdateManagerPaths.RequireOwnedManager(fixture.Root, null));
    }

    [Fact]
    public void ActualInstallationConsentNamesTheInsideRootLocationInEveryOfferedLanguage()
    {
        var layout = ReleasePackageLayout.ForRoot("INSTALL-ROOT", "0.3.0");
        var english = ToolText.For("en-US");
        foreach (var language in AppLanguage.SupportedIds)
        {
            var text = ToolText.For(language);
            Assert.Contains("Internal/Management", ResourceManager.Installer.InstallationConsent.Build(text,
                ResourceManager.Installer.PackageLayout.ForRoot(layout.Root, layout.Version), null));
            if (language != "en-US") Assert.NotEqual(english.InstallManager, text.InstallManager);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "rm-inner-manager-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, "ResourceManager");
        public string Manager => UpdateManagerPaths.InstalledExecutable(Root);
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Manager)!);
            File.WriteAllText(Manager, "stable-manager");
        }
        public FileStream LockManager() => new(Manager, FileMode.Open, FileAccess.Read, FileShare.Read);
        public string Journal(string stage)
        {
            var workspace = Directory.CreateDirectory(Path.Combine(Parent, "ResourceManager-update-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(workspace, "update-transaction.json"), JsonSerializer.Serialize(new
            {
                stage, InstallRoot = Root, PreviousVersion = "0.2.0", targetVersion = "0.3.0",
                serviceWasRunning = true, preserveManagement = true
            }));
            return workspace;
        }
        public void Dispose()
        {
            OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(Parent);
            Directory.Delete(Parent, true);
        }
    }
    private sealed class Runtime : IUpdateRuntime
    {
        public bool FailHealth;
        public Task<IDisposable> AcquirePackageMaintenanceAsync(string root, CancellationToken token) => Task.FromResult<IDisposable>(new MemoryStream());
        public void StopService(string root) { }
        public void InitializeTrustedAccess(string root) { }
        public void StartService(string root) { }
        public Task WaitForHealthAsync(CancellationToken token) => FailHealth ? Task.FromException(new IOException("health failure")) : Task.CompletedTask;
        public void WriteInstalledVersion(string root, string version) { }
    }
}
