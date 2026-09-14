namespace ResourceManager.App.Domain.ExternalInvocation;

public readonly record struct ExternalInvocationNoRequest;

public sealed record ExternalInvocationCaller(
    string SubjectId,
    string? WindowsSid,
    string? ApplicationId,
    ExternalInvocationTransport Transport,
    bool IsCorrespondingFrontend,
    bool IsAdministrator,
    int? ProcessId);

public sealed record ExternalInvocationRequest(
    string OperationId,
    object? Payload,
    ExternalInvocationCaller Caller,
    string CorrelationId);
