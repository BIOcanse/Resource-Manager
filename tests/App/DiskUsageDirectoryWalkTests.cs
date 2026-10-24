using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageDirectoryWalkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSubtreeMarksAncestorsAndSummaryIncompleteWithoutLosingReadableRoot(bool deferred)
    {
        var scanner = new DirectoryWalkDiskUsageScanner(new Catalog(), path =>
        {
            if (path == @"C:\") return [new DirectoryInfo(@"C:\denied")];
            if (path == @"D:\") return [];
            if (path == @"C:\denied\visible") return [];
            if (deferred) return Denied();
            throw new UnauthorizedAccessException("recorded denial");
        });
        var result = await scanner.ScanAsync(new(DiskUsageScanScopes.AllVolumes, DiskUsageScanModes.Full, ""), null, default);
        Assert.False(result.Summary.IsComplete);
        Assert.Equal(1, result.Summary.SkippedCount);
        Assert.Equal(2, result.Tree.Roots.Count);
        Assert.False(result.Tree.CountKnownOf(result.Tree.Roots[0]));
        Assert.False(result.Tree.LogicalSizeKnownOf(result.Tree.Roots[0]));
        Assert.True(result.Tree.CountKnownOf(result.Tree.Roots[1]));
    }

    private static IEnumerable<FileSystemInfo> Denied()
    {
        yield return new DirectoryInfo(@"C:\denied\visible");
        throw new IOException("recorded enumeration failure");
    }

    [Fact]
    public async Task SkippedRequestedVolumeIsIncomplete()
    {
        var scanner = new DirectoryWalkDiskUsageScanner(new Catalog());
        var result = await scanner.ScanAsync(new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Full, "Z:"), null, default);
        Assert.False(result.Summary.IsComplete);
        Assert.Single(result.Summary.Skipped);
        Assert.Empty(result.Tree.Roots);
    }

    private sealed class Catalog : IDiskUsageVolumeCatalog
    {
        public IReadOnlyList<DiskUsageVolume> ReadVolumes() =>
        [
            new("C:", "first", "NTFS", DiskUsageVolumeKinds.Physical, 100, 0, true, true),
            new("D:", "second", "NTFS", DiskUsageVolumeKinds.Physical, 100, 0, true, true)
        ];
    }
}
