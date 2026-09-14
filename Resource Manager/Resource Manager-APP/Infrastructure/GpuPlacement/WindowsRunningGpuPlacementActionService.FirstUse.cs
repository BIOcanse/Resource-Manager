using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsRunningGpuPlacementActionService
{
    public Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(
        RunningGpuPlacementActionRequest request, RunningGpuApiObservationExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(execution.DurationMilliseconds);
        ArgumentNullException.ThrowIfNull(execution.ExecuteRemoteCallAsync);
        ArgumentNullException.ThrowIfNull(execution.WaitAsync);
        return PrepareWithFirstApiObservationAsync(request, async (process, token) =>
        {
            var result = await gpuPlacementInjector.ObserveApiOnceAsync(process, execution.DurationMilliseconds,
                execution.ExecuteRemoteCallAsync, (window, waitToken) => execution.WaitAsync(process, window, waitToken),
                token, execution.CleanupCancellationToken);
            if (result.Snapshot is null)
                logger.LogDebug("GPU API observation for {ProcessId} ended with {Status}, native error {NativeError}.",
                    process.ProcessId, result.Status, result.NativeError);
            return result.Snapshot?.Apis;
        }, cancellationToken);
    }

    public async Task<RunningGpuPlacementPreparation> PrepareWithFirstApiObservationAsync(
        RunningGpuPlacementActionRequest request,
        Func<GpuPlacementProcessInstance, CancellationToken, Task<GpuGraphicsApi?>> observeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observeAsync);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.TargetId) || string.IsNullOrWhiteSpace(request.SoftwareId)
            || request.TargetAdapterKey == 0)
            return new(null, "目标缺少软件身份或精确 GPU 位置。");
        var processes = request.Processes.Distinct().ToArray();
        if (processes.Select(static process => process.ProcessId).Distinct().Count() != processes.Length)
            return new(null, "同一 PID 对应冲突的进程身份，未执行动作。");
        if (processes.Length == 0 || processes.Any(static process => process.ProcessId <= 4
                || process.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(process.ExecutablePath)
                || !Path.IsPathFullyQualified(process.ExecutablePath)
                || Path.GetPathRoot(process.ExecutablePath) == process.ExecutablePath))
            return new(null, "目标缺少可观察的精确进程身份。");
        request = request with { Processes = processes };

        foreach (var process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AllowsFirstApiObservation(request, process)) continue;
            var history = await processHistoryStore.GetSoftwareHistoryAsync(
                request.SoftwareId, request.DisplayName, cancellationToken);
            var key = JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath);
            if (history.Processes.Any(row => StringComparer.OrdinalIgnoreCase.Equals(row.ProcessKey, key)
                    && GpuGraphicsApiRoutes.IsIdentified(row.GraphicsApi))) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!AllowsFirstApiObservation(request, process)) continue;

            // The caller owns observation, including its wait and native stop; the store holds no lock here.
            var api = await observeAsync(process, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!GpuGraphicsApiRoutes.IsIdentified(api) || !AllowsFirstApiObservation(request, process)) continue;
            await processHistoryStore.SaveFirstGraphicsApiAsync(
                request.SoftwareId, request.DisplayName, process, api!.Value, cancellationToken);
        }

        // Re-read the committed winner and current movement policy, not the proposed observation result.
        return await PrepareAsync(request, cancellationToken);
    }

    private bool AllowsFirstApiObservation(RunningGpuPlacementActionRequest request, GpuPlacementProcessInstance process)
    {
        var plan = runtimePlans.Current.GpuPlacement;
        return plan.GlobalPreciseProviderEnabled && plan.Resolve(request.SoftwareId, request.DisplayName, null,
            JsonGpuPlacementProcessHistoryStore.BuildProcessKey(process.ProcessName, process.ExecutablePath))
            .AllowsStartupShimExecution();
    }
}
