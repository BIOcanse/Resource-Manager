using ResourceManager.App.Domain.Metrics;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

public enum MetricScalarKind : uint
{
    Float64 = 1, Signed64 = 2, Unsigned64 = 3, Unsigned128 = 4, Bytes = 5, Boolean = 6
}

public enum MetricViewReadStatus { Available, NotArrived, Invalid, Unavailable, Denied, Ended, DependencyNotReady, Failed }
public enum MetricValuePresence : uint { Absent, Present, Null }
public enum MetricTimestampUnit { UtcTicks, UnixMilliseconds }

/// <summary>Borrowed, already coherent selected rule columns. No transport or ownership.</summary>
public readonly ref struct MetricRuleColumns
{
    /// <summary>Direct native storage, with cold rule IDs and no kind/presence/time conversion arrays.</summary>
    public static MetricRuleColumns FromNative(ReadOnlySpan<ulong> ruleIds, ReadOnlySpan<ulong> identities,
        ReadOnlySpan<ulong> generations, ReadOnlySpan<uint> kinds, ReadOnlySpan<uint> presence,
        ReadOnlySpan<ulong> lowBits, ReadOnlySpan<ulong> highBits, ReadOnlySpan<ulong> payloadReferences,
        ReadOnlySpan<long> observedUnixMs, ReadOnlySpan<ulong> durationMilliseconds, ReadOnlySpan<byte> payload)
        => new(ruleIds, identities, generations, MemoryMarshal.Cast<uint, MetricScalarKind>(kinds),
            MemoryMarshal.Cast<uint, MetricValuePresence>(presence), lowBits, highBits, payloadReferences,
            observedUnixMs, durationMilliseconds, payload, MetricTimestampUnit.UnixMilliseconds);

    public MetricRuleColumns(ReadOnlySpan<ulong> ids, ReadOnlySpan<ulong> identities, ReadOnlySpan<ulong> generations,
        ReadOnlySpan<MetricScalarKind> kinds, ReadOnlySpan<MetricValuePresence> presence,
        ReadOnlySpan<ulong> lowBits, ReadOnlySpan<ulong> highBits,
        ReadOnlySpan<ulong> payloadReferences,
        ReadOnlySpan<long> observedAtUtcTicks, ReadOnlySpan<ulong> durationMilliseconds,
        ReadOnlySpan<byte> payload, MetricTimestampUnit timestampUnit = MetricTimestampUnit.UtcTicks)
    {
        var count = identities.Length;
        if (ids.Length != count || generations.Length != count || kinds.Length != count || presence.Length != count
            || lowBits.Length != count || highBits.Length != count || payloadReferences.Length != count
            || observedAtUtcTicks.Length != count || durationMilliseconds.Length != count)
            throw new ArgumentException("Selected rule column lengths differ.");
        Ids = ids; Identities = identities; Generations = generations; Kinds = kinds; Presence = presence;
        LowBits = lowBits; HighBits = highBits; PayloadReferences = payloadReferences;
        ObservedAtUtcTicks = observedAtUtcTicks; DurationMilliseconds = durationMilliseconds; Payload = payload;
        TimestampUnit = timestampUnit;
    }

    public ReadOnlySpan<ulong> Ids { get; }
    public ReadOnlySpan<ulong> Identities { get; }
    public ReadOnlySpan<ulong> Generations { get; }
    public ReadOnlySpan<MetricScalarKind> Kinds { get; }
    public ReadOnlySpan<MetricValuePresence> Presence { get; }
    public ReadOnlySpan<ulong> LowBits { get; }
    public ReadOnlySpan<ulong> HighBits { get; }
    // Native committed encoding: offset low32, length high32; no split mirror columns.
    public ReadOnlySpan<ulong> PayloadReferences { get; }
    public ReadOnlySpan<long> ObservedAtUtcTicks { get; }
    public ReadOnlySpan<ulong> DurationMilliseconds { get; }
    public ReadOnlySpan<byte> Payload { get; }
    public MetricTimestampUnit TimestampUnit { get; }
}

