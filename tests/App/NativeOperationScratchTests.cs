using ResourceManager.App.Infrastructure.Optimization;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;

namespace Resource_Manager_APP.Tests;

public sealed class NativeOperationScratchTests
{
    [Fact]
    public void FreshSmallWorkspaceImportsEveryCanonicalRecordBeforeGrowingItsOutput()
    {
        using var owner = new ResourceManagerLedgerOwner();
        using var previous = Create(owner.CreateScratch);
        for (ulong id = 1; id <= 12; id++) Submit(previous, id);
        var image = previous.ExportCanonicalNativeImageLocked();
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var candidate = new NativeOperationCoordinatorWorkspace(Plan(), previous.SessionInstanceId,
            previous.ClockInstanceId, bytes => { var region = owner.CreateScratch(bytes); regions.Add(region); return region; });
        Assert.Equal(912UL, Assert.Single(regions).Length);
        candidate.ImportCanonicalNativeImageLocked(image, Now(), Tick());
        Assert.Equal(12U, candidate.SnapshotLocked().OperationCount);
        var exported = candidate.ExportCanonicalNativeImageLocked();
        Assert.Equal(image.Records.Select(row => row.OperationId), exported.Records.Select(row => row.OperationId));
        Assert.True(regions[0].Closed);
        Assert.Equal(2, regions.Count);
        Assert.Equal(400UL + 112 + 400 * 12, regions[1].Length);
    }

    [Theory]
    [InlineData(1u, 1u, 1u, 912UL)]
    [InlineData(8192u, 1024u, 8192u, 6668288UL)]
    [InlineData(65536u, 4096u, 65536u, 52887552UL)]
    public void LayoutHasExactThreeColumns(uint read, uint actions, uint operations, ulong bytes)
    {
        var layout = new NativeOperationScratchLayout(read, actions, operations);
        Assert.Equal(400, Marshal.SizeOf<NativeOperationOutput>());
        Assert.Equal(112, Marshal.SizeOf<NativeOperationActionOutput>());
        Assert.Equal(0UL, layout.Operations);
        Assert.Equal(400UL * read, layout.Actions);
        Assert.Equal(400UL * read + 112UL * actions, layout.Persistence);
        Assert.Equal(bytes, layout.Length);
        Assert.Equal(0UL, layout.Length % 8);
    }

    [Fact]
    public void LayoutKeepsLegalElementCountsEvenWhenColumnBytesExceedInt32()
    {
        var layout = new NativeOperationScratchLayout(int.MaxValue, 1, int.MaxValue);
        Assert.Equal(int.MaxValue, layout.OperationCapacity);
        Assert.True(layout.Actions > int.MaxValue);
        Assert.Equal(800UL * int.MaxValue + 112, layout.Length);
        Assert.Throws<OverflowException>(() => new NativeOperationScratchLayout(uint.MaxValue, 1, 1));
    }

    [Fact]
    public void EmptySnapshotDoesNotAcquireButPlanAcquiresBeforeMutatingNativeEpoch()
    {
        OperationScratchFixture? scratch = null;
        using var workspace = Create(length => scratch = new(length));
        var recorded = Assert.IsType<OperationScratchFixture>(scratch);
        Assert.Empty(workspace.ReadCommittedProjectionLocked());
        Assert.Equal(0, recorded.Acquires);
        recorded.FailAcquire = true;
        Assert.Throws<InvalidOperationException>(() => workspace.PlanAndReadActionsLocked(Now(), Tick()));
        Assert.Equal(0, recorded.Acquires);
        recorded.FailAcquire = false;
        var batch = workspace.PlanAndReadActionsLocked(Now(), Tick());
        Assert.Equal(1UL, batch.Plan.PlanEpoch);
        Assert.Empty(batch.Actions);
        Assert.Equal(1, recorded.Acquires);
        Assert.Equal(recorded.Acquires, recorded.Returns);
        Assert.Equal(1, recorded.Completed);
    }

    [Fact]
    public void FailedExportAcquireDoesNotAdvanceNativePersistenceEpoch()
    {
        OperationScratchFixture? scratch = null;
        using var workspace = Create(length => scratch = new(length));
        var recorded = Assert.IsType<OperationScratchFixture>(scratch);
        recorded.FailAcquire = true;
        Assert.Throws<InvalidOperationException>(() => workspace.ExportCanonicalNativeImageLocked());
        recorded.FailAcquire = false;
        Assert.Equal(1UL, workspace.ExportCanonicalNativeImageLocked().Header.OperationEpoch);
        Assert.Equal(recorded.Acquires, recorded.Returns);
    }

