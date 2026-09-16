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
    /// <summary>
    /// 这一次视图下没有单独出方格的节点数：要么小到不够一个像素，要么在视野之外。
    /// 它们不是被丢掉了，放大或移过去就会出现。
    /// </summary>
    int OmittedCount);

/// <summary>
/// 客户端当前实际看到的东西。布局按它决定发哪些方格。
///
/// 为什么需要它：方格该不该画，取决于它在屏幕上占几个像素，
/// 而这取决于画布的物理像素尺寸、窗口缩放和滚轮倍数 —— 全都是客户端的事实，
/// 后端无从猜测。写死一个"面积小于百万分之二点五就不发"的阈值，
/// 在 4K 屏上会糊掉本该看得见的方格，放大十倍之后又只能看见一片空白。
///
/// 两道剪枝都由它推出来：
/// 1. **视野外的整棵子树直接跳过** —— 子方格一定在父方格里面，父的盒子不相交就不用往下看。
/// 2. **在屏幕上小于一个像素门槛的不发** —— 也不往下钻，因为子节点只会更小。
/// </summary>
public readonly record struct DiskUsageViewWindow(
    /// <summary>画布的物理像素宽高。含窗口缩放与屏幕缩放，由客户端算好。</summary>
    float PixelWidth,
    float PixelHeight,
    /// <summary>滚轮缩放倍数。1 表示整张图刚好铺满画布。</summary>
    float Scale,
    /// <summary>当前看得见的那块单位空间矩形。</summary>
    float MinX,
    float MinY,
    float MaxX,
    float MaxY)
{
    /// <summary>方格在屏幕上至少要占这么多物理像素才值得单独发。</summary>
    public const float MinimumTilePixelArea = 8f;

    /// <summary>
    /// 一次布局最多这么多方格。
    ///
    /// 这是**纯粹的资源上限**，不该在正常使用中碰到 —— 正常的限制器是上面那个像素门槛。
    /// 屏幕像素多本来就该看到更多方格，不能反过来用一个固定数量去限制它。
    /// 实测整块 C 盘缩到最小：1600×1000 出 33,390 个，4K 出 99,964 个，
    /// 所以这个数留了很大余量，只用来兜住"树坏掉了"这种情况。
    /// </summary>
    public const int MaximumTileCount = 400_000;

    /// <summary>客户端什么都没说时的口径：整张图、不缩放、按一块 1920×1080 的画布算。</summary>
    public static DiskUsageViewWindow Full { get; } =
        new(1920f, 1080f, 1f, 0f, 0f, 1f, 1f);

    /// <summary>
    /// 把客户端传来的值收拢成一个一定能用的视图。
    /// 越界、缺失、颠倒的值在这里一次性处理掉，布局算法里不再判断。
    /// </summary>
    public static DiskUsageViewWindow Normalize(
        float? pixelWidth,
        float? pixelHeight,
        float? scale,
        float? minX,
        float? minY,
        float? maxX,
        float? maxY)
    {
        var width = Clamp(pixelWidth, Full.PixelWidth, 64f, 32_768f);
        var height = Clamp(pixelHeight, Full.PixelHeight, 64f, 32_768f);
        // 缩放上限对应"整张图放大到一百万倍"，再深也没有更多节点可看了。
        var zoom = Clamp(scale, 1f, 1f, 1_000_000f);

        var left = Clamp(minX, 0f, 0f, 1f);
        var top = Clamp(minY, 0f, 0f, 1f);
        var right = Clamp(maxX, 1f, 0f, 1f);
        var bottom = Clamp(maxY, 1f, 0f, 1f);
        if (right <= left)
        {
            (left, right) = (0f, 1f);
        }
        if (bottom <= top)
        {
            (top, bottom) = (0f, 1f);
        }
        return new DiskUsageViewWindow(width, height, zoom, left, top, right, bottom);
    }

    /// <summary>这个单位空间矩形在屏幕上占多少物理像素。</summary>
    public float PixelAreaOf(float width, float height)
        => width * PixelWidth * Scale * height * PixelHeight * Scale;

    /// <summary>这个矩形和看得见的那块有没有交集。完全在外面的可以整棵跳过。</summary>
    public bool Intersects(float x, float y, float width, float height)
        => x + width >= MinX && x <= MaxX && y + height >= MinY && y <= MaxY;

    private static float Clamp(float? value, float fallback, float low, float high)
        => value is { } given && float.IsFinite(given)
            ? Math.Clamp(given, low, high)
            : fallback;
}

