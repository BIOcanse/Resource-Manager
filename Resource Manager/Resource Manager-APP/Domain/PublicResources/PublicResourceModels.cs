using ResourceManager.Adapter;
using ResourceManager.Adapter.SharedMemory;

namespace ResourceManager.App.Domain.PublicResources;

public sealed record PublicResourceDescriptor(
    ulong PublicResourceId,
    ulong OwnerApplicationKey,
    int OwnerProcessId,
    ulong AdapterKey,
    ulong SizeBytes,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    AdapterResourceRecoveryKind RecoveryKind,
    AdapterResourceGranularity Granularity,
    AdapterResourceDemandMask OwnerDemandMask,
    SharedResourceSubscriptionIntent SubscriptionIntentMask,
    SharedResourceAvailability Availability,
    SharedResourceFlags Flags,
    ushort MaxParallelGrants,
    int ActiveSubscriberCount,
    int QueuedRequestCount,
    int ReservedGrantCount,
    int ActiveUseCount,
    double SubscriptionMultiplier,
    DateTimeOffset LastUpdatedAt,
    AdapterResourceActionMask AllowedActions,
    bool ConsistencyStable,
    bool DestructiveActionActive);

public sealed record PublicResourceCatalogSnapshot(
    uint ProtocolVersion,
    ulong TopologyGeneration,
    IReadOnlyList<PublicResourceDescriptor> Resources,
    DateTimeOffset CapturedAt);

public sealed record PublicResourceTransportSummary(
    uint ProtocolVersion,
    string Transport,
    bool DirectMappingAvailable,
    string AccessMode);

public static class HostPublicResourceCapabilityStates
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
}

public sealed record HostPublicResourceCapabilitySnapshot(
    string State,
    bool Available,
    string Reason);

public sealed record HostPublicResourceDefinition(
    ulong PublicResourceId,
    ulong ResourceKey,
    uint ResourceId,
    ulong SizeBytes,
    ulong ContentIdentityHash,
    ulong PayloadMappingId,
    ulong PayloadGeneration,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    AdapterResourceRecoveryKind RecoveryKind,
    AdapterResourceGranularity Granularity,
    AdapterResourceActionMask InapplicableActions,
    AdapterResourceActionRoute ActionRoute,
    AdapterResourceDemandMask DemandMask,
    AdapterSoftwareSurfaceState SurfaceState,
    SharedResourceAvailability Availability,
    SharedResourceFlags Flags,
    ushort MaxParallelGrants = 1,
    ulong AdapterKey = 0);