    [Fact]
    public void PoisonedColumnsAreRebuiltAndReturnedResultsOwnTheirData()
    {
        OperationScratchFixture? scratch = null;
        using var workspace = Create(length => scratch = new(length));
        var recorded = Assert.IsType<OperationScratchFixture>(scratch);
        Submit(workspace);
        var records = workspace.ReadCommittedProjectionLocked();
        var saved = MemoryMarshal.AsBytes(records.AsSpan()).ToArray();
        var batch = workspace.PlanAndReadActionsLocked(Now(), Tick());
        var action = Assert.Single(batch.Actions);
        var expectedAction = MemoryMarshal.AsBytes(batch.Actions.AsSpan()).ToArray();
        var exported = workspace.ExportCanonicalNativeImageLocked();
        var exportBytes = MemoryMarshal.AsBytes(exported.Records.AsSpan()).ToArray();
        _ = workspace.ExportCanonicalNativeImageLocked(); // Each new borrow poisons all three columns.
        _ = workspace.ReadCommittedProjectionLocked();
        Assert.Equal<byte>(saved, MemoryMarshal.AsBytes(records.AsSpan()).ToArray());
        Assert.Equal<byte>(expectedAction, MemoryMarshal.AsBytes(batch.Actions.AsSpan()).ToArray());
        Assert.Equal<byte>(exportBytes, MemoryMarshal.AsBytes(exported.Records.AsSpan()).ToArray());
        Assert.Equal(records[0].OperationId, action.OperationId);
        Assert.Equal(recorded.Acquires, recorded.Returns);
        Assert.Equal(recorded.Acquires, recorded.Completed);
    }

