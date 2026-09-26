namespace ResourceManager.App.Domain.Migration;

public sealed record SoftwareDataDiscoveryRequest(
    string SoftwareName);

public sealed record SoftwareDataDiscoveryStartRequest(
    string SoftwareName,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<string> ProgramRootPaths);

public sealed record SoftwareDataDiscoveryEvidence(
    string Provider,
    string EventType,
    string Path,
    int ProcessId,
    string ProcessName,
    DateTimeOffset ObservedAt,
    long? SizeBytes);

public sealed record SoftwareDataDiscoveryCandidate(
    string Path,
    string RootPath,
    string Evidence,
    string RecommendedTargetCategory,
    string RecommendedMigrationKind,
    double Confidence,
    int ObservedWriteCount,
    DateTimeOffset LastObservedAt,
    string Message,
    string Provider,
    IReadOnlyList<SoftwareDataDiscoveryEvidence> EvidenceItems,
    IReadOnlyList<string> ProcessNames);

public sealed record SoftwareDataDiscoverySession(
    string Id,
    string SoftwareName,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<string> ProgramRootPaths,
    IReadOnlyList<SoftwareRootResolutionSource> ProgramRootSources,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    int ObservedWriteCount,
    IReadOnlyList<SoftwareDataDiscoveryCandidate> Candidates,
    string Message,
    string Provider,
    string ProviderState,
    string ProviderMessage);
