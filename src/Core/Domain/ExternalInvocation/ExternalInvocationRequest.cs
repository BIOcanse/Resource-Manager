namespace ResourceManager.App.Domain.ExternalInvocation;

public readonly record struct ExternalInvocationNoRequest;

public sealed record ExternalInvocationCaller(
    string SubjectId,
    string? WindowsSid,
    string? AccessKeyId,
    ExternalInvocationTransport Transport,
    IReadOnlyCollection<string> GrantedScopes,
    bool IsAdministrator,
    int? ProcessId,
    int? WindowsSessionId = null);

public sealed record ExternalInvocationRequest(
    string OperationId,
    object? Payload,
    ExternalInvocationCaller Caller,
    string CorrelationId);
