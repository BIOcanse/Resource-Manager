namespace ResourceManager.Adapter.LocalResources;

public static class LocalResourceManagerProtocol
{
    public const uint Version = 10;
}

public enum LocalResourceOperationKind : byte
{
    Maintenance = 1,
    Cleanup = 2,
    PartitionAdmission = 3,
    PartitionResourceAdmission = 4
}

public enum LocalResourceOperationPhase : byte
{
    Inactive = 0,
    Planning = 1,
    Executing = 2,
    Settling = 3,
    RecoveryRequired = 4
}

public readonly record struct LocalResourceOperationToken(
    ulong ManagerInstanceId,
    ulong OperationId,
    ulong OperationGeneration,
    ulong StartSnapshotGeneration,
    LocalResourceOperationKind Kind);

public readonly record struct LocalResourceOperationSnapshot(
    LocalResourceOperationToken? Token,
    ulong CurrentSnapshotGeneration,
    int SettledExecutionCount,
    int ConfirmedEffectCount,
    int PendingCount,
    int ReservedExecutionCount,
    int StartedExecutionCount,
    int UncertainExecutionCount,
    LocalResourceOperationPhase Phase);

public readonly record struct LocalResourceId(ulong Low, ulong High = 0)
{
    public bool IsEmpty => (Low | High) == 0;
}

public enum LocalResourceDomain : byte
{
    Memory = 0,
    Gpu = 1
}

public enum LocalResourceCapacityStrategy : byte
{
    Concentrated = 0,
    Smooth = 1
}

public enum LocalResourceCleanupMode : byte
{
    Unrestricted = 0,
    Normal = 1,
    Optimize = 2,
    ReleaseAll = 3
}

public enum LocalResourceSoftwareMemoryMode : byte
{
    Unrestricted = 0,
    Normal = 1,
    Optimize = 2,
    PagedFrozen = 3
}

public enum LocalResourceRecoverability : byte
{
    NotRecoverable = 0,
    Recoverable = 1
}

public enum LocalResourceAccessLossImpact : byte
{
    Fatal = 0,
    ObservableNow = 1,
    UnobservableNow = 2
}

public enum LocalResourceEffectOutcome : byte
{
    Applied = 0,
    NoEffect = 1,
    Rejected = 2,
    Busy = 3,
    Failed = 4,
    EffectUnknown = 5
}

[Flags]
public enum LocalResourceEffects : byte
{
    None = 0,
    ReleasesLedgerSlot = 1 << 0,
    ChangesSizeBytes = 1 << 1,
    ChangesTier = 1 << 2
}

[Flags]
public enum LocalResourceIntentReason : byte
{
    None = 0,
    Capacity = 1 << 0,
    Mode = 1 << 1,
    Partition = 1 << 2
}

public enum LocalResourcePartitionCellKind : byte
{
    Empty = 0,
    Resource = 1,
    ChildPartition = 2
}

public enum LocalResourcePartitionAdmissionStatus : byte
{
    Admitted = 0,
    Blocked = 1,
    EffectUncertain = 2,
    Canceled = 3,
    CommitRejected = 4
}

public enum LocalResourcePartitionCloseStatus : byte
{
    Closed = 0,
    Blocked = 1,
    EffectUncertain = 2,
    Canceled = 3,
    CommitRejected = 4
}

[Flags]
public enum LocalResourceTableStopReason : ushort
{
    None = 0,
    PendingCapacityExhausted = 1 << 0,
    ResourceBusy = 1 << 1,
    Stale = 1 << 2,
    CancellationRequested = 1 << 3,
    CapacityGoalNotReached = 1 << 4,
    NoEligibleCandidate = 1 << 5,
    EffectUncertain = 1 << 6,
    ModeSuperseded = 1 << 7,
    OperationRecoveryRequired = 1 << 8
}

public sealed record LocalResourceCapacityPolicy(
    uint ConcentratedTriggerFreePercent = 10,
    uint ConcentratedTargetFreePercent = 20,
    uint SmoothTriggerFreePercent = 15,
    uint SmoothEmergencyFreePercent = 5,
    int SmoothMaximumReleasesPerInterval = 1)
{
    public static LocalResourceCapacityPolicy Default { get; } = new();
}

