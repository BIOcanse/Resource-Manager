using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>
/// Step-1 compatibility boundary. The existing service retains its internal loops and resource policy.
/// Its hosted lifetime ends only when Generic Host requests stop, exactly as before this adapter.
/// </summary>
internal sealed class HostedBusinessBlock(Func<IHostedService> resolve) : IBusinessBlock, IBusinessBlockStartup
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken startupCancellation;
    private CancellationTokenSource? stopDeadline;
    public Task Started => started.Task;
    internal Exception? StopFailure { get; private set; }

    internal void SetStartCancellation(CancellationToken cancellation) => startupCancellation = cancellation;
    internal void SetStopDeadline(CancellationToken cancellation, TimeSpan? timeout)
    {
        stopDeadline?.Dispose();
        stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (timeout is { } deadline) stopDeadline.CancelAfter(deadline);
    }

    public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
    {
        IHostedService service;
        try
        {
            service = resolve();
            await service.StartAsync(startupCancellation).ConfigureAwait(false);
            started.TrySetResult();
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            return BusinessBlockResult.Failed(error);
        }
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var notification = stopNotification.Register(() => stop.TrySetResult());
        await stop.Task.ConfigureAwait(false);
        try
        {
            await service.StopAsync(stopDeadline?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) { StopFailure = error; return BusinessBlockResult.Failed(error); }
        finally { stopDeadline?.Dispose(); }
        // BackgroundService.StopAsync may return at its cancellation deadline while ExecuteTask still runs.
        // Keep the real execution outstanding so the controller reports timeout rather than false release.
        if (service is BackgroundService { ExecuteTask: { } execution })
        {
            try { await execution.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
            catch (Exception error) { return BusinessBlockResult.Failed(error); }
        }
        return BusinessBlockResult.Stopped;
    }
}
