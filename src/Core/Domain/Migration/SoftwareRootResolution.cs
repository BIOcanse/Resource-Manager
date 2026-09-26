namespace ResourceManager.App.Domain.Migration;

public sealed record SoftwareRootResolutionRequest(
    string SoftwareName,
    IReadOnlyList<string> ExplicitRootPaths,
    IReadOnlyList<string> ProcessNames);

public sealed record SoftwareRootResolution(
    IReadOnlyList<string> RootPaths,
    IReadOnlyList<SoftwareRootResolutionSource> Sources);

public sealed record SoftwareRootResolutionSource(
    string SourceType,
    string SourceId,
    string DisplayName,
    string RootPath,
    string MatchReason);