public sealed record LocalResourceManagerConfiguration(
    ulong Generation,
    int TableCapacity,
    int ResourceCapacity,
    int CapabilityCapacity,
    int PendingCapacity,
    int UidBucketCapacity,
    int MaximumConcurrentTables,
    int SmoothReleaseIntervalEpochs,
    uint ActivityDecayNumerator,
    uint ActivityDecayDenominator,
    LocalResourceCapacityPolicy? CapacityPolicy = null,
    int PartitionCapacity = 0)
{
    public LocalResourceCapacityPolicy EffectiveCapacityPolicy =>
        CapacityPolicy ?? LocalResourceCapacityPolicy.Default;
    public int EffectivePartitionCapacity =>
        PartitionCapacity == 0 ? ResourceCapacity : PartitionCapacity;

    public static LocalResourceManagerConfiguration CreateDefault(
        int tableCapacity,
        int resourceCapacity,
        ulong generation = 1,
        LocalResourceCapacityPolicy? capacityPolicy = null)
    {
        if (tableCapacity <= 0 || resourceCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                tableCapacity <= 0 ? nameof(tableCapacity) : nameof(resourceCapacity));
        }

        var bucketCapacity = 1;
        var minimumBuckets = checked(resourceCapacity * 2);
        while (bucketCapacity < minimumBuckets) bucketCapacity = checked(bucketCapacity * 2);
        return new(
            generation,
            tableCapacity,
            resourceCapacity,
            checked(tableCapacity * 4),
            tableCapacity,
            bucketCapacity,
            Math.Min(tableCapacity, 4),
            SmoothReleaseIntervalEpochs: 2,
            ActivityDecayNumerator: 1,
            ActivityDecayDenominator: 2,
            CapacityPolicy: capacityPolicy,
            PartitionCapacity: resourceCapacity);
    }
}

public readonly record struct LocalResourceTableDefinition(
    LocalResourceId TableId,
    ulong TableIncarnation,
    LocalResourceDomain Domain,
    int Capacity,
    LocalResourceId AdapterKey = default,
    ulong TopologyGeneration = 0,
    LocalResourcePartitionReservation? PartitionReservation = null);

public readonly record struct LocalResourcePartitionReservation(
    int Start,
    int Capacity);

public readonly record struct LocalResourceCapabilityDefinition(
    ulong CapabilityId,
    uint ActionCode,
    LocalResourceEffects ExpectedEffects,
    bool Destructive);

public readonly record struct LocalResourceDefinition(
    LocalResourceId ResourceUid,
    ulong SizeBytes,
    uint RecoveryCostCoefficient,
    LocalResourceRecoverability Recoverability,
    LocalResourceAccessLossImpact AccessLossImpact);

public readonly record struct LocalResourceModeRequest(
    LocalResourceTableHandle Table,
    LocalResourceCleanupMode Mode,
    int MaximumIntents,
    ulong Generation = 0);

public readonly record struct LocalResourceModeState(
    LocalResourceTableHandle Table,
    LocalResourceCleanupMode Mode,
    int MaximumIntentsPerTick,
    ulong Generation);

public readonly record struct LocalResourceSoftwareMemoryModeState(
    LocalResourceSoftwareMemoryMode Mode,
    int MaximumIntentsPerTick,
    ulong Generation);

public readonly record struct LocalResourceEffect(
    LocalResourceEffectOutcome Outcome,
    LocalResourceEffects Changes = LocalResourceEffects.None,
    ulong SizeBytesAfter = 0,
    ulong ReleasedBytes = 0)
{
    public static LocalResourceEffect Applied(
        LocalResourceEffects changes,
        ulong sizeBytesAfter = 0,
        ulong releasedBytes = 0)
        => new(LocalResourceEffectOutcome.Applied, changes, sizeBytesAfter, releasedBytes);

    public static LocalResourceEffect NoEffect => new(LocalResourceEffectOutcome.NoEffect);
    public static LocalResourceEffect Busy => new(LocalResourceEffectOutcome.Busy);
    public static LocalResourceEffect Failed => new(LocalResourceEffectOutcome.Failed);
    public static LocalResourceEffect Unknown => new(LocalResourceEffectOutcome.EffectUnknown);
}

public readonly record struct LocalResourceExecutionContext(
    LocalResourceId TableId,
    ulong TableIncarnation,
    LocalResourceId ResourceUid,
    ulong CapabilityId,
    ulong CapabilityGeneration,
    uint ActionCode,
    LocalResourceEffects ExpectedEffects,
    LocalResourceIntentReason Reason,
    ulong SizeBytes);

