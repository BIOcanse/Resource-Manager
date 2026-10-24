using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class PlacementWorkspaceLedgerTests
{
    [Fact]
    public void ActualRowsGrowOneRegionAndReclaimRefillsTheSameUid()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => { var region = owner.CreateScratch(bytes); regions.Add(region); return region; });
        Assert.Equal(320UL, Assert.Single(regions).Length);
        using (var operation = workspace.BeginOperation(32, 0))
        {
            using (var view = workspace.CreateScratchView())
            {
                for (var index = 0; index < 32; index++)
                    view.Desired[index] = new()
                    {
                        StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementDesiredInput>(),
                        TargetKey = (ulong)index + 101, RecordKey = (ulong)index + 201, DesiredDigest = 301,
                        ResourceKind = (uint)NativePlacementResourceKind.Cpu,
                        PlacementKind = (uint)NativePlacementKind.CpuSets,
                        ValidMask = (ulong)NativePlacementDesiredValidity.Required
                    };
            }
            var cycle = CreateCycle(configuration, 1, 32, 0);
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
            Assert.Equal(32U, cycle.ActionCount);
            operation.Complete();
        }
        Assert.True(regions[0].Closed);
        Assert.Equal(72UL * 32 + 88 + 160UL * 32, regions[1].Length);
        Assert.Equal(regions[1].Length, owner.ReadTable().RegisteredBytes);
        var uid = regions[1].Uid;
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        using (var operation = workspace.BeginOperation(32, 0)) operation.Complete();
        Assert.Equal(uid, regions[1].Uid);
        Assert.Equal(2, regions.Count);
        using (var operation = workspace.BeginOperation(64, 0)) operation.Complete();
        Assert.True(regions[1].Closed);
        Assert.Equal(3, regions.Count);
        Assert.Single(owner.ReadWorkspaceCapacities());
    }

    [Fact]
    public async Task ProjectionPlanAndFeedbackShareOneLazyIdentityAcrossAwaitAndIdleRefill()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        using var session = new NativePlacementCoordinatorSession(in configuration);
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => resource = owner.CreateScratch(bytes));
        Assert.NotNull(resource);
        var expected = checked(1UL * ((ulong)Unsafe.SizeOf<NativePlacementDesiredInput>()
            + (ulong)Unsafe.SizeOf<NativePlacementAppliedInput>() + (ulong)Unsafe.SizeOf<NativePlacementAction>()
            + (ulong)Unsafe.SizeOf<NativePlacementFeedback>()));
        Assert.Equal(320UL, expected);
        Assert.Equal(expected, resource.Length);
        var uid = resource.Uid;
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        NativePlacementAction copiedAction;
        using (var operation = workspace.BeginOperation())
        {
            FillDesired(workspace);
            var cycle = CreateCycle(configuration, 1, 1, 0);
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
            Assert.Equal(1U, cycle.ActionCount);
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
        ulong firstAllocation;
        using (var use = resource.Acquire()) firstAllocation = use.AllocationNumber;
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        AssertNativeAppliedState(session, copiedAction);

        using (var operation = workspace.BeginOperation())
        {
            FillApplied(workspace, copiedAction);
            var cycle = CreateCycle(configuration, 2, 0, 1);
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.Plan(session, ref cycle));
            Assert.Equal(1U, cycle.ActionCount);
            var restore = workspace.GetAction(0);
            Assert.Equal(NativePlacementActionDisposition.Restore, (NativePlacementActionDisposition)restore.Disposition);
            Assert.Equal(copiedAction.TargetKey, restore.TargetKey);
            Assert.Equal(copiedAction.RecordKey, restore.RecordKey);
            Assert.Equal(uid, resource.Uid);
            workspace.SetFeedback(0, CreateFeedback(restore, NativePlacementFeedbackStatus.AlreadySatisfied));
            Assert.Equal(NativePlacementCoordinatorStatus.Ok, workspace.ApplyFeedback(session, 1));
            operation.Complete();
        }
        Assert.Equal(NativePlacementActionDisposition.Apply, (NativePlacementActionDisposition)copiedAction.Disposition);
        using (var use = resource.Acquire()) Assert.Equal(firstAllocation, use.AllocationNumber);
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
        using (var use = resource!.Acquire())
        {
            use.Complete();
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
        using var workspace = new NativePlacementCoordinatorWorkspace(in configuration, session.Capacity,
            bytes => resource = new FailEndOnceScratch(owner.CreateScratch(bytes)));
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
            ActionCapacity = Math.Max(1U, Math.Min(configuration.MaximumActionCount, checked(desired + applied))), ValidMask = (ulong)NativePlacementCycleValidity.Required
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
