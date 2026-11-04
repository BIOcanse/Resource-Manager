using ResourceManager.ReadWrite.Native.MetricOwner;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

/// <summary>
/// A raw adapter fills the selected rows, without knowing subscriptions or API
/// requests. Native preparation waits for DrainAsync after SampleAsync. DrainAsync
/// completes or faults only after all borrowed writes/callbacks have ended,
/// including cancellation and errors; no background writer may survive it.
/// </summary>
public interface IMetricOwnerRawSampler
{
    ValueTask SampleAsync(MetricOwnerRawRound round, CancellationToken cancellationToken);
    ValueTask DrainAsync();
}

/// <summary>Non-owning access to one operation's native SoA workspace.</summary>
public sealed unsafe class MetricOwnerRawRound
{
    private MetricOwnerRound round;
    private int closed;
    private int payloadUsed;
    private int frozen;

    internal MetricOwnerRawRound(MetricOwnerRound round) => this.round = round;
    public int Count { get { RequireSampling(); return checked((int)round.Count); } }
    public ulong Token { get { RequireOpen(); return round.Token; } }
    public uint RuleIndex(int row) { RequireRow(row); return round.Observations.RuleIndices[row]; }
    public ulong RuleId(int row) { RequireRow(row); return round.Queries.RuleIds[row]; }
    public ulong SourceId(int row) { RequireRow(row); return round.Queries.SourceIds[row]; }
    public ulong MetricId(int row) { RequireRow(row); return round.Queries.MetricIds[row]; }
    public ulong RuleIdentity(int row) { RequireRow(row); return round.Queries.Identities[row]; }
    public ulong SourceIncarnation(int row) { RequireRow(row); return round.Queries.Incarnations[row]; }
    public ulong CapabilityGeneration(int row) { RequireRow(row); return round.Queries.Capabilities[row]; }
    public MetricOwnerValueKind ValueKind(int row) { RequireRow(row); return (MetricOwnerValueKind)round.Queries.ValueKinds[row]; }

    public void WriteScalar(int row, MetricOwnerValueKind kind, ulong low, ulong high,
        long observedUtcMs, ulong durationMs = 0, bool isNull = false, uint? quality = null, ulong? capabilityMask = null)
    {
        RequireWritable(row);
        ref var columns = ref round.Observations;
        columns.Kinds[row] = (uint)kind;
        columns.Low[row] = low;
        columns.High[row] = high;
        columns.Presence[row] = isNull ? 2U : 1U;
        columns.ObservedMs[row] = observedUtcMs;
        columns.DurationMs[row] = durationMs;
        columns.Status[row] = (uint)MetricOwnerObservationStatus.Current;
        columns.Reasons[row] = 0;
        if (quality.HasValue) columns.Quality[row] = quality.Value;
        if (capabilityMask.HasValue) columns.CapabilityMasks[row] = capabilityMask.Value;
    }

    public Span<byte> ReserveBytes(int row, int length, long observedUtcMs, bool isNull = false)
    {
        RequireWritable(row);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (isNull && length != 0) throw new ArgumentException("Null bytes have no payload.", nameof(length));
        var end = Interlocked.Add(ref payloadUsed, length);
        if (end < length || (ulong)end > round.PayloadCapacity)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.PayloadCapacity,
                Family = MetricOwnerFamily.Payload, Required = checked((ulong)Math.Max(end, 0)), Limit = round.PayloadCapacity });
        WriteScalar(row, MetricOwnerValueKind.Bytes, 0, 0, observedUtcMs, isNull: isNull);
        round.Observations.PayloadOffsets[row] = checked((uint)(end - length));
        round.Observations.PayloadLengths[row] = checked((uint)length);
        return new(round.Payload + end - length, length);
    }

    public void WriteBytes(int row, ReadOnlySpan<byte> bytes, long observedUtcMs)
        => bytes.CopyTo(ReserveBytes(row, bytes.Length, observedUtcMs));

    public void WriteUnavailable(int row, MetricOwnerObservationStatus status, uint reason, bool wholeSource = false)
    {
        RequireWritable(row);
        if (status == MetricOwnerObservationStatus.Current) throw new ArgumentException("Use WriteScalar for current values.", nameof(status));
        if (wholeSource && status != MetricOwnerObservationStatus.Unsupported)
            throw new ArgumentException("A whole-source unsupported fact requires Unsupported status.", nameof(status));
        round.Observations.Status[row] = (uint)status;
        round.Observations.Reasons[row] = reason;
        round.Observations.Presence[row] = 0;
        if (wholeSource) round.Observations.Flags[row] |= 32U; // native whole-source unsupported fact
    }

    public void WriteCounter(int row, uint counterFlags, ulong primary, ulong reference,
        ulong monotonicNanoseconds, ulong tickFrequency, long observedUtcMs, ulong referenceFrequencyMhz = 0)
    {
        WriteScalar(row, MetricOwnerValueKind.Float64, primary, reference, observedUtcMs);
        round.Observations.Flags[row] = counterFlags;
        round.Observations.Reference[row] = referenceFrequencyMhz;
        round.Observations.MonotonicNs[row] = monotonicNanoseconds;
        round.Observations.TickFrequency[row] = tickFrequency;
    }

    public void WriteCpuCounter(int row, ulong idleTicks, ulong kernelTicks, ulong userTicks,
        ulong monotonicTicks, ulong ticksPerSecond, long observedUtcMs)
    {
        WriteScalar(row, MetricOwnerValueKind.Float64, idleTicks, kernelTicks, observedUtcMs);
        round.Observations.Flags[row] = 16U;
        round.Observations.Reference[row] = userTicks;
        round.Observations.MonotonicNs[row] = monotonicTicks;
        round.Observations.TickFrequency[row] = ticksPerSecond;
    }

    public void InvalidateSourceIdentity(int row, uint reason)
    {
        WriteUnavailable(row, MetricOwnerObservationStatus.Unavailable, reason);
        round.Observations.Flags[row] |= 8U;
    }

    internal void Close() { Volatile.Write(ref closed, 1); round = default; }
    internal void Freeze() { RequireOpen(); Volatile.Write(ref frozen, 1); }
    private void RequireWritable(int row)
    {
        RequireRow(row);
        if (Volatile.Read(ref frozen) != 0) throw new InvalidOperationException("The raw round is sealed for native preparation.");
    }
    private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
    private void RequireRow(int row)
    {
        RequireSampling();
        if ((uint)row >= round.Count) throw new ArgumentOutOfRangeException(nameof(row));
    }
    private void RequireSampling()
    {
        RequireOpen();
        if (Volatile.Read(ref frozen) != 0) throw new InvalidOperationException("SDK query views expire before native preparation.");
    }
}
