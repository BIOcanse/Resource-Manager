using ResourceManager.Adapter;

namespace ResourceManager.Adapter.SharedMemory;

public static class SharedResourceProtocol
{
    public const uint Version = 11;
    public const ulong HostPublicRecallTransactionsCapability = 1UL << 0;
}

public readonly record struct SharedResourceAuthorityId(ulong High, ulong Low)
{
    public bool IsZero => (High | Low) == 0;
}

public enum SharedResourceLeaseClockDomain : uint
{
    WindowsPerformanceCounter = 1
}

public enum SharedResourceAvailability : byte
{
    Preparing = 0,
    Available = 1,
    Revoking = 2,
    Unavailable = 3
}

[Flags]
public enum SharedResourceSubscriptionIntent : byte
{
    None = 0,
    ReadySoon = 1 << 1,
    PreloadEager = 1 << 2,
    PreloadOpportunistic = 1 << 3
}

public enum SharedResourceTaskState : byte
{
    Queued = 1,
    Reserved = 2,
    Active = 3
}

[Flags]
public enum SharedResourceFlags : byte
{
    None = 0,
    ReadOnly = 1 << 0,
    GpuBacked = 1 << 2
}

public readonly record struct SharedResourceId(
    ulong LedgerInstanceId,
    uint ResourceSlot,
    ulong ResourceGeneration,
    ulong PublicResourceId);

public sealed record SharedResourceLedgerDescriptor(
    string MappingName,
    string MutexName,
    ulong LedgerInstanceId,
    int ResourceCapacity,
    int SubscriptionCapacity,
    int TaskCapacity,
    long MappingSizeBytes,
    double SubscriptionCoefficient,
    ulong OwnerApplicationKey,
    int OwnerProcessId,
    long OwnerProcessCreatedUtcTicks,
    SharedResourceLeaseClockDomain LeaseClockDomain,
    ulong LeaseClockFrequency,
    ulong MaximumSubscriptionTtl,
    ulong MaximumQueueTtl,
    ulong MaximumGrantTtl,
    uint ProtocolVersion = SharedResourceProtocol.Version);

public sealed record SharedResourceSessionOptions(
    int ResourceCapacity,
    int SubscriptionCapacity,
    int TaskCapacity,
    double SubscriptionCoefficient,
    ulong OwnerApplicationKey,
    int OwnerProcessId,
    DateTimeOffset OwnerProcessCreatedAt,
    TimeSpan MaximumSubscriptionLeaseDuration,
    TimeSpan MaximumQueueDuration,
    TimeSpan MaximumGrantDuration);

public sealed record SharedResourcePublication(
    ulong PublicResourceId,
    ulong OwnerApplicationKey,
    SharedResourceAuthorityId OwnerInstanceId,
    ulong OwnerContextGeneration,
    ulong LeaseGeneration,
    ulong BindingGeneration,
    ulong CapabilityGeneration,
    SharedResourceAuthorityId ExecutorId,
    int OwnerProcessId,
    ulong ResourceKey,
    ulong AdapterKey,
    uint ResourceId,
    ulong SizeBytes,
    ulong ContentIdentityHash,
    ulong PayloadMappingId,
    ulong PayloadGeneration,
    ushort MaxParallelGrants,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    AdapterResourceRecoveryKind RecoveryKind,
    AdapterResourceGranularity Granularity,
    AdapterResourceActionMask InapplicableActions,
    AdapterResourceActionRoute ActionRoute,
    AdapterResourceDemandMask OwnerDemandMask,
    AdapterSoftwareSurfaceState SurfaceState,
    SharedResourceAvailability Availability,
    SharedResourceFlags Flags);

