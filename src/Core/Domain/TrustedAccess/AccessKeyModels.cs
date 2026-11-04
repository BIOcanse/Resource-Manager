namespace ResourceManager.App.Domain.TrustedAccess;

public sealed record AccessKeySummary(
    string Id,
    string Name,
    string Purpose,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    bool IsDefault);

public sealed record AccessKeyCreated(
    AccessKeySummary Credential,
    string ApiKey);

public sealed record AccessKeyCreateRequest(
    string? Name,
    string Purpose,
    string[] Scopes);
