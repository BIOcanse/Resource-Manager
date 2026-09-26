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
    public void TilesTooSmallToSeeAreLeftOutAndCounted()
    {
        var tree = CreateSampleTree(out var root);

        // 一块很小的画布：除了最大的那几个，其余都不够一个像素门槛。
        var tiny = DiskUsageViewWindow.Normalize(64f, 64f, 1f, 0f, 0f, 1f, 1f);
        var layout = DiskUsageTreemapLayout.Create(tree, root, tiny);

        Assert.True(layout.OmittedCount > 0, "剪掉的方格要数出来，不能悄悄少画。");
        Assert.All(layout.Tiles.Where(static tile => tile.Depth > 0), tile =>
            Assert.True(
                tiny.PixelAreaOf(tile.Width, tile.Height)
                    >= DiskUsageViewWindow.MinimumTilePixelArea,
                "发下来的方格必须都够得上像素门槛。"));
    }

    [Fact]
    public void ZoomingInRevealsTilesThatWereTooSmallBefore()
    {
        var tree = CreateSampleTree(out var root);
        var window = new { MinX = 0f, MinY = 0f, MaxX = 1f, MaxY = 1f };

        var far = DiskUsageTreemapLayout.Create(
            tree,
            root,
            DiskUsageViewWindow.Normalize(200f, 200f, 1f, window.MinX, window.MinY, window.MaxX, window.MaxY));
        var near = DiskUsageTreemapLayout.Create(
            tree,
            root,
            DiskUsageViewWindow.Normalize(200f, 200f, 40f, window.MinX, window.MinY, window.MaxX, window.MaxY));

        // 同一块范围，放大之后只能看到更多，不能更少 —— 这就是"放大后显示"。
        Assert.True(
            near.Tiles.Count > far.Tiles.Count,
            $"放大后应该出现更多方格，却是 {far.Tiles.Count} → {near.Tiles.Count}。");
        var farNodes = far.Tiles.Select(static tile => tile.NodeId).ToHashSet();
        Assert.Subset(near.Tiles.Select(static tile => tile.NodeId).ToHashSet(), farNodes);
    }

    [Fact]
    public void TilesOutsideTheVisibleRectangleAreNotSent()
    {
        var tree = CreateSampleTree(out var root);

        // 只看左上角一小块。落在外面的方格连同它们的子树都不该发下来。
        var corner = DiskUsageViewWindow.Normalize(4000f, 4000f, 1f, 0f, 0f, 0.2f, 0.2f);
        var layout = DiskUsageTreemapLayout.Create(tree, root, corner);

        Assert.All(layout.Tiles.Where(static tile => tile.Depth > 0), tile =>
            Assert.True(
                corner.Intersects(tile.X, tile.Y, tile.Width, tile.Height),
                "看不见的方格不该发下来。"));
    }

    [Fact]
    public void AViewWithNothingUsableFallsBackToTheWholePicture()
    {
        // 客户端没说、说了越界值、或者把上下界写反了，都要收拢成一个能用的视图，
        // 而不是让布局算法去面对一个空矩形。
        var missing = DiskUsageViewWindow.Normalize(null, null, null, null, null, null, null);
        Assert.Equal(DiskUsageViewWindow.Full, missing);

        var inverted = DiskUsageViewWindow.Normalize(800f, 600f, 2f, 0.9f, 0.9f, 0.1f, 0.1f);
        Assert.Equal(0f, inverted.MinX);
        Assert.Equal(1f, inverted.MaxX);
        Assert.Equal(0f, inverted.MinY);
        Assert.Equal(1f, inverted.MaxY);

        var absurd = DiskUsageViewWindow.Normalize(
            float.NaN, -5f, 0.01f, -2f, -2f, 99f, 99f);
        Assert.Equal(DiskUsageViewWindow.Full.PixelWidth, absurd.PixelWidth);
        Assert.True(absurd.PixelHeight >= 64f);
        Assert.True(absurd.Scale >= 1f);
        Assert.Equal(0f, absurd.MinX);
        Assert.Equal(1f, absurd.MaxX);
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
        // 几个小到缩着看根本占不满一个像素的节点。
        // 少了它们就没有东西可剪，"太小的不发、放大再发"也就测不出来。
        var specks = builder.Add(root, "specks", isDirectory: true, 0, 0);
        _ = builder.Add(specks, "speck-1", isDirectory: false, 2, 2);
        _ = builder.Add(specks, "speck-2", isDirectory: false, 1, 1);
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
