using System.Reflection;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task ForegroundLifetimeShutdownJoinsOriginalOperationsWithoutHoldingGate(int count, bool directDispose)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var probe = new BlockingTransitionProbe(HostManagerSmartCoordinatorTransitionPoint.BeforeNativeCall);
        fixture.Coordinator.TransitionProbe = probe;
        var operations = Enumerable.Range(0, count).Select(_ => EnterOriginalForegroundOperation(fixture.Coordinator)).ToArray();
        var shutdown = directDispose ? Task.Run(fixture.Coordinator.Dispose)
            : fixture.Coordinator.StopAsync(CancellationToken.None);
        try
        {
            await probe.AdmissionClosed.WaitAsync(TimeSpan.FromSeconds(10));
            // Taking the original shutdown path again also serializes with BeginShutdown publication.
            var sameShutdown = fixture.Coordinator.StopAsync(CancellationToken.None);
            Assert.False(sameShutdown.IsCompleted);
            Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closing, fixture.Coordinator.LifecycleState);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace"));
            var gate = ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!;
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(10)));
            try
            {
                // An admitted operation must still be able to re-enter for cleanup while Closing.
                Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace"));
            }
            finally { gate.Release(); }
            for (var index = 0; index < operations.Length - 1; index++) operations[index].Dispose();
            Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
            Assert.False(sameShutdown.IsCompleted);
        }
        finally
        {
            foreach (var operation in operations) operation.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        probe.AssertShutdownOrder();
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task ForegroundLifetimeCancelledStopWaitDoesNotReleaseAnActiveOperation()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        using var operation = EnterOriginalForegroundOperation(fixture.Coordinator);
        using var cancellation = new CancellationTokenSource();
        var stop = fixture.Coordinator.StopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        var shared = fixture.Coordinator.StopAsync(CancellationToken.None);
        try
        {
            Assert.False(shared.IsCompleted);
            Assert.NotNull(ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace"));
        }
        finally
        {
            operation.Dispose();
            await shared.WaitAsync(TimeSpan.FromSeconds(10));
        }
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Fact]
    public async Task ForegroundLifetimeClosingRejectsAllNewControlOperationsBeforeStateAccess()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        using var operation = EnterOriginalForegroundOperation(fixture.Coordinator);
        var shutdown = fixture.Coordinator.StopAsync(CancellationToken.None);
        try
        {
            var loads = fixture.StateStore.LoadCalls;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.SetModeAsync(AppOptimizationModes.Smart, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.RestoreNormalModeAsync(CancellationToken.None));
            Assert.Equal(loads, fixture.StateStore.LoadCalls);
            Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            operation.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ForegroundLifetimeNewBatchCannotReuseThePreviousCompletionSignal()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        using (var first = EnterOriginalForegroundOperation(fixture.Coordinator))
        {
            first.Dispose();
            first.Dispose();
        }
        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        using var second = EnterOriginalForegroundOperation(fixture.Coordinator);
        var shutdown = fixture.Coordinator.StopAsync(CancellationToken.None);
        try { Assert.False(shutdown.IsCompleted); }
        finally
        {
            second.Dispose();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("mode")]
    [InlineData("restore")]
    public async Task ForegroundLifetimeRealControlCallCancelledAtGateSettlesItsLease(string entry)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var gate = ReadPrivateField<SemaphoreSlim>(fixture.Coordinator, "gate")!;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        Task operation;
        try
        {
            operation = entry switch
            {
                "manual" => fixture.Coordinator.RunOnceAsync(cancellation.Token),
                "mode" => fixture.Coordinator.SetModeAsync(AppOptimizationModes.Smart, cancellation.Token),
                _ => fixture.Coordinator.RestoreNormalModeAsync(cancellation.Token)
            };
            Assert.Equal(1, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "foregroundControlRequestCount"));
        }
        finally { gate.Release(); }
        await fixture.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        AssertOwnedResourcesCleared(fixture.Coordinator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundCloseHasNoScoringRegionToWaitOnBetweenRounds(bool directDispose)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(4242, 100, "software:scoring-close", 20, 50, 25));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.IsType<HostManagerComputeScoringWorkspace>(
            ReadPrivateField<object>(fixture.Coordinator, "computeScoringWorkspace"));
        // Scoring returned its region when the round ended, so the first close completes.
        if (directDispose) await Task.Run(fixture.Coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
        else await fixture.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closed, fixture.Coordinator.LifecycleState);
        AssertOwnedResourcesCleared(fixture.Coordinator);
        fixture.Coordinator.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SmartScratchHostCloseKeepsEnteredOperationUntilExplicitRetry(bool directDispose)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var workspace = fixture.Workspace;
        // The warm round returned its regions; this operation opens one fact row and holds it.
        workspace.EnsureRows(1);
        using var operation = workspace.BeginOperation();
        using (var view = workspace.CreateScratchView()) view.Inputs[0].TargetKey = 53;
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => directDispose
                ? Task.Run(fixture.Coordinator.Dispose)
                : fixture.Coordinator.StopAsync(CancellationToken.None));
            Assert.Contains("InUse", failure.ToString());
            Assert.Same(workspace, ReadPrivateField<object>(fixture.Coordinator, "nativeWorkspace"));
            Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closing, fixture.Coordinator.LifecycleState);
            Assert.Equal(0, ReadPrivateValue<int>(fixture.Coordinator, "baseDisposeStarted"));
            using (var view = workspace.CreateScratchView()) Assert.Equal(53UL, view.Inputs[0].TargetKey);
            Assert.Throws<ObjectDisposedException>(() => workspace.BeginOperation());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            operation.Complete();
        }
        finally { operation.Dispose(); }
        await fixture.Coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closed, fixture.Coordinator.LifecycleState);
        AssertOwnedResourcesCleared(fixture.Coordinator);
        Assert.Null(ReadPrivateField<object>(fixture.Coordinator, "pendingNativeWorkspaceClose"));
        var journal = JournalWorkspaceScratch(fixture);
        Assert.Equal(journal.Length >= fixture.OwnedComputeLedger!.NativePageSize ? journal.Length : 0,
            fixture.OwnedComputeLedger.ReadTable().RegisteredBytes);
        await fixture.NativeTransactions.DisposeAsync();
        Assert.Equal(0UL, fixture.OwnedComputeLedger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task SmartScratchBlockedReplacementKeepsOneRegisteredRegionAndRejectsThirdWorkspace()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(4242, 100, "software:smart-scratch", 20, 50, 25));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var previous = fixture.Workspace;
        // This lifecycle case deliberately exercises a page-backed Smart region;
        // ordinary small inputs remain on the compact scratch path.
        previous.EnsureRows(64);
        var scratch = Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.IComputeScratch>(
            ReadPrivateReference(previous, "facts"));
        using var use = scratch.Acquire();
        var ledger = fixture.OwnedComputeLedger!;
        ulong Registered(ResourceManager.App.Infrastructure.Adaptation.IComputeScratch area)
            => area.Length >= ledger.NativePageSize ? area.Length : 0;
        var smartBytes = Registered(scratch);
        var before = ledger.ReadTable().RegisteredBytes;
        var ownership = Assert.IsType<ResourceManager.App.Infrastructure.NativeCore.NativeAppliedOwnershipOwner>(
            ReadPrivateReference(fixture.NativeTransactions.AppliedOwnership, "owner"));
        var journalBytes = Registered(JournalWorkspaceScratch(fixture));
        // Scoring and ownership hold no region between rounds or operations.
        Assert.Equal(smartBytes + journalBytes, before);
        Assert.NotEqual(0UL, use.AllocationNumber);
        var probe = new SmartReplacementCountProbe();
        fixture.Coordinator.TransitionProbe = probe;
        fixture.PublishSmartCoordinatorRecreatePlan();
        NativeSmartCoordinatorWorkspace? current = null;
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            Assert.Contains("InUse", failure.ToString());
            current = ReadPrivateField<NativeSmartCoordinatorWorkspace>(fixture.Coordinator, "nativeWorkspace")!;
            Assert.NotSame(previous, current);
            Assert.Same(previous, ReadPrivateField<object>(fixture.Coordinator, "pendingNativeWorkspaceClose"));
            Assert.Null(ReadPrivateReference(current, "activeLease"));
            Assert.Equal(before, ledger.ReadTable().RegisteredBytes); // Candidate construction did not register.
            var again = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            Assert.Contains("InUse", again.ToString());
            Assert.Same(current, ReadPrivateField<object>(fixture.Coordinator, "nativeWorkspace"));
            Assert.Same(previous, ReadPrivateField<object>(fixture.Coordinator, "pendingNativeWorkspaceClose"));
            Assert.Equal(1, probe.Creates);
            Assert.Equal(before, ledger.ReadTable().RegisteredBytes);
            Assert.NotEmpty(use.GetMemoryChunk(0, 1).ToArray());
            var executionPlan = fixture.RuntimePlanProvider.Current;
            fixture.RuntimePlanProvider.Publish(executionPlan with
            {
                Version = executionPlan.Version + 1,
                Diagnostics = executionPlan.Diagnostics with { HostManagerSmartCoordinatorScoreOnlyEnabled = true }
            });
            try
            {
                await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
                Assert.Equal(0, probe.Plans); // Read-only reuse must not bypass the pending close.
                Assert.Equal(before, ledger.ReadTable().RegisteredBytes);
                Assert.Null(ReadPrivateReference(current, "activeLease"));
            }
            finally
            {
                fixture.RuntimePlanProvider.Publish(executionPlan with { Version = executionPlan.Version + 2 });
            }
        }
        finally { use.Dispose(); }
        Assert.Equal(before - smartBytes, ledger.ReadTable().RegisteredBytes);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Null(ReadPrivateField<object>(fixture.Coordinator, "pendingNativeWorkspaceClose"));
        Assert.Same(current, ReadPrivateField<object>(fixture.Coordinator, "nativeWorkspace"));
        Assert.Equal(1, probe.Creates);
        // The finished round returned its regions, so the facts owner holds none.
        var nextScratch = Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.IComputeScratch>(
            ReadPrivateReference(current!, "facts"));
        Assert.Equal(0UL, nextScratch.Length);
        Assert.Equal(journalBytes, ledger.ReadTable().RegisteredBytes);
        await fixture.Coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(journalBytes, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(ResourceManager.App.Infrastructure.NativeCore.NativeAppliedOwnershipOwnerState.Ready, ownership.State);
        await fixture.NativeTransactions.DisposeAsync();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task SmartScratchRealtimeFailureDiagnosticsUsesOnlyIndependentCapturedActions()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false, automaticMemoryCleanupEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(4242, 100, "software:smart-diagnostic", 20, 50, 25));
        var original = new InvalidDataException("recorded diagnostic boundary failure");
        var capture = new CapturingNativePlanProbe(fixture.Workspace, _ => throw original);
        fixture.Coordinator.TransitionProbe = capture;
        var failure = await Assert.ThrowsAsync<InvalidDataException>(fixture.RunRealtimeCycleAsync);
        Assert.Same(original, failure.InnerException);
        Assert.NotEmpty(capture.Actions);
        Assert.Contains("actionRows=[id=", failure.Message);
        Assert.Null(ReadPrivateReference(fixture.Workspace, "activeLease"));
        fixture.Coordinator.TransitionProbe = null;
    }

    private sealed class SmartReplacementCountProbe : IHostManagerSmartCoordinatorTransitionProbe
    {
        internal int Creates;
        internal int Plans;
        public void Reach(HostManagerSmartCoordinatorTransitionPoint point)
        {
            if (point == HostManagerSmartCoordinatorTransitionPoint.BeforeReplacementCreate) Creates++;
            if (point == HostManagerSmartCoordinatorTransitionPoint.BeforeNativeCall) Plans++;
        }
        public void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point) { }
    }

    private static IDisposable EnterOriginalForegroundOperation(HostManagerSmartCoordinator coordinator)
        => (IDisposable)typeof(HostManagerSmartCoordinator)
            .GetMethod("EnterForegroundControlRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, null)!;
}
