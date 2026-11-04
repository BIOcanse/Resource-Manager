using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Native.SharedSection;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public enum MetricOwnerSupplementalLocation { Catalog, GpuInventory, GpuVector, History, MixedHistory }

/// <summary>
/// Cold position declarations for the maintenance region owner. Capacities come
/// from native measurement/admitted demand. This type never creates a region,
/// assigns a global offset, acquires memory, or publishes data.
/// </summary>
public readonly struct MetricOwnerSupplementalDeclaration
{
    public MetricOwnerSupplementalLocation Location { get; }
    public MetricOwnerAccessRegion Region { get; }
    public uint Rows { get; }
    public uint Samples { get; }
    public uint TextBytes { get; }
    public int PositionCount => Location switch
    {
        MetricOwnerSupplementalLocation.Catalog => 7,
        MetricOwnerSupplementalLocation.GpuInventory => 14,
        MetricOwnerSupplementalLocation.GpuVector => 21,
        MetricOwnerSupplementalLocation.History => 9,
        _ => 13
    };
    public int SourceCount => Location switch
    {
        MetricOwnerSupplementalLocation.GpuVector => 3,
        MetricOwnerSupplementalLocation.History or MetricOwnerSupplementalLocation.MixedHistory => 2,
        _ => 1
    };

    private MetricOwnerSupplementalDeclaration(MetricOwnerSupplementalLocation location,
        MetricOwnerAccessRegion region, uint rows, uint samples, uint textBytes)
    { Location = location; Region = region; Rows = rows; Samples = samples; TextBytes = textBytes; }

    public static MetricOwnerSupplementalDeclaration Catalog(in MetricDiscoveryCatalogDeclaration measured)
        => new(MetricOwnerSupplementalLocation.Catalog, MetricOwnerAccessRegion.Normal, measured.Rows, 0, measured.StringBytes);
    public static MetricOwnerSupplementalDeclaration GpuInventory(uint rows, uint textBytes)
        => new(MetricOwnerSupplementalLocation.GpuInventory, MetricOwnerAccessRegion.Normal, rows, 0, textBytes);
    public static MetricOwnerSupplementalDeclaration GpuVector(uint rows, uint textBytes)
        => new(MetricOwnerSupplementalLocation.GpuVector, MetricOwnerAccessRegion.Normal, rows, 0, textBytes);
    public static MetricOwnerSupplementalDeclaration History(MetricOwnerAccessRegion region, uint rows, uint samples)
        => new(MetricOwnerSupplementalLocation.History, region, rows, samples, 0);
    public static MetricOwnerSupplementalDeclaration MixedHistory(uint rows, uint samples, uint addressBytes)
        => new(MetricOwnerSupplementalLocation.MixedHistory, MetricOwnerAccessRegion.Normal, rows, samples, addressBytes);

    public MetricOwnerPositionDeclaration Position(uint slot)
    {
        if (slot >= PositionCount) throw new ArgumentOutOfRangeException(nameof(slot));
        var width = 8U;
        var capacity = Rows;
        var writer = MetricOwnerWriterRole.Rows;
        var kind = SharedSectionPositionKind.Column;
        var identity = slot == 0 ? MetricOwnerAbi.None : 0U;
        var strings = MetricOwnerAbi.None;
        switch (Location)
        {
            case MetricOwnerSupplementalLocation.Catalog:
                width = slot is 1 or 2 or 3 ? 4U : slot == 6 ? 1U : 8U;
                if (slot is 4 or 5) { kind = SharedSectionPositionKind.StringColumn; strings = 6; }
                if (slot == 6) { kind = SharedSectionPositionKind.StringTable; capacity = TextBytes; identity = MetricOwnerAbi.None; }
                break;
            case MetricOwnerSupplementalLocation.GpuInventory:
                width = slot == 10 ? 4U : slot == 13 ? 1U : 8U;
                if (slot == 12) { kind = SharedSectionPositionKind.StringColumn; strings = 13; }
                if (slot == 13) { kind = SharedSectionPositionKind.StringTable; capacity = TextBytes; identity = MetricOwnerAbi.None; }
                break;
            case MetricOwnerSupplementalLocation.GpuVector:
                width = slot is 2 or 3 or 4 or 6 or 7 or 10 or 11 or 17 or 20 ? 4U : slot == 14 ? 1U : 8U;
                writer = slot <= 4 || slot is >= 13 and <= 18 ? MetricOwnerWriterRole.Rows
                    : slot is >= 10 and <= 12 || slot == 20 ? MetricOwnerWriterRole.Health : MetricOwnerWriterRole.Values;
                if (slot == 14) { kind = SharedSectionPositionKind.StringTable; capacity = TextBytes; identity = MetricOwnerAbi.None; }
                break;
            default:
                writer = slot == 0 ? MetricOwnerWriterRole.Rows : MetricOwnerWriterRole.Values;
                width = slot is 0 or 1 or 6 or 7 or 10 ? 8U : slot == 11 ? 1U : 4U;
                if (slot is >= 6 and <= 8) { capacity = Samples; identity = MetricOwnerAbi.None; }
                if (slot == 11) { kind = SharedSectionPositionKind.StringTable; capacity = TextBytes; identity = MetricOwnerAbi.None; }
                break;
        }
        var result = new MetricOwnerPositionDeclaration(slot, kind, width, capacity, writer, identity) { StringsSlot = strings };
        if (result.ByteCapacity > uint.MaxValue)
            throw new MetricOwnerException(new() { Code = MetricOwnerCode.CapacityExceeded,
                Family = MetricOwnerFamily.Binding, Required = result.ByteCapacity, Limit = uint.MaxValue });
        return result;
    }

    public unsafe MetricVectorBinding BindGpuVector(byte* region, ulong length, ulong sourceId,
        ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        Require(MetricOwnerSupplementalLocation.GpuVector, sources, positions);
        var result = new MetricVectorBinding { Region = region, ByteLength = length, SourceId = sourceId,
            RowsSource = sources[0], ValueSource = sources[1], HealthSource = sources[2] };
        for (var i = 0; i < PositionCount; i++) result.Positions[i] = positions[i];
        return result;
    }
    public unsafe MetricDiscoveryInventoryBinding BindGpuInventory(byte* region, ulong length,
        ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        Require(MetricOwnerSupplementalLocation.GpuInventory, sources, positions);
        var result = new MetricDiscoveryInventoryBinding { Region = region, Length = length, Source = sources[0] };
        for (var i = 0; i < PositionCount; i++) result.Positions[i] = positions[i];
        return result;
    }
    public unsafe MetricDiscoveryCatalogBinding BindCatalog(byte* region, ulong length,
        ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        Require(MetricOwnerSupplementalLocation.Catalog, sources, positions);
        var result = new MetricDiscoveryCatalogBinding { Region = region, Length = length, Source = sources[0] };
        for (var i = 0; i < PositionCount; i++) result.Positions[i] = positions[i];
        return result;
    }
    public unsafe MetricOwnerHistoryBinding BindHistory(byte* region, ulong length,
        ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        Require(MetricOwnerSupplementalLocation.History, sources, positions);
        return HistoryBinding(region, length, sources, positions);
    }
    public unsafe MetricOwnerMixedHistoryBinding BindMixedHistory(byte* region, ulong length,
        ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        Require(MetricOwnerSupplementalLocation.MixedHistory, sources, positions);
        return new() { Base = HistoryBinding(region, length, sources, positions),
            KindPosition = positions[9], AddressPosition = positions[10], TextPosition = positions[11], LivePosition = positions[12],
            Version = MetricOwnerSupportAbi.MixedHistoryVersion };
    }
    private static unsafe MetricOwnerHistoryBinding HistoryBinding(byte* region, ulong length,
        ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        var result = new MetricOwnerHistoryBinding { Region = region, ByteLength = length, RowsSource = sources[0], DataSource = sources[1] };
        for (var i = 0; i < 9; i++) result.Positions[i] = positions[i];
        return result;
    }
    private void Require(MetricOwnerSupplementalLocation location, ReadOnlySpan<uint> sources, ReadOnlySpan<uint> positions)
    {
        if (Location != location || sources.Length != SourceCount || positions.Length != PositionCount)
            throw new ArgumentException("The global binding differs from its metric location declaration.");
    }
}
