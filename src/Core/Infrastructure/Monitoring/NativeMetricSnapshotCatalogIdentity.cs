using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal enum NativeMetricSnapshotCatalogHandleKind : uint
{
    Scope = 1,
    Metric = 2,
    Rule = 3
}

internal sealed record NativeMetricSnapshotCatalogHandleEntry(
    NativeMetricSnapshotCatalogHandleKind Kind,
    ulong Handle,
    string ExactKey);

internal sealed record NativeMetricSnapshotCatalogPersistenceState(
    ulong CatalogGeneration,
    ulong NativeRowFingerprint,
    string CatalogIdentitySha256,
    string ManifestSha256,
    NativeMetricSnapshotCatalogHandleMap HandleMap,
    ImmutableDictionary<ulong, string> MetricIds);

internal sealed class NativeMetricSnapshotCatalogHandleMap
{
    private readonly ImmutableDictionary<string, ulong> handles;

    internal NativeMetricSnapshotCatalogHandleMap(
        IEnumerable<NativeMetricSnapshotCatalogHandleEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var byKey = ImmutableDictionary.CreateBuilder<string, ulong>(
            StringComparer.Ordinal);
        var byHandle = new HashSet<(NativeMetricSnapshotCatalogHandleKind, ulong)>();
        var rows = entries
            .OrderBy(static entry => entry.Kind)
            .ThenBy(static entry => entry.Handle)
            .ThenBy(static entry => entry.ExactKey, StringComparer.Ordinal)
            .ToArray();
        foreach (var entry in rows)
        {
            ValidateEntry(entry);
            if (!byKey.TryAdd(CompositeKey(entry.Kind, entry.ExactKey), entry.Handle)
                || !byHandle.Add((entry.Kind, entry.Handle)))
            {
                throw new InvalidDataException(
                    "The metric-snapshot catalog handle map contains a duplicate key or handle.");
            }
        }

        handles = byKey.ToImmutable();
        Entries = rows.ToImmutableArray();
    }

    internal ImmutableArray<NativeMetricSnapshotCatalogHandleEntry> Entries
    {
        get;
    }

    internal static NativeMetricSnapshotCatalogHandleMap Empty { get; } =
        new([]);

    internal Allocator CreateAllocator()
        => new(this);

    private static void ValidateEntry(
        NativeMetricSnapshotCatalogHandleEntry entry)
    {
        if (!Enum.IsDefined(entry.Kind)
            || entry.Handle == 0
            || string.IsNullOrWhiteSpace(entry.ExactKey)
            || entry.ExactKey != entry.ExactKey.Trim()
            || entry.ExactKey.Contains('\0')
            || Encoding.UTF8.GetByteCount(entry.ExactKey)
                > MaximumExactKeyByteCount
            || (entry.Kind == NativeMetricSnapshotCatalogHandleKind.Scope
                && entry.Handle < FirstDynamicScopeHandle))
        {
            throw new InvalidDataException(
                "The metric-snapshot catalog handle entry is invalid.");
        }
    }

    private static string CompositeKey(
        NativeMetricSnapshotCatalogHandleKind kind,
        string exactKey)
        => string.Create(
            exactKey.Length + 2,
            (kind, exactKey),
            static (destination, state) =>
            {
                destination[0] = checked((char)('0' + (uint)state.kind));
                destination[1] = '\0';
                state.exactKey.AsSpan().CopyTo(destination[2..]);
            });

    internal sealed class Allocator
    {
        private readonly Dictionary<string, ulong> handles;
        private readonly HashSet<(NativeMetricSnapshotCatalogHandleKind, ulong)>
            usedHandles;
        private readonly Dictionary<NativeMetricSnapshotCatalogHandleKind, ulong>
            nextHandles;

        internal Allocator(NativeMetricSnapshotCatalogHandleMap source)
        {
            handles = new Dictionary<string, ulong>(
                source.handles,
                StringComparer.Ordinal);
            usedHandles = source.Entries
                .Select(static entry => (entry.Kind, entry.Handle))
                .ToHashSet();
            nextHandles = new Dictionary<
                NativeMetricSnapshotCatalogHandleKind,
                ulong>
            {
                [NativeMetricSnapshotCatalogHandleKind.Scope] =
                    NextAfter(
                        source.Entries,
                        NativeMetricSnapshotCatalogHandleKind.Scope,
                        FirstDynamicScopeHandle),
                [NativeMetricSnapshotCatalogHandleKind.Metric] =
                    NextAfter(
                        source.Entries,
                        NativeMetricSnapshotCatalogHandleKind.Metric,
                        1),
                [NativeMetricSnapshotCatalogHandleKind.Rule] =
                    NextAfter(
                        source.Entries,
                        NativeMetricSnapshotCatalogHandleKind.Rule,
                        1)
            };
        }