/// <summary>
/// 方格图布局（squarified treemap）。
///
/// 一句话：把一个矩形按子节点的字节数切开，每个子节点拿到的面积正比于它的字节数，
/// 并且尽量让切出来的方格接近正方形 —— 长条形的方格既难看也难点。
///
/// 输出在单位空间里。矩形本身不随缩放变化，所以前端在一份布局之内，
/// 缩放和平移全是本地的视口变换，不用回后端重算。
///
/// 发哪些方格则**取决于当前视图**（见 <see cref="DiskUsageViewWindow"/>）：
/// 视野外的整棵子树跳过，在屏幕上不足一个像素门槛的不发也不往下钻。
/// 所以放大之后要再要一次布局，那时原先太小的方格就够大了，会被发下来。
/// 两种情况都计入 <see cref="DiskUsageLayout.OmittedCount"/>。
/// </summary>
public static class DiskUsageTreemapLayout
{
    public static DiskUsageLayout Create(
        DiskUsageTree tree,
        int rootNodeId,
        DiskUsageViewWindow? view = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var window = view ?? DiskUsageViewWindow.Full;
        var maximumTileCount = DiskUsageViewWindow.MaximumTileCount;
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
                // 看不见的：整棵子树都不用管，子方格一定在父方格里面。
                if (!window.Intersects(placed.X, placed.Y, placed.Width, placed.Height))
                {
                    omitted++;
                    continue;
                }
                // 小到看不出来的：也不往下钻，子节点只会更小。
                if (window.PixelAreaOf(placed.Width, placed.Height)
                        < DiskUsageViewWindow.MinimumTilePixelArea
                    || tiles.Count >= maximumTileCount)
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
            //
            // 一行里最差的长宽比只由最大和最小的那一个决定（其余都夹在中间），
            // 而 children 是按大小降序的，所以最大的就是行首、最小的就是刚加进来这个。
            // 两个值顺着记就行，不用每加一个成员再把整行扫一遍 ——
            // 那样一个有几万个子项的目录（WinSxS 之类）要平方级的时间。
            var rowCount = 0;
            double rowSum = 0;
            double rowLargest = 0;
            var bestRatio = double.MaxValue;
            while (index + rowCount < children.Length)
            {
                var candidate = Math.Max(0, tree.SizeOf(children[index + rowCount]));
                var candidateSum = rowSum + candidate;
                if (candidateSum <= 0)
                {
                    rowCount++;
                    continue;
                }
                var largest = rowCount == 0 ? candidate : rowLargest;
                var ratio = WorstAspectRatio(
                    largest,
                    candidate,
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
                rowLargest = largest;
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

    /// <summary>
    /// 这一行里最差的那个方格的长宽比。越接近 1 越好。
    ///
    /// 只看行里最大和最小的那两个：行内每个方格的厚度相同，宽度正比于自己的大小，
    /// 所以最扁的一定是最大的那个，最细的一定是最小的那个，中间的都比它们好。
    /// </summary>
    private static double WorstAspectRatio(
        double largest,
        double smallest,
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
        if (largest > 0)
        {
            var extent = largest / rowSum * shortSide;
            if (extent > 0)
            {
                worst = Math.Max(worst, Math.Max(thickness / extent, extent / thickness));
            }
        }
        if (smallest > 0)
        {
            var extent = smallest / rowSum * shortSide;
            if (extent > 0)
            {
                worst = Math.Max(worst, Math.Max(thickness / extent, extent / thickness));
            }
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
