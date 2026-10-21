namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>
/// 一次扫描的结果树。
///
/// 扫描临时数据使用并列数组；发布数据从操作租约保护的区域读取同样的列。
/// 不做行存也不做增量更新。节点按**先序**创建 —— 父节点的序号一定小于它的子节点，
/// 汇总时反向遍历一次就能把子节点累加进父节点，不需要递归。
/// </summary>
public sealed class DiskUsageTree
{
    private readonly DiskUsageTreeRegionLayout? layout;
    private readonly IDiskUsageTreeRegion? region;
    private readonly int[] parents;
    private readonly int[] firstChildren;
    private readonly int[] nextSiblings;
    private readonly long[] sizes;
    private readonly long[] allocated;
    private readonly int[] nameOffsets;
    private readonly int[] nameLengths;
    private readonly int[] fileCounts;
    private readonly bool[] directoryFlags;
    private readonly DiskUsageObjectIdentity?[] identities;
    private readonly bool[] logicalSizeKnown;
    private readonly bool[] allocationKnown;
    private readonly bool[] countKnown;
    private readonly char[] names;

    internal DiskUsageTree(DiskUsageTreeRegionLayout layout, IDiskUsageTreeRegion region)
    {
        this.layout = layout; this.region = region;
        Count = layout.Count; Roots = layout.Roots(region);
        // Published trees retain only region access metadata, never builder arrays.
        parents = firstChildren = nextSiblings = nameOffsets = nameLengths = fileCounts = null!;
        sizes = allocated = null!; directoryFlags = logicalSizeKnown = allocationKnown = countKnown = null!;
        identities = null!; names = null!;
    }

    internal DiskUsageTree(
        int count,
        int[] parents,
        int[] firstChildren,
        int[] nextSiblings,
        long[] sizes,
        long[] allocated,
        int[] nameOffsets,
        int[] nameLengths,
        int[] fileCounts,
        bool[] directoryFlags,
        char[] names,
        IReadOnlyList<int> roots,
        DiskUsageObjectIdentity?[] identities,
        bool[] logicalSizeKnown,
        bool[] allocationKnown,
        bool[] countKnown)
    {
        Count = count;
        this.parents = parents;
        this.firstChildren = firstChildren;
        this.nextSiblings = nextSiblings;
        this.sizes = sizes;
        this.allocated = allocated;
        this.nameOffsets = nameOffsets;
        this.nameLengths = nameLengths;
        this.fileCounts = fileCounts;
        this.directoryFlags = directoryFlags;
        this.identities = identities;
        this.logicalSizeKnown = logicalSizeKnown;
        this.allocationKnown = allocationKnown;
        this.countKnown = countKnown;
        this.names = names;
        Roots = roots;
    }

    /// <summary>节点总数。</summary>
    public int Count { get; }

    /// <summary>根节点序号。全局扫描会有多个根，一个卷一个。</summary>
    public IReadOnlyList<int> Roots { get; }

    /// <summary>
    /// Builder-array capacity estimate, or exact payload layout length for a
    /// region view. This is not proof of allocator ownership.
    /// </summary>
    public long ApproximateByteSize => layout is not null ? checked((long)layout.Length) :
        ((long)parents.Length * sizeof(int))
        + ((long)firstChildren.Length * sizeof(int))
        + ((long)nextSiblings.Length * sizeof(int))
        + ((long)sizes.Length * sizeof(long))
        + ((long)allocated.Length * sizeof(long))
        + ((long)nameOffsets.Length * sizeof(int))
        + ((long)nameLengths.Length * sizeof(int))
        + ((long)fileCounts.Length * sizeof(int))
        + directoryFlags.Length
        + ((long)identities.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<DiskUsageObjectIdentity?>())
        + logicalSizeKnown.Length
        + allocationKnown.Length
        + countKnown.Length
        + ((long)names.Length * sizeof(char));

    public int ParentOf(int node) => layout is not null ? layout.ParentOf(region!, node) : parents[node];

    public int FirstChildOf(int node) => layout is not null ? layout.FirstChildOf(region!, node) : firstChildren[node];

    public int NextSiblingOf(int node) => layout is not null ? layout.NextSiblingOf(region!, node) : nextSiblings[node];

    public long SizeOf(int node) => layout is not null ? layout.SizeOf(region!, node) : sizes[node];

    public long AllocatedOf(int node) => layout is not null ? layout.AllocatedOf(region!, node) : allocated[node];

    public DiskUsageObjectIdentity? IdentityOf(int node) => layout is not null ? layout.IdentityOf(region!, node) : identities[node];

    public bool LogicalSizeKnownOf(int node) => layout is not null ? layout.FlagOf(region!, node, 2) : logicalSizeKnown[node];

    public bool AllocationKnownOf(int node) => layout is not null ? layout.FlagOf(region!, node, 4) : allocationKnown[node];

    /// <summary>Completeness of subtree entry counts, including directory counts computed by a caller.</summary>
    public bool CountKnownOf(int node) => layout is not null ? layout.FlagOf(region!, node, 8) : countKnown[node];

    public int FileCountOf(int node) => layout is not null ? layout.FileCountOf(region!, node) : fileCounts[node];

    public bool IsDirectory(int node) => layout is not null ? layout.FlagOf(region!, node, 1) : directoryFlags[node];

    public ReadOnlySpan<char> NameOf(int node)
        => layout is not null ? layout.NameOf(region!, node) : names.AsSpan(nameOffsets[node], nameLengths[node]);

    /// <summary>
    /// 从根到这个节点的完整路径。沿父链往上走到根再反过来拼，同样不递归。
    /// </summary>
    public string PathOf(int node)
    {
        var chain = new List<int>(16);
        var current = node;
        var guard = 0;
        while (current >= 0 && guard++ <= Count)
        {
            chain.Add(current);
            current = ParentOf(current);
        }

        var builder = new System.Text.StringBuilder(96);
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var segment = NameOf(chain[index]);
            if (builder.Length > 0
                && builder[^1] != System.IO.Path.DirectorySeparatorChar)
            {
                builder.Append(System.IO.Path.DirectorySeparatorChar);
            }
            builder.Append(segment);
        }
        return builder.ToString();
    }

    /// <summary>某个节点的直接子节点，按字节数从大到小。布局要的就是这个顺序。</summary>
    public int[] ChildrenBySizeDescending(int node)
    {
        var count = 0;
        for (var child = FirstChildOf(node); child >= 0; child = NextSiblingOf(child))
        {
            count++;
        }
        if (count == 0)
        {
            return [];
        }

        var children = new int[count];
        var index = 0;
        for (var child = FirstChildOf(node); child >= 0; child = NextSiblingOf(child))
        {
            children[index++] = child;
        }
        Array.Sort(children, (left, right) => SizeOf(right).CompareTo(SizeOf(left)));
        return children;
    }
}