/// <summary>Selected result references and health, supplied by native without managed arbitration.</summary>
public readonly ref struct MetricReferenceColumns
{
    /// <summary>
    /// Native public state is authoritative. Never pass diagnostic raw MetricStatus in publicStatuses.
    /// The retained ID is the native-decided public reference, including explicit-null good values.
    /// </summary>
    public static MetricReferenceColumns FromNative(ReadOnlySpan<ulong> metricIdentities,
        ReadOnlySpan<ulong> retainedRuleIds, ReadOnlySpan<ulong> expectedRuleIdentities,
        ReadOnlySpan<ulong> expectedValueGenerations, ReadOnlySpan<uint> declaredKinds,
        ReadOnlySpan<uint> publicStatuses, ReadOnlySpan<ulong> resultGenerations,
        ReadOnlySpan<long> attemptUnixMs, ReadOnlySpan<int> ruleIndices = default,
        ReadOnlySpan<ulong> selectedRuleIds = default)
        => new(metricIdentities, ruleIndices, retainedRuleIds, expectedRuleIdentities, expectedValueGenerations,
            MemoryMarshal.Cast<uint, MetricScalarKind>(declaredKinds), publicStatuses, resultGenerations, attemptUnixMs, selectedRuleIds);

    private MetricReferenceColumns(ReadOnlySpan<ulong> identities, ReadOnlySpan<int> ruleIndices,
        ReadOnlySpan<ulong> ruleIds, ReadOnlySpan<ulong> ruleIdentities, ReadOnlySpan<ulong> valueGenerations,
        ReadOnlySpan<MetricScalarKind> kinds, ReadOnlySpan<uint> publicStatuses,
        ReadOnlySpan<ulong> generations, ReadOnlySpan<long> attemptUnixMs, ReadOnlySpan<ulong> selectedRuleIds)
    {
        var count = identities.Length;
        if ((!ruleIndices.IsEmpty && ruleIndices.Length != count) || ruleIds.Length != count
            || ruleIdentities.Length != count || valueGenerations.Length != count || kinds.Length != count
            || publicStatuses.Length != count || generations.Length != count || attemptUnixMs.Length != count
            || (!selectedRuleIds.IsEmpty && selectedRuleIds.Length != count))
            throw new ArgumentException("Native selected metric column lengths differ.");
        Identities = identities; RuleIndices = ruleIndices; RuleIds = ruleIds; RuleIdentities = ruleIdentities;
        ValueGenerations = valueGenerations; Kinds = kinds; Statuses = default; PublicStatuses = publicStatuses;
        SourceGenerations = generations; AttemptAtUtcTicks = attemptUnixMs; SuccessAtUtcTicks = default;
        TimestampUnit = MetricTimestampUnit.UnixMilliseconds; SuccessFromReferencedRule = true;
        SelectedRuleIds = selectedRuleIds;
    }

    public MetricReferenceColumns(ReadOnlySpan<ulong> identities, ReadOnlySpan<int> ruleIndices,
        ReadOnlySpan<ulong> ruleIds, ReadOnlySpan<ulong> ruleIdentities, ReadOnlySpan<ulong> valueGenerations,
        ReadOnlySpan<MetricScalarKind> kinds, ReadOnlySpan<SamplingObservationStatus> statuses,
        ReadOnlySpan<ulong> sourceGenerations, ReadOnlySpan<long> attemptAtUtcTicks,
        ReadOnlySpan<long> successAtUtcTicks, MetricTimestampUnit timestampUnit = MetricTimestampUnit.UtcTicks)
    {
        var count = identities.Length;
        if (!ruleIndices.IsEmpty && ruleIndices.Length != count || ruleIds.Length != count
            || ruleIdentities.Length != count || valueGenerations.Length != count
            || kinds.Length != count || statuses.Length != count || sourceGenerations.Length != count
            || attemptAtUtcTicks.Length != count || successAtUtcTicks.Length != count)
            throw new ArgumentException("Selected metric column lengths differ.");
        Identities = identities; RuleIndices = ruleIndices; RuleIds = ruleIds; RuleIdentities = ruleIdentities;
        ValueGenerations = valueGenerations; Kinds = kinds; Statuses = statuses;
        SourceGenerations = sourceGenerations; AttemptAtUtcTicks = attemptAtUtcTicks; SuccessAtUtcTicks = successAtUtcTicks;
        TimestampUnit = timestampUnit;
        PublicStatuses = default; SuccessFromReferencedRule = false;
        SelectedRuleIds = default;
    }
    public ReadOnlySpan<ulong> Identities { get; }
    public ReadOnlySpan<int> RuleIndices { get; }
    public ReadOnlySpan<ulong> RuleIds { get; }
    public ReadOnlySpan<ulong> SelectedRuleIds { get; }
    public ReadOnlySpan<ulong> RuleIdentities { get; }
    public ReadOnlySpan<ulong> ValueGenerations { get; }
    public ReadOnlySpan<MetricScalarKind> Kinds { get; }
    public ReadOnlySpan<SamplingObservationStatus> Statuses { get; }
    public ReadOnlySpan<uint> PublicStatuses { get; }
    public ReadOnlySpan<ulong> SourceGenerations { get; }
    public ReadOnlySpan<long> AttemptAtUtcTicks { get; }
    public ReadOnlySpan<long> SuccessAtUtcTicks { get; }
    public MetricTimestampUnit TimestampUnit { get; }
    public bool SuccessFromReferencedRule { get; }
    internal uint PublicStatus(int index) => SuccessFromReferencedRule ? PublicStatuses[index] : (uint)Statuses[index];
}

