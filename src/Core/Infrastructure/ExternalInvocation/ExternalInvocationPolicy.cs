using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Infrastructure.ExternalInvocation;

public sealed class ExternalInvocationPolicy : IExternalInvocationPolicy
{
    public ExternalInvocationPolicyDecision Evaluate(
        ExternalInvocationCaller caller,
        ExternalInvocationOperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(operation);

        return operation.AccessClass == ExternalInvocationAccessClass.Open
            || caller.IsCorrespondingFrontend
            || caller.IsAdministrator
            ? ExternalInvocationPolicyDecision.Allow
            : ExternalInvocationPolicyDecision.Reject(
                ExternalInvocationStatus.Unauthenticated,
                "frontend-or-administrator-required");
    }
}
