using ResourceManager.Adapter;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Adaptation;

internal sealed class TrustedAdapterControlPlaneAuthority(
    HostManagerRuntimeIdentity hostIdentity,
    IRuntimePlanProvider runtimePlanProvider) :
    ITrustedAdapterInstanceLeaseAuthority,
    IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<AdapterInstanceLeaseId, AdapterInstanceLease> leases = [];
    private NativeAdapterInstanceLeaseSession? nativeSession;
    private CompiledHostManagerAdapterInstanceLeaseRecreatePlan? appliedRecreatePlan;

    public AdapterInstanceLease Issue(
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterLeaseGrant grant)
    {
        ValidateCaller(caller);
        ArgumentNullException.ThrowIfNull(grant);
        var normalizedClaims = NormalizeClaims(grant.Claims);
        ValidateGrant(grant.GrantedCapabilities);
        var ownerApplicationKey = AdapterResourceKey.FromString(
            normalizedClaims.AppId.ToUpperInvariant());
        lock (gate)
        {
            var native = EnsureNativeSession();
            var receipt = native.Issue(
                ownerApplicationKey,
                grant.GrantedCapabilities,
                caller);
            try
            {
                var lease = ProjectLease(receipt, caller, normalizedClaims);
                leases.Add(lease.LeaseId, lease);
                return lease;
            }
            catch
            {
                if (!native.Revoke(
                    new AdapterInstanceLeaseId(receipt.LeaseIdHigh, receipt.LeaseIdLow),
                    receipt.HeartbeatGeneration))
                {
                    ResetNativeSession();
                }
                throw;
            }
        }
    }

    public bool TryRenew(
        AdapterInstanceLeaseId leaseId,
        ulong expectedGeneration,
        TrustedAdapterCallerIdentity caller,
        out AdapterInstanceLease? renewed)
    {
        ValidateCaller(caller);
        lock (gate)
        {
            var native = EnsureNativeSession();
            if (!leases.TryGetValue(leaseId, out var current))
            {
                renewed = null;
                return false;
            }
            if (!TryNextGeneration(current.HeartbeatGeneration, out var nextGeneration))
            {
                _ = native.Revoke(leaseId, current.HeartbeatGeneration);
                leases.Remove(leaseId);
                renewed = null;
                return false;
            }
            if (!native.TryRenew(
                    leaseId,
                    expectedGeneration,
                    caller,
                    out var receipt))
            {
                if (expectedGeneration == current.HeartbeatGeneration
                    && !native.TryResolve(
                        leaseId,
                        current.HeartbeatGeneration,
                        TrustedAdapterCapability.Register,
                        out _))
                {
                    leases.Remove(leaseId);
                }
                renewed = null;
                return false;
            }

            try
            {
                if (!ReceiptMatchesLeaseIdentity(receipt, current)
                    || receipt.HeartbeatGeneration != nextGeneration)
                {
                    throw new InvalidOperationException(
                        "Native adapter-instance lease renewal returned a mismatched receipt.");
                }
                renewed = ProjectLease(receipt, caller, current.Claims);
                leases[leaseId] = renewed;
                return true;
            }
            catch
            {
                ResetNativeSession();
                throw;
            }
        }
    }

    public bool TryResolve(
        AdapterInstanceLeaseId leaseId,
        ulong expectedGeneration,
        TrustedAdapterCapability requiredCapability,
        out AdapterInstanceLease? lease)
    {
        lock (gate)
        {
            var native = EnsureNativeSession();
            if (!leases.TryGetValue(leaseId, out var current)
                || !native.TryResolve(
                    leaseId,
                    expectedGeneration,
                    requiredCapability,
                    out var receipt)
                || !ReceiptMatchesLeaseIdentity(receipt, current)
                || receipt.HeartbeatGeneration != current.HeartbeatGeneration
                || receipt.DeadlineTimestamp != current.DeadlineMonotonicTimestamp)
            {
                lease = null;
                return false;
            }
            lease = current;
            return true;
        }
    }

    public bool Revoke(AdapterInstanceLeaseId leaseId, ulong expectedGeneration)
    {
        lock (gate)
        {
            var native = EnsureNativeSession();
            if (!leases.TryGetValue(leaseId, out var current)
                || current.HeartbeatGeneration != expectedGeneration)
            {
                return false;
            }
            if (!native.Revoke(leaseId, expectedGeneration))
            {
                return false;
            }
            if (!leases.Remove(leaseId))
            {
                ResetNativeSession();
                throw new InvalidOperationException(
                    "Adapter-instance lease metadata changed during revoke.");
            }
            return true;
        }
    }

    public int Expire()
    {
        lock (gate)
        {
            var native = EnsureNativeSession();
            var expiredCount = ExpireAndPruneInvalidLeases(native);
            return expiredCount;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            nativeSession?.Dispose();
            nativeSession = null;
            leases.Clear();
        }
    }

    private NativeAdapterInstanceLeaseSession EnsureNativeSession()
    {
        var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
        var desired = hostPlan.HostRecreate.AdapterInstanceLease;
        if (nativeSession is not null && appliedRecreatePlan == desired)
        {
            return nativeSession;
        }

        var replacement = new NativeAdapterInstanceLeaseSession(
            checked((uint)desired.Capacity),
            hostIdentity.InstanceId,
            MillisecondsToStopwatchTicks(desired.LeaseDurationMilliseconds),
            MillisecondsToStopwatchTicks(desired.MaximumAttestationAgeMilliseconds));
        var previous = nativeSession;
        nativeSession = replacement;
        appliedRecreatePlan = desired;
        leases.Clear();
        previous?.Dispose();
        return replacement;
    }

    private static AdapterInstanceLease ProjectLease(
        in NativeAdapterInstanceLeaseReceipt receipt,
        TrustedAdapterCallerIdentity caller,
        TrustedAdapterInstanceClaims claims)
        => new(
            new AdapterInstanceLeaseId(receipt.LeaseIdHigh, receipt.LeaseIdLow),
            new AdapterInstanceId(receipt.InstanceIdHigh, receipt.InstanceIdLow),
            receipt.Caller.HostInstanceId,
            receipt.OwnerApplicationKey,
            caller,
            claims,
            (TrustedAdapterCapability)receipt.Capabilities,
            1,
            receipt.IssuedAtTimestamp,
            receipt.DeadlineTimestamp,
            receipt.HeartbeatGeneration);

    private static bool ReceiptMatchesLeaseIdentity(
        in NativeAdapterInstanceLeaseReceipt receipt,
        AdapterInstanceLease lease)
        => receipt.LeaseIdHigh == lease.LeaseId.High
            && receipt.LeaseIdLow == lease.LeaseId.Low
            && receipt.InstanceIdHigh == lease.InstanceId.High
            && receipt.InstanceIdLow == lease.InstanceId.Low
            && receipt.Caller.HostInstanceId == lease.HostInstanceId
            && receipt.OwnerApplicationKey == lease.OwnerApplicationKey
            && receipt.Capabilities == (ulong)lease.Capabilities;

    private void ResetNativeSession()
    {
        var previous = ResetNativeSessionCore();
        previous?.Dispose();
    }

    private NativeAdapterInstanceLeaseSession? ResetNativeSessionCore()
    {
        var previous = nativeSession;
        nativeSession = null;
        appliedRecreatePlan = null;
        leases.Clear();
        return previous;
    }

    private int ExpireAndPruneInvalidLeases(NativeAdapterInstanceLeaseSession native)
    {
        var invalidLeases = new List<AdapterInstanceLeaseId>();
        foreach (var entry in leases)
        {
            if (native.TryResolve(
                entry.Key,
                entry.Value.HeartbeatGeneration,
                TrustedAdapterCapability.Register,
                out var receipt)
                && ReceiptMatchesLeaseIdentity(receipt, entry.Value)
                && receipt.HeartbeatGeneration == entry.Value.HeartbeatGeneration)
            {
                continue;
            }

            invalidLeases.Add(entry.Key);
        }

        var expiredCount = native.Expire();
        foreach (var leaseId in invalidLeases)
        {
            leases.Remove(leaseId);
        }
        return expiredCount;
    }

    private void ValidateCaller(TrustedAdapterCallerIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.HostInstanceId != hostIdentity.InstanceId
            || caller.TransportConnectionId == 0
            || caller.ProcessId <= 0
            || caller.ProcessCreatedUtcTicks <= 0
            || string.IsNullOrWhiteSpace(caller.WindowsSid)
            || caller.WindowsSessionId < 0
            || caller.AuthenticationIdLuid == 0
            || caller.IntegrityLevelRid == 0
            || string.IsNullOrWhiteSpace(caller.CanonicalExecutablePath)
            || caller.AttestedAtMonotonicTimestamp == 0)
        {
            throw new ArgumentException("The trusted adapter caller identity is incomplete.", nameof(caller));
        }
    }

    private static TrustedAdapterInstanceClaims NormalizeClaims(TrustedAdapterInstanceClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var adapterId = claims.AdapterId?.Trim();
        var appId = claims.AppId?.Trim();
        var displayName = claims.DisplayName?.Trim();
        var roots = (claims.CanonicalProgramRootPaths ?? [])
            .Select(path => path?.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(adapterId)
            || string.IsNullOrWhiteSpace(appId)
            || string.IsNullOrWhiteSpace(displayName)
            || roots.Length == 0)
        {
            throw new ArgumentException("The trusted adapter claims are incomplete.", nameof(claims));
        }
        return new TrustedAdapterInstanceClaims(adapterId, appId, displayName, roots);
    }

    private static void ValidateGrant(TrustedAdapterCapability capabilities)
    {
        const TrustedAdapterCapability all =
            TrustedAdapterCapability.Register
            | TrustedAdapterCapability.DispatchPolicy;
        if (capabilities == TrustedAdapterCapability.None
            || (capabilities & TrustedAdapterCapability.Register) == 0
            || (capabilities & ~all) != 0)
        {
            throw new ArgumentException("The trusted adapter lease grant is invalid.", nameof(capabilities));
        }
    }

    private static void ValidateRequiredCapabilities(TrustedAdapterCapability capabilities)
    {
        const TrustedAdapterCapability all =
            TrustedAdapterCapability.Register
            | TrustedAdapterCapability.DispatchPolicy;
        if (capabilities == TrustedAdapterCapability.None || (capabilities & ~all) != 0)
        {
            throw new ArgumentException(
                "The trusted adapter required capability set is invalid.",
                nameof(capabilities));
        }
    }

    internal static bool TryNextGeneration(ulong generation, out ulong nextGeneration)
    {
        if (generation == 0 || generation == ulong.MaxValue)
        {
            nextGeneration = 0;
            return false;
        }
        nextGeneration = checked(generation + 1);
        return true;
    }

    private static ulong MillisecondsToStopwatchTicks(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            throw new InvalidOperationException("Adapter-instance lease timing must be positive.");
        }
        return checked((ulong)Math.Ceiling(
            milliseconds / 1000d * System.Diagnostics.Stopwatch.Frequency));
    }


}