public sealed record SharedResourceSnapshot(
    SharedResourceId Id,
    ulong OwnerApplicationKey,
    SharedResourceAuthorityId OwnerInstanceId,
    ulong OwnerContextGeneration,
    ulong LeaseGeneration,
    ulong BindingGeneration,
    ulong CapabilityGeneration,
    SharedResourceAuthorityId ExecutorId,
    int OwnerProcessId,
    ulong ResourceKey,
    ulong AdapterKey,
    uint ResourceId,
    ulong SizeBytes,
    ulong ContentIdentityHash,
    ulong PayloadMappingId,
    ulong PayloadGeneration,
    ushort MaxParallelGrants,
    AdapterResourceTier Tier,
    AdapterResourceKind ResourceKind,
    AdapterResourceRecoveryKind RecoveryKind,
    AdapterResourceGranularity Granularity,
    AdapterResourceActionMask InapplicableActions,
    AdapterResourceActionRoute ActionRoute,
    AdapterResourceDemandMask OwnerDemandMask,
    SharedResourceSubscriptionIntent SubscriptionIntentMask,
    byte ActivityScore,
    AdapterSoftwareSurfaceState SurfaceState,
    SharedResourceAvailability Availability,
    SharedResourceFlags Flags,
    int ActiveSubscriberCount,
    int QueuedRequestCount,
    int ReservedGrantCount,
    int ActiveUseCount,
    ulong SchedulingRevision,
    ulong GateEpoch,
    double SubscriptionMultiplier,
    DateTimeOffset LastUpdatedAt,
    AdapterResourceActionMask AllowedActions,
    bool ConsistencyStable,
    bool DestructiveActionActive)
{
    public int ProtectedUseCount => checked(
        QueuedRequestCount + ReservedGrantCount + ActiveUseCount);
}

public enum HostPublicResourceUnloadReason : byte
{
    ZeroSubscribers = 1,
    CapacityShortage = 2
}

public sealed record HostPublicResourceManagerOptions(
    uint SubscriberWeight = 400,
    uint ActivityWeight = 600,
    uint WeightScale = 1000,
    uint SubscriberHalfSaturation = 4,
    uint ActivityHalfSaturation = 8);

public enum HostPublicResourceCapacityObservationState : byte
{
    UnknownOrStale = 0,
    HealthyFresh = 1,
    ShortageFresh = 2,
    Disabled = 3
}

public readonly record struct HostPublicResourceCapacityObservation
{
    public HostPublicResourceCapacityObservation(
        HostPublicResourceCapacityObservationState state,
        bool afterNormalReleaseRounds = false)
    {
        if (state is < HostPublicResourceCapacityObservationState.UnknownOrStale
            or > HostPublicResourceCapacityObservationState.Disabled)
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        if (afterNormalReleaseRounds
            && state != HostPublicResourceCapacityObservationState.ShortageFresh)
        {
            throw new ArgumentException(
                "Completed normal-release rounds require a fresh shortage observation.",
                nameof(afterNormalReleaseRounds));
        }

        State = state;
        AfterNormalReleaseRounds = afterNormalReleaseRounds;
    }

    public HostPublicResourceCapacityObservationState State { get; }

    public bool AfterNormalReleaseRounds { get; }

    public bool CurrentShortage =>
        State == HostPublicResourceCapacityObservationState.ShortageFresh;

    public bool RecallAllowed =>
        State == HostPublicResourceCapacityObservationState.HealthyFresh;

    public static HostPublicResourceCapacityObservation UnknownOrStale { get; } =
        new(HostPublicResourceCapacityObservationState.UnknownOrStale);

    public static HostPublicResourceCapacityObservation HealthyFresh { get; } =
        new(HostPublicResourceCapacityObservationState.HealthyFresh);

    public static HostPublicResourceCapacityObservation Disabled { get; } =
        new(HostPublicResourceCapacityObservationState.Disabled);

    public static HostPublicResourceCapacityObservation ShortageFresh(
        bool afterNormalReleaseRounds = false)
        => new(
            HostPublicResourceCapacityObservationState.ShortageFresh,
            afterNormalReleaseRounds);
}

public readonly record struct HostPublicResourceAdapterCapacityObservation(
    ulong AdapterKey,
    HostPublicResourceCapacityObservation Capacity);

