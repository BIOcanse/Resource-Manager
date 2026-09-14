using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task WindowHandoffRejectsAStaleCheckpointWhileTheOriginalCommitIsPending()
    {
        var path = Path.Combine(NewRoot("pending-owner-red-green"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var prepared = Prepared();
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
        { AppliedPlacements = [Placement(Policy(prepared))] };
        await store.SaveAsync(state, CancellationToken.None);
        var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
        committer.PauseNext();
        var preparation = Task.Run(() => InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync",
            state, key, prepared, CancellationToken.None));
        Task? stale = null;
        Exception? rejection = null;
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            stale = InvokeWindowCheckpoint(fixture.Coordinator, state);
            try { await stale.WaitAsync(TimeSpan.FromMilliseconds(200)); }
            catch (Exception error) { rejection = error; }
        }
        finally
        {
            committer.Release();
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            if (stale is not null) try { await stale.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
        var disk = ReadCanonical(path);
        output.WriteLine("windowHandoffStale=" + JsonSerializer.Serialize(new
        {
            path, rejection = rejection?.GetType().Name, committer.Calls,
            factCount = disk.AppliedPlacements.SelectMany(item => item.Records).Count(GpuWindowActionRecord.IsActionFact),
            canonicalReadBeforeDispose = true
        }));
        Assert.IsType<InvalidOperationException>(rejection);
        Assert.Single(disk.AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
    }

    private static Task InvokeWindowCheckpoint(HostManagerSmartCoordinator coordinator, HostManagerRollbackStateDocument state)
        => Assert.IsAssignableFrom<Task>(typeof(HostManagerSmartCoordinator)
            .GetMethod("SavePlacementCheckpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [state, "Stale checkpoint fixture.", CancellationToken.None]));

    internal static Task<bool> SettleWindowCheckpoint(HostManagerSmartCoordinator coordinator)
        => Assert.IsAssignableFrom<Task<bool>>(typeof(HostManagerSmartCoordinator)
            .GetMethod("TrySettleGpuActionCheckpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, null));

    internal static async Task WithWindowLedgerCoordinatorAsync(IHostManagerRollbackStateStore store,
        Func<HostManagerSmartCoordinator, Task> action,
        ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution.WindowsGpuWindowActionRuntime? windowRuntime = null,
        ResourceManager.App.Application.GpuPlacement.IRunningGpuPlacementActionService? actions = null,
        Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>>? processRecoveryRead = null)
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false,
            gpuWindowRuntime: windowRuntime, runningGpuActions: actions, processRecoveryRead: processRecoveryRead);
        await action(fixture.Coordinator);
    }

    private static Task<HostManagerRollbackStateDocument> CurrentWindowTask(HostManagerSmartCoordinator coordinator)
    {
        var value = typeof(HostManagerSmartCoordinator).GetField("gpuActionCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;
        return Assert.IsAssignableFrom<Task<HostManagerRollbackStateDocument>>(value.GetType().GetProperty("Completion")!.GetValue(value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowHandoffKeepsOneTaskThroughDelayedPreparationOrResult(bool delayResult)
    {
        var path = Path.Combine(NewRoot("handoff-chain"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        var counts = new WindowCountingStore(store);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: counts, automaticMemoryCleanupEnabled: false);
        var prepared = Prepared();
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
        { AppliedPlacements = [Placement(Policy(prepared))] };
        await store.SaveAsync(state, CancellationToken.None);
        var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
        if (!delayResult) committer.PauseNext();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var preparation = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Same(preparation, CurrentWindowTask(fixture.Coordinator));
        Task<HostManagerRollbackStateDocument>? final = null;
        try
        {
            if (delayResult)
            {
                await preparation.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
                committer.PauseNext();
            }
            else await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var result = delayResult ? Restored(prepared) : Unknown(prepared) with
            { Outcome = GpuWindowActionOutcome.NotExecuted, AuthorizationMayHaveBeenSent = false, PersistencePending = true };
            final = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowResultAsync", key,
                GpuWindowActionRecord.Create(prepared).RecordId, result, CancellationToken.None);
            Assert.Same(final, CurrentWindowTask(fixture.Coordinator));
            if (delayResult) await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(final.IsCompleted);
            Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
            var calls = (counts.Loads, counts.Saves, counts.Reservations);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.GetStateAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.GetStatusAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWindowCheckpoint(fixture.Coordinator, state));
            _ = await fixture.RunRealtimeCycleAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(calls, (counts.Loads, counts.Saves, counts.Reservations));
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            committer.Release();
            var saved = await final.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(result, GpuWindowActionRecord.TryRead(
                Assert.Single(saved.AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact), out var decoded) ? decoded.Result : null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWindowCheckpoint(fixture.Coordinator, state));
            Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
            var fresh = await fixture.Coordinator.GetStateAsync(CancellationToken.None);
            Assert.Single(fresh.AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
            await InvokeWindowCheckpoint(fixture.Coordinator, fresh);
            Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
            output.WriteLine("windowHandoffChain=" + JsonSerializer.Serialize(new
            { path, delayResult, samePreparationTask = true, sameResultTask = true, result, committer.Calls, canonicalReadBeforeDispose = true }));
        }
        finally
        {
            committer.Release();
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            if (final is not null) await final.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WindowHandoffObservesTheOriginalFailureOnceWithoutRetry(bool finalFailure, bool ambiguous)
    {
        var path = Path.Combine(NewRoot("handoff-failure"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
                   committer, WindowsHostManagerDurableRootManifestCommitter.Instance))
        {
            await WithWindowLedgerCoordinatorAsync(store, async coordinator =>
            {
                var prepared = Prepared();
                var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
                { AppliedPlacements = [Placement(Policy(prepared))] };
                await store.SaveAsync(state, CancellationToken.None);
                var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
                var outcome = ambiguous ? HostManagerRollbackStateCommitOutcome.CommitAmbiguous : HostManagerRollbackStateCommitOutcome.NotCommitted;
                if (!finalFailure) committer.FailNext = outcome;
                var preparation = InvokeWindowOwner(coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None);
                HostManagerRollbackStateCommitException? first = null;
                if (finalFailure)
                {
                    await preparation;
                    committer.FailNext = outcome;
                }
                else first = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => preparation);
                var result = finalFailure ? Restored(prepared) : Unknown(prepared) with
                { Outcome = GpuWindowActionOutcome.NotExecuted, AuthorizationMayHaveBeenSent = false };
                var completion = InvokeWindowOwner(coordinator, "SaveGpuWindowResultAsync", key,
                    GpuWindowActionRecord.Create(prepared).RecordId, result, CancellationToken.None);
                var error = await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => completion);
                if (first is not null) Assert.Same(first, error);
                Assert.Equal(outcome, error.Outcome);
                var commits = committer.Calls;
                Assert.False(await SettleWindowCheckpoint(coordinator));
                Assert.True(await SettleWindowCheckpoint(coordinator));
                var loaded = await coordinator.GetStateAsync(CancellationToken.None);
                Assert.Equal(commits, committer.Calls);
                var facts = loaded.AppliedPlacements.SelectMany(item => item.Records).Where(GpuWindowActionRecord.IsActionFact).ToArray();
                Assert.Equal(finalFailure || ambiguous ? 1 : 0, facts.Length);
                if (facts.Length != 0)
                {
                    Assert.True(GpuWindowActionRecord.TryRead(facts[0], out var decoded));
                    Assert.Equal(finalFailure && ambiguous ? result : null, decoded.Result);
                }
                output.WriteLine("windowHandoffFailure=" + JsonSerializer.Serialize(new
                { path, finalFailure, ambiguous, error.Outcome, committer.Calls, finalRecordCount = facts.Length, canonicalReadBeforeDispose = true }));
            });
        }
        using var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        Assert.Equal(finalFailure || ambiguous, (await reopened.LoadAsync(CancellationToken.None)).AppliedPlacements
            .SelectMany(item => item.Records).Any(GpuWindowActionRecord.IsActionFact));
    }

    [Theory]
    [InlineData("missing-preparation")]
    [InlineData("key")]
    [InlineData("record")]
    [InlineData("worker")]
    [InlineData("duplicate-result")]
    public async Task WindowHandoffRejectsResultsOutsideTheOriginalSingleSave(string mismatch)
    {
        var path = Path.Combine(NewRoot("handoff-identity"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await WithWindowLedgerCoordinatorAsync(store, async coordinator =>
        {
            var prepared = Prepared();
            var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
            { AppliedPlacements = [Placement(Policy(prepared))] };
            await store.SaveAsync(state, CancellationToken.None);
            var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
            var id = GpuWindowActionRecord.Create(prepared).RecordId;
            if (mismatch != "missing-preparation") await InvokeWindowOwner(coordinator,
                "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None);
            if (mismatch == "duplicate-result") await InvokeWindowOwner(coordinator,
                "SaveGpuWindowResultAsync", key, id, Restored(prepared), CancellationToken.None);
            var before = File.ReadAllBytes(path);
            var error = await Record.ExceptionAsync(() => InvokeWindowOwner(coordinator,
                "SaveGpuWindowResultAsync", mismatch == "key" ? HostManagerPlacementReceiptKey.Create(
                    state.AppliedPlacements[0] with { TargetId = "other" }) : key,
                mismatch == "record" ? "other" : id,
                Restored(mismatch == "worker" ? prepared with { Worker = prepared.Worker with { ProcessId = 50000 } } : prepared), CancellationToken.None));
            Assert.NotNull(error);
            Assert.IsAssignableFrom<Exception>(error);
            Assert.True(error is InvalidDataException or InvalidOperationException, error.ToString());
            Assert.Equal(before, File.ReadAllBytes(path));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowHandoffCancellationDoesNotCancelOrReissueTheOriginalPreparation(bool cancelResult)
    {
        var path = Path.Combine(NewRoot("handoff-cancel"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await WithWindowLedgerCoordinatorAsync(store, async coordinator =>
        {
            var prepared = Prepared();
            var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
            { AppliedPlacements = [Placement(Policy(prepared))] };
            await store.SaveAsync(state, CancellationToken.None);
            var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
            var canceled = new CancellationToken(true);
            var preparation = InvokeWindowOwner(coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared,
                cancelResult ? CancellationToken.None : canceled);
            if (cancelResult) await preparation;
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            var completion = InvokeWindowOwner(coordinator, "SaveGpuWindowResultAsync", key,
                GpuWindowActionRecord.Create(prepared).RecordId, Unknown(prepared), cancelResult ? canceled : CancellationToken.None);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
            Assert.False(await SettleWindowCheckpoint(coordinator));
            Assert.True(await SettleWindowCheckpoint(coordinator));
            Assert.Equal(cancelResult, ReadCanonical(path).AppliedPlacements.SelectMany(item => item.Records).Any(GpuWindowActionRecord.IsActionFact));
        });
    }

    private sealed class WindowCountingStore(IHostManagerRollbackStateStore inner) : IHostManagerRollbackStateStore
    {
        internal int Loads;
        internal int Saves;
        internal int Reservations;
        public Task<HostManagerRollbackStateDocument> LoadAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref Loads); return inner.LoadAsync(cancellationToken); }
        public Task SaveAsync(HostManagerRollbackStateDocument document, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Saves); return inner.SaveAsync(document, cancellationToken); }
        public Task<HostManagerRollbackStateDocument> ReserveNativeHostSessionIncarnationAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref Reservations); return inner.ReserveNativeHostSessionIncarnationAsync(cancellationToken); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowHandoffShutdownOwnsTheSamePendingSaveUntilItCompletes(bool cancelWait)
    {
        var path = Path.Combine(NewRoot("handoff-shutdown"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var prepared = Prepared();
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
        { AppliedPlacements = [Placement(Policy(prepared))] };
        await store.SaveAsync(state, CancellationToken.None);
        var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
        await InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None);
        committer.PauseNext();
        var completion = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowResultAsync", key,
            GpuWindowActionRecord.Create(prepared).RecordId, Restored(prepared), CancellationToken.None);
        Task? stop = null;
        bool closedBeforeCommit;
        HostManagerSmartCoordinatorLifecycleState stateBeforeCommit;
        Exception? stopWaitError = null;
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            stop = fixture.Coordinator.StopAsync(CancellationToken.None);
            closedBeforeCommit = stop.IsCompleted;
            stateBeforeCommit = fixture.Coordinator.LifecycleState;
            if (cancelWait) stopWaitError = await Record.ExceptionAsync(() => fixture.Coordinator.StopAsync(new CancellationToken(true)));
        }
        finally
        {
            committer.Release();
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        output.WriteLine("windowHandoffShutdown=" + JsonSerializer.Serialize(new
        { path, cancelWait, closedBeforeCommit, stateBeforeCommit, stopWaitError = stopWaitError?.GetType().Name,
            stateAfterCommit = fixture.Coordinator.LifecycleState, committer.Calls, canonicalReadBeforeDispose = true }));
        Assert.False(closedBeforeCommit);
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closing, stateBeforeCommit);
        if (cancelWait) Assert.IsAssignableFrom<OperationCanceledException>(stopWaitError);
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closed, fixture.Coordinator.LifecycleState);
        var fact = Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
        Assert.True(GpuWindowActionRecord.TryRead(fact, out var decoded));
        Assert.Equal(Restored(prepared), decoded.Result);
    }

    [Theory]
    [InlineData("preparation")]
    [InlineData("failed-preparation")]
    [InlineData("failed-result")]
    public async Task WindowHandoffShutdownDrainsPendingPreparationAndObservesLateFailureWithoutRetry(string scenario)
    {
        var path = Path.Combine(NewRoot("handoff-shutdown-chain"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var prepared = Prepared();
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(CancellationToken.None)) with
        { AppliedPlacements = [Placement(Policy(prepared))] };
        await store.SaveAsync(state, CancellationToken.None);
        var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
        var failResult = scenario == "failed-result";
        if (!failResult)
        {
            committer.PauseNext();
            if (scenario == "failed-preparation") committer.FailNext = HostManagerRollbackStateCommitOutcome.NotCommitted;
        }
        var preparation = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync", state, key, prepared, CancellationToken.None);
        if (failResult)
        {
            await preparation;
            committer.PauseNext();
            committer.FailNext = HostManagerRollbackStateCommitOutcome.NotCommitted;
        }
        var result = failResult ? Unknown(prepared) : Unknown(prepared) with
        { Outcome = GpuWindowActionOutcome.NotExecuted, AuthorizationMayHaveBeenSent = false, PersistencePending = true };
        var completion = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowResultAsync", key,
            GpuWindowActionRecord.Create(prepared).RecordId, result, CancellationToken.None);
        Task? stop = null;
        Exception? saveError = null;
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            stop = fixture.Coordinator.StopAsync(CancellationToken.None);
            Assert.False(stop.IsCompleted);
            Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closing, fixture.Coordinator.LifecycleState);
        }
        finally
        {
            committer.Release();
            try { await completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (HostManagerRollbackStateCommitException error) { saveError = error; }
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(scenario != "preparation", saveError is HostManagerRollbackStateCommitException);
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Closed, fixture.Coordinator.LifecycleState);
        var calls = committer.Calls;
        await fixture.Coordinator.StopAsync(CancellationToken.None);
        fixture.Coordinator.Dispose();
        Assert.Equal(calls, committer.Calls);
        var facts = ReadCanonical(path).AppliedPlacements.SelectMany(item => item.Records).Where(GpuWindowActionRecord.IsActionFact).ToArray();
        Assert.Equal(scenario == "failed-preparation" ? 0 : 1, facts.Length);
        if (facts.Length != 0)
        {
            Assert.True(GpuWindowActionRecord.TryRead(facts[0], out var decoded));
            Assert.Equal(failResult ? null : result, decoded.Result);
        }
        output.WriteLine("windowHandoffShutdownChain=" + JsonSerializer.Serialize(new
        { path, scenario, saveError = saveError?.GetType().Name, committer.Calls,
            finalRecordCount = facts.Length, stateAfterSave = fixture.Coordinator.LifecycleState, canonicalReadBeforeStoreDispose = true }));
    }

    internal sealed class PausedWindowCommitter : IHostManagerRollbackStateFileCommitter, IDisposable
    {
        private readonly ManualResetEventSlim release = new(true);
        private TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pauseNext;
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal Task Entered => entered.Task;
        internal HostManagerRollbackStateCommitOutcome? FailNext { get; set; }

        internal void PauseNext()
        {
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release.Reset();
            Volatile.Write(ref pauseNext, 1);
        }

        internal void Release() => release.Set();

        public void Commit(string temporaryPath, string canonicalPath, ReadOnlySpan<byte> expectedImage, bool replaceExisting)
        {
            Interlocked.Increment(ref calls);
            var failure = FailNext;
            FailNext = null;
            if (Interlocked.Exchange(ref pauseNext, 0) != 0)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Window commit fixture release was not received.");
            }
            if (failure != HostManagerRollbackStateCommitOutcome.NotCommitted)
                WindowsHostManagerRollbackStateFileCommitter.Instance.Commit(temporaryPath, canonicalPath, expectedImage, replaceExisting);
            if (failure is { } outcome)
                throw new HostManagerRollbackStateCommitException(outcome, new IOException("Paused window commit fixture."));
        }

        public void Dispose() => release.Dispose();
    }
}
