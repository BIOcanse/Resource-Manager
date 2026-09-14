using ResourceManager.App.Domain.Adaptation;

namespace ResourceManager.App.Application.Adaptation;

internal interface ITrustedAdapterInstanceLeaseAuthority
{
    AdapterInstanceLease Issue(
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterLeaseGrant grant);

    bool TryRenew(
        AdapterInstanceLeaseId leaseId,
        ulong expectedGeneration,
        TrustedAdapterCallerIdentity caller,
        out AdapterInstanceLease? renewed);

    bool TryResolve(
        AdapterInstanceLeaseId leaseId,
        ulong expectedGeneration,
        TrustedAdapterCapability requiredCapability,
        out AdapterInstanceLease? lease);

    bool Revoke(AdapterInstanceLeaseId leaseId, ulong expectedGeneration);

    int Expire();
}