        internal ulong GetOrAdd(
            NativeMetricSnapshotCatalogHandleKind kind,
            string exactKey)
        {
            ValidateEntry(new NativeMetricSnapshotCatalogHandleEntry(
                kind,
                kind == NativeMetricSnapshotCatalogHandleKind.Scope
                    ? FirstDynamicScopeHandle
                    : 1,
                exactKey));
            var composite = CompositeKey(kind, exactKey);
            if (handles.TryGetValue(composite, out var existing))
            {
                return existing;
            }

            var handle = nextHandles[kind];
            if (handle == 0 || !usedHandles.Add((kind, handle)))
            {
                throw new InvalidOperationException(
                    "The metric-snapshot catalog handle space is exhausted or inconsistent.");
            }
            nextHandles[kind] = checked(handle + 1);
            handles.Add(composite, handle);
            return handle;
        }

        internal NativeMetricSnapshotCatalogHandleMap Freeze()
        {
            var entries = handles.Select(pair =>
            {
                var separator = pair.Key.IndexOf('\0');
                if (separator != 1)
                {
                    throw new InvalidOperationException(
                        "The metric-snapshot catalog handle key is not canonical.");
                }
                var kind = checked(
                    (NativeMetricSnapshotCatalogHandleKind)(
                        pair.Key[0] - '0'));
                return new NativeMetricSnapshotCatalogHandleEntry(
                    kind,
                    pair.Value,
                    pair.Key[2..]);
            });
            return new NativeMetricSnapshotCatalogHandleMap(entries);
        }

        private static ulong NextAfter(
            ImmutableArray<NativeMetricSnapshotCatalogHandleEntry> entries,
            NativeMetricSnapshotCatalogHandleKind kind,
            ulong first)
        {
            var maximum = entries
                .Where(entry => entry.Kind == kind)
                .Select(static entry => entry.Handle)
                .DefaultIfEmpty(first - 1)
                .Max();
            return checked(maximum + 1);
        }
    }

    internal const int MaximumExactKeyByteCount = 4096;
    internal const ulong FirstDynamicScopeHandle =
        0x2000_0000_0000_0001UL;
}

internal static class NativeMetricSnapshotCatalogIdentity
{
    internal static string ComputeSha256(
        string manifestSha256,
        ulong nativeRowFingerprint,
        NativeMetricSnapshotCatalogHandleMap handles,
        IReadOnlyDictionary<ulong, string> metricIds,
        IEnumerable<string> activeTopologyKeys)
    {
        if (!IsSha256(manifestSha256) || nativeRowFingerprint == 0)
        {
            throw new InvalidOperationException(
                "The metric-snapshot catalog identity input is invalid.");
        }
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(metricIds);
        ArgumentNullException.ThrowIfNull(activeTopologyKeys);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false, true),
                   leaveOpen: true))
        {
            writer.Write(CatalogIdentityVersion);
            WriteString(writer, manifestSha256);
            writer.Write(nativeRowFingerprint);
            writer.Write(checked((uint)handles.Entries.Length));
            foreach (var entry in handles.Entries)
            {
                writer.Write((uint)entry.Kind);
                writer.Write(entry.Handle);
                WriteString(writer, entry.ExactKey);
            }
            var ids = metricIds.OrderBy(static pair => pair.Key).ToArray();
            writer.Write(checked((uint)ids.Length));
            foreach (var pair in ids)
            {
                writer.Write(pair.Key);
                WriteString(writer, pair.Value);
            }
            var topology = activeTopologyKeys
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            writer.Write(checked((uint)topology.Length));
            foreach (var key in topology)
            {
                WriteString(writer, key);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains('\0')
            || Encoding.UTF8.GetByteCount(value)
                > NativeMetricSnapshotCatalogHandleMap
                    .MaximumExactKeyByteCount)
        {
            throw new InvalidOperationException(
                "The metric-snapshot catalog identity text is invalid.");
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static bool IsSha256(string value)
        => value.Length == 64
            && value.All(static character => char.IsAsciiHexDigit(character));

    private const uint CatalogIdentityVersion = 1;
}
