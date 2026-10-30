using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Operations;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Operations;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task NeverStartedDisposeIsIdempotentAndRejectsLaterStart()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        fixture.Owner.Dispose();
        fixture.Owner.Dispose();
        await fixture.Owner.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Owner.StartAsync(CancellationToken.None));
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "store"));
    }

    [Fact]
    public async Task SyncDisposeWhileRunningDoesNotCancelEffectOrCloseResources()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewSignal();
        fixture.ComponentExecutor.OnExecute = async (_, token) =>
        {
            entered.TrySetResult(token);
            await release.Task;
            return new(HostManagerOperationEffectOutcome.Succeeded, "done");
        };
        try
        {
            var submitted = await fixture.SubmitComponentDownloadAsync();
            var effectToken = await entered.Task.WaitAsync(TestBound);
            var workspace = OwnerField<object>(fixture.Owner, "workspace");
            var store = OwnerField<object>(fixture.Owner, "store");
            Assert.Throws<InvalidOperationException>(() => fixture.Owner.Dispose());
            Assert.False(effectToken.IsCancellationRequested);
            Assert.True(fixture.Owner.GetHealth().Ready);
            Assert.False(OwnerField<bool>(fixture.Owner, "retiring"));
            Assert.Same(workspace, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(store, OwnerField<object>(fixture.Owner, "store"));
            release.TrySetResult();
            Assert.Equal(HostManagerOperationStates.Succeeded,
                (await fixture.WaitForTerminalAsync(submitted.Id)).State);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task SyncDisposeBusyGateRefusesWithoutRetiring()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        var gate = OwnerGate(fixture.Owner);
        await gate.WaitAsync();
        try
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Owner.Dispose());
            Assert.False(OwnerField<bool>(fixture.Owner, "retiring"));
            Assert.False(OwnerField<bool>(fixture.Owner, "disposed"));
        }
        finally
        {
            gate.Release();
        }
        await fixture.Owner.DisposeAsync();
    }

    [Fact]
    public async Task StopCancelledBeforeAdmissionLeavesRunningOwnerUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workspace = OwnerField<object>(fixture.Owner, "workspace");
        var cancellation = OwnerField<object>(fixture.Owner, "workerCancellation");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Owner.StopAsync(cancelled.Token));
        Assert.True(fixture.Owner.GetHealth().Ready);
        Assert.False(OwnerField<bool>(fixture.Owner, "retiring"));
        Assert.Same(workspace, OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Same(cancellation,
            OwnerField<object>(fixture.Owner, "workerCancellation"));
    }

    [Fact]
    public async Task StopCancelledWhileDrainingRetainsAdmissionAndRuntimeUntilFinalDisposal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = NewSignal();
        var stopping = NewSignal();
        var release = NewSignal();
        fixture.ComponentExecutor.OnExecute = async (_, token) =>
        {
            using var registration = token.Register(() => stopping.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return new(HostManagerOperationEffectOutcome.Succeeded, "late result");
        };
        try
        {
            var original = await fixture.SubmitComponentDownloadAsync();
            await entered.Task.WaitAsync(TestBound);
            var workspace = OwnerField<object>(fixture.Owner, "workspace");
            var store = OwnerField<object>(fixture.Owner, "store");
            var workers = OwnerField<object>(fixture.Owner, "actionWorkers");
            var cancellation = OwnerField<CancellationTokenSource>(
                fixture.Owner, "workerCancellation")!;
            using var stopCancellation = new CancellationTokenSource();
            var stop = fixture.Owner.StopAsync(stopCancellation.Token);
            await stopping.Task.WaitAsync(TestBound);
            Assert.False(OwnerField<bool>(fixture.Owner, "retiring"));
            Assert.True(fixture.Owner.GetHealth().Ready);
            Assert.True(cancellation.IsCancellationRequested);
            stopCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.False(fixture.Owner.GetHealth().PersistenceFaulted);
            Assert.Same(workspace, OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(store, OwnerField<object>(fixture.Owner, "store"));
            Assert.Same(workers, OwnerField<object>(fixture.Owner, "actionWorkers"));
            Assert.Same(cancellation,
                OwnerField<object>(fixture.Owner, "workerCancellation"));
            Assert.Throws<IOException>(() =>
            {
                using var conflict = new FileStream(fixture.EnvelopePath + ".lock",
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            var admitted = await fixture.SubmitComponentDownloadAsync();
            Assert.Equal(original.Id, admitted.Id);
            Assert.NotNull(fixture.Owner.Get(admitted.Id));
            Assert.Equal(1, fixture.ComponentExecutor.ExecuteCount);
            release.TrySetResult();
            await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(TestBound);
            Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Same(store, OwnerField<object>(fixture.Owner, "store"));
            Assert.True(fixture.Owner.GetHealth().Ready);
            Assert.Null(OwnerField<object>(fixture.Owner, "workerCancellation"));
            Assert.Throws<IOException>(() =>
            {
                using var conflict = new FileStream(fixture.EnvelopePath + ".lock",
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            await fixture.Owner.DisposeAsync();
            Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Null(OwnerField<object>(fixture.Owner, "store"));
            using var available = new FileStream(fixture.EnvelopePath + ".lock",
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fixture.Owner.Dispose();
            fixture.Owner.Dispose();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task AsyncServiceProviderDisposalDrainsRealOwnerBeforeReleasingStore()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = NewSignal();
        var stopping = NewSignal();
        var release = NewSignal();
        fixture.ComponentExecutor.OnExecute = async (_, token) =>
        {
            using var registration = token.Register(() => stopping.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return new(HostManagerOperationEffectOutcome.Succeeded, "late");
        };
        var services = new ServiceCollection();
        services.AddSingleton<HostManagerOperationCoordinatorOwner>(_ => fixture.Owner);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(fixture.Owner,
            provider.GetRequiredService<HostManagerOperationCoordinatorOwner>());
        try
        {
            _ = await fixture.SubmitComponentDownloadAsync();
            await entered.Task.WaitAsync(TestBound);
            var disposal = provider.DisposeAsync().AsTask();
            await stopping.Task.WaitAsync(TestBound);
            Assert.True(OwnerField<bool>(fixture.Owner, "retiring"));
            Assert.False(disposal.IsCompleted);
            Assert.NotNull(OwnerField<object>(fixture.Owner, "store"));
            release.TrySetResult();
            await disposal.WaitAsync(TestBound);
            Assert.True(OwnerField<bool>(fixture.Owner, "disposed"));
            Assert.Null(OwnerField<object>(fixture.Owner, "store"));
            Assert.False(fixture.Owner.GetHealth().PersistenceFaulted);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task QueuedDirectAndPublicationCannotReopenAfterFinalDisposalClosesRuntime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = OwnerGate(fixture.Owner);
        await gate.WaitAsync();
        Task stop;
        Task direct;
        try
        {
            // Both calls synchronously enqueue their first WaitAsync, in order.
            stop = fixture.Owner.DisposeAsync().AsTask();
            direct = fixture.SubmitComponentDownloadAsync();
            fixture.Provider.Publish(CompiledRuntimePlan.Default with
            {
                Version = 2,
                CompiledAt = DateTimeOffset.UtcNow,
                Reason = "queued-before-retirement",
                HostManager = fixture.Plan
            });
            Assert.False(stop.IsCompleted);
            Assert.False(direct.IsCompleted);
        }
        finally
        {
            gate.Release();
        }
        await stop.WaitAsync(TestBound);
        await Assert.ThrowsAsync<InvalidOperationException>(() => direct);
        Assert.Null(fixture.Owner.LastPlanApplicationError);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "store"));
        Assert.False(fixture.Owner.GetHealth().PersistenceFaulted);
        // The queued publication returned its real lease; a later publication
        // can proceed, but the retired owner is unsubscribed and stays closed.
        await Task.Run(() => fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 3,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "after-retirement",
            HostManager = fixture.Plan
        })).WaitAsync(TestBound);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
    }

    [Fact]
    public async Task DisposedOwnerKeepsManagedGateForLateRequestsAndCannotRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Owner.StopAsync(CancellationToken.None);
        fixture.Owner.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.SubmitComponentDownloadAsync());
        await InvokeOwnerTask(fixture.Owner, "ApplyPublishedPlanAsync");
        Assert.Null(fixture.Owner.LastPlanApplicationError);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Owner.StartAsync(CancellationToken.None));
        var gate = OwnerGate(fixture.Owner);
        Assert.True(gate.Wait(0));
        gate.Release();
    }

    [Fact]
    public async Task StartedAdmissionReturnsRegisteredControlWithDurableExactReceipt()
    {
        await using var fixture = await Fixture.CreateInitializedWithoutWorkersAsync();
        _ = await fixture.SubmitComponentDownloadAsync();
        var channel = OwnerField<Channel<HostManagerOperationActionTicket>>(
            fixture.Owner, "actionChannel")!;
        await InvokeOwnerTask(fixture.Owner, "PlanOnceAsync", CancellationToken.None);
        var ticket = await channel.Reader.ReadAsync().AsTask().WaitAsync(TestBound);
        var mark = InvokeOwnerTask(fixture.Owner, "MarkActionStartedAsync",
            ticket, CancellationToken.None);
        await mark;
        var control = Assert.IsType<HostManagerOperationAttemptArbiter.AttemptControl>(
            mark.GetType().GetProperty("Result")!.GetValue(mark));
        var arbiter = OwnerField<HostManagerOperationAttemptArbiter>(
            fixture.Owner, "attemptArbiter")!;
        Assert.False(arbiter.IsEmpty);
        Assert.Same(control.Completion, arbiter.RequestCancel(
            ticket.Action.OperationId, ticket.Action.AttemptToken));
        Assert.True(control.Token.IsCancellationRequested);
        var receipt = fixture.ReadEnvelope().Receipts.Require(ticket.ReceiptId);
        Assert.Equal(HostManagerOperationEffectReceiptState.Started, receipt.State);
        Assert.Equal(ticket.Action.OperationId, receipt.OperationId);
        Assert.Equal(ticket.Action.AttemptToken, receipt.AttemptToken);
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
        arbiter.Complete(ticket.Action.OperationId, ticket.Action.AttemptToken);
    }

    [Fact]
    public async Task RetiredWorkerEntriesExitWithoutNativeMutationOrNewFault()
    {
        await using var fixture = await Fixture.CreateInitializedWithoutWorkersAsync();
        _ = await fixture.SubmitComponentDownloadAsync();
        var channel = OwnerField<Channel<HostManagerOperationActionTicket>>(
            fixture.Owner, "actionChannel")!;
        await InvokeOwnerTask(fixture.Owner, "PlanOnceAsync", CancellationToken.None);
        var ticket = await channel.Reader.ReadAsync().AsTask().WaitAsync(TestBound);
        await fixture.Owner.DisposeAsync();
        var before = fixture.Owner.GetPublishedState();
        await InvokeOwnerTask(fixture.Owner, "PlanOnceAsync", CancellationToken.None);
        await InvokeOwnerTask(fixture.Owner, "MarkActionStartedAsync",
            ticket, CancellationToken.None);
        await InvokeOwnerTask(fixture.Owner, "ExecuteRecoveryTicketAsync",
            ticket, CancellationToken.None);
        await InvokeOwnerTask(fixture.Owner, "SettleCompletionAsync", ticket,
            new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Succeeded, "ignored"),
            CancellationToken.None);
        await InvokeOwnerTask(fixture.Owner, "ApplyActionFeedbackAsync", ticket,
            NativeOperationActionFeedbackOutcome.StartRetryableFailure,
            HostManagerOperationEffectReceiptOutcome.EffectNotObserved,
            "ignored", CancellationToken.None, null);
        await ((IHostManagerOperationProgressSink)fixture.Owner).ReportAsync(
            ticket, new HostManagerOperationProgressUpdate(), CancellationToken.None);
        await InvokeOwnerTask(fixture.Owner, "LatchWorkerFaultAsync",
            new IOException("retired worker"), "fixture");
        Assert.Same(before.Operations, fixture.Owner.GetPublishedState().Operations);
        Assert.Equal(before.PublicationRevision,
            fixture.Owner.GetPublishedState().PublicationRevision);
        Assert.False(fixture.Owner.GetHealth().PersistenceFaulted);
    }

    [Fact]
    public async Task RetirementDoesNotReplaceAnExistingRealPersistenceFault()
    {
        await using var fixture = await Fixture.CreateAsync();
        File.WriteAllBytes(fixture.EnvelopePath, [0xA5]);
        await Assert.ThrowsAnyAsync<Exception>(fixture.SubmitComponentDownloadAsync);
        var before = fixture.Owner.GetHealth();
        Assert.True(before.PersistenceFaulted);
        Assert.Equal("submit-publication", before.FaultStage);
        await fixture.Owner.DisposeAsync().AsTask().WaitAsync(TestBound);
        await InvokeOwnerTask(fixture.Owner, "LatchWorkerFaultAsync",
            new IOException("normal retirement must not replace this"), "retired");
        var after = fixture.Owner.GetHealth();
        Assert.True(after.PersistenceFaulted);
        Assert.Equal(before.FaultStage, after.FaultStage);
        Assert.Equal(before.FaultMessage, after.FaultMessage);
        fixture.Owner.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerPersistenceFaultRemainsTheRunResultAfterResourcesDrain(bool disposeDirectly)
    {
        var recording = new WorkspaceLifetimes();
        await using var fixture = await Fixture.CreateUnstartedWithWorkspaceAsync(recording.Create);
        var entered = NewSignal();
        var release = NewSignal();
        fixture.ComponentExecutor.OnExecute = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new(HostManagerOperationEffectOutcome.Succeeded, "done");
        };
        await fixture.Owner.StartAsync(CancellationToken.None);
        var execution = fixture.Owner.ExecutionTask!;
        var workers = OwnerField<Task[]>(fixture.Owner, "actionWorkers")!;
        var planner = OwnerField<Task>(fixture.Owner, "plannerWorker")!;
        try
        {
            _ = await fixture.SubmitComponentDownloadAsync();
            await entered.Task.WaitAsync(TestBound);
            File.WriteAllBytes(fixture.EnvelopePath, [0xA5]);
            release.TrySetResult();
            var original = await Assert.ThrowsAsync<IOException>(
                () => execution.WaitAsync(TestBound));
            Assert.Same(original, Assert.Single(execution.Exception!.InnerExceptions));
            Assert.Contains(workers.Append(planner), worker => worker.Exception is { } failure
                && failure.InnerExceptions.Any(error => ReferenceEquals(error, original)));
            var before = fixture.Owner.GetHealth();
            Assert.True(before.PersistenceFaulted);
            Assert.False(before.Ready);
            Assert.Equal("effect-completion-publication", before.FaultStage);
            Assert.Equal(original.Message, before.FaultMessage);

            if (disposeDirectly)
                await fixture.Owner.DisposeAsync().AsTask().WaitAsync(TestBound);
            else
                await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(TestBound);

            Assert.All(workers.Append(planner), worker => Assert.True(worker.IsCompleted));
            Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
            Assert.Null(OwnerField<object>(fixture.Owner, "workspaceHolder"));
            Assert.Null(OwnerField<object>(fixture.Owner, "workerCancellation"));
            Assert.Null(OwnerField<object>(fixture.Owner, "workspaceRunTask"));
            Assert.Equal(1, Assert.Single(recording.Workspaces).CloseCount);
            Assert.Same(execution, fixture.Owner.ExecutionTask);
            Assert.Same(original, Assert.Single(execution.Exception!.InnerExceptions));
            Assert.Equal(before, fixture.Owner.GetHealth());
            await fixture.Owner.DisposeAsync().AsTask().WaitAsync(TestBound);
            Assert.Null(OwnerField<object>(fixture.Owner, "store"));
            Assert.Equal(before, fixture.Owner.GetHealth());
            using var available = new FileStream(fixture.EnvelopePath + ".lock",
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public void SettledAndDisposedAttemptCanReceiveLateStopSnapshot()
    {
        var control = new HostManagerOperationAttemptArbiter.AttemptControl();
        control.Complete();
        control.Dispose();
        control.RequestStop();
        Assert.True(control.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void UnsettledDisposedAttemptStillReportsInvalidStop()
    {
        var control = new HostManagerOperationAttemptArbiter.AttemptControl();
        control.Dispose();
        Assert.Throws<ObjectDisposedException>(() => control.RequestStop());
        Assert.False(control.Completion.IsCompleted);
    }

    [Fact]
    public void StopDoesNotHideRealCancellationCallbackFailure()
    {
        using var control = new HostManagerOperationAttemptArbiter.AttemptControl();
        using var registration = control.Token.Register(
            () => throw new IOException("real callback failure"));
        var error = Assert.Throws<AggregateException>(() => control.RequestStop());
        Assert.IsType<IOException>(Assert.Single(error.InnerExceptions));
        Assert.False(control.Completion.IsCompleted);
    }

    [Fact]
    public void StopRunsCallbackOutsideArbiterGate()
    {
        using var arbiter = new HostManagerOperationAttemptArbiter();
        var operation = new NativeOperationHandle128(1, 2);
        var attempt = new NativeOperationHandle128(3, 4);
        var control = arbiter.Register(operation, attempt);
        var completion = control.Completion;
        using var registration = control.Token.Register(() =>
        {
            var completing = Task.Run(() => arbiter.Complete(operation, attempt));
            Assert.True(completing.Wait(TestBound));
        });
        // Complete needs the arbiter gate on another thread. A lock held around
        // callback execution would fail this bounded wait, not silently reenter.
        arbiter.RequestStopAll();
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.True(arbiter.IsEmpty);
    }

    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(15);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static SemaphoreSlim OwnerGate(HostManagerOperationCoordinatorOwner owner)
        => OwnerField<SemaphoreSlim>(owner, "mutationGate")!;

    private static T? OwnerField<T>(HostManagerOperationCoordinatorOwner owner, string name)
        => (T?)typeof(HostManagerOperationCoordinatorOwner)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner);

    private static Task InvokeOwnerTask(
        HostManagerOperationCoordinatorOwner owner, string method, params object[] arguments)
        => (Task)typeof(HostManagerOperationCoordinatorOwner)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, arguments)!;

    private sealed partial class Fixture
    {
        internal static Task<Fixture> CreateUnstartedAsync()
            => CreateAsync(CreateTemporaryRoot(), CreateSmallPlan(),
                ownsRoot: true, start: false);

        internal static async Task<Fixture> CreateInitializedWithoutWorkersAsync()
        {
            var fixture = await CreateUnstartedAsync();
            try
            {
                await InvokeOwnerTask(fixture.Owner, "ApplyPlanAsync",
                    fixture.Plan, true, CancellationToken.None);
                await HoldWorkspaceForManualWorkersAsync(fixture.Owner);
                typeof(HostManagerOperationCoordinatorOwner)
                    .GetField("actionChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(fixture.Owner,
                        Channel.CreateBounded<HostManagerOperationActionTicket>(
                            checked((int)fixture.Plan.OperationCoordinator.Recreate
                                .Capacity.MaximumActionCount)));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }
    }
}
