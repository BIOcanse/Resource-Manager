using System.Threading.Channels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner
{
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (initialized) return;
                if (retiring)
                {
                    // A canceled or failed stop still owns its runtime. Only a
                    // fully drained hosted run can reopen admission.
                    if (workspace is not null || pendingWorkspaceClose is not null || store is not null)
                        ThrowIfRetiringLocked();
                    attemptArbiter = new();
                    retiring = false;
                    persistenceFaulted = false;
                    faultStage = null;
                    faultMessage = null;
                    Volatile.Write(ref pendingQuiescentRecreate, 0);
                    Volatile.Write(ref lastPlanApplicationError, null);
                }
                planProvider.Published += OnPublished;
            }
            finally
            {
                mutationGate.Release();
            }

            try
            {
                await ApplyPlanAsync(
                        planProvider.Current.HostManager.RequirePublished(),
                        throwOnFailure: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                initialized = true;
            }
            catch
            {
                planProvider.Published -= OnPublished;
                throw;
            }
        }
        finally { initializationGate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        WorkspaceHold? runWorkspace = null;
        try
        {
            var firstPlannerCycle = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfRetiringLocked();
                if (started || workerCancellation is not null)
                    throw new InvalidOperationException("The operation coordinator owner is already started.");
                Volatile.Write(ref executionTask, null);
                runWorkspace = AcquireWorkspaceLocked();
                // The immediate first cycle covers mutations queued while stopped.
                _ = plannerWake.Wait(0);
                var plan = appliedPlan
                    ?? throw new InvalidOperationException(
                        "The operation coordinator plan was not applied.");
                var workerCount = checked((int)Math.Max(
                    1U,
                    checked(
                        plan.HotPublish.MaximumGlobalRunningCount
                        + plan.HotPublish.MaximumCancelActionsPerPlan
                        + plan.HotPublish.MaximumRecoverActionsPerPlan)));
                // Keep every successfully started task reachable even if a later
                // allocation or Task.Run fails during construction.
                actionWorkers = new Task[workerCount];
                Array.Fill(actionWorkers, Task.CompletedTask);
                actionChannel = Channel.CreateBounded<HostManagerOperationActionTicket>(
                    new BoundedChannelOptions(
                        checked((int)plan.Recreate.Capacity.MaximumActionCount))
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleWriter = true,
                        SingleReader = false,
                        AllowSynchronousContinuations = false
                    });
                var cancellation = new CancellationTokenSource();
                workerCancellation = cancellation;
                var ownerStopping = cancellation.Token;
                started = true;
                plannerWorker = Task.Run(
                    () => RunPlannerLoopAsync(ownerStopping, firstPlannerCycle),
                    CancellationToken.None);
                for (var index = 0; index < actionWorkers.Length; index++)
                {
                    actionWorkers[index] = Task.Run(
                        () => RunActionWorkerAsync(ownerStopping),
                        CancellationToken.None);
                }
                var workers = actionWorkers.Append(plannerWorker).ToArray();
                var firstWorker = Task.WhenAny(workers);
                // The owner ends its own internal workers off this start turn,
                // without invoking callbacks inline under the mutation gate.
                var ownerDrain = Task.Run(async () =>
                {
                    await firstWorker.ConfigureAwait(false);
                    await cancellation.CancelAsync().ConfigureAwait(false);
                }, CancellationToken.None);
                var lifetime = HostedWorkerLifetime.JoinAsync(
                    firstWorker, ownerDrain, ownerStopping, workers);
                Volatile.Write(ref executionTask, lifetime);
                workspaceRunTask = ReleaseRunWorkspaceAsync(runWorkspace, lifetime);
                runWorkspace = null;
            }
            finally
            {
                mutationGate.Release();
            }
            await firstPlannerCycle.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Startup retains the failure that initiated cleanup. */ }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            if (runWorkspace is not null)
                await runWorkspace.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ReleaseRunWorkspaceAsync(IAsyncDisposable runWorkspace, Task lifetime)
    {
        try
        {
            // ExecutionTask is the run's result. Cleanup observes that result,
            // then releases this run's local hold after every worker has exited.
            await lifetime.ConfigureAwait(false);
        }
        catch (Exception) { }
        finally { await runWorkspace.DisposeAsync().ConfigureAwait(false); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] workers;
        Task lifetime;
        Task workspaceRun;
        CancellationTokenSource? cancellation;
        Channel<HostManagerOperationActionTicket>? channel;
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            started = false;
            workers = actionWorkers
                .Append(plannerWorker)
                .Where(static worker => worker is not null)
                .Cast<Task>()
                .ToArray();
            cancellation = workerCancellation;
            channel = actionChannel;
            lifetime = cancellation is null ? Task.WhenAll(workers)
                : executionTask ?? Task.WhenAll(workers);
            workspaceRun = workspaceRunTask ?? Task.CompletedTask;
        }
        finally
        {
            mutationGate.Release();
        }

        // Cancellation callbacks and effects may enter the owner. Never hold its
        // mutation gate while cancelling or waiting for the original workers.
        Exception? stopFailure = null;
        try
        {
            if (workers.Any(static worker => !worker.IsCompleted))
                attemptArbiter.RequestStopAll();
        }
        catch (Exception error) { stopFailure = error; }
        await DrainStoppedRunAsync(cancellation, channel, lifetime, workspaceRun, stopFailure).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DrainStoppedRunAsync(CancellationTokenSource? cancellation,
        Channel<HostManagerOperationActionTicket>? channel, Task lifetime,
        Task workspaceRun, Exception? stopFailure)
    {
        try { if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { stopFailure ??= error; }
        channel?.Writer.TryComplete();
        try
        {
            await lifetime.ConfigureAwait(false);
        }
        catch (Exception) { /* The original failure remains on ExecutionTask. */ }
        try { await workspaceRun.ConfigureAwait(false); }
        catch (Exception error) { stopFailure ??= error; }
        await mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(workerCancellation, cancellation)) return;
            workerCancellation?.Dispose();
            workerCancellation = null;
            actionChannel = null;
            plannerWorker = null;
            actionWorkers = [];
            workspaceRunTask = null;
        }
        finally
        {
            mutationGate.Release();
        }
        if (stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
    }

    public void Dispose()
    {
        if (Volatile.Read(ref disposed))
        {
            return;
        }
        if (!mutationGate.Wait(0))
        {
            throw new InvalidOperationException(
                "The operation coordinator is busy; use DisposeAsync to drain it.");
        }
        try
        {
            if (disposed)
            {
                return;
            }
            if (started || workerCancellation is not null
                || workspaceHolder is not null
                || plannerWorker is { IsCompleted: false }
                || actionWorkers.Any(static worker => !worker.IsCompleted))
            {
                throw new InvalidOperationException(
                    "The operation coordinator is running; use DisposeAsync to drain it.");
            }
            retiring = true;
            ready = false;
            planProvider.Published -= OnPublished;
            CloseRuntimeLocked();
            plannerWake.Dispose();
            disposed = true;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            retiring = true;
            ready = false;
            planProvider.Published -= OnPublished;
            PublishHealthOnlyLocked();
        }
        finally { mutationGate.Release(); }
        Exception? cleanupFailure = null;
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { cleanupFailure = error; }
        await workspaceDrained.Task.ConfigureAwait(false);
        await mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            retiring = true;
            ready = false;
            planProvider.Published -= OnPublished;
            CloseRuntimeLocked();
            plannerWake.Dispose();
            disposed = true;
        }
        finally
        {
            // This purely managed gate intentionally survives resource closure:
            // a publication already queued on it must enter and reject retirement.
            mutationGate.Release();
        }
        if (cleanupFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private void CloseRuntimeLocked()
    {
        pendingWorkspaceClose?.Dispose();
        pendingWorkspaceClose = null;
        workspace?.Dispose();
        workspace = null;
        workspaceHolder = null;
        workspaceRunTask = null;
        store?.Dispose();
        store = null;
        appliedPlan = null;
        workerCancellation?.Dispose();
        workerCancellation = null;
        attemptArbiter.Dispose();
        actionChannel = null;
        plannerWorker = null;
        actionWorkers = [];
    }
}
