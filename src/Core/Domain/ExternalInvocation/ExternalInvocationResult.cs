namespace ResourceManager.App.Domain.ExternalInvocation;

public enum ExternalInvocationStatus : byte
{
    Succeeded = 0,
    NotFound = 1,
    Disabled = 2,
    Unavailable = 3,
    Unauthenticated = 4,
    Forbidden = 5,
    InvalidRequest = 6,
    Canceled = 7,
    Failed = 8
}

public sealed record ExternalInvocationResult(
    ExternalInvocationStatus Status,
    object? Value,
    string? ErrorCode)
{
    public bool Succeeded => Status == ExternalInvocationStatus.Succeeded;

    public static ExternalInvocationResult Success(object? value)
        => new(ExternalInvocationStatus.Succeeded, value, null);

    public static ExternalInvocationResult Reject(
        ExternalInvocationStatus status,
        string errorCode)
        => new(status, null, errorCode);
}

public sealed record ExternalInvocationAuditEvent(
    string CorrelationId,
    string OperationId,
    string? ModuleId,
    string SubjectId,
    string? WindowsSid,
    string? ApplicationId,
    ExternalInvocationTransport Transport,
    int? ProcessId,
    ExternalInvocationAccessClass? AccessClass,
    ExternalInvocationStatus Status,
    string? ErrorCode,
    long ElapsedMicroseconds,
    DateTimeOffset CompletedAt);
