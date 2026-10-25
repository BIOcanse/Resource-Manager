namespace ResourceManager.App.Domain.TrustedAccess;

public enum TrustedAccessRequestStatus : byte
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Expired = 3,
    Delivered = 4
}

public enum TrustedAccessRequestDenialReason : byte
{
    NotApproved = 0,
    Unsupported = 1,
    Duplicate = 2,
    NotNeeded = 3
}

public sealed record TrustedAccessRequestPendingSnapshot(
    string RequestId,
    string Name,
    IReadOnlyList<string> RequestedScopes,
    string ObservedExecutablePath,
    int ObservedProcessId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset ExpiresAt);

public sealed record TrustedAccessRequestCreateResult(
    string? RequestId,
    string? DeliverySecret,
    DateTimeOffset? ExpiresAt,
    string? ErrorCode)
{
    public bool Created => RequestId is not null && DeliverySecret is not null && ExpiresAt.HasValue;
}

public sealed record TrustedAccessRequestWaitResult(
    TrustedAccessRequestStatus Status,
    AccessKeySummary? Key = null,
    string? ApiKey = null,
    TrustedAccessRequestDenialReason? DenialReason = null,
    string? ErrorCode = null);

public sealed record TrustedAccessRequestDecisionResult(
    TrustedAccessRequestStatus Status,
    AccessKeySummary? Key = null,
    TrustedAccessRequestDenialReason? DenialReason = null,
    string? ErrorCode = null);
