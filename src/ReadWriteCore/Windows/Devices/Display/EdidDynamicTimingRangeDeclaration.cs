using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

/// <summary>Declared dynamic timing limits, not current refresh or OS mode availability.</summary>
public sealed record EdidDynamicTimingRangeDeclaration(
    uint BlockIndex, uint ByteOffset, byte Revision, byte ContainerFlags, byte ContainerRevision,
    ImmutableArray<byte> RawDescriptor, ulong MinimumPixelClockHz, ulong MaximumPixelClockHz,
    uint MinimumVerticalRateHz, uint MaximumVerticalRateHz,
    bool SeamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking);
