namespace ResourceManager.App.Domain.DeviceTopology;

public sealed record DeviceTopologySnapshotState(
    string SchemaVersion,
    string State,
    DeviceTopologySnapshot? Snapshot,
    long ContentGeneration,
    long StateRevision,
    string Source,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastAttemptAt,
    string? FailureCode,
    IReadOnlyList<DeviceTopologySourceDiagnostic> AttemptDiagnostics)
{
    public const string CurrentSchemaVersion = "3.0.0";

    public static DeviceTopologySnapshotState Warming { get; } = new(
        CurrentSchemaVersion,
        DeviceTopologySnapshotStatus.Warming,
        null,
        0,
        0,
        DeviceTopologySnapshotSource.Memory,
        null,
        null,
        null,
        []);
}

public sealed record DeviceTopologySourceDiagnostic(
    string SourceId,
    string Status,
    string Code,
    string Message);

public static class DeviceTopologySourceDiagnosticStatus
{
    public const string RequiredIncomplete = "required-incomplete";
    public const string OptionalDegraded = "optional-degraded";
}

public static class DeviceTopologySnapshotStatus
{
    public const string Warming = "warming";
    public const string Ready = "ready";
    public const string Refreshing = "refreshing";
    public const string Failed = "failed";
}

public static class DeviceTopologySnapshotSource
{
    public const string Memory = "memory";
    public const string Persisted = "persisted";
    public const string Live = "live";
}
