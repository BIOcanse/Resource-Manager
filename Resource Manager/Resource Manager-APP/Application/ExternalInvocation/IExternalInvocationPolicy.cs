using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Application.ExternalInvocation;

public sealed record ExternalInvocationPolicyDecision(
    bool Allowed,
    ExternalInvocationStatus RejectionStatus,
    string? ErrorCode)
{
    public static ExternalInvocationPolicyDecision Allow { get; } = new(
        true,
        ExternalInvocationStatus.Succeeded,
        null);

    public static ExternalInvocationPolicyDecision Reject(
        ExternalInvocationStatus status,
        string errorCode)
        => new(false, status, errorCode);
}

public interface IExternalInvocationPolicy
{
    ExternalInvocationPolicyDecision Evaluate(
        ExternalInvocationCaller caller,
        ExternalInvocationOperationDescriptor operation);
}
