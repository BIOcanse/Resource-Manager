using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSmartCoordinatorSnapshotContractTests
{
    [Fact]
    public void ValidateNativeSnapshot_AcceptsExactPlanEnvelope()
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();

        HostManagerSmartCoordinator.ValidateNativeSnapshot(
            snapshot,
            capacity,
            capacity.ConfigurationGeneration,
            snapshot.CycleSequence,
            snapshot.ObservedAtMilliseconds,
            snapshot.ObservedAtMilliseconds,
            0,
            expectedMaximumActionsThisCycle: 2,
            requireNoActions: false,
            expectedScoreOnly: false);
    }

    [Fact]
    public void ValidateNativeSnapshot_UsesFeedbackCompletionAsWakeBase()
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();
        snapshot.ActionCount = 0;
        snapshot.NextWakeAtMilliseconds = 2_200;

        HostManagerSmartCoordinator.ValidateNativeSnapshot(
            snapshot,
            capacity,
            capacity.ConfigurationGeneration,
            snapshot.CycleSequence,
            snapshot.ObservedAtMilliseconds,
            expectedScheduleBaseAtMilliseconds: 1_200,
            snapshot.PlanEpoch,
            expectedMaximumActionsThisCycle: 2,
            requireNoActions: true,
            expectedScoreOnly: false);
    }

    [Fact]
    public void ValidateNativeSnapshot_AcceptsDueWakeWithZeroDelay()
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();
        snapshot.WakeAfterMilliseconds = 0;
        snapshot.NextWakeAtMilliseconds = snapshot.ObservedAtMilliseconds;

        HostManagerSmartCoordinator.ValidateNativeSnapshot(
            snapshot,
            capacity,
            capacity.ConfigurationGeneration,
            snapshot.CycleSequence,
            snapshot.ObservedAtMilliseconds,
            snapshot.ObservedAtMilliseconds,
            0,
            expectedMaximumActionsThisCycle: 2,
            requireNoActions: false,
            expectedScoreOnly: false);
    }

    [Fact]
    public void ValidateNativeSnapshot_AcceptsPlanTruncatedToCycleLimit()
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();
        snapshot.ActionCount = 1;

        HostManagerSmartCoordinator.ValidateNativeSnapshot(
            snapshot,
            capacity,
            capacity.ConfigurationGeneration,
            snapshot.CycleSequence,
            snapshot.ObservedAtMilliseconds,
            snapshot.ObservedAtMilliseconds,
            0,
            expectedMaximumActionsThisCycle: 1,
            requireNoActions: false,
            expectedScoreOnly: false);
    }

    [Fact]
    public void ValidateNativeSnapshot_RejectsPlanActionCountAboveCycleLimit()
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeSnapshot(
                snapshot,
                capacity,
                capacity.ConfigurationGeneration,
                snapshot.CycleSequence,
                snapshot.ObservedAtMilliseconds,
                snapshot.ObservedAtMilliseconds,
                0,
                expectedMaximumActionsThisCycle: 1,
                requireNoActions: false,
                expectedScoreOnly: false));
    }

    [Theory]
    [InlineData((ulong)NativeSmartCoordinatorReason.ReservedProcessReason5)]
    [InlineData((ulong)NativeSmartCoordinatorReason.ReservedProcessReason15)]
    public void ValidateNativeSnapshot_RejectsReservedReasonBits(
        ulong reservedReason)
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();
        snapshot.ReasonMask = (NativeSmartCoordinatorReason)reservedReason;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeSnapshot(
                snapshot,
                capacity,
                capacity.ConfigurationGeneration,
                snapshot.CycleSequence,
                snapshot.ObservedAtMilliseconds,
                snapshot.ObservedAtMilliseconds,
                0,
                expectedMaximumActionsThisCycle: 2,
                requireNoActions: false,
                expectedScoreOnly: false));
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(3U)]
    public void ValidateNativeSnapshot_RejectsInvalidCycleLimit(uint cycleLimit)
    {
        var capacity = CreateCapacity();
        var snapshot = CreatePlanSnapshot();

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeSnapshot(
                snapshot,
                capacity,
                capacity.ConfigurationGeneration,
                snapshot.CycleSequence,
                snapshot.ObservedAtMilliseconds,
                snapshot.ObservedAtMilliseconds,
                0,
                expectedMaximumActionsThisCycle: cycleLimit,
                requireNoActions: false,
                expectedScoreOnly: false));
    }

    [Fact]
    public void ValidateNativeSnapshotRows_RejectsMetricValidityOnProcessState()
    {
        var snapshot = CreateRowSnapshot();
        var row = CreateProcessRow();
        row.ValidMask |= NativeSmartCoordinatorInputValidity.Metric;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeSnapshotRows([row], snapshot));
    }

    [Fact]
    public void ValidateNativeSnapshotRows_RejectsAdapterAppliedFactWithoutSoftwareIdentity()
    {
        var snapshot = CreateRowSnapshot();
        var row = CreateProcessRow();
        row.ValidMask |= NativeSmartCoordinatorInputValidity.AppliedCpuGrade;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeSnapshotRows([row], snapshot));
    }

    [Theory]
    [InlineData((ulong)NativeSmartCoordinatorReason.ReservedProcessReason5)]
    [InlineData((ulong)NativeSmartCoordinatorReason.ReservedProcessReason15)]
    public void ValidateNativeSnapshotRows_RejectsReservedReasonBits(
        ulong reservedReason)
    {
        var snapshot = CreateRowSnapshot();
        var row = CreateProcessRow();
        row.ReasonMask = (NativeSmartCoordinatorReason)reservedReason;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeSnapshotRows([row], snapshot));
    }

    private static NativeSmartCoordinatorCapacity CreateCapacity()
        => new()
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCapacity>(),
            ConfigurationGeneration = 14UL << 32,
            InputRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            CycleInputStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCycleInput>(),
            ActionStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            FeedbackStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>(),
            SnapshotStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshot>(),
            SnapshotRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshotRow>(),
            InputRowCapacity = 2,
            ActionCapacity = 2,
            FeedbackCapacity = 2,
            SnapshotRowCapacity = 2,
            ProcessCapacity = 1,
            SoftwareCapacity = 1,
            GpuStateCapacity = 1,
            AtomicGroupCapacity = 1
        };

    private static NativeSmartCoordinatorSnapshot CreatePlanSnapshot()
        => new()
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshot>(),
            SnapshotRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshotRow>(),
            ConfigurationGeneration = 14UL << 32,
            CycleSequence = 3,
            PlanEpoch = 5,
            StateRevision = 7,
            ObservedAtMilliseconds = 1_000,
            NextWakeAtMilliseconds = 2_000,
            WakeAfterMilliseconds = 1_000,
            ActionCount = 2,
            ProcessCount = 1,
            SoftwareCount = 1,
            SnapshotRowCount = 2
        };

    private static NativeSmartCoordinatorSnapshot CreateRowSnapshot()
        => new()
        {
            CycleSequence = 3,
            ProcessCount = 1,
            SnapshotRowCount = 1
        };

    private static NativeSmartCoordinatorSnapshotRow CreateProcessRow()
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorSnapshotRow>(),
            ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity,
            TargetKey = 1,
            ProcessStartKey = 2,
            LastSeenCycle = 3,
            ProcessId = 4,
            RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
            SoftwareKind = NativeSmartCoordinatorSoftwareKind.Unknown,
            RuntimeState = NativeSmartCoordinatorRuntimeState.Unknown,
            DesiredProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            PendingProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            DesiredCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            PendingCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            DesiredGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            PendingGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };
}
