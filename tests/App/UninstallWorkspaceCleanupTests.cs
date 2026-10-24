using System.Text.Json;
using ResourceManager.Shared.Packages;

namespace Resource_Manager_APP.Tests;

public sealed class UninstallWorkspaceCleanupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaunchCleanupFailureRetainsPayloadAndRegistrations(bool keepData)
    {
        using var fixture = new Fixture();
        var runtime = new Runtime { FailLaunchCleanup = true };
        Assert.Throws<IOException>(() => UninstallationWorkflow.Run(fixture.Plan, keepData, runtime));
        Assert.True(runtime.Stopped);
        Assert.False(runtime.ServiceRemoved);
        Assert.False(runtime.RegistrationRemoved);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "Start.exe")));
    }

    [Fact]
    public void KeepRemovesOwnedWorkspacesButLeavesOtherInstallation()
    {
        using var fixture = new Fixture();
        var old = fixture.Journal("committed");
        var foreign = fixture.Journal("committed", Path.Combine(fixture.Parent, "Other"));
        var package = Directory.CreateDirectory(Path.Combine(fixture.Parent, "ResourceManager-update-download-" + Guid.NewGuid().ToString("N"), "package")).FullName;
        ProductUpdateDownload.RecordReady(package, fixture.Root, "0.4.0");
        var archive = UninstallationWorkflow.Run(fixture.Plan, true, new Runtime())!;
        Assert.Equal([archive], Directory.GetFileSystemEntries(fixture.Root));
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(Path.GetDirectoryName(package)));
        Assert.True(Directory.Exists(foreign));
        Assert.False(Directory.Exists(fixture.Manager));
    }

    [Fact]
    public void PendingUpdateStopsBeforeDataOrRegistrationRemoval()
    {
        using var fixture = new Fixture();
        var pending = fixture.Journal("nextPromoted");
        var runtime = new Runtime();
        Assert.Throws<InvalidOperationException>(() => UninstallationWorkflow.Run(fixture.Plan, true, runtime));
        Assert.False(runtime.Stopped);
        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.Root, "UserData", "profile")));
        Assert.True(Directory.Exists(pending));
    }

    [Fact]
    public void RetryAfterPartialDeletionReusesTheCompleteArchive()
    {
        using var fixture = new Fixture();
        using (var locked = new FileStream(Path.Combine(fixture.Root, "Start.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => UninstallationWorkflow.Run(fixture.Plan, true, new Runtime()));
        var zip = Assert.Single(Directory.GetFiles(fixture.Root, "*.zip"));
        if (Directory.Exists(Path.Combine(fixture.Root, "UserData"))) OwnedPackageTree.Delete(Path.Combine(fixture.Root, "UserData"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "UserData"));
        Assert.Equal(zip, UninstallationWorkflow.Run(fixture.Plan, true, new Runtime()));
        var target = Directory.CreateDirectory(Path.Combine(fixture.Parent, "fresh")).FullName;
        UserDataArchive.Import(target, zip);
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "UserData", "profile")));
        Assert.Equal([zip], Directory.GetFileSystemEntries(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Parent { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-uninstall-workspaces-test-" + Guid.NewGuid().ToString("N"))).FullName;
        public string Root => Path.Combine(Parent, "ResourceManager");
        public string Manager => UpdateManagerPaths.InstalledDirectory(Root);
        public UninstallationPlan Plan => new(Root, "backend", Manager);
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "UserData"));
            Directory.CreateDirectory(Manager);
            File.WriteAllText(Path.Combine(Root, "UserData", "profile"), "original");
            File.WriteAllText(Path.Combine(Root, "Start.exe"), "program");
        }
        public string Journal(string stage, string? root = null)
        {
            var workspace = Directory.CreateDirectory(Path.Combine(Parent, "ResourceManager-update-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(workspace, "update-transaction.json"), JsonSerializer.Serialize(new { stage, InstallRoot = root ?? Root }));
            return workspace;
        }
        public void Dispose() { OwnedTestDirectoryPermissions.RestoreOwnerAccessForCleanup(Parent); Directory.Delete(Parent, true); }
    }
    private sealed class Runtime : IUninstallationRuntime
    {
        public bool Stopped;
        public bool FailLaunchCleanup;
        public bool ServiceRemoved;
        public bool RegistrationRemoved;
        public void RequireOwned(UninstallationPlan plan) { }
        public void StopProduct(UninstallationPlan plan) => Stopped = true;
        public void RemoveLaunchInterception(UninstallationPlan plan) { if (FailLaunchCleanup) throw new IOException("cleanup denied"); Assert.True(File.Exists(Path.Combine(plan.Root, "Start.exe"))); }
        public void RemoveService(UninstallationPlan plan) => ServiceRemoved = true;
        public void RemoveRegistration(UninstallationPlan plan) => RegistrationRemoved = true;
        public void ProtectArchive(string archive) { }
    }
}
