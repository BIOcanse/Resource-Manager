using System.Collections;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig.Adapters;

public sealed partial class MetricOwnerSdkSampler
{
    // Only synchronous helpers touch pointer columns. Iterators retain a checked
    // call token, never a copied native descriptor or pointer across suspension.
    private static unsafe class SdkNative
    {
        private static MetricSdkDispatch D(SourceRows r) { r.Borrow.Open(); return r.Borrow.Facts.SdkGrouping; }
        internal static void Initialize(SourceRows r)
        {
            if (!r.Borrow.Facts.HasSdkGrouping) return;
            var d = D(r); var i = 0;
            while (i < d.SourceCount && d.SourceIds[i] != r.source) i++;
            if (i == d.SourceCount) throw Contract();
            r.groupFirst = r.sourceFirst = checked((int)d.SourceGroupOffsets[i]);
            r.groupEnd = r.sourceEnd = checked((int)d.SourceGroupOffsets[i + 1]);
        }
        internal static int Representative(SourceRows r, int group) => checked((int)D(r).GroupFirstRows[group]);
        internal static int RowGroup(SourceRows r, int row) => unchecked((int)D(r).RawRowGroups[row]);
        internal static bool Excluded(MetricDiscoveryRoundFacts facts, int row) => facts.SdkGrouping.RawRowGroups[row] == uint.MaxValue;
        internal static bool SourceHasRows(MetricDiscoveryRoundFacts facts, int source)
        { var d = facts.SdkGrouping; return d.SourceGroupOffsets[source] != d.SourceGroupOffsets[source + 1]; }
        internal static int GroupAt(SourceRows r, int position)
        { return r.groupOrder switch { 1 => Ordered<string>(r, position), 2 => Ordered<int>(r, position), _ => position }; }
        internal static (int, int) RowBounds(SourceRows r, int group)
        { var d = D(r); return ((int)d.GroupRowOffsets[group], (int)d.GroupRowOffsets[group + 1]); }
        internal static int RawAt(SourceRows r, int offset) => checked((int)D(r).RowIndices[offset]);
        internal static bool Eligible(SourceRows r, int group)
        {
            var d = D(r); var representative = (int)d.GroupFirstRows[group];
            if (!r.AllowsScope(representative)) return false;
            var i = d.GroupFieldOffsets[group]; var end = d.GroupFieldOffsets[group + 1];
            while (i < end) if (r.AllowsField(d.FieldCodes[i++])) return true;
            return false;
        }
        internal static IEnumerable<int> Groups(SourceRows r) => new GroupSequence(r);
        internal static IEnumerable<int> Rows(SourceRows r, int group) => new GroupRows(r, group);
        internal static void Project(SourceRows r)
        {
            if (!r.Borrow.Facts.HasSdkGrouping) return;
            var d = D(r); r.projectionCount = 0;
            var order = r.projectionOrder == 2 ? d.DisplayOrder : d.KeyOrder; var i = r.sourceFirst;
            while (i < r.sourceEnd)
            { var group = (int)order[i++]; if (Eligible(r, group)) d.ResultOrdinals[r.sourceFirst + r.projectionCount++] = group; }
        }
        internal static int Projected(SourceRows r, int index)
        {
            r.Borrow.Open();
            if ((uint)index >= (uint)r.projectionCount) throw new ArgumentOutOfRangeException(nameof(index));
            return D(r).ResultOrdinals[r.sourceFirst + index] & int.MaxValue;
        }
        internal static int Compare<TKey>(SourceRows r, int group, TKey value) where TKey : notnull
        {
            var row = Representative(r, group);
            if (typeof(TKey) == typeof(int)) return unchecked((int)r.Borrow.Facts.Native.Dispatch.DisplayIndices[row]).CompareTo(Unsafe.As<TKey, int>(ref value));
            if (typeof(TKey) == typeof(string)) return -CompareUtf8((string)(object)value, r.Borrow.Facts.DeviceKey(row));
            throw Contract();
        }
        internal static TKey Key<TKey>(SourceRows r, int group) where TKey : notnull
        {
            var scope = r.Scope(Representative(r, group));
            if (typeof(TKey) == typeof(int)) { var index = scope.DisplayIndex; return Unsafe.As<int, TKey>(ref index); }
            if (typeof(TKey) == typeof(string)) return (TKey)(object)scope.DeviceKey;
            throw Contract();
        }
        internal static (int First, int End) Find<TKey>(SourceRows r, TKey key) where TKey : notnull
        {
            var low = r.sourceFirst; var end = OrderedEnd<TKey>(r); var high = end;
            while (low < high) { var mid = low + (high - low) / 2; if (Compare(r, Ordered<TKey>(r, mid), key) < 0) low = mid + 1; else high = mid; }
            var first = low; high = end;
            while (low < high) { var mid = low + (high - low) / 2; if (Compare(r, Ordered<TKey>(r, mid), key) <= 0) low = mid + 1; else high = mid; }
            return (first, low);
        }
        private static bool ProjectedOrder<TKey>(SourceRows r) => r.projector is { } p && p.projectionOrder == (typeof(TKey) == typeof(int) ? 2 : 1);
        internal static int OrderedEnd<TKey>(SourceRows r) => ProjectedOrder<TKey>(r) ? r.sourceFirst + r.projector!.projectionCount : r.sourceEnd;
        internal static int Ordered<TKey>(SourceRows r, int position) where TKey : notnull
        { var d = D(r); return ProjectedOrder<TKey>(r) ? d.ResultOrdinals[position] & int.MaxValue : (int)(typeof(TKey) == typeof(int) ? d.DisplayOrder[position] : d.KeyOrder[position]); }
        internal static void Narrow<TKey>(SourceRows r, TKey key) where TKey : notnull
        {
            if (!r.Borrow.Facts.HasSdkGrouping) return;
            (r.groupFirst, r.groupEnd) = Find(r, key); r.groupOrder = typeof(TKey) == typeof(int) ? 2 : 1;
        }
        internal static IEnumerable<int> KeyGroups<TKey>(SourceRows r) where TKey : notnull => new OrderedGroups<TKey>(r);
        internal static bool HasField(SourceRows r, int group, uint field)
        {
            var d = D(r); var low = d.GroupFieldOffsets[group]; var high = d.GroupFieldOffsets[group + 1]; var end = high;
            while (low < high) { var mid = low + (high - low) / 2; if (d.FieldCodes[mid] < field) low = mid + 1; else high = mid; }
            return low < end && d.FieldCodes[low] == field && r.AllowsField(field);
        }
        internal static uint? NextField(SourceRows r, uint? after)
        {
            var d = D(r); uint? next = null;
            using var groups = Groups(r).GetEnumerator();
            while (groups.MoveNext())
            {
                var g = groups.Current; var low = d.GroupFieldOffsets[g]; var high = d.GroupFieldOffsets[g + 1]; var end = high;
                if (after.HasValue) while (low < high) { var mid = low + (high - low) / 2; if (d.FieldCodes[mid] <= after.Value) low = mid + 1; else high = mid; }
                while (low < end && !r.AllowsField(d.FieldCodes[low])) low++;
                if (low < end && (!next.HasValue || d.FieldCodes[low] < next.Value)) next = d.FieldCodes[low];
            }
            return next;
        }
        internal static void ResetResults(SourceRows r)
        { var d = D(r); using var groups = Groups(r).GetEnumerator(); while (groups.MoveNext()) d.ResultOrdinals[groups.Current] = -1; }
        internal static void BeginMarks(SourceRows r)
        { var d = D(r); using var groups = Groups(r).GetEnumerator(); while (groups.MoveNext()) d.ResultOrdinals[groups.Current] &= int.MaxValue; }
        internal static bool Mark<TKey>(SourceRows r, TKey key, out bool added) where TKey : notnull
        {
            var d = D(r); var range = Find(r, key); var i = range.First;
            while (i < range.End)
            {
                var group = Ordered<TKey>(r, i++); if (!Eligible(r, group)) continue;
                added = (Interlocked.Or(ref d.ResultOrdinals[group], int.MinValue) & int.MinValue) == 0; return true;
            }
            added = false; return false;
        }
        internal static void EndMarks(SourceRows r)
        {
            var d = D(r); using var groups = Groups(r).GetEnumerator();
            while (groups.MoveNext()) { var g = groups.Current; var low = d.ResultOrdinals[g] & int.MaxValue; d.ResultOrdinals[g] = low == int.MaxValue ? -1 : low; }
        }
        internal static bool Join<TKey>(SourceRows r, TKey key, int ordinal) where TKey : notnull
        {
            var d = D(r); var range = Find(r, key); var i = range.First; var found = false;
            while (i < range.End)
            {
                var group = Ordered<TKey>(r, i++); if (!Eligible(r, group)) continue;
                if (d.ResultOrdinals[group] != -1) throw new InvalidDataException("Duplicate SDK result identity.");
                d.ResultOrdinals[group] = ordinal; found = true;
            }
            return found;
        }
        internal static int Result(SourceRows r, int row)
        { var d = D(r); var group = unchecked((int)d.RawRowGroups[row]); return group < 0 || !Eligible(r, group) ? -1 : d.ResultOrdinals[group]; }
        internal static int ResultKey<TKey>(SourceRows r, TKey key) where TKey : notnull
        {
            var d = D(r); var range = Find(r, key); var i = range.First;
            while (i < range.End) { var g = Ordered<TKey>(r, i++); if (Eligible(r, g) && d.ResultOrdinals[g] >= 0) return d.ResultOrdinals[g]; }
            return -1;
        }
        internal static void Validate(MetricOwnerRawRound raw, MetricDiscoveryRoundFacts facts)
        {
            if (!facts.HasSdkGrouping) { RequireSmall(raw.Count); return; }
            var d = facts.SdkGrouping;
            if (d.RowCount != raw.Count || d.SourceCount != facts.SourceCount || d.SdkRowCount > d.RowCount || d.GroupCount > d.SdkRowCount
                || d.Token != raw.Token || d.CatalogGeneration != facts.Native.Raw.CatalogGeneration || d.SourceGroupOffsets == null
                || (d.RowCount != 0 && d.RawRowGroups == null) || (d.SourceCount != 0 && d.SourceIds == null)
                || (d.GroupCount != 0 && (d.GroupFirstRows == null || d.GroupScopeIndices == null || d.GroupRowOffsets == null || d.RowIndices == null
                    || d.GroupFieldOffsets == null || d.FieldCodes == null || d.KeyOrder == null || d.DisplayOrder == null || d.ResultOrdinals == null))) throw Contract();
            var s = 0; uint previous = 0;
            while (s < d.SourceCount)
            { if (d.SourceIds[s] != facts.SourceId(s) || d.SourceGroupOffsets[s] != previous || d.SourceGroupOffsets[s + 1] < previous || d.SourceGroupOffsets[s + 1] > d.GroupCount) throw Contract(); previous = d.SourceGroupOffsets[++s]; }
            if (previous != d.GroupCount) throw Contract();
            var g = 0; uint priorRow = 0, priorField = 0;
            while (g < d.GroupCount)
            {
                if (d.GroupFirstRows[g] >= d.RowCount || d.GroupRowOffsets[g] != priorRow || d.GroupRowOffsets[g + 1] <= priorRow || d.GroupRowOffsets[g + 1] > d.SdkRowCount
                    || d.GroupFieldOffsets[g] != priorField || d.GroupFieldOffsets[g + 1] <= priorField || d.GroupFieldOffsets[g + 1] > d.SdkRowCount
                    || d.KeyOrder[g] >= d.GroupCount || d.DisplayOrder[g] >= d.GroupCount) throw Contract();
                var i = priorRow;
                while (i < d.GroupRowOffsets[g + 1]) { var row = d.RowIndices[i++]; if (row >= d.RowCount || d.RawRowGroups[row] != g) throw Contract(); }
                priorRow = d.GroupRowOffsets[g + 1]; priorField = d.GroupFieldOffsets[g + 1]; g++;
            }
            if (priorRow != d.SdkRowCount) throw Contract();
        }
    }
    private sealed class GroupSequence(SourceRows rows) : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        private IEnumerator<int> Enumerate()
        { var p = rows.groupFirst; while (p < rows.groupEnd) { rows.Borrow.Open(); var g = SdkNative.GroupAt(rows, p++); if (SdkNative.Eligible(rows, g)) yield return g; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class GroupRows(SourceRows rows, int group) : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        private IEnumerator<int> Enumerate()
        { var (first, end) = SdkNative.RowBounds(rows, group); while (first < end) { rows.Borrow.Open(); yield return SdkNative.RawAt(rows, first++); } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class OrderedGroups<TKey>(SourceRows rows) : IEnumerable<int> where TKey : notnull
    {
        public IEnumerator<int> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        private IEnumerator<int> Enumerate()
        {
            // Keep same-order per-device requests inside their binary-narrowed
            // range. A different order still checks the inherited scope filter.
            var narrowed = rows.groupOrder == (typeof(TKey) == typeof(int) ? 2 : 1);
            var p = narrowed ? rows.groupFirst : rows.sourceFirst;
            var end = narrowed ? rows.groupEnd : SdkNative.OrderedEnd<TKey>(rows);
            while (p < end) { rows.Borrow.Open(); var g = SdkNative.Ordered<TKey>(rows, p++); if (SdkNative.Eligible(rows, g)) yield return g; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private abstract class GroupSet<T>(SdkRequestScope borrow) : IReadOnlySet<T>
    {
        public abstract int Count { get; }
        public abstract bool Contains(T value);
        public abstract IEnumerator<T> GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        protected abstract (int Matched, bool Foreign) Measure(IEnumerable<T> other);
        public bool IsSubsetOf(IEnumerable<T> other) { borrow.Open(); return Measure(other).Matched == Count; }
        public bool IsProperSubsetOf(IEnumerable<T> other) { borrow.Open(); var m = Measure(other); return m.Matched == Count && m.Foreign; }
        public bool IsSupersetOf(IEnumerable<T> other) { borrow.Open(); return !Measure(other).Foreign; }
        public bool IsProperSupersetOf(IEnumerable<T> other) { borrow.Open(); var m = Measure(other); return !m.Foreign && m.Matched < Count; }
        public bool Overlaps(IEnumerable<T> other) { borrow.Open(); return other.Any(Contains); }
        public bool SetEquals(IEnumerable<T> other) { borrow.Open(); var m = Measure(other); return !m.Foreign && m.Matched == Count; }
    }
    private sealed class SdkKeys<TKey>(SourceRows rows) : GroupSet<TKey>(rows.Borrow) where TKey : notnull
    {
        private int? count;
        public override int Count { get { rows.Borrow.Open(); return count ??= this.Count(static _ => true); } }
        public override bool Contains(TKey key)
        { rows.Borrow.Open(); var range = SdkNative.Find(rows, key); var i = range.First; while (i < range.End) if (SdkNative.Eligible(rows, SdkNative.Ordered<TKey>(rows, i++))) return true; return false; }
        public override IEnumerator<TKey> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        protected override (int Matched, bool Foreign) Measure(IEnumerable<TKey> other)
        {
            lock (rows.Borrow)
            {
                rows.Borrow.Open(); if (rows.Borrow.AlgebraActive) throw new InvalidOperationException("Reentrant SDK set algebra.");
                rows.Borrow.AlgebraActive = true;
                try
                {
                    SdkNative.BeginMarks(rows); var matched = 0; var foreign = false;
                    using var e = other.GetEnumerator();
                    while (e.MoveNext()) { if (!SdkNative.Mark(rows, e.Current, out var added)) foreign = true; else if (added) matched++; }
                    return (matched, foreign);
                }
                finally { try { SdkNative.EndMarks(rows); } finally { rows.Borrow.AlgebraActive = false; } }
            }
        }
        private IEnumerator<TKey> Enumerate()
        {
            var hasPrior = false; TKey prior = default!;
            using var groups = SdkNative.KeyGroups<TKey>(rows).GetEnumerator();
            while (groups.MoveNext()) { var key = SdkNative.Key<TKey>(rows, groups.Current); if (!hasPrior || !EqualityComparer<TKey>.Default.Equals(prior, key)) { prior = key; hasPrior = true; yield return key; } }
        }
    }
    private sealed class SdkFields(SourceRows rows, Func<MetricFieldCode, bool>? include = null) : GroupSet<MetricFieldCode>(rows.Borrow)
    {
        private int? count;
        public override int Count { get { rows.Borrow.Open(); return count ??= this.Count(static _ => true); } }
        public override bool Contains(MetricFieldCode field)
        { rows.Borrow.Open(); if (include is not null && !include(field)) return false; using var g = SdkNative.Groups(rows).GetEnumerator(); while (g.MoveNext()) if (SdkNative.HasField(rows, g.Current, (uint)field)) return true; return false; }
        public override IEnumerator<MetricFieldCode> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        protected override (int Matched, bool Foreign) Measure(IEnumerable<MetricFieldCode> other)
        {
            // Fixed enum-domain metadata, derived from the current final SDK
            // enum member, never from owner row/group counts or a guessed ID.
            const int words = ((int)MetricFieldCode.GpuSharedMemoryUsed + 64) / 64;
            Span<ulong> seen = stackalloc ulong[words]; seen.Clear(); var matched = 0; var foreign = false;
            using var e = other.GetEnumerator();
            while (e.MoveNext())
            {
                if (!Contains(e.Current)) { foreign = true; continue; }
                var code = (uint)e.Current; if (code >= words * 64) throw Contract();
                var word = (int)(code >> 6); var mask = 1UL << (int)(code & 63);
                if ((seen[word] & mask) == 0) { seen[word] |= mask; matched++; }
            }
            return (matched, foreign);
        }
        private IEnumerator<MetricFieldCode> Enumerate()
        { uint? prior = null; while (true) { var next = SdkNative.NextField(rows, prior); if (!next.HasValue) yield break; prior = next; var field = (MetricFieldCode)next.Value; if (include is null || include(field)) yield return field; } }
    }
    private sealed class ScopeProjection<T>(SourceRows rows, Func<MetricOwnerSdkScope, T> select) : IReadOnlyList<T>
    {
        public int Count { get { rows.Borrow.Open(); return rows.projectionCount; } }
        public T this[int index] => select(rows.Scope(SdkNative.Representative(rows, SdkNative.Projected(rows, index))));
        public IEnumerator<T> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        private IEnumerator<T> Enumerate() { var i = 0; while (i < Count) yield return this[i++]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class SdkResults<TKey, TValue> : IReadOnlyDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly SourceRows rows; private readonly IReadOnlyList<TValue> values; private readonly Func<TValue, TKey> key; private readonly IEqualityComparer<TKey> equality;
        private readonly bool grouped;
        public SdkResults(SourceRows rows, IReadOnlyList<TValue> values, Func<TValue, TKey> key, IEqualityComparer<TKey>? equality = null)
        {
            this.rows = rows; this.values = values; this.key = key; this.equality = equality ?? EqualityComparer<TKey>.Default;
            grouped = rows.Borrow.Facts.HasSdkGrouping;
            if (!grouped) return;
            SdkNative.ResetResults(rows); var i = 0;
            while (i < values.Count) { SdkNative.Join(rows, key(values[i]), i); i++; }
        }
        public bool TryRow(int row, out TValue value)
        {
            rows.Borrow.Open();
            if (!grouped) return TryGetValue(RowKey(row), out value);
            var ordinal = SdkNative.Result(rows, row);
            if (ordinal >= 0) { value = values[ordinal]; return true; }
            value = default!; return false;
        }
        private TKey RowKey(int row)
        {
            if (typeof(TKey) == typeof(int)) { var index = rows.Scope(row).DisplayIndex; return Unsafe.As<int, TKey>(ref index); }
            return (TKey)(object)rows.Scope(row).DeviceKey;
        }
        public TValue? GetRow(int row) => TryRow(row, out var value) ? value : default;
        public bool TryGetValue(TKey k, out TValue value)
        {
            rows.Borrow.Open();
            if (grouped) { var i = SdkNative.ResultKey(rows, k); if (i >= 0) { value = values[i]; return true; } }
            else { var i = 0; while (i < values.Count) { var v = values[i++]; if (equality.Equals(key(v), k)) { value = v; return true; } } }
            value = default!; return false;
        }
        public TValue this[TKey k] => TryGetValue(k, out var v) ? v : throw new KeyNotFoundException();
        public bool ContainsKey(TKey k) => TryGetValue(k, out _);
        public int Count => this.Count(static _ => true);
        public IEnumerable<TKey> Keys => this.Select(static p => p.Key);
        public IEnumerable<TValue> Values => this.Select(static p => p.Value);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() { rows.Borrow.Open(); return Enumerate(); }
        private IEnumerator<KeyValuePair<TKey, TValue>> Enumerate()
        {
            if (grouped) { using var e = new SdkKeys<TKey>(rows).GetEnumerator(); while (e.MoveNext()) if (TryGetValue(e.Current, out var v)) yield return new(e.Current, v); }
            else { var i = 0; while (i < values.Count) { rows.Borrow.Open(); var v = values[i++]; yield return new(key(v), v); } }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static IReadOnlyList<T> SdkScopes<T>(SourceRows rows, Func<MetricOwnerSdkScope, T> select)
        => rows.Borrow.Facts.HasSdkGrouping ? new ScopeProjection<T>(rows, select) : SdkListView(rows, SelectedScopes(rows, rows.Borrow.Facts).Select(select));
    private static IReadOnlySet<int> SdkIndexes(SourceRows rows)
        => rows.Borrow.Facts.HasSdkGrouping ? new SdkKeys<int>(rows) : SdkSetView(rows, rows.Select(row => rows.Scope(row).DisplayIndex));
    private static IReadOnlySet<string> SdkDeviceKeys(SourceRows rows)
        => rows.Borrow.Facts.HasSdkGrouping ? new SdkKeys<string>(rows) : SdkSetView(rows, rows.Select(row => rows.Scope(row).DeviceKey), StringComparer.Ordinal);
    private static IReadOnlySet<MetricFieldCode> SdkCodes(SourceRows rows, Func<MetricFieldCode, bool>? include = null)
        => rows.Borrow.Facts.HasSdkGrouping ? new SdkFields(rows, include) : SdkSetView(rows, rows.Select(row => rows.Field(row)).Where(code => include is null || include(code)));
    private static unsafe void ValidateDisplay(MetricDiscoveryRoundFacts facts, int row, int display)
    { if (facts.Native.Dispatch.DisplayIndices == null || unchecked((int)facts.Native.Dispatch.DisplayIndices[row]) != display) throw Contract(); }
}
