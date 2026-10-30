using System.Runtime.ExceptionServices;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Joins worker tasks and the drain already requested by their business owner.</summary>
internal static class HostedWorkerLifetime
{
    internal static async Task JoinAsync(Task<Task> firstWorker, Task ownerDrain,
        CancellationToken stoppingToken, params Task[] workers)
    {
        var first = await firstWorker.ConfigureAwait(false);
        ExceptionDispatchInfo? failure = null;
        try { await first.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }

        ExceptionDispatchInfo? cancellationFailure = null;
        try { await ownerDrain.ConfigureAwait(false); }
        catch (Exception error) { cancellationFailure = ExceptionDispatchInfo.Capture(error); }
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }

        (failure ?? cancellationFailure)?.Throw();
    }
}
