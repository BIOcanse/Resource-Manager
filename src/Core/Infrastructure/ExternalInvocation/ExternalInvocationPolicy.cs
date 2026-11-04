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

        // Same holder rule as HTTP and the invocation pipe: a caller holding a scope whose holder must be an
        // elevated administrator is refused unless it is one, whatever the operation needs and before the scope
        // check (docs/trusted_access_scopes_2026-10-29.md).
        if (!caller.IsAdministrator && AccessKeyScopes.RequiresAdministratorHolder([.. caller.GrantedScopes]))
        {
            return ExternalInvocationPolicyDecision.Reject(
                ExternalInvocationStatus.Forbidden,
                "elevated-administrator-required");
        }

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

        return ExternalInvocationPolicyDecision.Allow;
    }
}
