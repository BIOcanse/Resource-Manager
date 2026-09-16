namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>布局输出的一个方格。坐标都在单位空间 [0,1]×[0,1] 里。</summary>
public readonly record struct DiskUsageTile(
    int NodeId,
    int ParentNodeId,
    /// <summary>这个方格在树里的深度，根为 0。画的时候用它做明度分层。</summary>
    int Depth,
    float X,
    float Y,
    float Width,
    float Height,
    bool IsDirectory,
    long SizeBytes);

/// <summary>一次布局的结果。</summary>
public sealed record DiskUsageLayout(
    int RootNodeId,
    IReadOnlyList<DiskUsageTile> Tiles,
    /// <summary>因为太小或超预算而没有单独出方格的节点数。界面可以据此提示"还有更小的没画"。</summary>
    int OmittedCount);

/// <summary>
/// 方格图布局（squarified treemap）。
///
/// 一句话：把一个矩形按子节点的字节数切开，每个子节点拿到的面积正比于它的字节数，
/// 并且尽量让切出来的方格接近正方形 —— 长条形的方格既难看也难点。
///
/// 输出在单位空间里。同一个根节点的布局是**稳定**的，不随缩放变化，
/// 所以前端拿到之后缩放和平移全是本地的视口变换，不用回后端重算。
///
/// 两道剪枝，都是为了不把看不见的东西发出去：
/// 面积小于阈值的不出方格，方格总数到预算就停。两者都计入 <see cref="DiskUsageLayout.OmittedCount"/>。
/// </summary>
public static class DiskUsageTreemapLayout
{
    public static DiskUsageLayout Create(
        DiskUsageTree tree,
        int rootNodeId,
        int maximumTileCount = 50_000,
        float minimumArea = 1f / 400_000f)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var tiles = new List<DiskUsageTile>(Math.Min(maximumTileCount, 4096));
        var omitted = 0;

        tiles.Add(new DiskUsageTile(
            rootNodeId,
            tree.ParentOf(rootNodeId),
            0,
            0f,
            0f,
            1f,
            1f,
            tree.IsDirectory(rootNodeId),
            tree.SizeOf(rootNodeId)));

        // 显式队列做广度优先：先把大的、浅的方格铺满，预算用完时剩下的都是小的深的。
        var pending = new Queue<PendingBox>();
        pending.Enqueue(new PendingBox(rootNodeId, 0, 0f, 0f, 1f, 1f));

        while (pending.Count > 0)
        {
            var box = pending.Dequeue();
            if (!tree.IsDirectory(box.NodeId))
            {
                continue;
            }

            var children = tree.ChildrenBySizeDescending(box.NodeId);
            if (children.Length == 0)
            {
                continue;
            }

            long total = 0;
            foreach (var child in children)
            {
                total += Math.Max(0, tree.SizeOf(child));
            }
            if (total <= 0)
            {
                continue;
            }

            foreach (var placed in Squarify(tree, children, total, box))
            {
                var area = placed.Width * placed.Height;
                if (area < minimumArea || tiles.Count >= maximumTileCount)
                {
                    omitted++;
                    continue;
                }
                tiles.Add(placed.Tile);
                if (placed.Tile.IsDirectory)
                {
                    pending.Enqueue(new PendingBox(
                        placed.Tile.NodeId,
                        placed.Tile.Depth,
                        placed.X,
                        placed.Y,
                        placed.Width,
                        placed.Height));
                }
            }
        }

        return new DiskUsageLayout(rootNodeId, tiles, omitted);
    }

    /// <summary>
    /// 把一个矩形按子节点大小切成一行行，每行内部再平分。
    /// 行的方向总是沿短边切，这样方格才接近正方形。
    /// </summary>
    private static List<PlacedTile> Squarify(
        DiskUsageTree tree,
        int[] children,
        long total,
        PendingBox box)
    {
        var placed = new List<PlacedTile>(children.Length);
        var x = box.X;
        var y = box.Y;
        var width = box.Width;
        var height = box.Height;
        var remaining = (double)total;
        var index = 0;

        while (index < children.Length && width > 0 && height > 0)
        {
            var alongWidth = width >= height;
            var shortSide = alongWidth ? height : width;

            // 先决定这一行放几个：加到长宽比不再变好为止。
            var rowCount = 0;
            double rowSum = 0;
            var bestRatio = double.MaxValue;
            while (index + rowCount < children.Length)
            {
                var candidateSum = rowSum + Math.Max(0, tree.SizeOf(children[index + rowCount]));
                if (candidateSum <= 0)
                {
                    rowCount++;
                    continue;
                }
                var ratio = WorstAspectRatio(
                    tree,
                    children,
                    index,
                    rowCount + 1,
                    candidateSum,
                    remaining,
                    alongWidth ? width : height,
                    shortSide);
                if (ratio > bestRatio)
                {
                    break;
                }
                bestRatio = ratio;
                rowSum = candidateSum;
                rowCount++;
            }

            if (rowCount == 0)
            {
                break;
            }

            // 这一行占掉的厚度，正比于它在剩余量里的份额。
            var rowFraction = remaining > 0 ? rowSum / remaining : 0;
            var thickness = (float)(rowFraction * (alongWidth ? width : height));
            var offset = alongWidth ? y : x;
            for (var item = 0; item < rowCount; item++)
            {
                var child = children[index + item];
                var size = Math.Max(0, tree.SizeOf(child));
                var share = rowSum > 0 ? size / rowSum : 0;
                var extent = (float)(share * shortSide);
                var tileX = alongWidth ? x : offset;
                var tileY = alongWidth ? offset : y;
                var tileWidth = alongWidth ? thickness : extent;
                var tileHeight = alongWidth ? extent : thickness;
                placed.Add(new PlacedTile(
                    new DiskUsageTile(
                        child,
                        box.NodeId,
                        box.Depth + 1,
                        tileX,
                        tileY,
                        tileWidth,
                        tileHeight,
                        tree.IsDirectory(child),
                        size),
                    tileX,
                    tileY,
                    tileWidth,
                    tileHeight));
                offset += extent;
            }

            if (alongWidth)
            {
                x += thickness;
                width -= thickness;
            }
            else
            {
                y += thickness;
                height -= thickness;
            }
            remaining -= rowSum;
            index += rowCount;
        }

        return placed;
    }

    /// <summary>这一行里最差的那个方格的长宽比。越接近 1 越好。</summary>
    private static double WorstAspectRatio(
        DiskUsageTree tree,
        int[] children,
        int start,
        int count,
        double rowSum,
        double remaining,
        double longSide,
        double shortSide)
    {
        if (rowSum <= 0 || remaining <= 0 || shortSide <= 0)
        {
            return double.MaxValue;
        }

        var thickness = rowSum / remaining * longSide;
        if (thickness <= 0)
        {
            return double.MaxValue;
        }

        var worst = 0d;
        for (var item = 0; item < count; item++)
        {
            var size = Math.Max(0, tree.SizeOf(children[start + item]));
            if (size <= 0)
            {
                continue;
            }
            var extent = size / rowSum * shortSide;
            if (extent <= 0)
            {
                continue;
            }
            worst = Math.Max(worst, Math.Max(thickness / extent, extent / thickness));
        }
        return worst == 0 ? double.MaxValue : worst;
    }

    private readonly record struct PendingBox(
        int NodeId,
        int Depth,
        float X,
        float Y,
        float Width,
        float Height);

    private readonly record struct PlacedTile(
        DiskUsageTile Tile,
        float X,
        float Y,
        float Width,
        float Height);
}
