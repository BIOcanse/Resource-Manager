using System.Runtime.CompilerServices;

namespace ResourceManager.Shared.BusinessBlocks.State;

/// <summary>Per-process UID directory. Claiming creates the sole write capability; readers only load publications.</summary>
public sealed class StateAuthority
{
    private readonly StateItemDeclaration?[] declarations;
    private readonly Entry?[] single;
    private readonly Entry?[][] perBlock;
    private readonly int[] blocks;
    private readonly NativeControl? runtime;

    public StateAuthority(IEnumerable<StateItemDeclaration> items, IEnumerable<int> blockUids)
        : this(items, blockUids, null, 0) { }

    internal StateAuthority(IEnumerable<StateItemDeclaration> items, IEnumerable<int> blockUids, NativeControl runtime)
        : this(items, blockUids, runtime ?? throw new ArgumentNullException(nameof(runtime)), 0) { }

    private StateAuthority(IEnumerable<StateItemDeclaration> items, IEnumerable<int> blockUids, NativeControl? runtime, byte _)
    {
        var source = items.ToArray();
        blocks = blockUids.ToArray();
        this.runtime = runtime;
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

    internal NativeControl? Runtime => runtime;

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
        if (runtime is { } native) entry.NativeClaim = native.Claim(key, storage);
        else if (Interlocked.CompareExchange(ref entry.Claimed, 1, 0) != 0)
            throw new InvalidOperationException($"Item {key} already has an owner.");
        return entry;
    }
    public StateRead<T> Read<T>(StateItemKey key)
    {
        var entry = Get(key); CheckType<T>(entry);
        if (runtime is { } native && typeof(T) == typeof(bool))
            return FromBoolean<T>(native.ReadBool(key));
        return Volatile.Read(ref entry.Current) is StatePublication<T> publication ? publication.Read : StateRead<T>.NoValue(NoValueReason.NotPublished);
    }
    internal IStatePublication? ReadPublication(StateItemKey key)
    {
        var entry = Get(key);
        return ReadPublication(entry);
    }
    internal void ValidateKey(StateItemKey key) => _ = Get(key);
    internal void ValidateType<T>(StateItemKey key) => CheckType<T>(Get(key));
    private static void CheckType<T>(Entry entry)
    {
        if (entry.Declaration.ValueType != typeof(T)) throw new InvalidOperationException($"Item {entry.Key} has type {entry.Declaration.ValueType}, not {typeof(T)}.");
    }
    internal IDisposable Subscribe(StateItemKey key, Func<BusinessBlockReason, Task> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var entry = Get(key);
        if (runtime is not null)
            throw new InvalidOperationException("Unnamed state subscriptions are only available to the managed scheduler.");
        lock (entry.Sync) Volatile.Write(ref entry.Readers, [.. entry.Readers, reader]);
        return new Subscription(entry, reader);
    }
    /// <summary>Dynamic readers use a named holder on the same publication path as declared readers.</summary>
    public IDisposable Subscribe(StateItemKey key, string holder, Func<BusinessBlockReason, Task> reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        ArgumentNullException.ThrowIfNull(reader);
        var entry = Get(key);
        if (runtime is { } native) return native.Subscribe(key, holder, reader);
        Func<BusinessBlockReason, Task> dispatch = reason => reader(reason);
        lock (entry.Sync)
        {
            entry.Holders ??= new(StringComparer.Ordinal);
            if (!entry.Holders.Add(holder)) throw new InvalidOperationException("A state subscription already uses this holder.");
            Volatile.Write(ref entry.Readers, [.. entry.Readers, dispatch]);
        }
        return new Subscription(entry, dispatch, holder);
    }
    internal Task Notify(Entry entry, BusinessBlockReason reason)
    {
        if (runtime is not null)
            throw new InvalidOperationException("Native state publications must be dispatched by the native controller.");
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
        return Snapshot(entry, ReadPublication(entry), nameLookup);
    }
    internal BusinessBlockItem Snapshot(StateItemKey key, IStatePublication? publication, Func<int, string?> nameLookup)
        => Snapshot(Get(key), publication, nameLookup);
    private BusinessBlockItem Snapshot(Entry entry, IStatePublication? publication, Func<int, string?> nameLookup)
    {
        var cacheSnapshot = runtime is null || entry.Declaration.ValueType != typeof(bool);
        var cached = cacheSnapshot ? Volatile.Read(ref entry.Snapshot) : null;
        if (cached is not null && ReferenceEquals(cached.Publication, publication) && ReferenceEquals(cached.NameLookup, nameLookup))
            return cached.Row;
        var key = entry.Key;
        var row = (publication?.Snapshot(key) ?? new(key.ItemUid, key.BlockUid, null, NoValueReason.NotPublished))
            with { Name = nameLookup(key.ItemUid), BlockName = key.BlockUid is { } block ? nameLookup(block) : null };
        // Cached rows are bounded per entry; older rounds may replace them but return their own publication.
        if (cacheSnapshot) Volatile.Write(ref entry.Snapshot, new(publication, nameLookup, row));
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

    private IStatePublication? ReadPublication(Entry entry)
    {
        if (runtime is { } native && entry.Declaration.ValueType == typeof(bool))
            return new StatePublication<bool>(native.ReadBool(entry.Key));
        return Volatile.Read(ref entry.Current);
    }

    internal static StateRead<bool> AsBoolean<T>(StateRead<T> read)
    {
        if (typeof(T) != typeof(bool)) throw new InvalidOperationException("Only Boolean state can cross the native Boolean API.");
        return Unsafe.As<StateRead<T>, StateRead<bool>>(ref read);
    }

    internal static StateRead<T> FromBoolean<T>(StateRead<bool> read)
    {
        if (typeof(T) != typeof(bool)) throw new InvalidOperationException("Only Boolean state can cross the native Boolean API.");
        return Unsafe.As<StateRead<bool>, StateRead<T>>(ref read);
    }

    internal sealed class Entry(StateItemKey key, StateItemDeclaration declaration)
    {
        internal readonly object Sync = new();
        internal readonly StateItemKey Key = key;
        internal readonly StateItemDeclaration Declaration = declaration;
        internal IStatePublication? Current;
        internal SnapshotRow? Snapshot;
        internal int Claimed;
        internal nint NativeClaim;
        internal Func<BusinessBlockReason, Task>[] Readers = [];
        internal HashSet<string>? Holders;
    }
    internal sealed record SnapshotRow(IStatePublication? Publication, Func<int, string?> NameLookup, BusinessBlockItem Row);
    private sealed class Subscription(Entry entry, Func<BusinessBlockReason, Task> reader, string? holder = null) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (entry.Sync)
            {
                Volatile.Write(ref entry.Readers, entry.Readers.Where(value => value != reader).ToArray());
                if (holder is not null)
                {
                    entry.Holders!.Remove(holder);
                    if (entry.Holders.Count == 0) entry.Holders = null;
                }
            }
        }
    }
}