public readonly ref struct MetricValueView
{
    internal MetricValueView(MetricScalarKind kind, MetricValuePresence presence, ulong low, ulong high,
        ReadOnlySpan<byte> bytes, SamplingObservationStatus status, ulong generation,
        long observedAt, ulong duration, long attemptAt, long successAt)
    {
        Kind = kind; Presence = presence; LowBits = low; HighBits = high; Bytes = bytes;
        Status = status; SourceGeneration = generation; ObservedAtUtcTicks = observedAt;
        DurationMilliseconds = duration; LastAttemptAtUtcTicks = attemptAt; LastSuccessAtUtcTicks = successAt;
    }
    public MetricScalarKind Kind { get; }
    public MetricValuePresence Presence { get; }
    public bool HasValue => Presence == MetricValuePresence.Present;
    public ulong LowBits { get; }
    public ulong HighBits { get; }
    public ReadOnlySpan<byte> Bytes { get; }
    public SamplingObservationStatus Status { get; }
    public ulong SourceGeneration { get; }
    public long ObservedAtUtcTicks { get; }
    public ulong DurationMilliseconds { get; }
    public long LastAttemptAtUtcTicks { get; }
    public long LastSuccessAtUtcTicks { get; }
    public double? Float64 => HasValue && Kind == MetricScalarKind.Float64 ? BitConverter.UInt64BitsToDouble(LowBits) : null;
    public long? Signed64 => HasValue && Kind == MetricScalarKind.Signed64 ? unchecked((long)LowBits) : null;
    public ulong? Unsigned64 => HasValue && Kind == MetricScalarKind.Unsigned64 ? LowBits : null;
    public UInt128? Unsigned128 => HasValue && Kind == MetricScalarKind.Unsigned128 ? ((UInt128)HighBits << 64) | LowBits : null;
    public bool? Boolean => HasValue && Kind == MetricScalarKind.Boolean ? LowBits == 1 : null;
}

