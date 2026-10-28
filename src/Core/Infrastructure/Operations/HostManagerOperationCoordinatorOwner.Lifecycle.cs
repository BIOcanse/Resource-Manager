using System.Threading.Channels;
using ResourceManager.App.Domain.RuntimeSpecialization;

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
                runWorkspaceHold = AcquireWorkspaceLocked();
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
                workerCancellation = new CancellationTokenSource();
                var ownerStopping = workerCancellation.Token;
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
            }
            finally
            {
                mutationGate.Release();
            }
            await firstPlannerCycle.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] workers;
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
        }
        finally
        {
            mutationGate.Release();
        }

        // Cancellation callbacks and effects may enter the owner. Never hold its
        // mutation gate while cancelling or waiting for the original workers.
        if (workers.Any(static worker => !worker.IsCompleted))
        {
            attemptArbiter.RequestStopAll();
        }
        cancellation?.Cancel();
        channel?.Writer.TryComplete();
        if (workers.Length != 0)
        {
            await Task.WhenAll(workers).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            workerCancellation?.Dispose();
            workerCancellation = null;
            actionChannel = null;
            plannerWorker = null;
            actionWorkers = [];
            if (runWorkspaceHold is not null)
            {
                // Native calls by workers have drained; finite commands retain
                // their own references until their individual return paths.
                if (workspaceHolder!.Release())
                {
                    workspace = null;
                    workspaceHolder = null;
                    workspaceDrained.TrySetResult();
                }
                runWorkspaceHold = null;
            }
        }
        finally
        {
            mutationGate.Release();
        }
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
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
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
    }

    private void CloseRuntimeLocked()
    {
        pendingWorkspaceClose?.Dispose();
        pendingWorkspaceClose = null;
        workspace?.Dispose();
        workspace = null;
        workspaceHolder = null;
        runWorkspaceHold = null;
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
