using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.ExternalInvocation;

namespace ResourceManager.App.Infrastructure.ExternalInvocation;

public sealed class ExternalInvocationPolicy(IRuntimePlanProvider runtimePlanProvider) : IExternalInvocationPolicy
{
    public ExternalInvocationPolicyDecision Evaluate(
        ExternalInvocationCaller caller,
        ExternalInvocationOperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(operation);

        return operation.AccessClass == ExternalInvocationAccessClass.Open
            || runtimePlanProvider.Current.Diagnostics.DebugModeEnabled
            || caller.IsCorrespondingFrontend
            || caller.IsAdministrator
            ? ExternalInvocationPolicyDecision.Allow
            : ExternalInvocationPolicyDecision.Reject(
                ExternalInvocationStatus.Unauthenticated,
                "frontend-or-administrator-required");
    }
}
