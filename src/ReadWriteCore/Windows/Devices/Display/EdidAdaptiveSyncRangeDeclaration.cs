using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

/// <summary>Original Adaptive-Sync declarations, not enabled state or measured refresh.</summary>
public sealed record EdidAdaptiveSyncRangeDeclaration(
    uint BlockIndex, uint ByteOffset, byte Revision, byte ContainerFlags, byte ContainerRevision,
    ImmutableArray<byte> RawDescriptor, uint BaseMinimumRefreshRateHz, uint BaseMaximumRefreshRateHz,
    bool NativePanelRange, bool AdaptiveVTotalSupported, bool SeamlessTransitionSupported,
    EdidAdaptiveSyncFrameDurationTolerance FrameDurationIncrease,
    EdidAdaptiveSyncFrameDurationTolerance FrameDurationDecrease);

/// <summary>
/// A declared limit in exact microseconds. Null is the explicit zero-code case:
/// any change throughout the range meets flicker performance without jitter.
/// The scope flag is independent; no actual jitter or timing is calculated here.
/// </summary>
public sealed record EdidAdaptiveSyncFrameDurationTolerance(
    bool FlickerRestrictedToSingleStep, uint? MaximumSingleStepMicroseconds)
{
    public bool ZeroCodeMeansNoJitterThroughoutRange => MaximumSingleStepMicroseconds is null;
}
