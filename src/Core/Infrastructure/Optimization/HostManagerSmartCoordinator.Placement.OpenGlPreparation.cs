using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.Preparation;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private WindowsOpenGlCallbackPreparation? gpuCallbackPreparation;
    private Task? gpuCallbackRelease;

    private async Task<(HostManagerRollbackStateDocument State, PreparedOpenGlCallbacks? Source)> ExecuteOwnedOpenGlPreparationAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key, GpuPlacementProcessInstance target,
        ulong targetAdapter, CompiledGpuWindowExecutionLimits limits, ulong deadline, CancellationToken cancellationToken)
    {
        RequireNoGpuWindowOwnerWork();
        var placement = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key);
        var policyRecord = placement.Records.Single(record => GpuShimPolicyRecord.TryRead(record, out var policy)
            && policy.AppliedValue.AsSpan().SequenceEqual(D3d11ProxyShimRuntime.CreateExactPolicyValue(targetAdapter))
            && record.Metadata?.GetValueOrDefault("processId") == target.ProcessId.ToString(CultureInfo.InvariantCulture)
            && record.Metadata?.GetValueOrDefault("processStartKey") == target.ProcessStartKey.ToString(CultureInfo.InvariantCulture));
        if (GpuActionFacts.BlocksProcess(state.AppliedPlacements, placement.TargetId, target.ProcessId, target.ProcessStartKey))
            throw new InvalidOperationException("An unsettled GPU action owns this exact process.");
        var remaining = RemainingGpuActionTime(deadline);
        var cleanup = TimeSpan.FromMilliseconds(limits.CleanupReserveMilliseconds);
        if (cancellationToken.IsCancellationRequested || remaining <= cleanup) return (state, null);

        var execution = gpuCallbackRuntime.Create(new(remaining, cleanup, limits.PreparationMaximumFrameBytes,
            limits.PipeBufferBytes), cancellationToken, deadline);
        gpuCallbackPreparation = execution;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gpuCallbackRelease = release.Task;
        PreparedOpenGlCallbacks? callbacks = null;
        // This single task owns preparation, native release and persistence, even after the caller stops waiting.
        var saving = Task.Run(async () =>
        {
            var saved = state;
            OpenGlCallbackPreparationResult? result = null;
            GpuWindowActionProcessCleanup? completedCleanup = null;
            try
            {
                saved = await SaveOpenGlPreparationFactAsync(saved, key, policyRecord.RecordId,
                    new(target, targetAdapter, "pending", null, null, null, null, null)).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested && RemainingGpuActionTime(deadline) > cleanup)
                    result = await execution.RunAsync(target.ProcessId, checked((long)target.ProcessStartKey), targetAdapter).ConfigureAwait(false);
                if (execution.CleanupCompletion is { } completion)
                    completedCleanup = await completion.ConfigureAwait(false);
            }
            finally
            {
                try { await execution.DisposeAsync().ConfigureAwait(false); release.SetResult(); }
                catch (Exception error) { release.SetException(error); throw; }
            }

            var failure = result as OpenGlCallbackPreparationResult.Failure;
            saved = await SaveOpenGlPreparationFactAsync(saved, key, policyRecord.RecordId,
                new(target, targetAdapter, result is null ? "not-executed" : "completed", result?.Worker,
                    failure?.AcquisitionError, failure?.NativeCleanupError, failure?.ExecutionError,
                    completedCleanup)).ConfigureAwait(false);
            if (result is OpenGlCallbackPreparationResult.Success success
                && completedCleanup is { Complete: true, ExitObserved: true, ExitCode: 0, TerminationRequested: false,
                    TerminationError: null, ObservationError: null }
                && !cancellationToken.IsCancellationRequested && RemainingGpuActionTime(deadline) > cleanup)
                callbacks = success.Source;
            return saved;
        });
        gpuActionCheckpoint = new(key, policyRecord, saving);
        try
        {
            remaining = RemainingGpuActionTime(deadline);
            if (remaining > TimeSpan.Zero) state = await saving.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return (state, null);
        }
        if (!saving.IsCompletedSuccessfully) return (state, null);
        state = await saving.ConfigureAwait(false);
        var released = TrySettleGpuCallbackPreparation();
        var persisted = await TrySettleGpuActionCheckpointAsync().ConfigureAwait(false);
        return (state, released && persisted && !cancellationToken.IsCancellationRequested
            && RemainingGpuActionTime(deadline) > cleanup ? callbacks : null);
    }

    private sealed record OpenGlPreparationFact(GpuPlacementProcessInstance Process, ulong CallbackAdapter,
        string Phase, GpuWindowActionProcessIdentity? Worker, uint? AcquisitionError, uint? NativeCleanupError,
        string? ExecutionError, GpuWindowActionProcessCleanup? Cleanup);

    private Task<HostManagerRollbackStateDocument> SaveOpenGlPreparationFactAsync(HostManagerRollbackStateDocument state,
        HostManagerPlacementReceiptKey key, string policyRecordId, OpenGlPreparationFact fact)
    {
        var placement = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key);
        var original = placement.Records.Single(record => record.RecordId == policyRecordId && GpuShimPolicyRecord.TryRead(record, out _));
        var updated = original with { Metadata = new Dictionary<string, string>(original.Metadata!, StringComparer.Ordinal)
            { ["openGlPreparation"] = JsonSerializer.Serialize(fact) } };
        return PersistGpuActionCheckpointAsync(ReplaceWindowPlacement(state, placement,
            placement.Records.Select(record => record == original ? updated : record).ToArray()),
            "Host Manager retained callback preparation on its original GPU policy record.", CancellationToken.None);
    }

    private bool TrySettleGpuCallbackPreparation()
    {
        if (gpuCallbackPreparation is null) return true;
        if (gpuCallbackRelease is not { IsCompletedSuccessfully: true }) return false;
        gpuCallbackPreparation = null;
        gpuCallbackRelease = null;
        return true;
    }

    private async Task DrainGpuCallbackPreparationAsync()
    {
        if (gpuCallbackPreparation is null) return;
        await (gpuCallbackRelease ?? throw new InvalidOperationException("Callback preparation has no original release task.")).ConfigureAwait(false);
        _ = TrySettleGpuCallbackPreparation();
    }
}
