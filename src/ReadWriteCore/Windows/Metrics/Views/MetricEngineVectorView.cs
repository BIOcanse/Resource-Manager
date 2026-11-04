using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.SharedSection;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

/// <summary>One borrowed native-selected engine row. Text expires with its package read scope.</summary>
public readonly ref struct MetricEngineVectorRow
{
    public ulong Identity { get; init; }
    public ulong AdapterLuid { get; init; }
    public uint PhysicalIndex { get; init; }
    public uint EngineIndex { get; init; }
    public uint EngineClass { get; init; }
    public ulong TriggerMetricId { get; init; }
    public uint ScopeIndex { get; init; }
    public ulong ScopeIdentity { get; init; }
    public ulong SourceObservation { get; init; }
    public uint RawHealth { get; init; }
    public uint Reason { get; init; }
    public ReadOnlySpan<byte> AddressUtf8 { get; init; }
    public ReadOnlySpan<byte> DeviceKeyUtf8 { get; init; }
    public MetricValueView Value { get; init; }
}

/// <summary>
/// Direct 21-slot normal-region vector, never a current-value copy. Package owns coherence.
/// Recreate after refresh/dispose. Selection and exact guards are native/caller supplied facts.
/// </summary>
public ref struct MetricEngineVectorView
{
    private SharedSectionReadScope scope;
    private readonly ReadOnlySpan<uint> positions, rows;
    private readonly ReadOnlySpan<ulong> identities, generations;

    public MetricEngineVectorView(SharedSectionReadScope scope, ReadOnlySpan<uint> positions,
        ReadOnlySpan<uint> selectedRows, ReadOnlySpan<ulong> expectedIdentities,
        ReadOnlySpan<ulong> expectedGoodGenerations = default)
    {
        if (positions.Length != 21 || selectedRows.Length != expectedIdentities.Length
            || !expectedGoodGenerations.IsEmpty && expectedGoodGenerations.Length != selectedRows.Length)
            throw new ArgumentException("Engine vector requires21 positions and parallel selection guards.");
        this.scope = scope; this.positions = positions; rows = selectedRows;
        identities = expectedIdentities; generations = expectedGoodGenerations;
    }

    public readonly int Count => rows.Length;
    public MetricViewReadStatus TryRead(int index, out MetricEngineVectorRow value)
        => TryRead(index, out value, out _);

    public MetricViewReadStatus TryRead(int index, out MetricEngineVectorRow value, out MetricDataAreaReadResult detail)
    {
        value = default;
        if ((uint)index >= (uint)Count || rows[index] > int.MaxValue)
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        var row = rows[index];
        detail = Position(0, out var identityColumn); if (!detail.IsAvailable) return detail.Status;
        var count = identityColumn.Length;
        if (identityColumn.Kind != SharedSectionPositionKind.Column || (ulong)identityColumn.Bytes.Length != (ulong)count * 8)
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        if (row >= count) { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
        var identity = identityColumn.Element<ulong>((int)row);
        if (identity == 0 || identities[index] == 0 || identity != identities[index])
        { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
        detail = Element(1, row, count, out ulong luid); if (!detail.IsAvailable) return detail.Status;
        detail = Element(2, row, count, out uint physical); if (!detail.IsAvailable) return detail.Status;
        detail = Element(3, row, count, out uint engine); if (!detail.IsAvailable) return detail.Status;
        detail = Element(4, row, count, out uint engineClass); if (!detail.IsAvailable) return detail.Status;
        detail = Element(6, row, count, out uint kind); if (!detail.IsAvailable) return detail.Status;
        detail = Element(7, row, count, out uint presence); if (!detail.IsAvailable) return detail.Status;
        detail = Element(8, row, count, out ulong generation); if (!detail.IsAvailable) return detail.Status;
        detail = Element(10, row, count, out uint rawHealth); if (!detail.IsAvailable) return detail.Status;
        detail = Element(11, row, count, out uint reason); if (!detail.IsAvailable) return detail.Status;
        detail = Element(12, row, count, out long attemptMs); if (!detail.IsAvailable) return detail.Status;
        detail = Element(13, row, count, out ulong addressRef); if (!detail.IsAvailable) return detail.Status;
        detail = Element(15, row, count, out ulong deviceRef); if (!detail.IsAvailable) return detail.Status;
        detail = Element(16, row, count, out ulong trigger); if (!detail.IsAvailable) return detail.Status;
        detail = Element(17, row, count, out uint scopeIndex); if (!detail.IsAvailable) return detail.Status;
        detail = Element(18, row, count, out ulong scopeIdentity); if (!detail.IsAvailable) return detail.Status;
        detail = Element(19, row, count, out ulong observation); if (!detail.IsAvailable) return detail.Status;
        detail = Element(20, row, count, out uint status); if (!detail.IsAvailable) return detail.Status;
        if (!generations.IsEmpty && generation != generations[index] || presence != 0 && generation == 0)
        { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
        if (kind != 1 || presence > 2 || status > 7 || trigger == 0 || scopeIdentity == 0
            || !MetricTimestamp.TryUtcTicks(attemptMs, MetricTimestampUnit.UnixMilliseconds, out var attempt))
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        detail = Position(14, out var text); if (!detail.IsAvailable) return detail.Status;
        if (text.Kind != SharedSectionPositionKind.StringTable || text.Bytes.Length != text.Length
            || !Text(text.Bytes, addressRef, out var address) || !Text(text.Bytes, deviceRef, out var device))
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        // Even absent/null columns must have valid lengths, but old good bits/times are not exposed.
        detail = Element(5, row, count, out ulong bits); if (!detail.IsAvailable) return detail.Status;
        detail = Element(9, row, count, out long observedMs); if (!detail.IsAvailable) return detail.Status;
        long observed = 0;
        if (presence != 0 && !MetricTimestamp.TryUtcTicks(observedMs, MetricTimestampUnit.UnixMilliseconds, out observed)
            || presence == 1 && !double.IsFinite(BitConverter.UInt64BitsToDouble(bits)))
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        value = new()
        {
            Identity = identity, AdapterLuid = luid, PhysicalIndex = physical, EngineIndex = engine,
            EngineClass = engineClass, TriggerMetricId = trigger, ScopeIndex = scopeIndex,
            ScopeIdentity = scopeIdentity, SourceObservation = observation, RawHealth = rawHealth, Reason = reason,
            AddressUtf8 = address, DeviceKeyUtf8 = device,
            Value = new(MetricScalarKind.Float64, (MetricValuePresence)presence, presence == 1 ? bits : 0,
                0, default, (SamplingObservationStatus)status, generation, observed, 0, attempt, observed)
        };
        detail = new(MetricViewReadStatus.Available, Reason: reason); return detail.Status;
    }

    private MetricDataAreaReadResult Element<T>(int slot, uint row, uint count, out T value) where T : unmanaged
    {
        value = default;
        var result = Position(slot, out var column); if (!result.IsAvailable) return result;
        if (column.Length != count) return new(MetricViewReadStatus.NotArrived);
        if (column.Kind != SharedSectionPositionKind.Column
            || (ulong)column.Bytes.Length != (ulong)count * (uint)Unsafe.SizeOf<T>())
            return new(MetricViewReadStatus.Invalid);
        value = column.Element<T>((int)row); return result;
    }

    private MetricDataAreaReadResult Position(int slot, out SharedSectionPositionView position)
    {
        var result = scope.ReadPosition(positions[slot], out position);
        if (!result.IsSuccess)
            return new(result.Status == SharedSectionReaderStatus.DependencyNotReady ? MetricViewReadStatus.DependencyNotReady
                : result.Failure is SharedSectionFailure.PositionIndex or SharedSectionFailure.NotSelected ? MetricViewReadStatus.Unavailable : MetricViewReadStatus.Failed, result);
        return new(position.Status switch
        {
            SharedSectionPositionStatus.HasValue => MetricViewReadStatus.Available,
            SharedSectionPositionStatus.NotArrived => MetricViewReadStatus.NotArrived,
            SharedSectionPositionStatus.Unavailable => MetricViewReadStatus.Unavailable,
            SharedSectionPositionStatus.Ended => MetricViewReadStatus.Ended,
            _ => MetricViewReadStatus.Failed
        }, result, position.Reason);
    }

    private static bool Text(ReadOnlySpan<byte> table, ulong reference, out ReadOnlySpan<byte> text)
    {
        text = default;
        var offset = (uint)reference; var length = (uint)(reference >> 32);
        if ((ulong)offset + length > (ulong)table.Length || length == 0) return false;
        text = table.Slice((int)offset, (int)length);
        var remaining = text;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
