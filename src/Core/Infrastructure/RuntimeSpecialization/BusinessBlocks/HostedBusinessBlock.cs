using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Owns the original hosted service's readiness and stop contract.</summary>
internal sealed class HostedBusinessBlock(string id, Func<IHostedService> resolve,
    HostedBusinessBlockRegistry registry, BusinessBlockDiagnosticsRegistry diagnostics) : IBusinessBlock
{
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly TaskCompletionSource hostStopTurn = NewSignal();
    private CancellationToken startupCancellation;
    private Session? pendingSession;
    private Session? current;

    internal Session PrepareStart(CancellationToken cancellation)
    {
        lock (sync)
        {
            startupCancellation = cancellation;
            if (current is { StopStarted: false }) return current;
            return pendingSession ??= new(NewSignal(), cancellation);
        }
    }

    // Records the host's original turn without notifying the running entry to stop.
    internal void SetHostStopTurn()
    {
        lock (sync)
        {
            hostStopTurn.TrySetResult();
        }
    }

    // Read after demand withdrawal is dispatched: Host must wait for a restarted run,
    // while its original session remains the source of the original execution failure.
    internal Task CurrentStopCompletion()
    {
        lock (sync) return current?.StopCompleted.Task ?? Task.CompletedTask;
    }

    public async Task RunAsync(BusinessBlockRunContext context)
    {
        Session session;
        lock (sync)
        {
            session = pendingSession ?? new(NewSignal(), startupCancellation);
            pendingSession = null;
            current = session;
            session.StopNotification = context.StopNotification;
            diagnostics.BeginRun(id);
        }
        using var notification = context.StopNotification.Register(() =>
        {
            lock (sync) BeginStop(session);
        });
        var result = BusinessBlockResult.Done;
        var stage = BusinessBlockFailureStage.Start;
        try
        {
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                session.Service = resolve();
                await session.Service.StartAsync(session.StartCancellation).ConfigureAwait(false);
            }
            finally
            {
                lifecycle.Release();
                session.StartFinished.TrySetResult();
            }
            session.Execution = (session.Service as BackgroundService)?.ExecuteTask;
            context.ReportRunning();
            session.Readiness.TrySetResult();
            stage = BusinessBlockFailureStage.Run;
            var execution = session.Execution;
            if (execution is not null)
            {
                await Task.WhenAny(execution, session.StopCompleted.Task).ConfigureAwait(false);
                if (context.StopNotification.IsCancellationRequested)
                {
                    stage = BusinessBlockFailureStage.Stop;
                    await session.StopCompleted.Task.ConfigureAwait(false);
                }
                stage = BusinessBlockFailureStage.Run;
                await execution.ConfigureAwait(false);
            }
            else
            {
                stage = BusinessBlockFailureStage.Stop;
                await session.StopCompleted.Task.ConfigureAwait(false);
            }
            if (context.StopNotification.IsCancellationRequested) result = BusinessBlockResult.Stopped;
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested
            && stage != BusinessBlockFailureStage.Stop)
        {
            result = BusinessBlockResult.Stopped;
        }
        catch (Exception error)
        {
            result = BusinessBlockResult.Failed(error, stage);
        }
        finally
        {
            // Complete this run's real-service cleanup before the controller can start
            // another run on the same container-owned service instance.
            session.Execution ??= (session.Service as BackgroundService)?.ExecuteTask;
            lock (sync) BeginStop(session);
            try { await session.StopCompleted.Task.ConfigureAwait(false); }
            catch (Exception error)
            {
                if (result.Outcome != BusinessBlockOutcome.Failed)
                    result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop);
            }
            if (session.Execution is { } execution)
            {
                try { await execution.ConfigureAwait(false); }
                catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (result.Outcome != BusinessBlockOutcome.Failed)
                        result = BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Run);
                }
            }
        }
        if (result.Error is { } failure) session.Readiness.TrySetException(failure);
        else if (context.StopNotification.IsCancellationRequested) session.Readiness.TrySetCanceled(context.StopNotification);
        RecordResult(session, result);
    }

    // Called under sync; each run owns exactly one original StopAsync operation.
    private void BeginStop(Session session)
    {
        if (session.StopStarted) return;
        session.StopStarted = true;
        _ = StopAsync(session);
    }

    private async Task StopAsync(Session session)
    {
        try
        {
            await session.StartFinished.Task.ConfigureAwait(false);
            if (registry.IsHostStopping) await hostStopTurn.Task.ConfigureAwait(false);
            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (session.Service is not null)
                    // The host may stop waiting at its process-boundary deadline.
                    // This run still owns cleanup and cannot return before it drains.
                    await session.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { lifecycle.Release(); }
            session.StopCompleted.TrySetResult();
        }
        catch (Exception error)
        {
            RecordResult(session, BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop));
            session.StopCompleted.TrySetException(error);
        }
    }

    private void RecordResult(Session session, BusinessBlockResult result)
    {
        lock (sync)
            if (ReferenceEquals(current, session)) diagnostics.RecordResult(id, result);
    }

    internal sealed class Session(TaskCompletionSource readiness, CancellationToken startCancellation)
    {
        internal readonly TaskCompletionSource Readiness = readiness;
        internal readonly CancellationToken StartCancellation = startCancellation;
        internal CancellationToken StopNotification;
        internal readonly TaskCompletionSource StartFinished = NewSignal();
        internal readonly TaskCompletionSource StopCompleted = NewSignal();
        internal IHostedService? Service;
        internal Task? Execution;
        internal bool StopStarted;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
