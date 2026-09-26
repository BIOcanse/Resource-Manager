using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsMftDiskUsageScannerTests
{
    [Fact]
    public void FolderRootBuildsOnlyRequestedNtfsSubtree()
    {
        var records = Records();
        var folder = Path.Combine("C:\\", "Work");
        var builder = new DiskUsageTreeBuilder();

        Assert.True(WindowsMftDiskUsageScanner.BuildSubtree("C:", folder, records,
            builder, new WindowsMftDiskUsageScanner.ScanTotals(), CancellationToken.None));

        var tree = builder.Build();
        Assert.Equal(2, tree.Count);
        Assert.Equal(folder, tree.PathOf(Assert.Single(tree.Roots)));
        Assert.Equal(20, tree.SizeOf(0));
        Assert.Equal(Path.Combine(folder, "inside.txt"), tree.PathOf(1));
    }

    [Fact]
    public void WholeVolumeStillIncludesOtherDirectoriesAndMissingFolderIsNotRelabeled()
    {
        var records = Records();
        var volumeBuilder = new DiskUsageTreeBuilder();
        Assert.True(WindowsMftDiskUsageScanner.BuildSubtree("C:", null, records,
            volumeBuilder, new WindowsMftDiskUsageScanner.ScanTotals(), CancellationToken.None));
        Assert.Equal(2, volumeBuilder.Build().FileCountOf(0));

        var missingBuilder = new DiskUsageTreeBuilder();
        Assert.False(WindowsMftDiskUsageScanner.BuildSubtree("C:", @"C:\Missing", records,
            missingBuilder, new WindowsMftDiskUsageScanner.ScanTotals(), CancellationToken.None));
        Assert.Empty(missingBuilder.Build().Roots);
    }

    private static Dictionary<long, NtfsFileRecord> Records() => new()
    {
        [5] = new(true, true, 5, "", 0, 0),
        [10] = new(true, true, 5, "Work", 0, 0),
        [11] = new(true, false, 10, "inside.txt", 20, 20),
        [20] = new(true, true, 5, "Other", 0, 0),
        [21] = new(true, false, 20, "outside.txt", 30, 30)
    };
}
