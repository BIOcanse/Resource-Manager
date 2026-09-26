namespace ResourceManager.App.Domain.Settings;

public sealed record DashboardSettings(
    int Version,
    IReadOnlyList<DashboardCardSettings> Cards,
    IReadOnlyList<ResourceBarSettings> ResourceBars,
    IReadOnlyList<ResourceTableColumnSettings> ResourceTableColumns,
    IReadOnlyList<ResourceTableColumnSettings>? ResourceTableProcessColumns = null);

public sealed record DashboardCardSettings(
    string Id,
    string? Main,
    IReadOnlyList<string> Small,
    DashboardMetricBinding? MainBinding = null,
    IReadOnlyList<DashboardMetricBinding?>? SmallBindings = null);

public sealed record DashboardMetricBinding(
    string ScopeKind,
    string ScopeKey);

public sealed record ResourceBarSettings(
    string Id,
    string MetricId,
    string ScaleMode,
    DashboardMetricBinding? Binding = null);

public sealed record ResourceTableColumnSettings(
    string Id,
    bool Visible,
    double? Width = null,
    DashboardMetricBinding? Binding = null);

public enum DashboardSettingsSourceKind : byte
{
    BundledFirstRun = 0,
    Persisted = 1,
    MigratedPersisted = 2,
    SavedPersisted = 3,
    RecoveredLastKnownGood = 4,
    RecoveredDefaultsAfterCorruption = 5,
    TestFixture = 255
}

public sealed record DashboardSettingsSourceMetadata(
    DashboardSettingsSourceKind Kind,
    int SourceVersion,
    string InputSha256,
    string EffectiveSha256,
    bool RewritePerformed)
{
    public string RecoveryDisposition { get; init; } = "none";

    public string? RecoveryArtifactPath { get; init; }

    public string? LastKnownGoodPath { get; init; }
}

public sealed record DashboardSettingsUpdateResult(
    DashboardSettings Settings,
    DateTimeOffset UpdatedAt,
    string StoragePath,
    DashboardSettingsSourceMetadata Source);
