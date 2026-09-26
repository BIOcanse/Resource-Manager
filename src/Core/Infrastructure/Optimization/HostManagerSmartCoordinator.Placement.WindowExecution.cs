using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using System.Globalization;
using System.Text.Json;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private WindowsGpuWindowActionExecutor? gpuWindowExecution;
    private Task<bool>? gpuWindowRelease;

    private async Task<RunningGpuPlacementWindowResult> ExecuteOwnedGpuWindowAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key, GpuWindowActionRequest request,
        CompiledGpuWindowExecutionLimits limits, ulong deadline, CancellationToken cancellationToken)
    {
        RequireNoGpuWindowOwnerWork();
        request.Validate();
        var remaining = RemainingGpuActionTime(deadline);
        var cleanup = TimeSpan.FromMilliseconds(limits.CleanupReserveMilliseconds);
        if (cancellationToken.IsCancellationRequested || remaining <= cleanup)
            return new(GpuWindowActionOutcome.NotExecuted, null, false);

        var execution = gpuWindowRuntime.Create(new(remaining, cleanup, limits.MaximumFrameBytes, limits.PipeBufferBytes), cancellationToken, deadline);
        gpuWindowExecution = execution;
        var result = await execution.RunAsync(request, prepared => SaveGpuWindowPreparationAsync(state, key, prepared, CancellationToken.None));
        gpuWindowRelease = ReleaseGpuWindowExecutionAsync(execution);
        if (gpuActionCheckpoint is { } checkpoint)
            _ = SaveGpuWindowResultAsync(key, checkpoint.Record.RecordId, result, CancellationToken.None);

        var record = CreateGpuWindowRuntimeRecord(result);
        var save = gpuActionCheckpoint?.Completion;
        var pending = Task.WhenAll(gpuWindowRelease, (Task?)save ?? Task.CompletedTask);
        remaining = RemainingGpuActionTime(deadline);
        try
        {
            if (remaining > TimeSpan.Zero)
                await pending.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "GPU window result handed back to its original owner for settlement.");
        }
        var canContinue = pending.IsCompletedSuccessfully && gpuWindowRelease.Result
            && !cancellationToken.IsCancellationRequested && RemainingGpuActionTime(deadline) > cleanup;
        return new(result.Outcome, record, canContinue);
    }

    private static RunningGpuPlacementActionRecord? CreateGpuWindowRuntimeRecord(GpuWindowActionResult result)
    {
        if (result.Prepared is not { } prepared) return null;
        var request = prepared.Window.Request;
        var fact = GpuWindowActionRecord.Complete(GpuWindowActionRecord.Create(prepared), result);
        return new(fact.RecordId, request.Method == GpuWindowActionMethod.Resize
            ? "window-rerender-noactivate-resizebuffers-rebuild-opportunity" : "future-frame-takeover-recreate-opportunity",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["processId"] = request.ProcessId.ToString(CultureInfo.InvariantCulture),
                ["processStartKey"] = request.CreationFileTimeUtc.ToString(CultureInfo.InvariantCulture),
                ["hwnd"] = $"0x{request.Window:x}",
                ["windowOutcome"] = result.Outcome.ToString(),
                ["windowExecution"] = JsonSerializer.Serialize(result),
                ["methodBoundary"] = "controlled window request only; not proof of device or workload migration"
            });
    }

    private async Task<bool> ReleaseGpuWindowExecutionAsync(WindowsGpuWindowActionExecutor execution)
    {
        try
        {
            await (execution.CleanupCompletion ?? throw new InvalidOperationException("The execution did not provide its cleanup task."));
            await execution.DisposeAsync();
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "GPU window execution cleanup is unconfirmed; retain {Worker} without retry.", execution.Worker);
            return false;
        }
    }

    private bool TrySettleGpuWindowExecution()
    {
        if (gpuWindowExecution is null) return true;
        if (gpuWindowRelease is not { IsCompletedSuccessfully: true } || !gpuWindowRelease.Result) return false;
        gpuWindowRelease = null;
        gpuWindowExecution = null;
        return true;
    }

    private async Task DrainGpuWindowExecutionAsync()
    {
        if (gpuWindowExecution is null) return;
        if (gpuWindowRelease is null || !await gpuWindowRelease.ConfigureAwait(false))
            throw new InvalidOperationException("The original window execution has not released its native resources.");
        _ = TrySettleGpuWindowExecution();
    }

    private void RequireNoGpuWindowOwnerWork()
    {
        RequireNoGpuActionCheckpoint();
        if (gpuCallbackPreparation is not null)
            throw new InvalidOperationException("The original callback preparation has not been settled.");
        if (gpuWindowExecution is not null)
            throw new InvalidOperationException("The original window execution has not been settled.");
    }

    private static TimeSpan RemainingGpuActionTime(ulong deadline)
    {
        var now = MonotonicMilliseconds();
        return deadline <= now ? TimeSpan.Zero : TimeSpan.FromMilliseconds(checked((long)(deadline - now)));
    }
}
