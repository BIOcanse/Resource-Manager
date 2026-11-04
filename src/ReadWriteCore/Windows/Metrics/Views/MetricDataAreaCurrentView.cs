using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.SharedSection;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

public enum MetricDataAreaRegion { Normal, Administrator }

/// <summary>Cold creator binding: region and package position indices, never addresses or owned mappings.</summary>
public readonly struct MetricDataAreaTableBinding
{
    private readonly uint p0, p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15;
    public MetricDataAreaRegion Region { get; }
    public MetricDataAreaTableBinding(MetricDataAreaRegion region, ReadOnlySpan<uint> positions)
    {
        if (positions.Length != 16) throw new ArgumentException("Native table binding requires sixteen position slots.");
        Region = region;
        p0 = positions[0]; p1 = positions[1]; p2 = positions[2]; p3 = positions[3];
        p4 = positions[4]; p5 = positions[5]; p6 = positions[6]; p7 = positions[7];
        p8 = positions[8]; p9 = positions[9]; p10 = positions[10]; p11 = positions[11];
        p12 = positions[12]; p13 = positions[13]; p14 = positions[14]; p15 = positions[15];
    }
    public uint Position(int slot) => slot switch
    {
        0 => p0, 1 => p1, 2 => p2, 3 => p3, 4 => p4, 5 => p5, 6 => p6, 7 => p7,
        8 => p8, 9 => p9, 10 => p10, 11 => p11, 12 => p12, 13 => p13, 14 => p14, 15 => p15,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}

public readonly record struct MetricDataAreaReadResult(MetricViewReadStatus Status,
    SharedSectionReaderResult ReaderResult = default, uint Reason = 0)
{
    public bool IsAvailable => Status == MetricViewReadStatus.Available;
}

/// <summary>Cold object materialization failure preserves the direct read outcome; never serves an old object.</summary>
public sealed class MetricDataAreaReadException(MetricDataAreaReadResult result)
    : IOException("Metric data-area input is not available: " + result.Status)
{
    public MetricDataAreaReadResult Result { get; } = result;
}

/// <summary>
/// Borrowed direct result-to-origin read. Package scopes own coherence; neither scope nor storage is acquired here.
/// Recreate after scope refresh/dispose. Bindings are cold; selection indexes result rows without any catalog scan.
/// Cached address selections pass expected metric identities; omission means a read by the current row index.
/// </summary>
public ref struct MetricDataAreaCurrentView
{
    private SharedSectionReadScope normal;
    private SharedSectionReadScope administrator;
    private readonly bool hasAdministrator;
    private readonly MetricDataAreaTableBinding results;
    private readonly ReadOnlySpan<MetricDataAreaTableBinding> resultGroups;
    private readonly ReadOnlySpan<uint> selectedResultGroups;
    private readonly ReadOnlySpan<MetricDataAreaTableBinding> origins;
    private readonly ReadOnlySpan<int> selection;
    private readonly ReadOnlySpan<uint> kinds;
    private readonly ReadOnlySpan<ulong> expectedMetricIdentities;

    public MetricDataAreaCurrentView(SharedSectionReadScope normal, SharedSectionReadScope administrator,
        bool hasAdministrator, MetricDataAreaTableBinding results, ReadOnlySpan<MetricDataAreaTableBinding> origins,
        ReadOnlySpan<int> selectedResultRows, ReadOnlySpan<uint> declaredKinds,
        ReadOnlySpan<ulong> expectedMetricIdentities = default)
    {
        if (selectedResultRows.Length != declaredKinds.Length
            || !expectedMetricIdentities.IsEmpty && expectedMetricIdentities.Length != selectedResultRows.Length)
            throw new ArgumentException("Selected result rows and declared kinds differ.");
        this.normal = normal; this.administrator = administrator; this.hasAdministrator = hasAdministrator;
        this.results = results; this.origins = origins; selection = selectedResultRows; kinds = declaredKinds;
        resultGroups = default; selectedResultGroups = default;
        this.expectedMetricIdentities = expectedMetricIdentities;
    }

    public MetricDataAreaCurrentView(SharedSectionReadScope normal, SharedSectionReadScope administrator,
        bool hasAdministrator, ReadOnlySpan<MetricDataAreaTableBinding> resultGroups,
        ReadOnlySpan<MetricDataAreaTableBinding> origins, ReadOnlySpan<uint> selectedResultGroups,
        ReadOnlySpan<int> selectedResultRows, ReadOnlySpan<uint> declaredKinds,
        ReadOnlySpan<ulong> expectedMetricIdentities = default)
    {
        if (resultGroups.Length != 2 || resultGroups[0].Region != MetricDataAreaRegion.Normal
            || resultGroups[1].Region != MetricDataAreaRegion.Administrator)
            throw new ArgumentException("Result groups must be ordered normal and administrator bindings.");
        if (selectedResultRows.Length != declaredKinds.Length || selectedResultGroups.Length != selectedResultRows.Length
            || !expectedMetricIdentities.IsEmpty && expectedMetricIdentities.Length != selectedResultRows.Length)
            throw new ArgumentException("Selected result groups, rows and declared kinds differ.");
        this.normal = normal; this.administrator = administrator; this.hasAdministrator = hasAdministrator;
        this.resultGroups = resultGroups; this.selectedResultGroups = selectedResultGroups;
        results = default; this.origins = origins; selection = selectedResultRows; kinds = declaredKinds;
        this.expectedMetricIdentities = expectedMetricIdentities;
    }

    public readonly int Count => selection.Length;
    public readonly MetricScalarKind Kind(int selectedIndex)
    {
        if ((uint)selectedIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        var outcome = ResultBinding(selectedIndex, out _);
        if (!outcome.IsAvailable) throw new MetricDataAreaReadException(outcome);
        return (MetricScalarKind)kinds[selectedIndex];
    }
    public ulong Identity(int selectedIndex)
    {
        if ((uint)selectedIndex >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        var outcome = ResultBinding(selectedIndex, out var binding);
        if (!outcome.IsAvailable) throw new MetricDataAreaReadException(outcome);
        outcome = Element(binding, 0, (uint)selection[selectedIndex], out ulong identity);
        if (outcome.IsAvailable && !expectedMetricIdentities.IsEmpty && identity != expectedMetricIdentities[selectedIndex])
            outcome = new(MetricViewReadStatus.NotArrived);
        return outcome.IsAvailable ? identity : throw new MetricDataAreaReadException(outcome);
    }
    public MetricViewReadStatus TryRead(int selectedIndex, out MetricValueView value)
        => TryRead(selectedIndex, out value, out _);

    public MetricViewReadStatus TryRead(int selectedIndex, out MetricValueView value, out MetricDataAreaReadResult detail)
    {
        value = default;
        if ((uint)selectedIndex >= (uint)Count || selection[selectedIndex] < 0 || kinds[selectedIndex] is < 1 or > 6)
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        if (!expectedMetricIdentities.IsEmpty && expectedMetricIdentities[selectedIndex] == 0)
        { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
        detail = ResultBinding(selectedIndex, out var binding);
        if (!detail.IsAvailable) return detail.Status;
        return TryReadRow(binding, (uint)selection[selectedIndex], (MetricScalarKind)kinds[selectedIndex],
            expectedMetricIdentities.IsEmpty ? 0 : expectedMetricIdentities[selectedIndex], out value, out detail);
    }

    private readonly MetricDataAreaReadResult ResultBinding(int selectedIndex, out MetricDataAreaTableBinding binding)
    {
        binding = default;
        if ((uint)selectedIndex >= (uint)Count || selection[selectedIndex] < 0)
            return new(MetricViewReadStatus.Invalid);
        if (resultGroups.IsEmpty) binding = results;
        else
        {
            var group = selectedResultGroups[selectedIndex];
            if (group >= (uint)resultGroups.Length) return new(MetricViewReadStatus.Invalid);
            binding = resultGroups[(int)group];
        }
        return new(MetricViewReadStatus.Available);
    }

    private MetricViewReadStatus TryReadRow(MetricDataAreaTableBinding results, uint row, MetricScalarKind kind,
        ulong expectedMetricIdentity, out MetricValueView value,
        out MetricDataAreaReadResult detail)
    {
        value = default;
        detail = Element(results, 0, row, out ulong metricIdentity); if (!detail.IsAvailable) return detail.Status;
        if (expectedMetricIdentity != 0 && expectedMetricIdentity != metricIdentity)
        { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
        detail = Element(results, 2, row, out ulong retainedId); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 3, row, out ulong expectedGeneration); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 8, row, out ulong expectedIdentity); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 9, row, out uint sourceIndex); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 10, row, out uint localRow); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 11, row, out uint publicStatus); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 5, row, out uint reason); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 6, row, out long attemptMs); if (!detail.IsAvailable) return detail.Status;
        detail = Element(results, 7, row, out ulong generation); if (!detail.IsAvailable) return detail.Status;
        if (metricIdentity == 0 || publicStatus > 7 || !MetricTimestamp.TryUtcTicks(attemptMs, MetricTimestampUnit.UnixMilliseconds, out var attempt))
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        if (retainedId == 0)
        {
            if (sourceIndex != uint.MaxValue || localRow != uint.MaxValue || expectedIdentity != 0 || expectedGeneration != 0)
            {
                // Native can locate the selected rule solely to define typed absence.
                // It is not a retained value reference and must never expose its good bits.
                if (sourceIndex >= (uint)origins.Length || localRow == uint.MaxValue || expectedIdentity == 0 || expectedGeneration == 0)
                { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
                detail = Element(results, 1, row, out ulong selectedId); if (!detail.IsAvailable) return detail.Status;
                var selectedOrigin = origins[(int)sourceIndex];
                detail = Element(selectedOrigin, 15, localRow, out ulong selectedActualId); if (!detail.IsAvailable) return detail.Status;
                detail = Element(selectedOrigin, 0, localRow, out ulong selectedIdentity); if (!detail.IsAvailable) return detail.Status;
                detail = Element(selectedOrigin, 5, localRow, out ulong selectedGeneration); if (!detail.IsAvailable) return detail.Status;
                detail = Element(selectedOrigin, 3, localRow, out uint selectedKind); if (!detail.IsAvailable) return detail.Status;
                if (selectedId == 0 || selectedActualId != selectedId || selectedIdentity != expectedIdentity
                    || selectedGeneration != expectedGeneration || selectedKind != (uint)kind)
                { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
            }
            value = new(kind, MetricValuePresence.Absent, 0, 0, default, (SamplingObservationStatus)publicStatus,
                generation, 0, 0, attempt, 0);
            detail = new(MetricViewReadStatus.Available, Reason: reason); return detail.Status;
        }
        if (sourceIndex >= (uint)origins.Length)
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        var origin = origins[(int)sourceIndex];
        detail = Element(origin, 15, localRow, out ulong actualId); if (!detail.IsAvailable) return detail.Status;
        detail = Element(origin, 0, localRow, out ulong actualIdentity); if (!detail.IsAvailable) return detail.Status;
        detail = Element(origin, 5, localRow, out ulong actualGeneration); if (!detail.IsAvailable) return detail.Status;
        if (actualId != retainedId || actualIdentity != expectedIdentity || actualGeneration != expectedGeneration)
        { detail = new(MetricViewReadStatus.NotArrived); return detail.Status; }
        detail = Element(origin, 3, localRow, out uint actualKind); if (!detail.IsAvailable) return detail.Status;
        detail = Element(origin, 4, localRow, out uint presence); if (!detail.IsAvailable) return detail.Status;
        detail = Element(origin, 6, localRow, out long observedMs); if (!detail.IsAvailable) return detail.Status;
        detail = Element(origin, 7, localRow, out ulong duration); if (!detail.IsAvailable) return detail.Status;
        if (actualKind != (uint)kind || presence > 2
            || !MetricTimestamp.TryUtcTicks(observedMs, MetricTimestampUnit.UnixMilliseconds, out var observed))
        { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
        ulong low = 0, high = 0;
        ReadOnlySpan<byte> bytes = default;
        if (presence == 1)
        {
            detail = Element(origin, 1, localRow, out low); if (!detail.IsAvailable) return detail.Status;
            detail = Element(origin, 2, localRow, out high); if (!detail.IsAvailable) return detail.Status;
            if (kind == MetricScalarKind.Boolean && low > 1)
            { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
            if (kind == MetricScalarKind.Bytes)
            {
                detail = Element(origin, 8, localRow, out ulong payload); if (!detail.IsAvailable) return detail.Status;
                detail = Position(origin, 9, out var table); if (!detail.IsAvailable) return detail.Status;
                var offset = (uint)payload; var length = (uint)(payload >> 32);
                if (table.Kind != SharedSectionPositionKind.StringTable || (ulong)offset + length > (ulong)table.Bytes.Length)
                { detail = new(MetricViewReadStatus.Invalid); return detail.Status; }
                bytes = table.Bytes.Slice((int)offset, (int)length);
            }
        }
        value = new(kind, (MetricValuePresence)presence, low, high, bytes, (SamplingObservationStatus)publicStatus,
            generation, observed, duration, attempt, observed);
        detail = new(MetricViewReadStatus.Available, Reason: reason); return detail.Status;
    }

    private MetricDataAreaReadResult Element<T>(MetricDataAreaTableBinding table, int slot, uint row, out T value) where T : unmanaged
    {
        value = default;
        var result = Position(table, slot, out var position);
        if (!result.IsAvailable) return result;
        if (row >= position.Length || row > int.MaxValue) return new(MetricViewReadStatus.NotArrived);
        if (position.Kind != SharedSectionPositionKind.Column || (ulong)position.Bytes.Length != (ulong)position.Length * (uint)Unsafe.SizeOf<T>())
            return new(MetricViewReadStatus.Invalid);
        value = position.Element<T>((int)row); return result;
    }

    private MetricDataAreaReadResult Position(MetricDataAreaTableBinding binding, int slot, out SharedSectionPositionView position)
    {
        position = default;
        if (binding.Region == MetricDataAreaRegion.Administrator && !hasAdministrator) return new(MetricViewReadStatus.Denied);
        if (binding.Region is not (MetricDataAreaRegion.Normal or MetricDataAreaRegion.Administrator)) return new(MetricViewReadStatus.Invalid);
        var result = binding.Region == MetricDataAreaRegion.Normal
            ? normal.ReadPosition(binding.Position(slot), out position)
            : administrator.ReadPosition(binding.Position(slot), out position);
        if (!result.IsSuccess)
            return new(result.Status == SharedSectionReaderStatus.DependencyNotReady ? MetricViewReadStatus.DependencyNotReady
                : result.Status == SharedSectionReaderStatus.NativeFailure && result.Failure is (SharedSectionFailure.PositionIndex or SharedSectionFailure.NotSelected)
                    ? MetricViewReadStatus.Unavailable : MetricViewReadStatus.Failed, result);
        return new(position.Status switch
        {
            SharedSectionPositionStatus.HasValue => MetricViewReadStatus.Available,
            SharedSectionPositionStatus.NotArrived => MetricViewReadStatus.NotArrived,
            SharedSectionPositionStatus.Unavailable => MetricViewReadStatus.Unavailable,
            SharedSectionPositionStatus.Ended => MetricViewReadStatus.Ended,
            _ => MetricViewReadStatus.Failed
        }, result, position.Reason);
    }
}