/// <summary>Stack-only view over bounded caller-supplied columns; expires with their read scope.</summary>
public readonly ref struct MetricCurrentView
{
    private readonly MetricReferenceColumns metrics;
    private readonly MetricRuleColumns rules;
    public MetricCurrentView(MetricReferenceColumns metrics, MetricRuleColumns rules)
    { this.metrics = metrics; this.rules = rules; }

    public int Count => metrics.Identities.Length;
    public ulong Identity(int index) => metrics.Identities[index];
    public MetricScalarKind Kind(int index) => metrics.Kinds[index];
    public SamplingObservationStatus Health(int index) => metrics.PublicStatus(index) <= (uint)SamplingObservationStatus.Warming
        ? (SamplingObservationStatus)metrics.PublicStatus(index) : throw new InvalidDataException("Invalid metric public status.");

    public MetricViewReadStatus TryRead(int index, out MetricValueView value)
    {
        value = default;
        if ((uint)index >= (uint)Count) return MetricViewReadStatus.Invalid;
        var rawStatus = metrics.PublicStatus(index);
        if (metrics.Identities[index] == 0 || rawStatus > (uint)SamplingObservationStatus.Warming)
            return MetricViewReadStatus.Invalid;
        var status = (SamplingObservationStatus)rawStatus;
        var kind = metrics.Kinds[index];
        if (kind is < MetricScalarKind.Float64 or > MetricScalarKind.Boolean) return MetricViewReadStatus.Invalid;
        var success = 0L;
        if (!MetricTimestamp.TryUtcTicks(metrics.AttemptAtUtcTicks[index], metrics.TimestampUnit, out var attempt)
            || !metrics.SuccessFromReferencedRule
                && !MetricTimestamp.TryUtcTicks(metrics.SuccessAtUtcTicks[index], metrics.TimestampUnit, out success))
            return MetricViewReadStatus.Invalid;
        if (metrics.RuleIds[index] == 0)
            return ReadAbsence(index, kind, status, attempt, out value);
        // A cold/bound local index is optional. Native rule-ID references can also be joined directly
        // against bounded selected IDs, without constructing a managed index/value mirror.
        var row = metrics.RuleIndices.IsEmpty
            ? rules.Ids.IndexOf(metrics.RuleIds[index]) : metrics.RuleIndices[index];
        if ((uint)row >= (uint)rules.Identities.Length
            || rules.Ids[row] != metrics.RuleIds[index]
            || rules.Identities[row] != metrics.RuleIdentities[index]
            || rules.Generations[row] != metrics.ValueGenerations[index])
            return MetricViewReadStatus.NotArrived;
        if (rules.Ids[row] == 0 || rules.Identities[row] == 0) return MetricViewReadStatus.Invalid;
        if (rules.Kinds[row] != kind || rules.Presence[row] > MetricValuePresence.Null
            || !MetricTimestamp.TryUtcTicks(rules.ObservedAtUtcTicks[row], rules.TimestampUnit, out var observed))
            return MetricViewReadStatus.Invalid;
        var present = rules.Presence[row] == MetricValuePresence.Present;
        if (metrics.SuccessFromReferencedRule) success = observed;
        ReadOnlySpan<byte> bytes = default;
        if (present && kind == MetricScalarKind.Bytes)
        {
            var payload = rules.PayloadReferences[row];
            var offset = (uint)payload; var length = (uint)(payload >> 32);
            if ((ulong)offset + length > (ulong)rules.Payload.Length) return MetricViewReadStatus.Invalid;
            bytes = rules.Payload.Slice((int)offset, (int)length);
        }
        if (present && kind == MetricScalarKind.Boolean && rules.LowBits[row] > 1) return MetricViewReadStatus.Invalid;
        value = new(kind, rules.Presence[row], present ? rules.LowBits[row] : 0, present ? rules.HighBits[row] : 0, bytes,
            status, metrics.SourceGenerations[index], observed,
            rules.DurationMilliseconds[row], attempt, success);
        return MetricViewReadStatus.Available;
    }

    private MetricViewReadStatus ReadAbsence(int index, MetricScalarKind kind, SamplingObservationStatus status,
        long attempt, out MetricValueView value)
    {
        value = default;
        var selectedId = metrics.SelectedRuleIds.IsEmpty ? 0 : metrics.SelectedRuleIds[index];
        var identity = metrics.RuleIdentities[index]; var generation = metrics.ValueGenerations[index];
        if (selectedId != 0 || identity != 0 || generation != 0)
        {
            // This is a definition guard, not permission to expose the selected row's old good value.
            if (identity == 0 || generation == 0) return MetricViewReadStatus.NotArrived;
            var row = metrics.RuleIndices.IsEmpty ? selectedId == 0 ? -1 : rules.Ids.IndexOf(selectedId)
                : metrics.RuleIndices[index];
            if ((uint)row >= (uint)rules.Ids.Length || rules.Ids[row] == 0
                || (selectedId != 0 && rules.Ids[row] != selectedId) || rules.Identities[row] != identity
                || rules.Generations[row] != generation || rules.Kinds[row] != kind)
                return MetricViewReadStatus.NotArrived;
        }
        value = new(kind, MetricValuePresence.Absent, 0, 0, default, status, metrics.SourceGenerations[index],
            0, 0, attempt, 0);
        return MetricViewReadStatus.Available;
    }
}

internal static class MetricTimestamp
{
    internal static long UtcTicks(long value, MetricTimestampUnit unit)
        => TryUtcTicks(value, unit, out var ticks) ? ticks : throw new InvalidDataException("Invalid metric timestamp.");

    internal static bool TryUtcTicks(long value, MetricTimestampUnit unit, out long ticks)
    {
        ticks = 0;
        if (unit == MetricTimestampUnit.UtcTicks)
        {
            if (value < 0 || value > DateTime.MaxValue.Ticks) return false;
            ticks = value; return true;
        }
        if (unit != MetricTimestampUnit.UnixMilliseconds || value < -62135596800000L || value > 253402300799999L)
            return false;
        ticks = value * TimeSpan.TicksPerMillisecond + DateTime.UnixEpoch.Ticks;
        return true;
    }
}
