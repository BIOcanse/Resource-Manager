using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints.Transport;

internal static class ResourceBreakdownCurrentBars
{
    internal static ResourceBreakdownBar[] Select(ResourceBreakdownSnapshot source,
        IReadOnlyList<string> ids)
        => NativeResourceBarsProjection.Select(source, ids);

    internal static ResourceBreakdownBar WithoutReading(ResourceBreakdownBar bar, string reason)
        => NativeResourceBarsProjection.WithoutReading(bar, reason);

    internal static string UnavailableReason(ResourceBreakdownSnapshot source, string id,
        string? observedReason = null, string fallbackReason = ResourceBarsProjectionDeclarations.DefaultReason)
        => NativeResourceBarsProjection.UnavailableReason(source, id, observedReason, fallbackReason);
}

// Original wire identities and missing-bar choices, supplied at the interaction
// edge to both baseline and native projection. No second set of Zig defaults.
// Selection still calls the original directory/Normalize declarations.
internal static class ResourceBarsProjectionDeclarations
{
    internal const string DiskPrefix = "disk.";
    internal const string NetworkPrefix = "network.";
    internal const string UsageSuffix = "usage";
    internal const string VramSuffix = ".vram";
    internal const string RateUnit = "B/s";
    internal const string DefaultReason = "resource-breakdown-total-unavailable";
    internal const string WarmingReason = "resource-breakdown-warming";
    internal const string GpuMemoryReason = "gpu-dedicated-memory-total-unavailable";
}
