using System.Reflection;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;
using static Resource_Manager_APP.Tests.GpuWindowActionExitRecordTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(GpuRemoteCallKind.LoadProvider)]
    [InlineData(GpuRemoteCallKind.ConfigureProvider)]
    [InlineData(GpuRemoteCallKind.ReadDevices)]
    public async Task RemoteCallUsesOriginalPolicyAndDurableIntentBeforeStarting(GpuRemoteCallKind kind)
    {
        var path = Path.Combine(NewRoot("remote-normal"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var state = await SeedRemotePolicy(store);
        var call = new ControlledRemoteCall(kind, completed: true);
        call.BeforeStart = () => Assert.True(ReadRemote(path).BlocksProcess);
        var returned = await InvokeRemoteOwner(fixture.Coordinator, state, call, default);
        Assert.True(returned.Result.Completed);
        Assert.Equal(1, call.Starts);
        Assert.Equal(1, call.Disposals);
        var fact = ReadRemote(path);
        Assert.Equal(call.Request, fact.Request);
        Assert.NotNull(fact.Started);
        Assert.True(fact.Result!.Completed);
        Assert.False(fact.BlocksProcess);
        Assert.Null(fact.Settlement);
        Assert.Equal(2, ReadCanonical(path).AppliedPlacements[0].Records.Count);
    }

    [Fact]
    public async Task RemoteCallTimeoutBlocksOnlyItsExactGpuTargetAndLateCompletionSettlesTheSameFact()
    {
        var path = Path.Combine(NewRoot("remote-late"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var state = await SeedRemotePolicy(store);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.ReadDevices);
        await CancelRemoteOwner(fixture.Coordinator, state, call);
        var timedOut = ReadRemote(path);
        Assert.Equal("remote-call-cancelled", timedOut.Result!.Status);
        Assert.Null(timedOut.Result.ExitCode);
        Assert.Equal(0, call.Disposals);
        var target = call.Request.Process;
        var placements = ReadCanonical(path).AppliedPlacements;
        Assert.True(GpuActionFacts.BlocksProcess(placements, "alias", target.ProcessId, target.ProcessStartKey));
        Assert.False(GpuActionFacts.BlocksProcess(placements, "alias", target.ProcessId, target.ProcessStartKey + 1));
        Assert.False(GpuActionFacts.BlocksProcess(placements, "alias", target.ProcessId + 1, target.ProcessStartKey));
        Assert.Empty(GpuActionFacts.AvailablePlacementEffects(placements));
        var cpu = placements[0] with { ResourceKind = OptimizationResourceKinds.Cpu };
        Assert.Single(GpuActionFacts.AvailablePlacementEffects([.. placements, cpu]));
        var rejected = new ControlledRemoteCall(GpuRemoteCallKind.ConfigureProvider, completed: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeRemoteOwner(fixture.Coordinator,
            ReadCanonical(path), rejected, default));
        Assert.Equal(0, rejected.Starts);
        Assert.Equal(1, rejected.Disposals);
        Assert.True(await ReconcileRemoteOwner(fixture.Coordinator));
        Assert.Equal(timedOut.Result, ReadRemote(path).Result);
        Assert.Equal(1, call.Starts);
        call.Complete = true;
        Assert.True(await ReconcileRemoteOwner(fixture.Coordinator));
        var settled = ReadRemote(path);
        Assert.Equal(timedOut.RecordId, settled.RecordId);
        Assert.Equal(timedOut.Result, settled.Result);
        Assert.True(settled.Settlement!.Completed);
        Assert.False(settled.BlocksProcess);
        Assert.Equal(1, call.Starts);
        Assert.Equal(1, call.Disposals);
        Assert.Single(GpuActionFacts.AvailablePlacementEffects(ReadCanonical(path).AppliedPlacements));
    }

    [Fact]
    public async Task RemoteCallCancellationWhileInitialCommitIsPausedNeverStartsTheCall()
    {
        var path = Path.Combine(NewRoot("remote-before-start"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var state = await SeedRemotePolicy(store);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.LoadProvider);
        using var stop = new CancellationTokenSource();
        committer.PauseNext();
        var running = InvokeRemoteOwner(fixture.Coordinator, state, call, stop.Token);
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var original = CurrentWindowTask(fixture.Coordinator);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(0, call.Starts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeWindowCheckpoint(fixture.Coordinator, state));
            committer.Release();
            await original.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
            Assert.Equal(0, call.Starts);
            Assert.False(ReadRemote(path).BlocksProcess);
            Assert.Equal("not-started", ReadRemote(path).Result!.Status);
        }
        finally { committer.Release(); }
    }

    [Fact]
    public async Task RemoteCallOwnerShutdownAndLedgerReopenKeepUnknownWorkBlockedUntilExactTargetExit()
    {
        var path = Path.Combine(NewRoot("remote-restart"), "recovery.json");
        var call = new ControlledRemoteCall(GpuRemoteCallKind.ReadDevices);
        using (var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1)))
        {
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
                rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
            await CancelRemoteOwner(fixture.Coordinator, await SeedRemotePolicy(store), call);
        }
        Assert.Equal(1, call.Disposals);
        var original = ReadRemote(path);
        Assert.True(original.BlocksProcess);
        Assert.Null(original.Result!.ExitCode);
        using var reopened = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        var exited = false;
        await using var restarted = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: reopened, automaticMemoryCleanupEnabled: false,
            processRecoveryRead: pid => exited ? Gone() : Live(pid, checked((long)call.Request.Process.ProcessStartKey)));
        Assert.True(await ReconcileRemoteOwner(restarted.Coordinator));
        Assert.True(ReadRemote(path).BlocksProcess);
        Assert.Equal(1, call.Starts);
        exited = true;
        Assert.True(await ReconcileRemoteOwner(restarted.Coordinator));
        var settled = ReadRemote(path);
        Assert.False(settled.BlocksProcess);
        Assert.NotNull(settled.ExitSettlement);
        Assert.Equal(original.Result, settled.Result);
        Assert.Null(settled.Result!.ExitCode);
        Assert.Equal(1, call.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteCallFailedIntentNeverStartsAndReleasesOnlyItsUnstartedObject(bool closeDirectly)
    {
        var path = Path.Combine(NewRoot("remote-failed-intent"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var state = await SeedRemotePolicy(store);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.LoadProvider);
        committer.FailNext = HostManagerRollbackStateCommitOutcome.NotCommitted;
        await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() =>
            InvokeRemoteOwner(fixture.Coordinator, state, call, default));
        Assert.Equal(0, call.Starts);
        Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
        var commits = committer.Calls;
        if (closeDirectly) await fixture.Coordinator.StopAsync(default);
        else Assert.True(await ReconcileRemoteOwner(fixture.Coordinator));
        Assert.Equal(commits, committer.Calls);
        Assert.Equal(1, call.Disposals);
        Assert.DoesNotContain(ReadCanonical(path).AppliedPlacements.SelectMany(item => item.Records), GpuRemoteCallRecord.IsActionFact);
    }

    [Fact]
    public async Task RemoteCallFailedResultSaveRetainsObservedCancellationWhenItLaterSettles()
    {
        var path = Path.Combine(NewRoot("remote-failed-result"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            rollbackStateStoreOverride: store, automaticMemoryCleanupEnabled: false);
        var call = new ControlledRemoteCall(GpuRemoteCallKind.ReadDevices);
        using var cancel = new CancellationTokenSource();
        var running = InvokeRemoteOwner(fixture.Coordinator, await SeedRemotePolicy(store), call, cancel.Token);
        await call.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var originalSave = CurrentWindowTask(fixture.Coordinator);
        committer.FailNext = HostManagerRollbackStateCommitOutcome.NotCommitted;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Assert.ThrowsAsync<HostManagerRollbackStateCommitException>(() => originalSave);
        Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
        Assert.NotNull(ReadRemote(path).Started);
        Assert.Null(ReadRemote(path).Result);
        Assert.Equal(0, call.Disposals);
        call.Complete = true;
        Assert.True(await ReconcileRemoteOwner(fixture.Coordinator));
        var saved = ReadRemote(path);
        Assert.Equal("remote-call-cancelled", saved.Result!.Status);
        Assert.Null(saved.Result.ExitCode);
        Assert.True(saved.Settlement!.Completed);
        Assert.False(saved.BlocksProcess);
        Assert.Equal(1, call.Starts);
        Assert.Equal(1, call.Disposals);
    }

    private static async Task<HostManagerRollbackStateDocument> SeedRemotePolicy(JsonHostManagerRollbackStateStore store)
    {
        var state = (await store.LoadAsync(default)) with { AppliedPlacements = [Placement(Policy(Prepared()))] };
        await store.SaveAsync(state, default);
        return state;
    }

    [Fact]
    public async Task RemoteCallGpuPolicySaveUsesOriginalDeadlineAndNeverPublishesAfterLateCommit()
    {
        var path = Path.Combine(NewRoot("remote-policy-deadline"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions, store);
        var desired = (await CreateGpuDesired(fixture.Coordinator, AutomaticGpuProcess(), null))!;
        var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], new NativePlacementDesiredInput[1]).Records.Single();
        var state = await store.ReserveNativeHostSessionIncarnationAsync(default);
        var admission = HostManagerCycleEffectAdmission.Create(false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit));
        var action = new NativePlacementAction
        {
            TargetKey = projected.Identity.TargetKey, RecordKey = projected.Identity.RecordKey,
            DeadlineMilliseconds = checked((ulong)Environment.TickCount64 + 500)
        };
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        committer.PauseNext();
        var running = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ApplyAutomaticPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [permit, state, action, projected, CancellationToken.None])!;
        try
        {
            await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var original = CurrentWindowTask(fixture.Coordinator);
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            var result = running.GetType().GetProperty("Result")!.GetValue(running)!;
            Assert.False((bool)result.GetType().GetProperty("CanContinue")!.GetValue(result)!);
            Assert.False(original.IsCompleted);
            Assert.False(await SettleWindowCheckpoint(fixture.Coordinator));
            Assert.Null(runtime.ReadPolicy(desired.Placement.TargetId));
            Assert.Equal(0, actions.ApplyCalls);
            committer.Release();
            await original.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
            Assert.Null(runtime.ReadPolicy(desired.Placement.TargetId));
            Assert.Single(ReadCanonical(path).AppliedPlacements);
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.Empty(ReadCanonical(path).AppliedPlacements);
            Assert.Equal(0, actions.ApplyCalls);
            Assert.Null(runtime.ReadPolicy(desired.Placement.TargetId));
        }
        finally { committer.Release(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task RemoteCallGpuPolicyDoesNotPublishWhenProcessReadCrossesTheOriginalBoundary(bool cancel, int readResult)
    {
        var path = Path.Combine(NewRoot("remote-policy-read-boundary"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        var actions = new RecordingRunningGpuActions { Available = true };
        await using var fixture = await CreateGpuActionFixture(actions, store);
        var process = AutomaticGpuProcess();
        var desired = (await CreateGpuDesired(fixture.Coordinator, process, null))!;
        var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], new NativePlacementDesiredInput[1]).Records.Single();
        var state = await store.ReserveNativeHostSessionIncarnationAsync(default);
        var admission = HostManagerCycleEffectAdmission.Create(false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit));
        using var stop = new CancellationTokenSource();
        var action = new NativePlacementAction
        {
            TargetKey = projected.Identity.TargetKey, RecordKey = projected.Identity.RecordKey,
            DeadlineMilliseconds = checked((ulong)Environment.TickCount64 + 2000)
        };
        var reads = 0;
        fixture.ProcessPolicyWriter.RecoveryReadHandler = pid =>
        {
            reads++;
            Assert.Single(ReadCanonical(path).AppliedPlacements);
            Assert.True((ulong)Environment.TickCount64 < action.DeadlineMilliseconds);
            if (cancel) stop.Cancel();
            else
                Assert.True(SpinWait.SpinUntil(() => (ulong)Environment.TickCount64 >= action.DeadlineMilliseconds,
                    TimeSpan.FromSeconds(5)));
            return readResult switch
            {
                1 => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(5, "fixture denied"),
                2 => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(87, "fixture exited"),
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(
                    pid, DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey + (readResult == 3 ? 1 : 0)))))
            };
        };
        var running = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ApplyAutomaticPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [permit, state, action, projected, stop.Token])!;
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        var result = running.GetType().GetProperty("Result")!.GetValue(running)!;
        Assert.False((bool)result.GetType().GetProperty("CanContinue")!.GetValue(result)!);
        Assert.Equal(1, reads);
        Assert.Equal(0, actions.ApplyCalls);
        var runtime = new D3d11ProxyShimRuntime(new GpuPolicyEnvironment(fixture.Root));
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        Assert.Single(ReadCanonical(path).AppliedPlacements);
        fixture.ProcessPolicyWriter.RecoveryReadHandler = pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(
            pid, DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey))));
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Empty(ReadCanonical(path).AppliedPlacements);
        Assert.Null(runtime.ReadPolicy(process.TargetId));
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteCallGpuPolicyRetainsIntentWhenBaselineReadCrossesTheOriginalBoundary(bool cancel)
    {
        var path = Path.Combine(NewRoot("remote-policy-baseline-boundary"), "recovery.json");
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1));
        var actions = new RecordingRunningGpuActions { Available = true };
        var preferences = new GpuPreferenceValues();
        await using var fixture = await CreateGpuActionFixture(actions, store, preferences);
        var process = AutomaticGpuProcess();
        var desired = (await CreateGpuDesired(fixture.Coordinator, process, null))!;
        var record = new HostManagerAppliedRecord(HostManagerAppliedRecordKinds.GpuPreference, "fixture preference",
            new Dictionary<string, string>
            {
                ["processId"] = process.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["processStartKey"] = process.ProcessStartKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["path"] = process.ExecutablePath!, ["hadValue"] = "false", ["previousValue"] = string.Empty, ["appliedValue"] = "GpuPreference=1;"
            });
        desired = new(desired.Placement with { Records = [record] }, record, desired.Priority);
        var projected = HostManagerPlacementCoordinatorProjection.ProjectDesired([desired], new NativePlacementDesiredInput[1]).Records.Single();
        var state = await store.ReserveNativeHostSessionIncarnationAsync(default);
        var admission = HostManagerCycleEffectAdmission.Create(false);
        admission.InitializeBudgets(1, 1);
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NativeActionTransaction, out var permit));
        using var stop = new CancellationTokenSource();
        var action = new NativePlacementAction
        {
            TargetKey = projected.Identity.TargetKey, RecordKey = projected.Identity.RecordKey,
            DeadlineMilliseconds = checked((ulong)Environment.TickCount64 + 2000)
        };
        var reads = 0;
        preferences.Read = _ =>
        {
            reads++;
            Assert.Single(ReadCanonical(path).AppliedPlacements);
            Assert.True((ulong)Environment.TickCount64 < action.DeadlineMilliseconds);
            if (cancel) stop.Cancel();
            else Assert.True(SpinWait.SpinUntil(() => (ulong)Environment.TickCount64 >= action.DeadlineMilliseconds,
                TimeSpan.FromSeconds(5)));
            return RecoveryReadResult<string>.Found("GpuPreference=2;");
        };
        var running = (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ApplyAutomaticPlacementAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Coordinator, [permit, state, action, projected, stop.Token])!;
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        var result = running.GetType().GetProperty("Result")!.GetValue(running)!;
        Assert.Equal(1, reads);
        Assert.False((bool)result.GetType().GetProperty("CanContinue")!.GetValue(result)!);
        Assert.Single(ReadCanonical(path).AppliedPlacements);
        Assert.Equal(0, preferences.Writes);
        Assert.Equal(0, actions.ApplyCalls);
        preferences.Read = null;
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Empty(ReadCanonical(path).AppliedPlacements);
        Assert.Equal(0, preferences.Writes);
    }

    private static GpuRemoteCallRecord ReadRemote(string path)
    {
        var record = Assert.Single(ReadCanonical(path).AppliedPlacements.SelectMany(item => item.Records), GpuRemoteCallRecord.IsActionFact);
        Assert.True(GpuRemoteCallRecord.TryRead(record, out var fact));
        return fact;
    }

    private static Task<(HostManagerRollbackStateDocument State, GpuRemoteCallSnapshot Result)> InvokeRemoteOwner(
        HostManagerSmartCoordinator coordinator, HostManagerRollbackStateDocument state, GpuRemoteCallExecution call, CancellationToken token)
        => (Task<(HostManagerRollbackStateDocument, GpuRemoteCallSnapshot)>)typeof(HostManagerSmartCoordinator)
            .GetMethod("ExecuteOwnedGpuRemoteCallAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [state, HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]), call, token])!;

    private static Task<bool> ReconcileRemoteOwner(HostManagerSmartCoordinator coordinator)
        => (Task<bool>)typeof(HostManagerSmartCoordinator).GetMethod("ReconcileGpuRemoteCallsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(coordinator, [ExitAdmission(), CancellationToken.None])!;

    private static async Task CancelRemoteOwner(HostManagerSmartCoordinator coordinator,
        HostManagerRollbackStateDocument state, ControlledRemoteCall call)
    {
        using var stop = new CancellationTokenSource();
        var running = InvokeRemoteOwner(coordinator, state, call, stop.Token);
        await call.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = CurrentWindowTask(coordinator);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await saved.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await SettleWindowCheckpoint(coordinator));
    }

    private sealed class ControlledRemoteCall(GpuRemoteCallKind kind, bool completed = false,
        GpuRemoteCallRequest? request = null) : GpuRemoteCallExecution
    {
        private GpuRemoteCallSnapshot current = new(0, null, null, null, null, true, "not-started", null);
        public override GpuRemoteCallRequest Request { get; } = request ?? new(Guid.NewGuid(),
            new(Prepared().Window.Request.ProcessId, checked((ulong)Prepared().Window.Request.CreationFileTimeUtc), "fixture", "C:\\fixture.exe"),
            kind, 4096, 80, kind is GpuRemoteCallKind.ReadDevices or GpuRemoteCallKind.StartApiObservation
                or GpuRemoteCallKind.ReadApiObservation or GpuRemoteCallKind.StopApiObservation);
        public override GpuRemoteCallSnapshot Snapshot => current;
        internal bool Complete { get; set; } = completed;
        internal int Starts { get; private set; }
        internal int Disposals { get; private set; }
        internal Action? BeforeStart { get; set; }
        internal TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Start()
        {
            BeforeStart?.Invoke();
            Starts++;
            current = new(8192, 333, 133900000000000333, null, null, false, "running", null);
        }
        public override async Task WaitAsync(CancellationToken token)
        {
            WaitEntered.TrySetResult();
            if (!Complete)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { current = current with { Status = "remote-call-cancelled" }; }
            }
            Observe();
        }
        public override GpuRemoteCallSnapshot Observe()
        {
            if (Starts != 0 && Complete)
                current = current with { ResourcesReleased = true, Status = "completed", ExitCode = 1,
                    Response = Request.ReadResponse ? new byte[Request.ParameterByteLength] : null };
            return current;
        }
        public override void Dispose() => Disposals++;
    }
}
