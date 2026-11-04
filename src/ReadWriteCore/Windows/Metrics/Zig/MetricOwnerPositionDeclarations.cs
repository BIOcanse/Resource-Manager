using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Native.SharedSection;

namespace ResourceManager.ReadWrite.Windows.Metrics.Zig;

public enum MetricOwnerWriterRole { Rows, Values, Health, Provenance }
public readonly record struct MetricOwnerPositionDeclaration(uint Slot, SharedSectionPositionKind Kind,
    uint ElementWidth, uint Capacity, MetricOwnerWriterRole Writer, uint IdentitySlot)
{
    public ulong ByteCapacity => (ulong)ElementWidth * Capacity;
    /// <summary>Local UTF8 table slot for package StringColumn positions.</summary>
    public uint StringsSlot { get; init; } = MetricOwnerAbi.None;
}

/// <summary>
/// Input to the maintenance-owned global region creator. No offsets, mapping
/// creation, publication, lease or source registration occurs in this declaration.
/// Changing an origin's access region changes its declaration and dependency group.
/// </summary>
public readonly struct MetricOwnerTableDeclaration
{
    public MetricOwnerAccessRegion Region { get; }
    public uint Rows { get; }
    public uint PayloadBytes { get; }
    public bool IsResult { get; }
    public int ValuePositionCount => IsResult ? 12 : 16;
    public int PositionCount => ValuePositionCount + 1;
    public int SourceCount => 4;

    private MetricOwnerTableDeclaration(MetricOwnerAccessRegion region, uint rows, uint payloadBytes, bool result)
    { Region = region; Rows = rows; PayloadBytes = payloadBytes; IsResult = result; }

    public static MetricOwnerTableDeclaration Raw(MetricOwnerAccessRegion region, uint rules, uint payloadBytes)
        => new(region, rules, payloadBytes, false);
    /// <param name="region">Highest privilege of all possible dependencies, fixed by the declaration.</param>
    public static MetricOwnerTableDeclaration Result(MetricOwnerAccessRegion region, uint metrics)
        => new(region, metrics, 0, true);

    public MetricOwnerPositionDeclaration Position(uint slot)
    {
        if (slot >= PositionCount) throw new ArgumentOutOfRangeException(nameof(slot));
        if (slot == ValuePositionCount)
        {
            var words = 8UL + (ulong)Rows * (IsResult ? 4UL : 12UL);
            if (words > uint.MaxValue / 8U) throw new MetricOwnerException(new() {
                Code = MetricOwnerCode.CapacityExceeded, Family = MetricOwnerFamily.Binding,
                Required = words * 8, Limit = uint.MaxValue });
            return new(slot, SharedSectionPositionKind.Column, 8, (uint)words,
                MetricOwnerWriterRole.Provenance, MetricOwnerAbi.None);
        }
        var payload = !IsResult && slot == 9;
        var width = IsResult ? slot is 4 or 5 or 9 or 10 or 11 ? 4U : 8U
            : payload ? 1U : slot is 3 or 4 or 10 or 11 or 13 ? 4U : 8U;
        var writer = slot == 0 || !IsResult && slot == 15 ? MetricOwnerWriterRole.Rows
            : !IsResult && slot is >= 10 and <= 12 ? MetricOwnerWriterRole.Health : MetricOwnerWriterRole.Values;
        return new(slot, payload ? SharedSectionPositionKind.StringTable : SharedSectionPositionKind.Column,
            width, payload ? PayloadBytes : Rows, writer, slot == 0 || payload ? MetricOwnerAbi.None : 0);
    }

    /// <summary>Resolve the creator's global indices into a non-owning native table binding.</summary>
    public unsafe MetricOwnerTableBinding Bind(byte* region, ulong byteLength,
        ReadOnlySpan<uint> sourceIndices, ReadOnlySpan<uint> positionIndices)
    {
        if (sourceIndices.Length != SourceCount || positionIndices.Length != PositionCount)
            throw new ArgumentException("The global binding does not match its table declaration.");
        var binding = new MetricOwnerTableBinding { Region = region, ByteLength = byteLength,
            RowsSource = sourceIndices[0], ValueSource = sourceIndices[1], HealthSource = sourceIndices[2],
            ProvenanceSource = sourceIndices[3], ProvenancePosition = positionIndices[ValuePositionCount] };
        var slot = 0;
        while (slot < 16)
        {
            binding.Positions[slot] = slot < ValuePositionCount ? positionIndices[slot] : MetricOwnerAbi.None;
            slot++;
        }
        return binding;
    }
}
