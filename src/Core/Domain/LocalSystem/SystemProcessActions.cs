namespace ResourceManager.App.Domain.LocalSystem;

public sealed record LocalOnlineSearchRequest(string Query);

public sealed record LocalOnlineSearchResult(
    string Query,
    string Url,
    string Message);

public sealed record LocalPathPropertiesRequest(string Path);

public sealed record SystemProcessIdentity(
    int ProcessId,
    string ProcessStartKey);

public sealed record SystemProcessOperationRequest(
    IReadOnlyList<SystemProcessIdentity> Targets);

public sealed record SystemProcessOperationResult(
    string Message,
    IReadOnlyList<SystemProcessOperationItem> Items,
    string? DirectoryPath = null);

public sealed record SystemProcessOperationItem(
    int ProcessId,
    string? ProcessName,
    string State,
    string Message,
    string? Path = null,
    string? ProcessStartKey = null);
