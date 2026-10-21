using System.Text.Json;
using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class CompletedUpdateCleanupTests
{
    [Fact]
    public void CommittedUpdateRetainsItsBackupAndLeavesFailedPendingAndUnrelatedWork()
    {
        using var tree = new Tree();
        var old = tree.Journal("committed");
        var failed = tree.Journal("rolledBack");
        var pending = tree.Journal("previousMoved");
        var latest = tree.Journal("committed");
        var foreign = tree.Journal("committed", Path.Combine(tree.Root, "OtherInstallation"));
        PackageSwitchTransaction.RetainLatestBackup(tree.InstallRoot, latest);
        Assert.False(Directory.Exists(old));
        foreach (var retained in new[] { failed, pending, latest, foreign }) Assert.True(File.Exists(Path.Combine(retained, "previous", "data")));
        Assert.Equal(pending, Assert.Single(PackageSwitchTransaction.ListPending(tree.InstallRoot)).Workspace);
    }

    [Fact]
    public void UncommittedKeepTargetCannotDeleteAnExistingBackup()
    {
        using var tree = new Tree();
        var backup = tree.Journal("committed");
        var pending = tree.Journal("previousMoved");
        Assert.Throws<InvalidDataException>(() => PackageSwitchTransaction.RetainLatestBackup(tree.InstallRoot, pending));
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void OnlyReceiptedCompletedDownloadIsDeletedAndVersionMismatchKeepsIt()
    {
        using var tree = new Tree();
        var workspace = Path.Combine(tree.Root, "ResourceManager-update-download-" + Guid.NewGuid().ToString("N"));
        var package = Directory.CreateDirectory(Path.Combine(workspace, "package")).FullName;
        File.WriteAllText(Path.Combine(package, "program"), "payload");
        Assert.False(ProductUpdateDownload.RemoveCompleted(package, tree.InstallRoot, "0.4.0"));
        ProductUpdateDownload.RecordReady(package, tree.InstallRoot, "0.4.0");
        Assert.Throws<InvalidDataException>(() => ProductUpdateDownload.RemoveCompleted(package, tree.InstallRoot, "0.3.0"));
        Assert.True(Directory.Exists(workspace));
        Assert.True(ProductUpdateDownload.RemoveCompleted(package, tree.InstallRoot, "0.4.0"));
        Assert.False(Directory.Exists(workspace));
        var manual = Directory.CreateDirectory(Path.Combine(tree.Root, "manual", "package")).FullName;
        Assert.False(ProductUpdateDownload.RemoveCompleted(manual, tree.InstallRoot, "0.4.0"));
        Assert.True(Directory.Exists(manual));
    }

    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rm-completed-update-test-" + Guid.NewGuid().ToString("N"))).FullName;
        public string InstallRoot => Path.Combine(Root, "ResourceManager");
        public string Journal(string stage, string? target = null)
        {
            var workspace = Path.Combine(Root, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(workspace, "previous"));
            File.WriteAllText(Path.Combine(workspace, "previous", "data"), "backup");
            File.WriteAllText(Path.Combine(workspace, "update-transaction.json"), JsonSerializer.Serialize(new
            { InstallRoot = target ?? InstallRoot, stage, PreviousVersion = "0.3.0", targetVersion = "0.4.0", serviceWasRunning = true }));
            return workspace;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
