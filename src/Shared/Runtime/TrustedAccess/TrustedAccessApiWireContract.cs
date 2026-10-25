namespace ResourceManager.Shared.Runtime.TrustedAccess;

public static class TrustedAccessApiContract
{
    public const int Version = 1;
    public const string AccessKeysPath = "/api/access-keys";
    public const string AccessRequestsPath = "/api/access-requests";
    public const string AccessRequestsSubscriptionPath = "/api/access-requests/subscribe";
    public const int AccessRequestMaximumBodyBytes = 16 * 1024;
    public const int AccessRequestWaitMaximumMilliseconds = 25_000;
}

public sealed record TrustedAccessKeyWireSummary(
    string Id,
    string Name,
    string Purpose,
    string[] Scopes,
    DateTimeOffset CreatedAt,
    bool IsDefault);

public sealed record TrustedAccessKeysSnapshotWireResponse(
    TrustedAccessKeyWireSummary[] Keys,
    bool DefaultEnabled,
    string? DefaultKeyId,
    string[] AvailableScopes);

public sealed record TrustedAccessKeyCreateWireRequest(
    string? Name,
    string? Purpose,
    string[]? Scopes);

public sealed record TrustedAccessKeyCreatedWireResponse(
    TrustedAccessKeyWireSummary Key,
    string ApiKey);

public sealed record TrustedAccessDefaultWireRequest(
    bool Enabled,
    bool Rotate);

public sealed record TrustedAccessDefaultWireResponse(
    bool DefaultEnabled,
    string? DefaultKeyId);

public sealed record TrustedAccessRequestCreateWireRequest(
    string? Name,
    string[]? RequestedScopes);

public sealed record TrustedAccessRequestCreatedWireResponse(
    string RequestId,
    string DeliverySecret,
    string Status,
    DateTimeOffset ExpiresAt);

public sealed record TrustedAccessRequestPendingWireSnapshot(
    string RequestId,
    string Name,
    string[] RequestedScopes,
    string ObservedExecutablePath,
    int ObservedProcessId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ExpiresAt);

public sealed record TrustedAccessRequestWaitWireRequest(string? DeliverySecret);

public sealed record TrustedAccessRequestWaitWireResponse(
    string Status,
    TrustedAccessKeyWireSummary? Key,
    string? ApiKey,
    string? DenialReason);

public sealed record TrustedAccessRequestApproveWireRequest(string[]? Scopes);

public sealed record TrustedAccessRequestDenyWireRequest(string? ReasonCode);

public sealed record TrustedAccessRequestDecisionWireResponse(
    string Status,
    TrustedAccessKeyWireSummary? Key,
    string? DenialReason);
