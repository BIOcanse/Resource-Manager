using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Observes the existing hosted lifetime without adding a default stop budget.</summary>
internal sealed class HostedBusinessBlock(Func<IHostedService> resolve) : IBusinessBlock, IBusinessBlockStartup
{
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<BusinessBlockResult> stopped = new();
    private CancellationToken startupCancellation;
    private IHostedService? service;
    public Task Started => started.Task;

    internal void SetStartCancellation(CancellationToken cancellation) => startupCancellation = cancellation;

    // Called at the host's original stop turn, even when its token has already expired.
    // The synchronous prefix invokes the real StopAsync before another service's turn.
    internal Task StopAsync(CancellationToken hostCancellation, TimeSpan? deadline)
        => StopCoreAsync(hostCancellation, deadline);

    private async Task StopCoreAsync(CancellationToken hostCancellation, TimeSpan? deadline)
    {
        using var configuredDeadline = deadline is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(hostCancellation) : null;
        if (deadline is { } timeout) configuredDeadline!.CancelAfter(timeout);
        try
        {
            service ??= resolve();
            await service.StopAsync(configuredDeadline?.Token ?? hostCancellation).ConfigureAwait(false);
            stopped.TrySetResult(BusinessBlockResult.Stopped);
        }
        catch (Exception error)
        {
            stopped.TrySetResult(BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Stop));
            throw;
        }
    }

    public async Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
    {
        try
        {
            service = resolve();
            await service.StartAsync(startupCancellation).ConfigureAwait(false);
            started.TrySetResult();
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            return BusinessBlockResult.Failed(error, BusinessBlockFailureStage.Start);
        }
        var result = await stopped.Task.ConfigureAwait(false);
        if (result.Outcome == BusinessBlockOutcome.Failed) return result;
        // This observes actual execution for diagnostics. Host stop awaits StopAsync above,
        // not this completion, so an expired host budget does not become a new wait.
        if (service is BackgroundService { ExecuteTask: { } execution })
        {
            try { await execution.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopNotification.IsCancellationRequested) { }
            catch (Exception error) { return BusinessBlockResult.Failed(error); }
        }
        return result;
    }
}
