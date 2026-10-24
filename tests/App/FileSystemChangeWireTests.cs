using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed class FileSystemChangeWireTests
{
    [Fact]
    public void ComparePublishesCanonicalEntryAndChangeKindsWithCorrectCounts()
    {
        var now = DateTimeOffset.UtcNow;
        var before = Snapshot(
            new("C:\\wire\\modified", "C:\\wire", "file", 2, now),
            new("C:\\wire\\deleted", "C:\\wire", "file", 4, now));
        var after = Snapshot(
            new("C:\\wire\\modified", "C:\\wire", "file", 5, now.AddSeconds(1)),
            new("C:\\wire\\created", "C:\\wire", "file", 8, now),
            new("C:\\wire\\directory", "C:\\wire", "directory", 0, now));

        var report = new FileSystemChangeTracker().Compare(before, after);

        Assert.Equal(2, report.CreatedCount);
        Assert.Equal(1, report.DeletedCount);
        Assert.Equal(1, report.ModifiedCount);
        Assert.Equal(7, report.NetBytes);
        Assert.Equal(["created", "deleted", "created", "modified"], report.Changes.Select(item => item.ChangeKind));
        Assert.Equal(["file", "file", "directory", "file"], report.Changes.Select(item => item.EntryKind));
    }

    private static FileInventorySnapshot Snapshot(params FileInventoryEntry[] entries)
        => new("wire", DateTimeOffset.UtcNow, ["C:\\wire"], 100,
            entries.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase), []);
}
