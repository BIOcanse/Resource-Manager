using System.Buffers;
using System.Collections;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Metrics.Zig.Discovery;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    // Missing native grouping is an explicit integration dependency. Never turn
    // a large owner round into quadratic managed scans or a managed work bank.
    public const int UngroupedRowLimit = 256;
    private static void RequireSmall(int count)
    {
        if (count > UngroupedRowLimit)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = MetricOwnerFamily.Binding, Required = (ulong)count, Limit = UngroupedRowLimit });
    }

    // A source callback owns this token; retained SDK request wrappers lose all
    // query/facts references at Dispose, before native prepare or the next source.
    private sealed class SdkRequestScope(MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts) : IDisposable
    {
        private MetricOwnerRawRound? raw = raw;
        private MetricDiscoveryRoundFacts? facts = facts;
        internal bool AlgebraActive;
        public MetricOwnerRawRound Raw { get { Open(); return raw!; } }
        public MetricDiscoveryRoundFacts Facts { get { Open(); return facts!; } }
        public void Open()
        {
            ObjectDisposedException.ThrowIf(raw is null, this);
            _ = raw!.Count; _ = facts!.SourceCount;
        }
        public void Dispose() { raw = null; facts = null; }
    }

    private sealed class SourceRows : IEnumerable<int>, IDisposable
    {
        internal readonly SdkRequestScope Borrow;
        private readonly MetricOwnerSdkSampler owner;
        internal readonly ulong source;
        internal SourceRows? parent;
        internal Func<SourceRows, int, bool>? predicate;
        internal MetricFieldCode[]? onlyFields;
        internal int groupFirst, groupEnd, groupOrder, sourceFirst, sourceEnd, projectionCount = -1;
        internal SourceRows? projector;
        internal int projectionOrder;
        private readonly bool ownsBorrow;
        private int? count;
        public SourceRows(SdkRequestScope borrow, MetricOwnerSdkSampler owner, ulong source)
        { Borrow = borrow; this.owner = owner; this.source = source; SdkNative.Initialize(this); }
        private SourceRows(SourceRows parent, Func<SourceRows, int, bool> predicate)
        { Borrow = parent.Borrow; owner = parent.owner; source = parent.source; this.parent = parent; this.predicate = predicate; CopyRange(parent); }
        private SourceRows(SourceRows parent, MetricFieldCode[] fields)
        { Borrow = parent.Borrow; owner = parent.owner; source = parent.source; this.parent = parent; onlyFields = fields; CopyRange(parent); }
        private void CopyRange(SourceRows from)
        { groupFirst = from.groupFirst; groupEnd = from.groupEnd; groupOrder = from.groupOrder; sourceFirst = from.sourceFirst; sourceEnd = from.sourceEnd; projector = from.projector; }
        private SourceRows(SourceRows parent, int order)
        {
            Borrow = new(parent.Borrow.Raw, parent.Borrow.Facts); owner = parent.owner;
            source = parent.source; this.parent = parent; ownsBorrow = true; CopyRange(parent); projector = this; projectionOrder = order; SdkNative.Project(this);
        }
        public SourceRows BeginCall(int order = 1) => new(this, order);
        public void Dispose()
        {
            if (!ownsBorrow) return;
            Borrow.Dispose(); parent = null; predicate = null; onlyFields = null; projector = null; projectionCount = -1;
        }
        public int Count { get { Borrow.Open(); return count ??= this.Count(static _ => true); } }
        public MetricOwnerSdkScope Scope(int row) => owner.Scope(Borrow.Facts, row);
        public MetricFieldCode Field(int row) => (MetricFieldCode)Borrow.Facts.FieldCode(row);
        private bool Includes(int row)
        {
            if (Borrow.Raw.SourceId(row) != source) return false;
            SourceRows? filter = this;
            while (filter is not null)
            {
                if (filter.predicate is not null && !filter.predicate(filter, row)) return false;
                if (filter.onlyFields is not null && !filter.onlyFields.Contains(Field(row))) return false;
                filter = filter.parent;
            }
            return true;
        }
        public SourceRows Filter(Func<SourceRows, int, bool> filter) => new(this, filter);
        public SourceRows Fields(params MetricFieldCode[] fields) => new(this, fields);
        public SourceRows ForIndex(int index) { var view = Filter((r, row) => r.Scope(row).DisplayIndex == index); SdkNative.Narrow(view, index); return view; }
        public SourceRows ForKey(string key) { var view = Filter((r, row) => r.Scope(row).DeviceKey == key); SdkNative.Narrow(view, key); return view; }
        internal bool AllowsField(uint field)
        {
            SourceRows? filter = this;
            while (filter is not null)
            { if (filter.onlyFields is not null && !filter.onlyFields.Contains((MetricFieldCode)field)) return false; filter = filter.parent; }
            return true;
        }
        internal bool AllowsScope(int row)
        {
            SourceRows? filter = this;
            while (filter is not null)
            { if (filter.predicate is not null && !filter.predicate(filter, row)) return false; filter = filter.parent; }
            return true;
        }
        public IEnumerator<int> GetEnumerator()
        { Borrow.Open(); return Enumerate(); }
        private IEnumerator<int> Enumerate()
        {
            if (Borrow.Facts.HasSdkGrouping)
            {
                using var groups = SdkNative.Groups(this).GetEnumerator();
                while (groups.MoveNext())
                {
                    var (offset, end) = SdkNative.RowBounds(this, groups.Current);
                    while (offset < end) { Borrow.Open(); var groupedRow = SdkNative.RawAt(this, offset++); if (Includes(groupedRow)) yield return groupedRow; }
                }
                yield break;
            }
            Borrow.Open(); var row = 0;
            while (row < Borrow.Raw.Count)
            {
                Borrow.Open(); var current = row++;
                if (Includes(current)) yield return current;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // The following are SDK signature adapters, not reusable collection/cache
    // infrastructure. Their only backing is selected native rows and original
    // immutable SDK references. The bounded scan route is used only by the
    // ungrouped small fixture; grouped calls use native offsets/indices.
    private sealed class SdkList<T>(SdkRequestScope borrow, IEnumerable<T> values) : IReadOnlyList<T>
    {
        private int? count;
        public int Count { get { borrow.Open(); return count ??= values.Count(); } }
        public T this[int index]
        {
            get
            {
                borrow.Open(); ArgumentOutOfRangeException.ThrowIfNegative(index);
                using var e = values.GetEnumerator(); var i = 0;
                while (e.MoveNext()) { if (i++ == index) return e.Current; }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public IEnumerator<T> GetEnumerator()
        { borrow.Open(); return Enumerate(); }
        private IEnumerator<T> Enumerate()
        {
            borrow.Open(); using var e = values.GetEnumerator();
            while (true) { borrow.Open(); if (!e.MoveNext()) yield break; yield return e.Current; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SdkSet<T>(SdkRequestScope borrow, IEnumerable<T> values, IEqualityComparer<T>? equality = null, bool unique = false) : IReadOnlySet<T>
    {
        private readonly IEqualityComparer<T> equality = equality ?? EqualityComparer<T>.Default;
        private int? count;
        public int Count { get { borrow.Open(); return count ??= unique ? values.Count() : this.Count(static _ => true); } }
        public bool Contains(T value)
        { borrow.Open(); using var e = values.GetEnumerator(); while (e.MoveNext()) if (equality.Equals(e.Current, value)) return true; return false; }
        public IEnumerator<T> GetEnumerator()
        { borrow.Open(); return Enumerate(); }
        private IEnumerator<T> Enumerate()
        {
            borrow.Open(); using var e = values.GetEnumerator(); var ordinal = 0;
            while (true)
            {
                borrow.Open(); if (!e.MoveNext()) yield break;
                borrow.Open(); var value = e.Current; var duplicate = false; var prior = 0;
                if (unique) { yield return value; continue; }
                using var check = values.GetEnumerator();
                while (prior++ < ordinal && check.MoveNext()) if (equality.Equals(value, check.Current)) { duplicate = true; break; }
                ordinal++; if (!duplicate) yield return value;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private SdkSet<T> Other(IEnumerable<T> other) { RequireSmall(other.TryGetNonEnumeratedCount(out var n) ? n : other.Take(UngroupedRowLimit + 1).Count()); return new(borrow, other, equality); }
        public bool IsSubsetOf(IEnumerable<T> other) { var set = Other(other); return this.All(set.Contains); }
        public bool IsProperSubsetOf(IEnumerable<T> other) { var set = Other(other); return Count < set.Count && this.All(set.Contains); }
        public bool IsSupersetOf(IEnumerable<T> other) { var set = Other(other); return set.All(Contains); }
        public bool IsProperSupersetOf(IEnumerable<T> other) { var set = Other(other); return Count > set.Count && set.All(Contains); }
        public bool Overlaps(IEnumerable<T> other) { var set = Other(other); return set.Any(Contains); }
        public bool SetEquals(IEnumerable<T> other) { var set = Other(other); return Count == set.Count && this.All(set.Contains); }
    }

    private sealed class SdkMap<TKey, TValue>(SdkRequestScope borrow, IReadOnlySet<TKey> keys, Func<TKey, TValue> value) : IReadOnlyDictionary<TKey, TValue> where TKey : notnull
    {
        public int Count { get { borrow.Open(); return keys.Count; } }
        public IEnumerable<TKey> Keys { get { borrow.Open(); return keys; } }
        public IEnumerable<TValue> Values { get { borrow.Open(); return this.Select(static x => x.Value); } }
        public TValue this[TKey key] => TryGetValue(key, out var result) ? result : throw new KeyNotFoundException();
        public bool ContainsKey(TKey key) { borrow.Open(); return keys.Contains(key); }
        public bool TryGetValue(TKey key, out TValue result)
        { borrow.Open(); if (keys.Contains(key)) { result = value(key); return true; } result = default!; return false; }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        { borrow.Open(); return Enumerate(); }
        private IEnumerator<KeyValuePair<TKey, TValue>> Enumerate()
        { borrow.Open(); using var e = keys.GetEnumerator(); while (true) { borrow.Open(); if (!e.MoveNext()) yield break; yield return new(e.Current, value(e.Current)); } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static IReadOnlyList<T> SdkListView<T>(SourceRows rows, IEnumerable<T> values) => new SdkList<T>(rows.Borrow, values);
    private static IReadOnlySet<T> SdkSetView<T>(SourceRows rows, IEnumerable<T> values, IEqualityComparer<T>? equality = null) => new SdkSet<T>(rows.Borrow, values, equality);
    private static IReadOnlyDictionary<TKey, TValue> SdkMapView<TKey, TValue>(SourceRows rows, IReadOnlySet<TKey> keys, Func<TKey, TValue> value) where TKey : notnull
        => new SdkMap<TKey, TValue>(rows.Borrow, keys, value);
    private static SdkResults<TKey, TValue> SdkResultMap<TKey, TValue>(SourceRows rows, IReadOnlyList<TValue> values, Func<TValue, TKey> key, IEqualityComparer<TKey>? equality = null) where TKey : notnull
    {
        if (rows.Borrow.Facts.HasSdkGrouping) return new(rows, values, key);
        RequireSmall(values.Count);
        var comparer = equality ?? EqualityComparer<TKey>.Default;
        var i = 0;
        while (i < values.Count)
        {
            var j = 0; while (j < i) { if (comparer.Equals(key(values[i]), key(values[j++]))) throw new InvalidDataException("Duplicate SDK result identity."); }
            i++;
        }
        return new(rows, values, key, comparer);
    }
    private static string SdkIdentityKey<T>(SourceRows rows, T identity, Func<MetricOwnerSdkScope, T?> handle, Func<T, string> externalKey) where T : class
    {
        // The original SDK model has no native scope ordinal. Format its exact
        // key once per returned identity, join natively, then use O(1) raw-row
        // result ordinals; never scan all owner scopes per returned identity.
        if (rows.Borrow.Facts.HasSdkGrouping) return externalKey(identity);
        // Original SDK returns normally carry the original borrowed identity.
        // Reuse its caller-owned key; do not repeatedly format ExactIdentityKey.
        using var e = rows.GetEnumerator();
        while (e.MoveNext()) { var scope = rows.Scope(e.Current); if (ReferenceEquals(handle(scope), identity)) return scope.DeviceKey; }
        // An unexpected identity must still undergo the original exact-key guard.
        return externalKey(identity);
    }

    private static bool ExactUtf8(string value, ReadOnlySpan<byte> bytes)
        => CompareUtf8(value, bytes) == 0;
    private static int CompareUtf8(string value, ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<char> chars = value; Span<byte> encoded = stackalloc byte[4]; var offset = 0;
        while (!chars.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(chars, out var rune, out var consumed) != OperationStatus.Done) throw Contract();
            var written = rune.EncodeToUtf8(encoded);
            var i = 0;
            while (i < written)
            { if (offset + i >= bytes.Length) return 1; var compare = encoded[i].CompareTo(bytes[offset + i]); if (compare != 0) return compare; i++; }
            offset += written; chars = chars[consumed..];
        }
        return offset.CompareTo(bytes.Length);
    }
    private static void ValidateUtf16(string value)
    {
        ReadOnlySpan<char> chars = value;
        while (!chars.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(chars, out _, out var consumed) != OperationStatus.Done) throw Contract();
            chars = chars[consumed..];
        }
    }
    private static void DeclareQuantity(MetricDiscoveryRoundFacts facts, int row, string value)
    {
        // Fixed stack marshalling, never a per-scope or per-row managed UTF8 bank.
        // Parent may replace this bound with a direct UTF16→facts arena overload.
        Span<byte> text = stackalloc byte[1024]; uint used = 0;
        MetricDiscoveryBorrowed.WriteUtf8(value, text, ref used);
        facts.DeclareQuantity(row, text[..checked((int)used)]);
    }
}
