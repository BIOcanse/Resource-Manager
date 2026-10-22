namespace ResourceManager.App.Domain.Metrics;

/// <summary>One machine-bound physical reading and its declared source preference.
/// Id and device identities are opaque. Availability is a capability fact, not a sample's status.</summary>
public sealed record MetricReadingSource(
    string Id,
    string Quantity,
    int SourceRank,
    string SourceAvailability,
    bool IsPreferredSource,
    string? PreferredDeviceIdentity,
    string? PreferredFieldName);
