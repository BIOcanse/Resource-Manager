using ResourceManager.App.Application.ExternalInvocation;
using ResourceManager.App.Domain.ExternalInvocation;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.ExternalInvocation;

public sealed class ExternalInvocationPolicy : IExternalInvocationPolicy
{
    public ExternalInvocationPolicyDecision Evaluate(
        ExternalInvocationCaller caller,
        ExternalInvocationOperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.AccessClass == ExternalInvocationAccessClass.Open)
        {
            return operation.RequiredScopes.Count == 0
                ? ExternalInvocationPolicyDecision.Allow
                : ExternalInvocationPolicyDecision.Reject(
                    ExternalInvocationStatus.Forbidden,
                    "open-operation-cannot-require-scopes");
        }

        if (caller.IsAdministrator && caller.AccessKeyId is null)
            return ExternalInvocationPolicyDecision.Allow;

        if (operation.RequiredScopes.Count == 0)
        {
            return ExternalInvocationPolicyDecision.Reject(
                ExternalInvocationStatus.Forbidden,
                "operation-scope-not-declared");
        }

        var grantedScopes = new HashSet<string>(caller.GrantedScopes, StringComparer.Ordinal);
        if (!operation.RequiredScopes.All(grantedScopes.Contains))
        {
            return ExternalInvocationPolicyDecision.Reject(
                caller.AccessKeyId is null && caller.WindowsSid is null
                    ? ExternalInvocationStatus.Unauthenticated
                    : ExternalInvocationStatus.Forbidden,
                caller.AccessKeyId is null && caller.WindowsSid is null
                    ? "local-windows-caller-required"
                    : "access-scope-required");
        }

        if ((operation.RequiredScopes.Contains(AccessKeyScopes.MachineWrite, StringComparer.Ordinal)
                || operation.RequiredScopes.Contains(AccessKeyScopes.InjectionConfigure, StringComparer.Ordinal))
            && !caller.IsAdministrator)
        {
            return ExternalInvocationPolicyDecision.Reject(
                ExternalInvocationStatus.Forbidden,
                "elevated-administrator-required");
        }

        return ExternalInvocationPolicyDecision.Allow;
    }
}