public delegate ValueTask<LocalResourceEffect> LocalResourceCapabilityHandler(
    LocalResourceExecutionContext context,
    CancellationToken cancellationToken);

public readonly record struct LocalResourceCommitResult(
    ulong SnapshotGeneration,
    ulong TableRevision,
    bool ResourceSlotReleased,
    bool EffectUncertain);

public readonly record struct LocalResourceExecutionResult(
    LocalResourceCommitResult Commit,
    LocalResourceUncertainExecution? UncertainExecution)
{
    public bool ResourceSlotReleased => Commit.ResourceSlotReleased;
    public bool EffectUncertain => Commit.EffectUncertain;
}

internal sealed class LocalResourcePlanProducer;

internal enum LocalResourcePlanKind : byte
{
    Capacity = 1,
    Mode = 2,
    Merged = 3,
    Partition = 4,
}

public sealed class LocalResourcePlan
{
    internal LocalResourcePlan(
        LocalResourcePlanProducer producer,
        ulong managerInstanceId,
        ulong operationId,
        ulong operationGeneration,
        ulong snapshotGeneration,
        LocalResourcePlanKind kind,
        LocalResourceIntent[] intents,
        int affectedTableCount,
        int triggeredTableCount = 0,
        int stalledTableCount = 0,
        int emergencyTableCount = 0)
    {
        Producer = producer ?? throw new ArgumentNullException(nameof(producer));
        ManagerInstanceId = managerInstanceId;
        OperationId = operationId;
        OperationGeneration = operationGeneration;
        SnapshotGeneration = snapshotGeneration;
        Kind = kind;
        ArgumentNullException.ThrowIfNull(intents);
        Intents = Array.AsReadOnly((LocalResourceIntent[])intents.Clone());
        AffectedTableCount = affectedTableCount;
        TriggeredTableCount = triggeredTableCount;
        StalledTableCount = stalledTableCount;
        EmergencyTableCount = emergencyTableCount;
    }

    internal LocalResourcePlanProducer Producer { get; }
    internal LocalResourcePlanKind Kind { get; }
    public ulong ManagerInstanceId { get; }
    public ulong OperationId { get; }
    public ulong OperationGeneration { get; }
    public ulong SnapshotGeneration { get; }
    public IReadOnlyList<LocalResourceIntent> Intents { get; }
    public int AffectedTableCount { get; }
    public int TriggeredTableCount { get; }
    public int StalledTableCount { get; }
    public int EmergencyTableCount { get; }
}

internal sealed record LocalResourceCapacityPlanningResult(
    LocalResourcePlan Plan,
    IReadOnlyList<LocalResourceCapacityTablePlan> TriggeredTables);

internal readonly record struct LocalResourceCapacityTablePlan(
    LocalResourceTableHandle Table,
    LocalResourceTableCapacity Capacity,
    bool Affected,
    bool Stalled,
    bool Emergency);

public sealed record LocalResourceManagerTickResult(
    LocalResourcePlan CapacityPlan,
    int CapacityActionCount,
    int ModeActionCount,
    IReadOnlyList<(LocalResourceId TableId, ulong TableIncarnation)> StoppedTables,
    IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions,
    IReadOnlyList<LocalResourceTableTickResult> Tables,
    bool CancellationObserved = false);

public sealed record LocalResourceTableTickResult(
    LocalResourceId TableId,
    ulong TableIncarnation,
    LocalResourceTableCapacity BeforeCapacity,
    LocalResourceTableCapacity AfterCapacity,
    int CapacityActionCount,
    int ModeActionCount,
    int ReleasedSlotCount,
    bool CapacityGoalReached,
    bool Stopped,
    bool EffectUncertain)
{
    public LocalResourceTableStopReason StopReasons { get; init; }
}

public readonly struct LocalResourceTableHandle
{
    internal LocalResourceTableHandle(NativeLocalResourceTableHandle value) => Native = value;
    internal NativeLocalResourceTableHandle Native { get; }
    public LocalResourceId TableId => new(Native.TableId.Low, Native.TableId.High);
    public ulong TableIncarnation => Native.TableIncarnation;
}

public readonly struct LocalResourceCapabilityHandle
{
    internal LocalResourceCapabilityHandle(NativeLocalResourceCapabilityHandle value) => Native = value;
    internal NativeLocalResourceCapabilityHandle Native { get; }
    public ulong CapabilityId => Native.CapabilityId;
    public ulong Generation => Native.SlotGeneration;
}

