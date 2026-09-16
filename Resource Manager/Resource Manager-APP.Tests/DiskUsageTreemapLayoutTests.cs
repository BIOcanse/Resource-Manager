using ResourceManager.App.Domain.DiskUsage;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageTreemapLayoutTests
{
    [Fact]
    public void BuilderRollsChildSizesIntoParentsWithoutRecursion()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", isDirectory: true, 0, 0);
        var docs = builder.Add(root, "docs", isDirectory: true, 0, 0);
        _ = builder.Add(docs, "a.txt", isDirectory: false, 300, 300);
        _ = builder.Add(docs, "b.txt", isDirectory: false, 700, 700);
        _ = builder.Add(root, "c.bin", isDirectory: false, 1000, 1000);

        var tree = builder.Build();

        Assert.Equal(2000, tree.SizeOf(root));
        Assert.Equal(1000, tree.SizeOf(docs));
        // 目录自己不算一个文件，但要数得出子树里有几个文件。
        Assert.Equal(3, tree.FileCountOf(root));
        Assert.Equal(2, tree.FileCountOf(docs));
        Assert.Equal("C:\\", tree.PathOf(root));
        Assert.Equal(Path.Combine("C:\\", "docs", "a.txt"), tree.PathOf(docs + 1));
    }

    [Fact]
    public void LayoutFillsTheUnitSquareWithoutGapsOrOverlap()
    {
        var tree = CreateSampleTree(out var root);

        var layout = DiskUsageTreemapLayout.Create(tree, root);

        // 第一层必须把整块铺满：面积正比于字节数，加起来就是全部。
        var depthOne = layout.Tiles.Where(static tile => tile.Depth == 1).ToArray();
        Assert.Equal(1d, depthOne.Sum(tile => (double)tile.Width * tile.Height), 3);
        Assert.All(depthOne, tile =>
        {
            Assert.True(tile.Width > 0 && tile.Height > 0);
            Assert.True(tile.X >= -1e-4 && tile.X + tile.Width <= 1.0001f);
            Assert.True(tile.Y >= -1e-4 && tile.Y + tile.Height <= 1.0001f);
        });

        for (var left = 0; left < depthOne.Length; left++)
        {
            for (var right = left + 1; right < depthOne.Length; right++)
            {
                Assert.False(
                    Overlaps(depthOne[left], depthOne[right]),
                    "同一层的方格不能互相压住。");
            }
        }
    }

    [Fact]
    public void LayoutAreaFollowsByteShare()
    {
        var tree = CreateSampleTree(out var root);

        var layout = DiskUsageTreemapLayout.Create(tree, root);

        var total = (double)tree.SizeOf(root);
        foreach (var tile in layout.Tiles.Where(static tile => tile.Depth == 1))
        {
            var area = (double)tile.Width * tile.Height;
            Assert.Equal(tile.SizeBytes / total, area, 3);
        }
    }

    [Fact]
    public void LayoutStopsAtTheTileBudgetAndSaysHowManyItLeftOut()
    {
        var tree = CreateSampleTree(out var root);

        var layout = DiskUsageTreemapLayout.Create(tree, root, maximumTileCount: 3);

        Assert.True(layout.Tiles.Count <= 3);
        Assert.True(layout.OmittedCount > 0, "剪掉的方格要数出来，不能悄悄少画。");
    }

    private static DiskUsageTree CreateSampleTree(out int root)
    {
        var builder = new DiskUsageTreeBuilder();
        root = builder.Add(-1, "root", isDirectory: true, 0, 0);
        var big = builder.Add(root, "big", isDirectory: true, 0, 0);
        _ = builder.Add(big, "big-1", isDirectory: false, 4000, 4000);
        _ = builder.Add(big, "big-2", isDirectory: false, 2000, 2000);
        _ = builder.Add(root, "medium", isDirectory: false, 2500, 2500);
        _ = builder.Add(root, "small", isDirectory: false, 900, 900);
        _ = builder.Add(root, "tiny", isDirectory: false, 100, 100);
        return builder.Build();
    }

    private static bool Overlaps(DiskUsageTile left, DiskUsageTile right)
    {
        const float tolerance = 1e-4f;
        return left.X + left.Width > right.X + tolerance
            && right.X + right.Width > left.X + tolerance
            && left.Y + left.Height > right.Y + tolerance
            && right.Y + right.Height > left.Y + tolerance;
    }
}
