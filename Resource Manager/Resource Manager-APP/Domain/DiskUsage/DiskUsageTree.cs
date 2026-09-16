namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>
/// 一次扫描的结果树。
///
/// 只读、一次性、进程内：换一次扫描整棵丢掉重建，所以用并列数组存，
/// 不做行存也不做增量更新。节点按**先序**创建 —— 父节点的序号一定小于它的子节点，
/// 汇总时反向遍历一次就能把子节点累加进父节点，不需要递归。
/// </summary>
public sealed class DiskUsageTree
{
    private readonly int[] parents;
    private readonly int[] firstChildren;
    private readonly int[] nextSiblings;
    private readonly long[] sizes;
    private readonly long[] allocated;
    private readonly int[] nameOffsets;
    private readonly int[] nameLengths;
    private readonly int[] fileCounts;
    private readonly bool[] directoryFlags;
    private readonly char[] names;

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
        IReadOnlyList<int> roots)
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
        this.names = names;
        Roots = roots;
    }

    /// <summary>节点总数。</summary>
    public int Count { get; }

    /// <summary>根节点序号。全局扫描会有多个根，一个卷一个。</summary>
    public IReadOnlyList<int> Roots { get; }

    /// <summary>
    /// 这棵树占多少字节。
    ///
    /// 它要登记进内存账本，账本按大小排队回收，所以这个数必须是实际占用，
    /// 不能拿节点数估。各个并列数组的长度加起来就是准确值 ——
    /// 名字缓冲往往比索引列还大，漏掉它会少算将近一半。
    /// 数组对象头这类常数开销忽略不计。
    /// </summary>
    public long ApproximateByteSize =>
        ((long)parents.Length * sizeof(int))
        + ((long)firstChildren.Length * sizeof(int))
        + ((long)nextSiblings.Length * sizeof(int))
        + ((long)sizes.Length * sizeof(long))
        + ((long)allocated.Length * sizeof(long))
        + ((long)nameOffsets.Length * sizeof(int))
        + ((long)nameLengths.Length * sizeof(int))
        + ((long)fileCounts.Length * sizeof(int))
        + directoryFlags.Length
        + ((long)names.Length * sizeof(char));

    public int ParentOf(int node) => parents[node];

    public int FirstChildOf(int node) => firstChildren[node];

    public int NextSiblingOf(int node) => nextSiblings[node];

    public long SizeOf(int node) => sizes[node];

    public long AllocatedOf(int node) => allocated[node];

    public int FileCountOf(int node) => fileCounts[node];

    public bool IsDirectory(int node) => directoryFlags[node];

    public ReadOnlySpan<char> NameOf(int node)
        => names.AsSpan(nameOffsets[node], nameLengths[node]);

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
            current = parents[current];
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
        for (var child = firstChildren[node]; child >= 0; child = nextSiblings[child])
        {
            count++;
        }
        if (count == 0)
        {
            return [];
        }

        var children = new int[count];
        var index = 0;
        for (var child = firstChildren[node]; child >= 0; child = nextSiblings[child])
        {
            children[index++] = child;
        }
        Array.Sort(children, (left, right) => sizes[right].CompareTo(sizes[left]));
        return children;
    }
}