public readonly struct LocalResourceHandle
{
    internal LocalResourceHandle(NativeLocalResourceHandle value) => Native = value;
    internal NativeLocalResourceHandle Native { get; }
    public LocalResourceId TableId => new(Native.TableId.Low, Native.TableId.High);
    public ulong TableIncarnation => Native.TableIncarnation;
    public LocalResourceId ResourceUid => new(Native.ResourceUid.Low, Native.ResourceUid.High);
}

public readonly struct LocalResourcePartitionHandle : IEquatable<LocalResourcePartitionHandle>
{
    internal LocalResourcePartitionHandle(NativeLocalResourcePartitionHandle value) => Native = value;
    internal NativeLocalResourcePartitionHandle Native { get; }
    public LocalResourceId TableId => new(Native.TableId.Low, Native.TableId.High);
    public ulong TableIncarnation => Native.TableIncarnation;
    public ulong Generation => Native.PartitionSlotGeneration;
    public uint SlotIndex => Native.PartitionSlotIndex;
    public bool IsEmpty => Native.ManagerInstanceId == 0;
    internal LocalResourceTableHandle TableHandle => new(new()
    {
        ManagerInstanceId = Native.ManagerInstanceId,
        SlotGeneration = Native.TableSlotGeneration,
        TableId = Native.TableId,
        TableIncarnation = Native.TableIncarnation,
        SlotIndex = Native.TableSlotIndex
    });

    public bool Equals(LocalResourcePartitionHandle other) => Native.Equals(other.Native);
    public override bool Equals(object? obj) =>
        obj is LocalResourcePartitionHandle other && Equals(other);
    public override int GetHashCode() => Native.GetHashCode();
    public static bool operator ==(
        LocalResourcePartitionHandle left,
        LocalResourcePartitionHandle right) => left.Equals(right);
    public static bool operator !=(
        LocalResourcePartitionHandle left,
        LocalResourcePartitionHandle right) => !left.Equals(right);
}

public readonly struct LocalResourceUseLease
{
    internal LocalResourceUseLease(NativeLocalResourceUseLease value) => Native = value;
    internal NativeLocalResourceUseLease Native { get; }
}

public readonly struct LocalResourceIntent
{
    internal LocalResourceIntent(NativeLocalResourceIntent value) => Native = value;
    internal NativeLocalResourceIntent Native { get; }
    public LocalResourceId TableId => new(Native.Resource.TableId.Low, Native.Resource.TableId.High);
    public ulong TableIncarnation => Native.Resource.TableIncarnation;
    public LocalResourceId ResourceUid => new(Native.Resource.ResourceUid.Low, Native.Resource.ResourceUid.High);
    public ulong CapabilityId => Native.CapabilityId;
    public ulong CapabilityGeneration => Native.CapabilityGeneration;
    public uint ActionCode => Native.ActionCode;
    public LocalResourceEffects ExpectedEffects => (LocalResourceEffects)Native.ExpectedEffects;
    public LocalResourceIntentReason Reason => (LocalResourceIntentReason)Native.ReasonFlags;
    public ulong SizeBytes => Native.SizeBytes;
    public ulong OperationId => Native.OperationId;
    public ulong OperationGeneration => Native.OperationGeneration;
    internal LocalResourceTableHandle TableHandle => new(new()
    {
        ManagerInstanceId = Native.Resource.ManagerInstanceId,
        SlotGeneration = Native.Resource.TableSlotGeneration,
        TableId = Native.Resource.TableId,
        TableIncarnation = Native.Resource.TableIncarnation,
        SlotIndex = Native.Resource.TableSlotIndex
    });
}

public readonly record struct LocalResourceTableCapacity(
    int Capacity,
    int OccupiedCount,
    int FreeCount,
    int ActivePendingCount,
    int DirectCapacity,
    int DirectOccupiedCount,
    int DirectFreeCount,
    int PartitionReservationStart,
    int PartitionReservationCapacity,
    int PartitionOccupiedCount,
    int PartitionFreeCount,
    ulong Revision);

public readonly record struct LocalResourceSnapshot(
    ulong SizeBytes,
    ulong SettledActivity,
    ulong RowRevision,
    ulong ActivityRevision,
    uint RecoveryCostCoefficient,
    LocalResourceRecoverability Recoverability,
    LocalResourceAccessLossImpact AccessLossImpact,
    bool ProtectedUse,
    bool Pending);

