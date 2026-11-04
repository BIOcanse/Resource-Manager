namespace ResourceManager.Ledger;

/// <summary>
/// Cumulative development facts since instance creation. Saturated UInt64.MaxValue
/// means at least that value. Byte totals refer to each fresh allocation's original size.
/// </summary>
public readonly record struct LedgerDevelopmentStatistics(
    ulong NewAllocations,
    ulong SameEpochReleases,
    ulong SameEpochAllocationBytes,
    ulong SubpageAllocations,
    ulong SubpageAllocationBytes);
