namespace ResourceManager.App.Domain.Adaptation;

public readonly record struct AdapterInstanceId(ulong High, ulong Low)
{
    public bool IsValid => (High | Low) != 0;
}

public readonly record struct AdapterInstanceLeaseId(ulong High, ulong Low)
{
    public bool IsValid => (High | Low) != 0;
}

public readonly record struct AdapterExecutableFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow);

[Flags]
public enum TrustedAdapterCapability : ulong
{
    None = 0,
    Register = 1UL << 0,
    DispatchPolicy = 1UL << 1
}

public sealed record TrustedAdapterCallerIdentity(
    ulong HostInstanceId,
    ulong TransportConnectionId,
    int ProcessId,
    long ProcessCreatedUtcTicks,
    string WindowsSid,
    int WindowsSessionId,
    ulong AuthenticationIdLuid,
    uint IntegrityLevelRid,
    bool IsElevated,
    string CanonicalExecutablePath,
    AdapterExecutableFileIdentity ExecutableFileIdentity,
    ulong AttestedAtMonotonicTimestamp);

public sealed record TrustedAdapterInstanceClaims(
    string AdapterId,
    string AppId,
    string DisplayName,
    IReadOnlyList<string> CanonicalProgramRootPaths);

internal sealed record TrustedAdapterLeaseGrant(
    TrustedAdapterInstanceClaims Claims,
    TrustedAdapterCapability GrantedCapabilities);

internal sealed record TrustedAdapterLeaseAssertion(
    TrustedAdapterInstanceClaims Claims,
    TrustedAdapterCapability RequestedCapabilities);

internal sealed record TrustedAdapterRegistration(
    TrustedAdapterInstanceClaims Claims,
    string CanonicalExecutablePath,
    AdapterExecutableFileIdentity ExecutableFileIdentity,
    TrustedAdapterCapability CapabilityCeiling);

public sealed record AdapterInstanceLease(
    AdapterInstanceLeaseId LeaseId,
    AdapterInstanceId InstanceId,
    ulong HostInstanceId,
    ulong OwnerApplicationKey,
    TrustedAdapterCallerIdentity Caller,
    TrustedAdapterInstanceClaims Claims,
    TrustedAdapterCapability Capabilities,
    ulong CapabilityGeneration,
    ulong IssuedAtMonotonicTimestamp,
    ulong DeadlineMonotonicTimestamp,
    ulong HeartbeatGeneration);
