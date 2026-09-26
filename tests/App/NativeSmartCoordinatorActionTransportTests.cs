using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSmartCoordinatorActionTransportTests
{
    [Fact]
    public void ValidateNativeAction_AcceptsExactProcessNoOpShape()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);

        HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot);
    }

    [Fact]
    public void ValidateNativeAction_AcceptsDiagnosticNoOpWithDifferentDesiredGrade()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);
        action.ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level3;

        HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot);
    }

    [Fact]
    public void ValidateNativeAction_IgnoresRawScoreWhenValidityIsAbsent()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);
        action.ValidMask &= ~NativeSmartCoordinatorActionValidity.CpuScore;
        action.CpuScore = 17.25;

        HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot);
    }

    [Fact]
    public void ValidateNativeAction_RejectsAnyUnknownValidityBit()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);
        action.ValidMask |= (NativeSmartCoordinatorActionValidity)(1UL << 63);

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeAction_RejectsSnapshotPlanEpochMismatch()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);
        action.PlanEpoch++;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeAction_RejectsAnyNonzeroReservedField()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);
        action.Reserved0 = 1;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeAction_RejectsExecutableAdapterWithoutExactGradeValidity()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAdapterActionWithoutScore(snapshot);
        action.ValidMask &= ~NativeSmartCoordinatorActionValidity.CpuGrade;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeAction_RejectsExecutableAdapterWithoutCurrentScore()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAdapterActionWithoutScore(snapshot);

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeAction_AcceptsIndependentAdapterCpuAndGpuScores()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAdapterActionWithoutScore(snapshot);
        action.ValidMask |= NativeSmartCoordinatorActionValidity.GpuGrade
            | NativeSmartCoordinatorActionValidity.CpuScore
            | NativeSmartCoordinatorActionValidity.GpuScore;
        action.DomainMask |= NativeSmartCoordinatorGradeDomains.Gpu;
        action.ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme;
        action.CpuScore = 3.25;
        action.GpuScore = 9.75;

        HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot);
    }

    [Fact]
    public void ValidateNativeAction_AcceptsNonAtomicExecutableLevel4Singleton()
    {
        var snapshot = CreateSnapshot();
        var action = CreateProcessAction(snapshot);
        action.Disposition = NativeSmartCoordinatorActionDisposition.Apply;
        action.Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback;
        action.ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level4;

        HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot);
    }

    [Fact]
    public void ValidateNativeAction_RejectsAtomicSingleton()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAtomicProcessAction(snapshot);
        action.GroupMemberCount = 1;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeAction_RejectsAtomicGroupWithoutSoftwareIdentity()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAtomicProcessAction(snapshot);
        action.ValidMask &= ~NativeSmartCoordinatorActionValidity.SoftwareIdentity;
        action.SoftwareKey = 0;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeAction(action, 0, snapshot));
    }

    [Fact]
    public void ValidateNativeFeedback_AcceptsExactSuccessfulProcessResult()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAtomicProcessAction(snapshot);
        var feedback = CreateSuccessfulProcessFeedback(action);

        HostManagerSmartCoordinator.ValidateNativeFeedback(feedback, action, 1_000);
    }

    [Fact]
    public void ValidateNativeFeedback_RejectsSuccessfulOwnershipWithoutRollbackPayload()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAtomicProcessAction(snapshot);
        var feedback = CreateSuccessfulProcessFeedback(action);
        feedback.Flags &= ~NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeFeedback(feedback, action, 1_000));
    }

    [Fact]
    public void ValidateNativeFeedback_RejectsOwnershipLostActualGrade()
    {
        var snapshot = CreateSnapshot();
        var action = CreateAtomicProcessAction(snapshot);
        var feedback = CreateSuccessfulProcessFeedback(action);
        feedback.Status = NativeSmartCoordinatorFeedbackStatus.OwnershipLost;
        feedback.Flags = NativeSmartCoordinatorFeedbackFlags.None;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeFeedback(feedback, action, 1_000));
    }

    [Theory]
    [InlineData((int)NativeSmartCoordinatorFeedbackStatus.Skipped)]
    [InlineData((int)NativeSmartCoordinatorFeedbackStatus.StateUncertain)]
    public void ValidateNativeFeedback_IndeterminateResultsRejectActualGradeAndProof(
        int status)
    {
        var snapshot = CreateSnapshot();
        var action = CreateAtomicProcessAction(snapshot);
        var feedback = CreateIndeterminateProcessFeedback(
            action,
            (NativeSmartCoordinatorFeedbackStatus)status);

        HostManagerSmartCoordinator.ValidateNativeFeedback(feedback, action, 1_000);

        var withActual = feedback;
        withActual.ValidMask |= NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade;
        withActual.ActualProcessGrade = action.FromProcessGrade;
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeFeedback(withActual, action, 1_000));

        var withProof = feedback;
        withProof.Flags = NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted;
        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ValidateNativeFeedback(withProof, action, 1_000));
    }

    private static NativeSmartCoordinatorSnapshot CreateSnapshot()
        => new()
        {
            ConfigurationGeneration = 8UL << 32,
            PlanEpoch = 7,
            WakeAfterMilliseconds = 1_000
        };

    private static NativeSmartCoordinatorAction CreateProcessAction(
        NativeSmartCoordinatorSnapshot snapshot)
        => new()
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>()),
            ValidMask = NativeSmartCoordinatorActionValidity.ProcessIdentity
                | NativeSmartCoordinatorActionValidity.SoftwareIdentity
                | NativeSmartCoordinatorActionValidity.ProcessGrade
                | NativeSmartCoordinatorActionValidity.CpuScore,
            ActionId = 3,
            PlanEpoch = snapshot.PlanEpoch,
            ConfigurationGeneration = snapshot.ConfigurationGeneration,
            TargetKey = 11,
            SoftwareKey = 22,
            ProcessStartKey = 33,
            CpuScore = 1,
            ProcessId = 44,
            OrderKey = 0,
            WakeAfterMilliseconds = snapshot.WakeAfterMilliseconds,
            Scope = NativeSmartCoordinatorActionScope.ProcessPolicy,
            Disposition = NativeSmartCoordinatorActionDisposition.NoOp,
            DomainMask = NativeSmartCoordinatorGradeDomains.Process,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };

    private static NativeSmartCoordinatorAction CreateAtomicProcessAction(
        NativeSmartCoordinatorSnapshot snapshot)
    {
        var action = CreateProcessAction(snapshot);
        action.Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback
            | NativeSmartCoordinatorActionFlags.Atomic;
        action.ValidMask |= NativeSmartCoordinatorActionValidity.AtomicGroup;
        action.Disposition = NativeSmartCoordinatorActionDisposition.Apply;
        action.ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level4;
        action.AtomicGroupId = 5;
        action.GroupMemberCount = 2;
        return action;
    }

    private static NativeSmartCoordinatorAction CreateAdapterActionWithoutScore(
        NativeSmartCoordinatorSnapshot snapshot) => new()
        {
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>()),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = NativeSmartCoordinatorActionValidity.SoftwareIdentity
            | NativeSmartCoordinatorActionValidity.CpuGrade,
            ActionId = 8,
            PlanEpoch = snapshot.PlanEpoch,
            ConfigurationGeneration = snapshot.ConfigurationGeneration,
            TargetKey = 42,
            SoftwareKey = 42,
            Scope = NativeSmartCoordinatorActionScope.AdapterSoftware,
            Disposition = NativeSmartCoordinatorActionDisposition.Apply,
            DomainMask = NativeSmartCoordinatorGradeDomains.Cpu,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            WakeAfterMilliseconds = snapshot.WakeAfterMilliseconds
        };

    private static NativeSmartCoordinatorFeedback CreateSuccessfulProcessFeedback(
        NativeSmartCoordinatorAction action)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>(),
            Flags = NativeSmartCoordinatorFeedbackFlags.ProcessOwned
                | NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted,
            ValidMask = NativeSmartCoordinatorFeedbackValidity.CompletedAt
                | NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade,
            ActionId = action.ActionId,
            PlanEpoch = action.PlanEpoch,
            ConfigurationGeneration = action.ConfigurationGeneration,
            TargetKey = action.TargetKey,
            SoftwareKey = action.SoftwareKey,
            ProcessStartKey = action.ProcessStartKey,
            CompletedAtMilliseconds = 1_001,
            ProcessId = action.ProcessId,
            Status = NativeSmartCoordinatorFeedbackStatus.Succeeded,
            Scope = action.Scope,
            ActualProcessGrade = action.ToProcessGrade
        };

    private static NativeSmartCoordinatorFeedback CreateIndeterminateProcessFeedback(
        NativeSmartCoordinatorAction action,
        NativeSmartCoordinatorFeedbackStatus status)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>(),
            ValidMask = NativeSmartCoordinatorFeedbackValidity.CompletedAt,
            ActionId = action.ActionId,
            PlanEpoch = action.PlanEpoch,
            ConfigurationGeneration = action.ConfigurationGeneration,
            TargetKey = action.TargetKey,
            SoftwareKey = action.SoftwareKey,
            ProcessStartKey = action.ProcessStartKey,
            CompletedAtMilliseconds = 1_001,
            ProcessId = action.ProcessId,
            Status = status,
            Scope = action.Scope
        };
}