public readonly record struct LocalResourcePartitionSnapshot(
    LocalResourcePartitionHandle Partition,
    LocalResourcePartitionHandle? Parent,
    ulong SettledActivity,
    ulong StructureRevision,
    int LocalStart,
    int Capacity,
    int DirectChildCount,
    int DescendantResourceCount,
    bool ReclaimProtected);

public readonly record struct LocalResourcePartitionCell(
    LocalResourcePartitionHandle Partition,
    int LocalOrdinal,
    LocalResourcePartitionCellKind Kind,
    LocalResourceHandle? Resource,
    LocalResourcePartitionHandle? ChildPartition);

public readonly record struct LocalResourcePartitionResourcePlacement(
    LocalResourcePartitionHandle Partition,
    int LocalOrdinal,
    LocalResourceHandle Resource);

public sealed class LocalResourcePartitionAdmissionPlan
{
    internal LocalResourcePartitionAdmissionPlan(
        LocalResourcePlanProducer producer,
        ulong managerInstanceId,
        NativeLocalResourcePartitionAdmissionTicket ticket,
        LocalResourcePartitionHandle[] victims,
        LocalResourceIntent[] intents,
        ulong snapshotGeneration,
        int localStart,
        int capacity)
    {
        Producer = producer;
        ManagerInstanceId = managerInstanceId;
        Ticket = ticket;
        Victims = Array.AsReadOnly(victims);
        Intents = Array.AsReadOnly(intents);
        SnapshotGeneration = snapshotGeneration;
        LocalStart = localStart;
        Capacity = capacity;
    }

    internal LocalResourcePlanProducer Producer { get; }
    internal ulong ManagerInstanceId { get; }
    internal NativeLocalResourcePartitionAdmissionTicket Ticket { get; }
    public IReadOnlyList<LocalResourcePartitionHandle> Victims { get; }
    public IReadOnlyList<LocalResourceIntent> Intents { get; }
    public ulong SnapshotGeneration { get; }
    public int LocalStart { get; }
    public int Capacity { get; }
}

public sealed class LocalResourcePartitionClosePlan
{
    internal LocalResourcePartitionClosePlan(
        LocalResourcePlanProducer producer,
        ulong managerInstanceId,
        NativeLocalResourcePartitionAdmissionTicket ticket,
        LocalResourcePartitionHandle target,
        LocalResourceIntent[] intents,
        ulong snapshotGeneration,
        int localStart,
        int capacity)
    {
        Producer = producer;
        ManagerInstanceId = managerInstanceId;
        Ticket = ticket;
        Target = target;
        Intents = Array.AsReadOnly(intents);
        SnapshotGeneration = snapshotGeneration;
        LocalStart = localStart;
        Capacity = capacity;
    }

    internal LocalResourcePlanProducer Producer { get; }
    internal ulong ManagerInstanceId { get; }
    internal NativeLocalResourcePartitionAdmissionTicket Ticket { get; }
    public LocalResourcePartitionHandle Target { get; }
    public IReadOnlyList<LocalResourceIntent> Intents { get; }
    public ulong SnapshotGeneration { get; }
    public int LocalStart { get; }
    public int Capacity { get; }
}

internal readonly record struct LocalResourcePartitionResourceAdmissionPlan(
    LocalResourcePartitionHandle Partition,
    int LocalOrdinal,
    LocalResourceIntent? ReleaseIntent,
    ulong SnapshotGeneration,
    ulong StructureRevision,
    NativeLocalResourcePartitionResourceAdmission Native);

public sealed record LocalResourcePartitionAdmissionResult(
    LocalResourcePartitionAdmissionStatus Status,
    LocalResourcePartitionHandle? Partition,
    int InvokedActionCount,
    int ReleasedResourceCount,
    IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions)
{
    public LocalResourceManagerError? FailureError { get; init; }
}

public sealed record LocalResourcePartitionCloseResult(
    LocalResourcePartitionCloseStatus Status,
    int InvokedActionCount,
    int ReleasedResourceCount,
    IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions)
{
    public LocalResourceManagerError? FailureError { get; init; }
}

public sealed record LocalResourcePartitionResourceAdmissionResult(
    LocalResourcePartitionAdmissionStatus Status,
    LocalResourcePartitionResourcePlacement? Placement,
    int InvokedActionCount,
    int ReleasedResourceCount,
    IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions)
{
    public LocalResourceManagerError? FailureError { get; init; }
}
