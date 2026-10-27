namespace ResourceManager.Shared.BusinessBlocks.State;

/// <summary>Per-process UID directory. Claiming creates the sole write capability; readers only load publications.</summary>
public sealed class StateAuthority
{
    private readonly StateItemDeclaration?[] declarations;
    private readonly Entry?[] single;
    private readonly Entry?[][] perBlock;
    private readonly int[] blocks;
    public StateAuthority(IEnumerable<StateItemDeclaration> items, IEnumerable<int> blockUids)
    {
        var source = items.ToArray();
        blocks = blockUids.ToArray();
        if (blocks.Any(id => id < 1) || blocks.Distinct().Count() != blocks.Length)
            throw new ArgumentException("Block UIDs must be unique positive integers.", nameof(blockUids));
        var blockSet = blocks.ToHashSet();
        var length = source.Select(item => item.Uid).DefaultIfEmpty().Max() + 1;
        declarations = new StateItemDeclaration?[length]; single = new Entry?[length]; perBlock = new Entry?[length][];
        foreach (var item in source)
        {
            if (item.Uid < 1 || declarations[item.Uid] is not null) throw new ArgumentException($"Invalid or duplicate item UID: {item.Uid}.");
            if (item.Owner.Kind == StateItemOwnerKind.Block && (item.Owner.BlockUid is not { } owner || !blockSet.Contains(owner)))
                throw new ArgumentException($"Unknown owner for item {item.Uid}.");
            declarations[item.Uid] = item;
            if (item.PerBlock)
            {
                var entries = new Entry?[blocks.DefaultIfEmpty().Max() + 1];
                foreach (var block in blocks) entries[block] = new(new(item.Uid, block), item);
                perBlock[item.Uid] = entries;
            }
            else single[item.Uid] = new(new(item.Uid), item);
        }
    }

    public StateWriter<T> ClaimWrite<T>(StateItemKey key)
    {
        var entry = Claim<T>(key, StateItemStorage.Own);
        return new(this, entry);
    }
    /// <summary>Claim and initially bind a reference. It reads NotPublished until its owner's first publication.</summary>
    public StateCell<T> ClaimCell<T>(StateItemKey key)
    {
        var entry = Claim<T>(key, StateItemStorage.Reference);
        return new(this, entry);
    }
    private Entry Claim<T>(StateItemKey key, StateItemStorage storage)
    {
        var entry = Get(key); CheckType<T>(entry);
        if (entry.Declaration.Storage != storage) throw new InvalidOperationException($"Item {key} uses {entry.Declaration.Storage} storage.");
        if (Interlocked.CompareExchange(ref entry.Claimed, 1, 0) != 0) throw new InvalidOperationException($"Item {key} already has an owner.");
        return entry;
    }
    public StateRead<T> Read<T>(StateItemKey key)
    {
        var entry = Get(key); CheckType<T>(entry);
        return Volatile.Read(ref entry.Current) is StatePublication<T> publication ? publication.Read : StateRead<T>.NoValue(NoValueReason.NotPublished);
    }
    internal IStatePublication? ReadPublication(StateItemKey key) => Volatile.Read(ref Get(key).Current);
    internal void ValidateKey(StateItemKey key) => _ = Get(key);
    internal void ValidateType<T>(StateItemKey key) => CheckType<T>(Get(key));
    private static void CheckType<T>(Entry entry)
    {
        if (entry.Declaration.ValueType != typeof(T)) throw new InvalidOperationException($"Item {entry.Key} has type {entry.Declaration.ValueType}, not {typeof(T)}.");
    }
    internal IDisposable Subscribe(StateItemKey key, Func<BusinessBlockReason, Task> reader)
    {
        var entry = Get(key);
        lock (entry.Sync) Volatile.Write(ref entry.Readers, [.. entry.Readers, reader]);
        return new Subscription(entry, reader);
    }
    internal Task Notify(Entry entry, BusinessBlockReason reason)
    {
        var readers = Volatile.Read(ref entry.Readers);
        if (readers.Length == 0) return Task.CompletedTask;
        var tasks = new Task[readers.Length];
        for (var index = 0; index < readers.Length; index++) tasks[index] = readers[index](reason);
        return Task.WhenAll(tasks);
    }
    public IReadOnlyList<BusinessBlockItem> ReadAll(Func<int, string?> nameLookup)
    {
        var result = new List<BusinessBlockItem>();
        foreach (var item in declarations)
        {
            if (item is null) continue;
            if (item.PerBlock) { foreach (var block in blocks) result.Add(Snapshot(new(item.Uid, block), nameLookup)); }
            else result.Add(Snapshot(item.Uid, nameLookup));
        }
        return result;
    }
    internal BusinessBlockItem Snapshot(StateItemKey key, Func<int, string?> nameLookup)
    {
        var entry = Get(key);
        return Snapshot(entry, Volatile.Read(ref entry.Current), nameLookup);
    }
    internal BusinessBlockItem Snapshot(StateItemKey key, IStatePublication? publication, Func<int, string?> nameLookup)
        => Snapshot(Get(key), publication, nameLookup);
    private static BusinessBlockItem Snapshot(Entry entry, IStatePublication? publication, Func<int, string?> nameLookup)
    {
        var cached = Volatile.Read(ref entry.Snapshot);
        if (cached is not null && ReferenceEquals(cached.Publication, publication) && ReferenceEquals(cached.NameLookup, nameLookup))
            return cached.Row;
        var key = entry.Key;
        var row = (publication?.Snapshot(key) ?? new(key.ItemUid, key.BlockUid, null, NoValueReason.NotPublished))
            with { Name = nameLookup(key.ItemUid), BlockName = key.BlockUid is { } block ? nameLookup(block) : null };
        // One bounded diagnostic row per entry; concurrent older rounds may replace this cache,
        // but always return their own captured publication rather than re-reading the current value.
        Volatile.Write(ref entry.Snapshot, new(publication, nameLookup, row));
        return row;
    }
    private Entry Get(StateItemKey key)
    {
        if (key.ItemUid < 1 || key.ItemUid >= declarations.Length || declarations[key.ItemUid] is not { } declaration)
            throw new KeyNotFoundException($"Undeclared state item: {key}.");
        if (!declaration.PerBlock && key.BlockUid is null) return single[key.ItemUid]!;
        if (declaration.PerBlock && key.BlockUid is { } block && block > 0 && block < perBlock[key.ItemUid].Length
            && perBlock[key.ItemUid][block] is { } entry) return entry;
        throw new KeyNotFoundException($"Invalid block key for state item: {key}.");
    }
    internal sealed class Entry(StateItemKey key, StateItemDeclaration declaration)
    {
        internal readonly object Sync = new();
        internal readonly StateItemKey Key = key;
        internal readonly StateItemDeclaration Declaration = declaration;
        internal IStatePublication? Current;
        internal SnapshotRow? Snapshot;
        internal int Claimed;
        internal Func<BusinessBlockReason, Task>[] Readers = [];
    }
    internal sealed record SnapshotRow(IStatePublication? Publication, Func<int, string?> NameLookup, BusinessBlockItem Row);
    private sealed class Subscription(Entry entry, Func<BusinessBlockReason, Task> reader) : IDisposable
    {
        public void Dispose() { lock (entry.Sync) Volatile.Write(ref entry.Readers, entry.Readers.Where(value => value != reader).ToArray()); }
    }
}
