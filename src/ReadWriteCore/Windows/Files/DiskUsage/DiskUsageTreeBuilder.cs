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
    private DiskUsageObjectIdentity?[] identities = new DiskUsageObjectIdentity?[Math.Max(16, capacityHint)];
    private bool[] logicalSizeKnown = new bool[Math.Max(16, capacityHint)];
    private bool[] allocationKnown = new bool[Math.Max(16, capacityHint)];
    private bool[] countKnown = new bool[Math.Max(16, capacityHint)];
    // One construction-only alias index over this same tree. No per-directory sets or retained second tree.
    private readonly Dictionary<DiskUsageObjectIdentity, int> aliasHeads = [];
    private int[] nextAliases = new int[Math.Max(16, capacityHint)];
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
        long allocatedBytes,
        DiskUsageObjectIdentity? identity = null,
        bool logicalSizeKnown = true,
        bool allocationKnown = true)
    {
        if (parent < -1 || parent >= Count) throw new ArgumentOutOfRangeException(nameof(parent));
        if (Count >= MaximumNodeCount)
        {
            Truncated = true;
            if (parent >= 0) SetIncomplete(parent);
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
        identities[node] = identity;
        this.logicalSizeKnown[node] = logicalSizeKnown && sizeBytes >= 0;
        this.allocationKnown[node] = allocationKnown && allocatedBytes >= 0;
        countKnown[node] = true;
        nextAliases[node] = -1;
        fileCounts[node] = isDirectory ? 0 : 1;
        nameOffsets[node] = nameLength;
        nameLengths[node] = name.Length;
        name.CopyTo(names.AsSpan(nameLength));
        nameLength += name.Length;

        if (!isDirectory && identity is { } objectIdentity)
            DeduplicateAllocation(node, objectIdentity);

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

    /// <summary>A missing entry/subtree makes all three directory aggregates incomplete.
    /// The existing reverse aggregation propagates this state to ancestors.</summary>
    public void SetIncomplete(int node)
    {
        if ((uint)node >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(node));
        logicalSizeKnown[node] = false;
        allocationKnown[node] = false;
        countKnown[node] = false;
    }

    private void DeduplicateAllocation(int node, DiskUsageObjectIdentity identity)
    {
        if (aliasHeads.TryGetValue(identity, out var previous))
        {
            var deepestCommonAncestor = -1;
            var conflictingAllocation = false;
            for (var alias = previous; alias >= 0; alias = nextAliases[alias])
            {
                var common = CommonAncestor(node, alias);
                if (common < 0) continue; // Separate roots keep independent allocation totals.
                // Every candidate is on the new node's parent chain; larger preorder indices are deeper.
                deepestCommonAncestor = Math.Max(deepestCommonAncestor, common);
                conflictingAllocation |= allocated[alias] != allocated[node] || !allocationKnown[alias];
            }
            if (deepestCommonAncestor >= 0)
            {
                try { allocated[deepestCommonAncestor] = checked(allocated[deepestCommonAncestor] - allocated[node]); }
                catch (OverflowException) { allocationKnown[deepestCommonAncestor] = false; }
                if (conflictingAllocation || !allocationKnown[node]) allocationKnown[deepestCommonAncestor] = false;
            }
            nextAliases[node] = previous;
        }
        aliasHeads[identity] = node;
    }

    private int CommonAncestor(int left, int right)
    {
        // Parent indices strictly decrease. Walking the larger index cannot skip a shared ancestor.
        while (left >= 0 && right >= 0 && left != right)
            if (left > right) left = parents[left]; else right = parents[right];
        return left == right ? left : -1;
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
            logicalSizeKnown[parent] &= logicalSizeKnown[node];
            allocationKnown[parent] &= allocationKnown[node];
            countKnown[parent] &= countKnown[node];
            try { sizes[parent] = checked(sizes[parent] + sizes[node]); }
            catch (OverflowException) { logicalSizeKnown[parent] = false; }
            try { allocated[parent] = checked(allocated[parent] + allocated[node]); }
            catch (OverflowException) { allocationKnown[parent] = false; }
            try { fileCounts[parent] = checked(fileCounts[parent] + fileCounts[node]); }
            catch (OverflowException) { countKnown[parent] = false; }
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
            roots.ToArray(),
            identities,
            logicalSizeKnown,
            allocationKnown,
            countKnown);
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
        Array.Resize(ref identities, capacity);
        Array.Resize(ref logicalSizeKnown, capacity);
        Array.Resize(ref allocationKnown, capacity);
        Array.Resize(ref countKnown, capacity);
        Array.Resize(ref nextAliases, capacity);
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
