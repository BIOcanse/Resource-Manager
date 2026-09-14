using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Adaptation;

internal interface ITrustedAdapterLeaseGrantCompiler
{
    ValueTask<TrustedAdapterLeaseGrant> CompileAsync(
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterLeaseAssertion assertion,
        CancellationToken cancellationToken);
}

internal interface ITrustedAdapterLeaseIssuer
{
    ValueTask<AdapterInstanceLease> IssueAsync(
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterLeaseAssertion assertion,
        CancellationToken cancellationToken);
}

internal interface ITrustedAdapterRegistrationCatalog
{
    ValueTask<TrustedAdapterRegistration?> FindAsync(
        string adapterId,
        string appId,
        CancellationToken cancellationToken);
}
