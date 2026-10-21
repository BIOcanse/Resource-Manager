using System.Threading.Channels;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfRetiringLocked();
            if (started || workerCancellation is not null)
            {
                throw new InvalidOperationException(
                    "The operation coordinator owner is already started.");
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
            var firstPlannerCycle = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfRetiringLocked();
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
            SignalPlanner();
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
            if (!retiring)
            {
                retiring = true;
                ready = false;
                started = false;
                planProvider.Published -= OnPublished;
                PublishHealthOnlyLocked();
            }
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
        SignalPlanner();
        if (workers.Length != 0)
        {
            await Task.WhenAll(workers).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseRuntimeLocked();
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
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
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
        store?.Dispose();
        store = null;
        workerCancellation?.Dispose();
        workerCancellation = null;
        attemptArbiter.Dispose();
        actionChannel = null;
        plannerWorker = null;
        actionWorkers = [];
    }
}
