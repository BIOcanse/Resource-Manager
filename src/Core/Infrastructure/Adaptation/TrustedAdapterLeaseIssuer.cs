using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class TrustedAdapterLeaseIssuer(
    ITrustedAdapterLeaseGrantCompiler grantCompiler,
    TrustedAdapterControlPlaneAuthority authority)
    : ITrustedAdapterLeaseIssuer
{
    public async ValueTask<AdapterInstanceLease> IssueAsync(
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterLeaseAssertion assertion,
        CancellationToken cancellationToken)
    {
        var grant = await grantCompiler.CompileAsync(
            caller,
            assertion,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return authority.Issue(caller, grant);
    }
}
