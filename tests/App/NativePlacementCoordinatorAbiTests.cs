using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class NativePlacementCoordinatorAbiTests
{
    [Fact]
    public void ReconfigureAcceptsChangedContentAtSameGenerationAndRollbackToPreviousConfiguration()
    {
        var original = CreateConfiguration(21);
        using var session = new NativePlacementCoordinatorSession(in original);
        var changed = original;
        changed.RetryDelayMilliseconds++;
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.Reconfigure(in changed));
        changed.Generation++;
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.Reconfigure(in changed));
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.Reconfigure(in original));
        using var owner = new ResourceManagerLedgerOwner();
        using var workspace = new NativePlacementCoordinatorWorkspace(in original, session.Capacity, owner.CreateScratch);
        using var operation = workspace.BeginOperation();
        using var view = workspace.CreateScratchView();
        var cycle = CreateCycle(original.Generation, 1, 1000, 0, 0, original.MaximumActionCount);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok,
            session.Plan(ref cycle, [], [], view.Actions));
    }

    [Fact]
    public void AbiUsesPublishedVersionSizesAndOffsets()
    {
        Assert.Equal(NativePlacementCoordinatorAbi.Version, NativePlacementCoordinatorSession.GetAbiVersion());
        Assert.Equal(80, Unsafe.SizeOf<NativePlacementCoordinatorConfiguration>());
        Assert.Equal(48, Unsafe.SizeOf<NativePlacementCoordinatorCapacity>());
        Assert.Equal(88, Unsafe.SizeOf<NativePlacementCoordinatorCycleInput>());
        Assert.Equal(72, Unsafe.SizeOf<NativePlacementDesiredInput>());
        Assert.Equal(88, Unsafe.SizeOf<NativePlacementAppliedInput>());
        Assert.Equal(96, Unsafe.SizeOf<NativePlacementAction>());
        Assert.Equal(64, Unsafe.SizeOf<NativePlacementFeedback>());
        Assert.Equal(96, Unsafe.SizeOf<NativePlacementSnapshotHeader>());
        Assert.Equal(112, Unsafe.SizeOf<NativePlacementState>());

        AssertOffset<NativePlacementCoordinatorConfiguration>(nameof(NativePlacementCoordinatorConfiguration.Generation), 8);
        AssertOffset<NativePlacementCoordinatorConfiguration>(nameof(NativePlacementCoordinatorConfiguration.MaximumDesiredCount), 16);
        AssertOffset<NativePlacementCoordinatorConfiguration>(nameof(NativePlacementCoordinatorConfiguration.RetryDelayMilliseconds), 32);
        AssertOffset<NativePlacementCoordinatorConfiguration>(nameof(NativePlacementCoordinatorConfiguration.Reserved), 64);
        AssertOffset<NativePlacementCoordinatorCycleInput>(nameof(NativePlacementCoordinatorCycleInput.ValidMask), 32);
        AssertOffset<NativePlacementCoordinatorCycleInput>(nameof(NativePlacementCoordinatorCycleInput.ActionCount), 52);
        AssertOffset<NativePlacementCoordinatorCycleInput>(nameof(NativePlacementCoordinatorCycleInput.Reserved), 72);
        AssertOffset<NativePlacementDesiredInput>(nameof(NativePlacementDesiredInput.DesiredDigest), 32);
        AssertOffset<NativePlacementDesiredInput>(nameof(NativePlacementDesiredInput.ValidMask), 56);
        AssertOffset<NativePlacementAppliedInput>(nameof(NativePlacementAppliedInput.CurrentDigest), 48);
        AssertOffset<NativePlacementAppliedInput>(nameof(NativePlacementAppliedInput.ValidMask), 72);
        AssertOffset<NativePlacementAction>(nameof(NativePlacementAction.ActionId), 8);
        AssertOffset<NativePlacementAction>(nameof(NativePlacementAction.ReasonMask), 72);
        AssertOffset<NativePlacementAction>(nameof(NativePlacementAction.Priority), 88);
        AssertOffset<NativePlacementAction>(nameof(NativePlacementAction.Reserved), 92);
        AssertOffset<NativePlacementFeedback>(nameof(NativePlacementFeedback.CompletedAtMilliseconds), 32);
        AssertOffset<NativePlacementFeedback>(nameof(NativePlacementFeedback.ValidMask), 56);
        AssertOffset<NativePlacementSnapshotHeader>(nameof(NativePlacementSnapshotHeader.ActiveStateCount), 40);
        AssertOffset<NativePlacementSnapshotHeader>(nameof(NativePlacementSnapshotHeader.Reserved), 80);
        AssertOffset<NativePlacementState>(nameof(NativePlacementState.PendingActionId), 56);
        AssertOffset<NativePlacementState>(nameof(NativePlacementState.Reserved), 96);
    }

    [Fact]
    public void SessionRoundTripAppliesAndSnapshotsWithoutManagedStateMachine()
    {
        var configuration = CreateConfiguration(0x0000_0011_0000_0000);
        using var session = new NativePlacementCoordinatorSession(in configuration);
        using var owner = new ResourceManagerLedgerOwner();
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity, owner.CreateScratch);
        using var operation = workspace.BeginOperation();
        using var view = workspace.CreateScratchView();
        view.Desired[0] = CreateDesired(101, 201, 301);
        var cycle = CreateCycle(configuration.Generation, 1, 1_000, 1, 0, configuration.MaximumActionCount);

        var result = session.Plan(
            ref cycle,
            view.Desired.Slice(0, 1),
            ReadOnlySpan<NativePlacementAppliedInput>.Empty,
            view.Actions);

        Assert.Equal(NativePlacementCoordinatorStatus.Ok, result);
        Assert.Equal(1U, cycle.ActionCount);
        var action = view.Actions[0];
        Assert.Equal(NativePlacementActionDisposition.Apply, (NativePlacementActionDisposition)action.Disposition);
        Assert.NotEqual(0UL, action.ActionId);

        var feedback = new NativePlacementFeedback
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementFeedback>(),
            Status = (uint)NativePlacementFeedbackStatus.Applied,
            ActionId = action.ActionId,
            TargetKey = action.TargetKey,
            RecordKey = action.RecordKey,
            CompletedAtMilliseconds = 1_010,
            ObservedDigest = action.DesiredDigest,
            ValidMask = (ulong)NativePlacementFeedbackValidity.ObservedDigest
        };
        result = session.ApplyFeedback([feedback]);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, result);

        var header = new NativePlacementSnapshotHeader
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementSnapshotHeader>()
        };
        var snapshotStates = new NativePlacementState[session.Capacity.SnapshotStateCapacity];
        result = session.GetSnapshot(ref header, snapshotStates);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, result);
        Assert.Equal(1U, header.ActiveStateCount);
        Assert.Equal(NativePlacementSlotState.Applied, (NativePlacementSlotState)snapshotStates[0].State);
        Assert.Equal(301UL, snapshotStates[0].AppliedDigest);
    }

    [Theory]
    [InlineData(5U)]
    [InlineData(7U)]
    public void UncheckedDurableReceiptIsConditionallyRestoredAndClearedByExactFeedback(uint placementKind)
    {
        var configuration = CreateConfiguration(0x0000_0012_0000_0000);
        using var session = new NativePlacementCoordinatorSession(in configuration);
        using var owner = new ResourceManagerLedgerOwner();
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity, owner.CreateScratch);
        using var operation = workspace.BeginOperation();
        using var view = workspace.CreateScratchView();
        view.Applied[0] = new NativePlacementAppliedInput
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementAppliedInput>(),
            Flags = (uint)NativePlacementAppliedFlags.PayloadValid,
            TargetKey = 401,
            RecordKey = 402,
            ResourceKind = (uint)NativePlacementResourceKind.Gpu,
            PlacementKind = placementKind,
            ReceiptDigest = 403,
            PreviousDigest = 404,
            ObservationStatus = (uint)NativePlacementObservationStatus.Unchecked,
            ValidMask = (ulong)NativePlacementAppliedValidity.Required
        };
        var cycle = CreateCycle(configuration.Generation, 1, 2_000, 0, 1, configuration.MaximumActionCount);

        var result = session.Plan(
            ref cycle,
            ReadOnlySpan<NativePlacementDesiredInput>.Empty,
            view.Applied.Slice(0, 1),
            view.Actions);

        Assert.Equal(NativePlacementCoordinatorStatus.Ok, result);
        var action = Assert.Single(view.Actions.Slice(0, checked((int)cycle.ActionCount)).ToArray());
        Assert.Equal(NativePlacementActionDisposition.Restore, (NativePlacementActionDisposition)action.Disposition);
        Assert.Equal(placementKind, action.PlacementKind);
        Assert.Equal(NativePlacementActionReason.DesiredMissing, (NativePlacementActionReason)action.ReasonMask);
        Assert.Equal(0U, action.Priority);

        view.Feedback[0] = new NativePlacementFeedback
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementFeedback>(),
            Status = (uint)NativePlacementFeedbackStatus.AlreadySatisfied,
            ActionId = action.ActionId,
            TargetKey = action.TargetKey,
            RecordKey = action.RecordKey,
            CompletedAtMilliseconds = 2_010
        };
        result = session.ApplyFeedback(view.Feedback.Slice(0, 1));
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, result);

        var header = new NativePlacementSnapshotHeader
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementSnapshotHeader>()
        };
        var snapshotStates = new NativePlacementState[session.Capacity.SnapshotStateCapacity];
        result = session.GetSnapshot(ref header, snapshotStates);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, result);
        Assert.Equal(0U, header.ActiveStateCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PrefixFeedbackSettlesOnlySubmittedRowsAndLeavesTheTailPending(int count)
    {
        var configuration = CreateConfiguration(0x0000_0013_0000_0000);
        using var session = new NativePlacementCoordinatorSession(in configuration);
        using var owner = new ResourceManagerLedgerOwner();
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity, owner.CreateScratch);
        using var operation = workspace.BeginOperation();
        using var view = workspace.CreateScratchView();
        for (var index = 0; index < 3; index++)
            view.Desired[index] = CreateDesired((ulong)(101 + index), (ulong)(201 + index), (ulong)(301 + index));
        var cycle = CreateCycle(configuration.Generation, 1, 1000, 3, 0, configuration.MaximumActionCount);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.Plan(ref cycle, view.Desired.Slice(0, 3), [], view.Actions));
        Assert.Equal(3U, cycle.ActionCount);
        for (var index = 0; index < count; index++)
        {
            var action = view.Actions[index];
            view.Feedback[index] = new()
            {
                StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementFeedback>(),
                Status = (uint)NativePlacementFeedbackStatus.Applied, ActionId = action.ActionId,
                TargetKey = action.TargetKey, RecordKey = action.RecordKey, CompletedAtMilliseconds = 1010,
                ObservedDigest = action.DesiredDigest, ValidMask = (ulong)NativePlacementFeedbackValidity.ObservedDigest
            };
        }
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.ApplyFeedback(view.Feedback.Slice(0, count)));
        var header = new NativePlacementSnapshotHeader
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementSnapshotHeader>()
        };
        var snapshotStates = new NativePlacementState[session.Capacity.SnapshotStateCapacity];
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.GetSnapshot(ref header, snapshotStates));
        Assert.Equal(3U, header.ActiveStateCount);
        var states = snapshotStates.AsSpan(0, 3).ToArray();
        for (var index = 0; index < 3; index++)
        {
            var action = view.Actions[index];
            var state = Assert.Single(states, item => item.TargetKey == action.TargetKey && item.RecordKey == action.RecordKey);
            Assert.Equal(index < count ? NativePlacementSlotState.Applied : NativePlacementSlotState.PendingApply, (NativePlacementSlotState)state.State);
            Assert.Equal(index < count ? 0UL : action.ActionId, state.PendingActionId);
            Assert.Equal(index < count ? 0UL : action.DeadlineMilliseconds, state.RetryAtMilliseconds);
        }
    }

    private static NativePlacementCoordinatorConfiguration CreateConfiguration(ulong generation)
        => new()
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorConfiguration>(),
            Generation = generation,
            MaximumDesiredCount = 8,
            MaximumAppliedCount = 8,
            MaximumActionCount = 8,
            MaximumStateCount = 8,
            RetryDelayMilliseconds = 100,
            ActionTimeoutMilliseconds = 50,
            MaximumFutureSkewMilliseconds = 5
        };

    private static NativePlacementDesiredInput CreateDesired(
        ulong targetKey,
        ulong recordKey,
        ulong desiredDigest)
        => new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementDesiredInput>(),
            TargetKey = targetKey,
            RecordKey = recordKey,
            ResourceKind = (uint)NativePlacementResourceKind.Cpu,
            PlacementKind = (uint)NativePlacementKind.CpuSets,
            DesiredDigest = desiredDigest,
            ProcessId = 11,
            ProcessStartKey = 22,
            ValidMask = (ulong)(NativePlacementDesiredValidity.Required |
                NativePlacementDesiredValidity.ProcessIdentity)
        };

    private static NativePlacementCoordinatorCycleInput CreateCycle(
        ulong generation,
        ulong epoch,
        ulong observedAt,
        uint desiredCount,
        uint appliedCount,
        uint actionCapacity)
        => new()
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorCycleInput>(),
            ConfigurationGeneration = generation,
            CycleEpoch = epoch,
            ObservedAtMilliseconds = observedAt,
            ValidMask = (ulong)NativePlacementCycleValidity.Required,
            DesiredCount = desiredCount,
            AppliedCount = appliedCount,
            ActionCapacity = actionCapacity
        };

    private static void AssertOffset<T>(string fieldName, int expected) where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
