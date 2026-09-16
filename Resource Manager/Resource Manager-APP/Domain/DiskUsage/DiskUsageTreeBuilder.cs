namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>
/// 按**先序**往里加节点，最后一次性合成 <see cref="DiskUsageTree"/>。
///
/// 调用方必须保证：父节点先于它的任何子节点加入。扫描器用显式栈做深度优先遍历，
/// 天然满足这一点；汇总因此只要反向扫一遍数组。
/// </summary>
public sealed class DiskUsageTreeBuilder(int capacityHint = 1024)
{
    private const int MaximumNodeCount = 8_000_000;

    private int[] parents = new int[Math.Max(16, capacityHint)];
    private int[] firstChildren = new int[Math.Max(16, capacityHint)];
    private int[] lastChildren = new int[Math.Max(16, capacityHint)];
    private int[] nextSiblings = new int[Math.Max(16, capacityHint)];
    private long[] sizes = new long[Math.Max(16, capacityHint)];
    private long[] allocated = new long[Math.Max(16, capacityHint)];
    private int[] nameOffsets = new int[Math.Max(16, capacityHint)];
    private int[] nameLengths = new int[Math.Max(16, capacityHint)];
    private int[] fileCounts = new int[Math.Max(16, capacityHint)];
    private bool[] directoryFlags = new bool[Math.Max(16, capacityHint)];
    private char[] names = new char[Math.Max(256, capacityHint * 16)];
    private int nameLength;
    private readonly List<int> roots = [];

    public int Count { get; private set; }

    /// <summary>到达条目上限后为 true。结果里要如实说明这次扫描被截断了。</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// 加一个节点。<paramref name="parent"/> 传 -1 表示这是一个根。
    /// 返回新节点的序号；到达上限时返回 -1，调用方应当停止继续加。
    /// </summary>
    public int Add(
        int parent,
        ReadOnlySpan<char> name,
        bool isDirectory,
        long sizeBytes,
        long allocatedBytes)
    {
        if (Count >= MaximumNodeCount)
        {
            Truncated = true;
            return -1;
        }

        EnsureNodeCapacity(Count + 1);
        EnsureNameCapacity(nameLength + name.Length);

        var node = Count++;
        parents[node] = parent;
        firstChildren[node] = -1;
        lastChildren[node] = -1;
        nextSiblings[node] = -1;
        sizes[node] = sizeBytes;
        allocated[node] = allocatedBytes;
        directoryFlags[node] = isDirectory;
        fileCounts[node] = isDirectory ? 0 : 1;
        nameOffsets[node] = nameLength;
        nameLengths[node] = name.Length;
        name.CopyTo(names.AsSpan(nameLength));
        nameLength += name.Length;

        if (parent < 0)
        {
            roots.Add(node);
            return node;
        }

        // 兄弟链按加入顺序接在尾部，不用每次从头走一遍。
        if (lastChildren[parent] < 0)
        {
            firstChildren[parent] = node;
        }
        else
        {
            nextSiblings[lastChildren[parent]] = node;
        }
        lastChildren[parent] = node;
        return node;
    }

    /// <summary>
    /// 汇总并合成。利用先序性质反向扫一遍：走到某个节点时它的子树已经算完，
    /// 直接累加进父节点即可。不递归，也不需要额外的栈。
    /// </summary>
    public DiskUsageTree Build()
    {
        for (var node = Count - 1; node >= 0; node--)
        {
            var parent = parents[node];
            if (parent < 0)
            {
                continue;
            }
            sizes[parent] += sizes[node];
            allocated[parent] += allocated[node];
            fileCounts[parent] += fileCounts[node];
        }

        return new DiskUsageTree(
            Count,
            parents,
            firstChildren,
            nextSiblings,
            sizes,
            allocated,
            nameOffsets,
            nameLengths,
            fileCounts,
            directoryFlags,
            names,
            roots.ToArray());
    }

    private void EnsureNodeCapacity(int required)
    {
        if (required <= parents.Length)
        {
            return;
        }
        var capacity = Math.Min(MaximumNodeCount, Math.Max(required, parents.Length * 2));
        Array.Resize(ref parents, capacity);
        Array.Resize(ref firstChildren, capacity);
        Array.Resize(ref lastChildren, capacity);
        Array.Resize(ref nextSiblings, capacity);
        Array.Resize(ref sizes, capacity);
        Array.Resize(ref allocated, capacity);
        Array.Resize(ref nameOffsets, capacity);
        Array.Resize(ref nameLengths, capacity);
        Array.Resize(ref fileCounts, capacity);
        Array.Resize(ref directoryFlags, capacity);
    }

    private void EnsureNameCapacity(int required)
    {
        if (required <= names.Length)
        {
            return;
        }
        Array.Resize(ref names, Math.Max(required, names.Length * 2));
    }
}