    [Fact]
    public void RealRegionReallocationKeepsUidSessionEpochsAndReturnedRecords()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? scratch = null;
        using var workspace = Create(length => scratch = owner.CreateScratch(length));
        var recorded = Assert.IsType<ResourceManagerLedgerOwner.ComputeScratch>(scratch);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        for (var row = 1; row <= 12; row++) Submit(workspace, (ulong)row);
        _ = workspace.PlanAndReadActionsLocked(Now(), Tick());
        var first = workspace.ExportCanonicalNativeImageLocked();
        _ = workspace.ReadCommittedProjectionLocked(); // Establish both output column high waters before observing this UID.
        recorded = Assert.IsType<ResourceManagerLedgerOwner.ComputeScratch>(scratch);
        Assert.NotEqual(0UL, first.Header.LastSubmitEpoch);
        Assert.NotEqual(0UL, first.Header.LastPlanEpoch);
        var saved = MemoryMarshal.AsBytes(first.Records.AsSpan()).ToArray();
        var uid = recorded.Uid;
        ulong allocation;
        using (var lease = recorded.Acquire())
        {
            allocation = lease.AllocationNumber;
            lease.GetMemoryChunk(0, checked((int)lease.Length)).Span.Fill(0xCC);
            lease.Complete();
        }
        var rebuilt = workspace.ReadCommittedProjectionLocked();
        Assert.Equal<byte>(saved, MemoryMarshal.AsBytes(rebuilt.AsSpan()).ToArray());
        owner.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var second = workspace.ExportCanonicalNativeImageLocked();
        Assert.Equal(uid, recorded.Uid);
        using (var lease = recorded.Acquire()) Assert.NotEqual(allocation, lease.AllocationNumber);
        Assert.Equal(first.Header.SessionInstanceId, second.Header.SessionInstanceId);
        Assert.Equal(first.Header.ClockInstanceId, second.Header.ClockInstanceId);
        Assert.Equal(first.Header.OperationEpoch + 1, second.Header.OperationEpoch);
        Assert.Equal(first.Header.LastSubmitEpoch, second.Header.LastSubmitEpoch);
        Assert.Equal(first.Header.LastPlanEpoch, second.Header.LastPlanEpoch);
        Assert.Equal(first.Header.StateRevision, second.Header.StateRevision);
        Assert.Equal<byte>(saved, MemoryMarshal.AsBytes(second.Records.AsSpan()).ToArray());
        Assert.Equal<byte>(saved, MemoryMarshal.AsBytes(first.Records.AsSpan()).ToArray());
    }

    [Fact]
    public void ScoringLayoutUseOldOperationRegistrationAndCandidateExportUseAllThreeSlots()
    {
        using var owner = new ResourceManagerLedgerOwner();
        // The scoring work area is the one Ledger registration here. Since 084dfdfb output rows are 16 B instead of 96 B, so
        // 56 outputs (3,088 B) fall below one native page and become managed; 128 outputs keep it a Ledger allocation.
        var scoringLayout = new ComputeScoringScratchLayout(8, 16, 128, 1, 0, 1);
        Assert.True(scoringLayout.Length >= owner.NativePageSize);
        using var score = owner.CreateScratch(scoringLayout.Length);
        using var scoring = score.Acquire();
        scoring.Complete();
        using var old = Create(owner.CreateScratch);
        Submit(old);
        var image = old.ExportCanonicalNativeImageLocked();
        var plan = Plan();
        using var candidate = new NativeOperationCoordinatorWorkspace(plan,
            old.SessionInstanceId, old.ClockInstanceId, owner.CreateScratch);
        candidate.ImportCanonicalNativeImageLocked(image, Now(), Tick());
        var committed = candidate.ExportCanonicalNativeImageLocked();
        var capacity = plan.Recreate.Capacity;
        var layout = new NativeOperationScratchLayout(capacity.MaximumReadCount,
            capacity.MaximumActionCount, capacity.MaximumOperationCount);
        Assert.Equal(scoringLayout.Length, owner.ReadTable().RegisteredBytes); // One operation stays compact in both workspaces.
        Assert.Equal(image.Records[0].OperationId, Assert.Single(committed.Records).OperationId);
    }

    [Fact]
    public void WorkspaceCloseInUseKeepsNativeSessionAndHandleUntilLeaseReturns()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? scratch = null;
        var workspace = Create(length => scratch = owner.CreateScratch(length));
        try
        {
            _ = workspace.ExportCanonicalNativeImageLocked();
            var lease = scratch!.Acquire();
            try
            {
                var session = typeof(NativeOperationCoordinatorWorkspace)
                    .GetField("session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(workspace);
                Assert.NotNull(session);
                var error = Assert.Throws<LedgerException>(() => workspace.Dispose());
                Assert.Equal(LedgerStatus.InUse, error.Status);
                Assert.Throws<ObjectDisposedException>(() => workspace.SnapshotLocked());
                Assert.Same(session, typeof(NativeOperationCoordinatorWorkspace)
                    .GetField("session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(workspace));
                var retained = Assert.IsType<NativeOperationCoordinatorSession>(session);
                Assert.Equal(NativeOperationCoordinatorStatus.Ok, retained.Snapshot(out var stillLive));
                Assert.NotEqual(0UL, stillLive.StateRevision);
                Assert.Equal(LedgerStatus.InUse, owner.TryClose());
                Assert.Single(owner.ReadWorkspaceCapacities());
            }
            finally { lease.Dispose(); }
            workspace.Dispose();
            Assert.Equal(LedgerStatus.Ok, owner.TryClose());
        }
        finally { workspace.Dispose(); }
    }

    [Fact]
    public void WrongFactoryLengthRetiresRealHandleDuringConstructionFailure()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ResourceManagerLedgerOwner.ComputeScratch? wrong = null;
        Assert.Throws<InvalidOperationException>(() => Create(
            length => wrong = owner.CreateScratch(length + 8)));
        Assert.NotNull(wrong);
        Assert.Throws<ObjectDisposedException>(() => wrong!.Acquire());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
    }

    [Fact]
    public void ConstructorCleanupInUseKeepsRealLedgerObligationUntilBorrowReturns()
    {
        using var owner = new ResourceManagerLedgerOwner();
        ComputeScratchLease? lease = null;
        try
        {
            var failure = Assert.Throws<AggregateException>(() => Create(length =>
            {
                var wrong = owner.CreateScratch(length + 8);
                lease = wrong.Acquire();
                return wrong;
            }));
            Assert.Contains(failure.Flatten().InnerExceptions, error => error.GetType() == typeof(InvalidOperationException));
            Assert.Contains(failure.Flatten().InnerExceptions, error => error is LedgerException { Status: LedgerStatus.InUse });
            Assert.Equal(LedgerStatus.InUse, owner.TryClose());
            Assert.NotNull(lease);
            lease!.Dispose();
            lease = null;
            Assert.Equal(LedgerStatus.Ok, owner.TryClose());
        }
        finally { lease?.Dispose(); }
    }

    [Fact]
    public void FactoryThrowLeavesNoLedgerPayloadAndDoesNotBlockLaterConstruction()
    {
        using var owner = new ResourceManagerLedgerOwner();
        Assert.Throws<IOException>(() => Create(_ => throw new IOException("recorded factory failure")));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        using var valid = Create(owner.CreateScratch);
        Assert.Empty(valid.ExportCanonicalNativeImageLocked().Records);
    }

    internal static CompiledHostManagerOperationCoordinatorPlan Plan()
        => HostManagerOperationCoordinatorOwnerTests.CreateSmallPlan().OperationCoordinator;
    private static NativeOperationCoordinatorWorkspace Create(Func<ulong, IComputeScratch> factory)
        => new(Plan(), new(1, 2), new(3, 4), factory);
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static ulong Tick() => NativeOperationCoordinatorWorkspace.MonotonicMilliseconds();
    private static void Submit(NativeOperationCoordinatorWorkspace workspace, ulong id = 11)
    {
        var now = Now();
        var tick = Tick();
        var input = new NativeOperationSubmitInput
        {
            OperationId = new(id, 12), KindHandle = new(21, 22), RequestHandle = new(31, 32),
            MaximumAttempts = 1, RetryDelayMilliseconds = 1,
            ExecutionTimeoutMilliseconds = 30000, CancelGraceMilliseconds = 5000,
            TerminalRetentionMilliseconds = 60000,
            CreatedUtcMilliseconds = now, ObservedUtcMilliseconds = now,
            CreatedMonotonicMilliseconds = tick, ObservedMonotonicMilliseconds = tick,
            ValidMask = (ulong)NativeOperationSubmitValidity.Request
        };
        _ = workspace.SubmitLocked(ref input);
    }
}
