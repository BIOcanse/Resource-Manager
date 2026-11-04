namespace ResourceManager.App.Domain.SoftwareDiscovery;

public sealed record PortableSoftwareObservation(
    string SoftwareId,
    string CatalogEntryId,
    string Name,
    string Kind,
    string ExecutablePath,
    string SuggestedRootPath,
    bool IdentityConfirmed = false,
    bool RootPathConfirmed = false);

public sealed record PortableSoftwareRegistration(
    string SoftwareId,
    string CatalogEntryId,
    string Name,
    string Kind,
    IReadOnlyList<string> ExecutablePaths,
    IReadOnlyList<string> RootPaths,
    DateTimeOffset FirstObservedAt,
    IReadOnlyList<string>? SuggestedRootPaths = null,
    bool IdentityConfirmed = false,
    bool RequiresRootPathConfirmation = true);

public sealed record PortableSoftwareRootConfirmationRequest(
    string SoftwareId,
    string RootPath);

public sealed record PortableSoftwareRootConfirmationResult(
    string SoftwareId,
    string RootPath,
    int ConfirmedExecutableCount,
    bool RequiresRootPathConfirmation,
    string State,
    string Message);
