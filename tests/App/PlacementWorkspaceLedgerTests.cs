using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class PlacementWorkspaceLedgerTests
{
    [Fact]
    public void EachRoundOpensExactRowsAndActionsAndReturnsThemAtItsEnd()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => { var region = owner.CreateScratch(bytes); regions.Add(region); return region; });
        Assert.Empty(regions); // Nothing is open before a round.
        var desiredWidth = (ulong)Unsafe.SizeOf<NativePlacementDesiredInput>();
        var appliedWidth = (ulong)Unsafe.SizeOf<NativePlacementAppliedInput>();
        var actionWidth = (ulong)Unsafe.SizeOf<NativePlacementAction>() + (ulong)Unsafe.SizeOf<NativePlacementFeedback>();
        Assert.Equal((72UL, 88UL, 160UL), (desiredWidth, appliedWidth, actionWidth));

        // Round 1: 64 new identities issue 64 applies.
        var first = PlanRound(workspace, session, configuration, owner, regions, epoch: 1,
            desired: 64, applied: 0, expectedActions: 64, desiredWidth, appliedWidth, actionWidth);
        Assert.All(first, action => Assert.Equal(NativePlacementActionDisposition.Apply,
            (NativePlacementActionDisposition)action.Disposition));
        Assert.Equal(2, regions.Count);
        Assert.All(regions, region => Assert.True(region.Closed));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Empty(owner.ReadWorkspaceCapacities());
        Assert.Null(workspace.Layout);
        Assert.Equal(0, workspace.ActionCapacity);

        // Round 2: the 64 report their receipts and 100 more arrive; only the new
        // ones act, so the action region grows past round 1 and loses none.
        var second = PlanRound(workspace, session, configuration, owner, regions, epoch: 2,
            desired: 164, applied: 64, expectedActions: 100, desiredWidth, appliedWidth, actionWidth);
        Assert.Equal(Enumerable.Range(64, 100).Select(index => (ulong)index + 101),
            second.Select(action => action.TargetKey).Order());
        Assert.Equal(4, regions.Count);
        Assert.All(regions, region => Assert.True(region.Closed));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);

        // Round 3: every identity is settled, so no action region opens.
        Assert.Empty(PlanRound(workspace, session, configuration, owner, regions, epoch: 3,
            desired: 164, applied: 164, expectedActions: 0, desiredWidth, appliedWidth, actionWidth));
        Assert.Equal(5, regions.Count);
        Assert.True(regions[4].Closed);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        AssertNativeAppliedStates(session, 164);
    }

    [Fact]
    public async Task PlanAndFeedbackKeepTheirRegionsAcrossAwaitAndReturnThemAtRoundEnd()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => { var region = owner.CreateScratch(bytes); regions.Add(region); return region; });
        Assert.Empty(regions);
        owner.SetMode(LedgerTableMode.Optimize);
        NativePlacementAction copiedAction;
        using (var operation = workspace.BeginOperation())
        {
            Assert.Equal((ulong)Unsafe.SizeOf<NativePlacementDesiredInput>(), Assert.Single(regions).Length);
            FillDesired(workspace);
            var cycle = CreateCycle(configuration, 1, 1, 0);
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
            Assert.Equal(1U, cycle.ActionCount);
            Assert.Equal(160UL, regions[1].Length);
            copiedAction = workspace.GetAction(0);
            Assert.Equal(NativePlacementActionDisposition.Apply, (NativePlacementActionDisposition)copiedAction.Disposition);
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // One real row is compact.
            Assert.Equal(0UL, owner.Tick().Released);
            await Task.Yield();
            Assert.Equal(0UL, owner.Tick().Released);
            workspace.SetFeedback(0, CreateFeedback(copiedAction, NativePlacementFeedbackStatus.Applied));
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.ApplyFeedback(session, 1));
            operation.Complete();
        }
        Assert.All(regions, region => Assert.True(region.Closed));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        AssertNativeAppliedState(session, copiedAction);

        using (var operation = workspace.BeginOperation(0, 1))
        {
            Assert.Equal((ulong)Unsafe.SizeOf<NativePlacementAppliedInput>(), regions[2].Length);
            FillApplied(workspace, copiedAction);
            var cycle = CreateCycle(configuration, 2, 0, 1);
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
            Assert.Equal(1U, cycle.ActionCount);
            var restore = workspace.GetAction(0);
            Assert.Equal(NativePlacementActionDisposition.Restore, (NativePlacementActionDisposition)restore.Disposition);
            Assert.Equal(copiedAction.TargetKey, restore.TargetKey);
            Assert.Equal(copiedAction.RecordKey, restore.RecordKey);
            workspace.SetFeedback(0, CreateFeedback(restore, NativePlacementFeedbackStatus.AlreadySatisfied));
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.ApplyFeedback(session, 1));
            operation.Complete();
        }
        Assert.Equal(NativePlacementActionDisposition.Apply, (NativePlacementActionDisposition)copiedAction.Disposition);
        Assert.Equal(4, regions.Count);
        Assert.All(regions, region => Assert.True(region.Closed));
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void ActiveOperationCloseRetainsLeaseUntilItsRealEndAndCanBeRetried()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity, owner.CreateScratch);
        owner.SetMode(LedgerTableMode.Optimize);
        using (var operation = workspace.BeginOperation())
        {
            FillDesired(workspace);
            var cycle = CreateCycle(configuration, 1, 1, 0);
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
            Assert.Throws<LedgerException>(() => workspace.Dispose());
            Assert.Equal(0UL, owner.Tick().Released);
            Assert.NotEqual(0UL, workspace.GetAction(0).ActionId);
            operation.Complete();
        }
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Throws<ObjectDisposedException>(() => workspace.BeginOperation());
    }

    [Fact]
    public void FailedRegionCloseRetainsWorkspaceForExplicitRetry()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => resource = owner.CreateScratch(bytes));
        Assert.Null(resource);
        var operation = workspace.BeginOperation();
        using (var use = resource!.Acquire())
        {
            use.Complete();
            // The round's end cannot close a region another use still holds.
            Assert.Throws<LedgerException>(() => operation.Dispose());
            Assert.Throws<LedgerException>(() => workspace.Dispose());
            Assert.Throws<ObjectDisposedException>(() => workspace.BeginOperation());
            Assert.NotEqual(0U, session.Capacity.StateCapacity);
        }
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedEndUseIsRetriedOnlyByExplicitClose()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        FailEndOnceScratch? resource = null;
        // Only the operation's row region fails its first endUse; the plan's action
        // region ends normally before it.
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => resource is null
                ? (IComputeScratch)(resource = new FailEndOnceScratch(owner.CreateScratch(bytes)))
                : owner.CreateScratch(bytes));
        owner.SetMode(LedgerTableMode.Optimize);
        var operation = workspace.BeginOperation();
        FillDesired(workspace);
        var cycle = CreateCycle(configuration, 1, 1, 0);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
        operation.Complete();
        Assert.Throws<InvalidOperationException>(() => operation.Dispose());
        operation.Dispose(); // A surrounding finally must not silently retry endUse.
        Assert.Equal(1, resource!.EndAttempts);
        Assert.Throws<InvalidOperationException>(() => workspace.BeginOperation());
        Assert.Equal(0UL, owner.Tick().Released);
        workspace.Dispose();
        Assert.Equal(2, resource.EndAttempts);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    private static void FillDesired(NativePlacementCoordinatorWorkspace workspace)
    {
        using var view = workspace.CreateScratchView();
        view.Desired.Clear(); view.Applied.Clear(); view.Actions.Clear(); view.Feedback.Clear();
        view.Desired[0] = new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementDesiredInput>(),
            TargetKey = 101, RecordKey = 201, DesiredDigest = 301,
            ResourceKind = (uint)NativePlacementResourceKind.Cpu,
            PlacementKind = (uint)NativePlacementKind.CpuSets,
            ValidMask = (ulong)NativePlacementDesiredValidity.Required
        };
    }

    // Plans one round with `desired` identities (the first `applied` of them also
    // report receipts), checks both regions hold exactly their rows while it runs,
    // settles every action and returns the actions.
    private static NativePlacementAction[] PlanRound(NativePlacementCoordinatorWorkspace workspace,
        NativePlacementCoordinatorSession session, NativePlacementCoordinatorConfiguration configuration,
        ResourceManagerLedgerOwner owner, List<ResourceManagerLedgerOwner.ComputeScratch> regions, ulong epoch,
        int desired, int applied, int expectedActions, ulong desiredWidth, ulong appliedWidth, ulong actionWidth)
    {
        var opened = regions.Count;
        using var operation = workspace.BeginOperation(checked((uint)desired), checked((uint)applied));
        var rowBytes = desiredWidth * (ulong)desired + appliedWidth * (ulong)applied;
        Assert.Equal(rowBytes, regions[opened].Length);
        Assert.Equal(rowBytes, workspace.Layout!.Length);
        using (var view = workspace.CreateScratchView())
        {
            for (var index = 0; index < desired; index++)
                view.Desired[index] = CreateDesired(index);
            for (var index = 0; index < applied; index++)
                view.Applied[index] = CreateApplied(index);
        }
        var cycle = CreateCycle(configuration, epoch, checked((uint)desired), checked((uint)applied));
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
        Assert.Equal(checked((uint)expectedActions), cycle.ActionCount);
        Assert.Equal(cycle.ActionCount, cycle.ActionCapacity);
        Assert.Equal(expectedActions, workspace.ActionCapacity);
        if (expectedActions == 0)
        {
            Assert.Equal(opened + 1, regions.Count);
            Assert.Null(workspace.ActionLayout);
        }
        else
        {
            Assert.Equal(opened + 2, regions.Count);
            Assert.Equal(actionWidth * (ulong)expectedActions, regions[opened + 1].Length);
            Assert.Equal(actionWidth * (ulong)expectedActions, workspace.ActionLayout!.Length);
            Assert.Equal(rowBytes + actionWidth * (ulong)expectedActions, owner.ReadTable().RegisteredBytes);
            Assert.Equal(new[] { "placement", "placement-actions" },
                owner.ReadWorkspaceCapacities().Select(capacity => capacity.WorkArea).Order());
        }
        var actions = new NativePlacementAction[expectedActions];
        for (var index = 0; index < expectedActions; index++)
        {
            actions[index] = workspace.GetAction(index);
            workspace.SetFeedback(index, CreateFeedback(actions[index], NativePlacementFeedbackStatus.Applied));
        }
        if (expectedActions != 0)
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.ApplyFeedback(session, expectedActions));
        operation.Complete();
        return actions;
    }

    private static NativePlacementDesiredInput CreateDesired(int index)
        => new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementDesiredInput>(),
            TargetKey = (ulong)index + 101, RecordKey = (ulong)index + 201, DesiredDigest = (ulong)index + 301,
            ResourceKind = (uint)NativePlacementResourceKind.Cpu,
            PlacementKind = (uint)NativePlacementKind.CpuSets,
            ValidMask = (ulong)NativePlacementDesiredValidity.Required
        };

    // The receipt of CreateDesired(index) after its apply settled.
    private static NativePlacementAppliedInput CreateApplied(int index)
        => new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementAppliedInput>(),
            Flags = (uint)NativePlacementAppliedFlags.PayloadValid,
            TargetKey = (ulong)index + 101, RecordKey = (ulong)index + 201,
            ReceiptDigest = (ulong)index + 301, PreviousDigest = 1,
            ResourceKind = (uint)NativePlacementResourceKind.Cpu,
            PlacementKind = (uint)NativePlacementKind.CpuSets,
            ObservationStatus = (uint)NativePlacementObservationStatus.Unchecked,
            ValidMask = (ulong)NativePlacementAppliedValidity.Required
        };

    private static void AssertNativeAppliedStates(NativePlacementCoordinatorSession session, int count)
    {
        var header = new NativePlacementSnapshotHeader
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementSnapshotHeader>()
        };
        var rows = new NativePlacementState[session.Capacity.SnapshotStateCapacity];
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.GetSnapshot(ref header, rows));
        Assert.Equal(checked((uint)count), header.ActiveStateCount);
        Assert.All(rows.Take(count), state =>
            Assert.Equal(NativePlacementSlotState.Applied, (NativePlacementSlotState)state.State));
    }

    private static void FillApplied(NativePlacementCoordinatorWorkspace workspace, NativePlacementAction action)
    {
        using var view = workspace.CreateScratchView();
        view.Desired.Clear(); view.Applied.Clear(); view.Actions.Clear(); view.Feedback.Clear();
        view.Applied[0] = new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementAppliedInput>(),
            Flags = (uint)NativePlacementAppliedFlags.PayloadValid,
            TargetKey = action.TargetKey, RecordKey = action.RecordKey,
            ReceiptDigest = action.DesiredDigest, PreviousDigest = 401,
            ResourceKind = action.ResourceKind, PlacementKind = action.PlacementKind,
            ObservationStatus = (uint)NativePlacementObservationStatus.Unchecked,
            ValidMask = (ulong)NativePlacementAppliedValidity.Required
        };
    }

    private static void AssertNativeAppliedState(NativePlacementCoordinatorSession session, NativePlacementAction action)
    {
        var header = new NativePlacementSnapshotHeader
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementSnapshotHeader>()
        };
        var rows = new NativePlacementState[session.Capacity.SnapshotStateCapacity];
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.GetSnapshot(ref header, rows));
        var state = Assert.Single(rows.Take(checked((int)header.ActiveStateCount)));
        Assert.Equal(action.DesiredDigest, state.AppliedDigest);
        Assert.Equal(NativePlacementSlotState.Applied, (NativePlacementSlotState)state.State);
    }

    private static NativePlacementFeedback CreateFeedback(NativePlacementAction action, NativePlacementFeedbackStatus status)
        => new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementFeedback>(), Status = (uint)status,
            ActionId = action.ActionId, TargetKey = action.TargetKey, RecordKey = action.RecordKey,
            CompletedAtMilliseconds = action.DeadlineMilliseconds - 40,
            ObservedDigest = status == NativePlacementFeedbackStatus.Applied ? action.DesiredDigest : 0,
            ValidMask = status == NativePlacementFeedbackStatus.Applied
                ? (ulong)NativePlacementFeedbackValidity.ObservedDigest : 0
        };

    private static NativePlacementCoordinatorCycleInput CreateCycle(NativePlacementCoordinatorConfiguration configuration,
        ulong epoch, uint desired, uint applied)
        => new()
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorCycleInput>(),
            ConfigurationGeneration = configuration.Generation, CycleEpoch = epoch,
            ObservedAtMilliseconds = 1000 + (epoch - 1) * 100, DesiredCount = desired, AppliedCount = applied,
            ValidMask = (ulong)NativePlacementCycleValidity.Required // The workspace sizes ActionCapacity.
        };

    private static NativePlacementCoordinatorConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorConfiguration>(),
            Generation = 1, MaximumDesiredCount = 8192, MaximumAppliedCount = 8192,
            MaximumActionCount = 8192, MaximumStateCount = 8192,
            RetryDelayMilliseconds = 100, ActionTimeoutMilliseconds = 50, MaximumFutureSkewMilliseconds = 5
        };

    private sealed class FailEndOnceScratch(IComputeScratch inner) : IComputeScratch
    {
        internal int EndAttempts { get; private set; }
        public ulong Length => inner.Length;
        public ComputeScratchLease Acquire() => new Lease(this, inner.Acquire());
        public void Dispose() => inner.Dispose();

        private sealed class Lease(FailEndOnceScratch owner, ComputeScratchLease inner) : ComputeScratchLease
        {
            internal override ulong Length => inner.Length;
            internal override ulong AllocationNumber => inner.AllocationNumber;
            internal override bool IsLoading => inner.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => inner.GetMemoryChunk(offset, count);
            internal override void Complete() => inner.Complete();
            public override void Dispose()
            {
                if (++owner.EndAttempts == 1) throw new InvalidOperationException("Injected endUse failure.");
                inner.Dispose();
            }
        }
    }
}
