using System.Text.Json;
using ResourceManager.Updater;

namespace Resource_Manager_APP.Tests;

public sealed class UpdateTransactionHistoryTests
{
    [Fact]
    public void ClosedHistoryDoesNotHideTheRemainingInterruptedUpdate()
    {
        using var tree = new Tree();
        string[] closed = ["committed", "rolledBack", "recovered"];
        for (var index = 0; index < 1002; index++) tree.Journal(closed[index % closed.Length]);
        tree.EmptyWorkspace(); // The automatic download directory has no transaction journal.
        var interrupted = tree.Journal("previousMoved");
        var pending = Assert.Single(PackageSwitchTransaction.ListPending(tree.InstallRoot));
        Assert.Equal(interrupted, pending.Workspace);
        Assert.Equal("previousMoved", pending.Stage);
        Assert.True(pending.ServiceWasRunning);
        Assert.Equal(1004, Directory.GetDirectories(tree.Root).Length);
        Assert.Equal(1003, tree.Journals.Count);
        Assert.All(tree.Journals, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void ActionableHistoryStillHasItsOwnBound()
    {
        using var tree = new Tree();
        for (var index = 0; index < 1001; index++) tree.Journal("previousMoved");
        Assert.Throws<InvalidDataException>(() => PackageSwitchTransaction.ListPending(tree.InstallRoot));
        Assert.All(tree.Journals, path => Assert.True(File.Exists(path)));
    }

    private sealed class Tree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "rm-update-history-" + Guid.NewGuid().ToString("N"));
        internal string InstallRoot => Path.Combine(Root, "ResourceManager");
        internal List<string> Journals { get; } = [];
        private readonly List<string> directories = [];
        internal Tree() => Directory.CreateDirectory(Root);
        internal string EmptyWorkspace()
        {
            var workspace = Path.Combine(Root, "ResourceManager-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            directories.Add(workspace);
            return workspace;
        }
        internal string Journal(string stage)
        {
            var workspace = EmptyWorkspace();
            var path = Path.Combine(workspace, "update-transaction.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                InstallRoot, stage, PreviousVersion = "0.2.0", targetVersion = "0.3.0", serviceWasRunning = true
            }));
            Journals.Add(path);
            return workspace;
        }
        public void Dispose()
        {
            foreach (var path in Journals) File.Delete(path);
            foreach (var directory in directories) Directory.Delete(directory);
            Directory.Delete(Root);
        }
    }
}
