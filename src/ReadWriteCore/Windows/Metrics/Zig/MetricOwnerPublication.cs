using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>Synchronous wake dispatch; all borrows expire on callback return.</summary>
public delegate void MetricOwnerPublished(in MetricOwnerPublication publication);

/// <summary>One publication's native-decided metadata, borrowed from its local work lease.</summary>
public readonly unsafe ref struct MetricOwnerPublication
{
    private readonly MetricOwnerRawRound query;
    private readonly nint receipt;
    internal MetricOwnerPublication(MetricOwnerRawRound query, nint receipt)
    { this.query = query; this.receipt = receipt; }
    private ref readonly MetricOwnerReceipt Native
    {
        get
        {
            _ = query.Token; // same operation's guard; never dereference expired work
            if (receipt == 0 || ((MetricOwnerReceipt*)receipt)->Committed != 1)
                throw new InvalidOperationException("No accepted publication receipt.");
            return ref *(MetricOwnerReceipt*)receipt;
        }
    }
    public ReadOnlySpan<uint> AffectedMetrics => new(Native.MetricIndices, checked((int)Native.AffectedCount));
    public ReadOnlySpan<ulong> AffectedMetricIds => new(Native.MetricIds, checked((int)Native.AffectedCount));
    public ReadOnlySpan<uint> InvalidatedAliases => new(Native.InvalidatedAliases, checked((int)Native.InvalidatedAliasCount));
    public ulong RoundGeneration => Native.RoundGeneration;
    public long WriteTimeUnixMs => Native.UtcMs;
    public int QueryCount => checked((int)Native.QueryCount);
    private void QueryRow(int row)
    { if ((uint)row >= Native.QueryCount) throw new ArgumentOutOfRangeException(nameof(row)); }
    public ulong RuleId(int row) { QueryRow(row); return Native.Queries.RuleIds[row]; }
    public ulong MetricId(int row) { QueryRow(row); return Native.Queries.MetricIds[row]; }
    public ulong SourceId(int row) { QueryRow(row); return Native.Queries.SourceIds[row]; }
    public ulong SourceIncarnation(int row) { QueryRow(row); return Native.Queries.Incarnations[row]; }
    public ulong CapabilityGeneration(int row) { QueryRow(row); return Native.Queries.Capabilities[row]; }
}