public readonly record struct HostPublicResourceCapacityShortage
{
    private readonly IReadOnlyList<HostPublicResourceAdapterCapacityObservation>?
        videoMemoryAdapters;

    public HostPublicResourceCapacityShortage(
        HostPublicResourceCapacityObservation memory,
        HostPublicResourceCapacityObservation videoMemory,
        IReadOnlyList<HostPublicResourceAdapterCapacityObservation>? videoMemoryAdapters = null)
    {
        Memory = memory;
        VideoMemory = videoMemory;
        var copiedAdapters = videoMemoryAdapters is null
            ? []
            : videoMemoryAdapters.ToArray();
        this.videoMemoryAdapters = Array.AsReadOnly(copiedAdapters);

        var adapterKeys = new HashSet<ulong>();
        var anyShortage = false;
        foreach (var adapter in this.videoMemoryAdapters)
        {
            if (adapter.AdapterKey == 0 || !adapterKeys.Add(adapter.AdapterKey))
            {
                throw new ArgumentException(
                    "Video-memory adapter observations require unique non-zero adapter keys.",
                    nameof(videoMemoryAdapters));
            }
            if (adapter.Capacity.State is not (
                    HostPublicResourceCapacityObservationState.HealthyFresh
                    or HostPublicResourceCapacityObservationState.ShortageFresh))
            {
                throw new ArgumentException(
                    "Exact video-memory adapter observations must be fresh.",
                    nameof(videoMemoryAdapters));
            }
            anyShortage |= adapter.Capacity.CurrentShortage;
        }

        if (this.videoMemoryAdapters.Count != 0
            && videoMemory.State != (anyShortage
                ? HostPublicResourceCapacityObservationState.ShortageFresh
                : HostPublicResourceCapacityObservationState.HealthyFresh))
        {
            throw new ArgumentException(
                "The video-memory summary must conservatively match its exact adapter observations.",
                nameof(videoMemory));
        }
    }

    public HostPublicResourceCapacityObservation Memory { get; }

    public HostPublicResourceCapacityObservation VideoMemory { get; }

    public IReadOnlyList<HostPublicResourceAdapterCapacityObservation> VideoMemoryAdapters =>
        videoMemoryAdapters ?? Array.Empty<HostPublicResourceAdapterCapacityObservation>();

    public bool MemoryCurrentShortage => Memory.CurrentShortage;

    public bool VideoMemoryCurrentShortage => VideoMemory.CurrentShortage;

    public bool MemoryAfterNormalReleaseRounds => Memory.AfterNormalReleaseRounds;

    public bool VideoMemoryAfterNormalReleaseRounds =>
        VideoMemory.AfterNormalReleaseRounds;

    public HostPublicResourceCapacityObservation VideoMemoryForAdapter(
        ulong adapterKey)
    {
        if (adapterKey == 0)
        {
            return HostPublicResourceCapacityObservation.UnknownOrStale;
        }

        foreach (var adapter in videoMemoryAdapters
            ?? Array.Empty<HostPublicResourceAdapterCapacityObservation>())
        {
            if (adapter.AdapterKey == adapterKey)
            {
                return adapter.Capacity;
            }
        }

        return HostPublicResourceCapacityObservation.UnknownOrStale;
    }
}

public readonly record struct HostPublicResourceUnloadCandidate(
    SharedResourceId ResourceId,
    ulong SchedulingRevision,
    ulong GateEpoch,
    ulong SizeBytes,
    uint RetentionScoreQ16,
    int SubscriberCount,
    byte ActivityScore,
    HostPublicResourceUnloadReason Reason,
    SharedResourceFlags Flags);

public sealed record HostPublicResourceUnloadPlan(
    ulong TopologyGeneration,
    ulong SampleGeneration,
    int ResourceCount,
    int ProtectedResourceCount,
    int ZeroSubscriberCandidateCount,
    int CapacityCandidateCount,
    IReadOnlyList<HostPublicResourceUnloadCandidate> Candidates);

