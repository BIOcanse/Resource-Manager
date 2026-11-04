using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>
/// Binding for a .NET-only SDK. It receives only native-selected raw rows. The
/// callback performs SDK request/shape adaptation, with no normalization, current
/// cache, source arbitration, sampling timer or subscription knowledge.
/// </summary>
public sealed record MetricOwnerDotNetAdapter(ulong SourceId,
    Func<MetricOwnerSourceQuery, CancellationToken, ValueTask> Sample,
    Func<ValueTask> Drain);

public sealed class MetricOwnerDotNetSampler : IMetricOwnerRawSampler
{
    private readonly MetricOwnerDotNetAdapter[] adapters;
    private readonly Action<Exception, ulong>? collectionFailed;

    public MetricOwnerDotNetSampler(ReadOnlySpan<MetricOwnerDotNetAdapter> adapters,
        Action<Exception, ulong>? collectionFailed = null)
    {
        this.collectionFailed = collectionFailed;
        this.adapters = adapters.ToArray(); // cold bindings, once at catalog binding; no sampled values
        var index = 0;
        while (index < this.adapters.Length)
        {
            var adapter = this.adapters[index];
            ArgumentNullException.ThrowIfNull(adapter.Sample); ArgumentNullException.ThrowIfNull(adapter.Drain);
            if (adapter.SourceId == 0) throw new ArgumentException("SDK source identity is zero.", nameof(adapters));
            var previous = 0;
            while (previous < index)
                if (this.adapters[previous++].SourceId == adapter.SourceId)
                    throw new ArgumentException("SDK source identity is duplicated.", nameof(adapters));
            index++;
        }
    }

    public async ValueTask SampleAsync(MetricOwnerRawRound round, CancellationToken cancellationToken)
    {
        // Reject an unbound selected source before any SDK call; no silent loss.
        var row = 0;
        while (row < round.Count)
        {
            var source = round.SourceId(row++);
            var adapter = 0;
            while (adapter < adapters.Length && adapters[adapter].SourceId != source) adapter++;
            if (adapter == adapters.Length) throw new InvalidDataException("Native selected a source with no SDK adapter binding.");
        }
        var index = 0;
        while (index < adapters.Length)
        {
            var adapter = adapters[index++];
            var query = new MetricOwnerSourceQuery(round, adapter.SourceId);
            var cursor = 0;
            if (!query.TryNext(ref cursor, out _)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            try { await adapter.Sample(query, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not MetricOwnerException
                && !(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // SDK acquisition failure is raw health. Native alone decides retention/arbitration.
                cursor = 0;
                while (query.TryNext(ref cursor, out var failedRow))
                    query.WriteUnavailable(failedRow, MetricOwnerObservationStatus.Unavailable, 0);
                collectionFailed?.Invoke(error, adapter.SourceId);
            }
        }
    }

    public async ValueTask DrainAsync()
    {
        List<Exception>? failures = null;
        var index = 0;
        while (index < adapters.Length)
        {
            try { await adapters[index++].Drain().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("SDK drains completed with errors.", failures);
    }
}

/// <summary>Borrowed SDK request/response view; all access expires when the round is detached.</summary>
public sealed class MetricOwnerSourceQuery
{
    private readonly MetricOwnerRawRound round;
    public ulong SourceId { get; }

    internal MetricOwnerSourceQuery(MetricOwnerRawRound round, ulong sourceId)
    { this.round = round; SourceId = sourceId; }

    public bool TryNext(ref int cursor, out int row)
    {
        if ((uint)cursor > (uint)round.Count) throw new ArgumentOutOfRangeException(nameof(cursor));
        while (cursor < round.Count)
        {
            var candidate = cursor++;
            if (Belongs(candidate)) { row = candidate; return true; }
        }
        row = -1; return false;
    }
    public uint RuleIndex(int row) { Require(row); return round.RuleIndex(row); }
    public ulong RuleId(int row) { Require(row); return round.RuleId(row); }
    public ulong MetricId(int row) { Require(row); return round.MetricId(row); }
    public ulong RuleIdentity(int row) { Require(row); return round.RuleIdentity(row); }
    public MetricOwnerValueKind ValueKind(int row) { Require(row); return round.ValueKind(row); }
    public void WriteScalar(int row, MetricOwnerValueKind kind, ulong low, ulong high, long utcMs,
        ulong durationMs = 0, bool isNull = false, uint? quality = null, ulong? capabilityMask = null)
    { Require(row); round.WriteScalar(row, kind, low, high, utcMs, durationMs, isNull, quality, capabilityMask); }
    public void WriteBytes(int row, ReadOnlySpan<byte> bytes, long utcMs)
    { Require(row); round.WriteBytes(row, bytes, utcMs); }
    public void WriteCounter(int row, uint flags, ulong primary, ulong reference, ulong monotonicNs,
        ulong tickFrequency, long utcMs, ulong referenceFrequencyMhz = 0)
    { Require(row); round.WriteCounter(row, flags, primary, reference, monotonicNs, tickFrequency, utcMs, referenceFrequencyMhz); }
    public void WriteUnavailable(int row, MetricOwnerObservationStatus status, uint reason, bool wholeSource = false)
    { Require(row); round.WriteUnavailable(row, status, reason, wholeSource); }
    public void WriteCpuCounter(int row, ulong idleTicks, ulong kernelTicks, ulong userTicks,
        ulong monotonicTicks, ulong ticksPerSecond, long utcMs)
    { Require(row); round.WriteCpuCounter(row, idleTicks, kernelTicks, userTicks, monotonicTicks, ticksPerSecond, utcMs); }
    public void InvalidateSourceIdentity(int row, uint reason)
    { Require(row); round.InvalidateSourceIdentity(row, reason); }
    private bool Belongs(int row)
    {
        return round.SourceId(row) == SourceId;
    }
    private void Require(int row)
    {
        if (!Belongs(row)) throw new ArgumentException("SDK cannot write another source's row.", nameof(row));
    }
}