public readonly record struct HostPublicResourceRecallCandidate(
    SharedResourceId ResourceId,
    ulong SchedulingRevision,
    ulong GateEpoch,
    ulong PayloadGeneration,
    ulong AdapterKey,
    uint RetentionScoreQ16,
    int SubscriberCount,
    byte ActivityScore,
    AdapterResourceTier Tier,
    SharedResourceFlags Flags);

public sealed record HostPublicResourceManagementPlan(
    ulong TopologyGeneration,
    ulong SampleGeneration,
    int ResourceCount,
    int ProtectedResourceCount,
    int ZeroSubscriberUnloadCount,
    int CapacityUnloadCount,
    IReadOnlyList<HostPublicResourceUnloadCandidate> UnloadCandidates,
    IReadOnlyList<HostPublicResourceRecallCandidate> RecallCandidates);

public readonly record struct SharedResourceSubscriptionReceipt(
    SharedResourceId ResourceId,
    uint SubscriptionSlot,
    ulong SubscriptionGeneration,
    ulong SubscriberInstanceId,
    ulong SubscriberSessionId,
    int SubscriberProcessId,
    ulong DeadlineTimestamp,
    SharedResourceSubscriptionIntent Intent);

public sealed record SharedResourceUseRequest(
    SharedResourceId ResourceId,
    ulong RequestKey,
    ulong RequesterApplicationKey,
    ulong RequesterInstanceId,
    ulong RequesterSessionId,
    int RequesterProcessId,
    ulong ScorePlanGeneration,
    double BaseScore,
    TimeSpan QueueLeaseDuration);

public readonly record struct SharedResourceTaskReceipt(
    SharedResourceId ResourceId,
    ulong RequestKey,
    ulong RequesterApplicationKey,
    ulong RequesterInstanceId,
    ulong RequesterSessionId,
    uint TaskSlot,
    ulong TaskGeneration,
    SharedResourceTaskState State);

public readonly record struct SharedResourceGrantReceipt(
    SharedResourceTaskReceipt Task,
    ulong GrantGeneration,
    ulong PayloadGeneration,
    ulong DeadlineTimestamp);

public readonly record struct SharedResourceDestructiveExpected(
    SharedResourceId ResourceId,
    ulong SchedulingRevision,
    SharedResourceAuthorityId ActionAttemptId,
    AdapterResourceActionMask Action);

public readonly record struct SharedResourceDestructiveToken(
    SharedResourceId ResourceId,
    ulong SchedulingRevision,
    ulong GateEpoch,
    SharedResourceAuthorityId ActionAttemptId,
    AdapterResourceActionMask Action);

public readonly record struct SharedResourceRecallExpected(
    SharedResourceId ResourceId,
    ulong SchedulingRevision,
    ulong GateEpoch,
    SharedResourceAuthorityId ActionAttemptId,
    ulong PayloadGeneration,
    int SubscriberCount,
    byte ActivityScore,
    SharedResourceFlags Flags);

public readonly record struct SharedResourceRecallToken(
    SharedResourceId ResourceId,
    ulong SchedulingRevision,
    ulong GateEpoch,
    SharedResourceAuthorityId ActionAttemptId,
    ulong PayloadGeneration);

public enum SharedResourceNoEffectReason : byte
{
    ExecutorRejectedBeforeEffect = 1,
    SourceUnavailableBeforeEffect = 2,
    ReadbackMatchesExpected = 3
}

[Flags]
public enum SharedResourceNoEffectProof : byte
{
    None = 0,
    NotInvoked = 1 << 0,
    ReadbackMatchesExpected = 1 << 1
}

public readonly record struct SharedResourceDestructiveNoEffectReceipt(
    SharedResourceAuthorityId ActionAttemptId,
    SharedResourceAuthorityId ReceiptId,
    ulong ObservedMonotonicTimestamp,
    SharedResourceNoEffectProof Proof,
    SharedResourceNoEffectReason Reason);
