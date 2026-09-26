using System.Globalization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private GpuActionCheckpoint? gpuActionCheckpoint;

    private sealed record GpuActionCheckpoint(HostManagerPlacementReceiptKey Key, HostManagerAppliedRecord Record,
        Task<HostManagerRollbackStateDocument> Completion, GpuWindowActionResult? Result = null,
        RunningGpuPlacementActionResult? ActionResult = null, GpuWindowActionExitSettlement? ExitSettlement = null,
        Guid? RemoteCallId = null);

    private Task<HostManagerRollbackStateDocument> SaveGpuWindowPreparationAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key,
        GpuWindowActionPrepared prepared, CancellationToken cancellationToken)
    {
        RequireNoGpuActionCheckpoint();
        var placement = RequireWindowPolicyOwner(state, key, prepared);
        var target = prepared.Window.Request;
        if (GpuActionFacts.BlocksProcess(state.AppliedPlacements, placement.TargetId,
            target.ProcessId, checked((ulong)target.CreationFileTimeUtc)))
            throw new InvalidOperationException("An unsettled window action already owns this process instance.");
        var record = GpuWindowActionRecord.Create(prepared);
        if (placement.Records.Any(item => item.Kind == record.Kind && item.RecordId == record.RecordId))
            throw new InvalidDataException("This window execution already has a durable fact.");
        var next = ReplaceWindowPlacement(state, placement, [.. placement.Records, record]);
        // The store includes synchronous native commits. Return the owned task before entering that I/O.
        var saving = Task.Run(() => PersistGpuActionCheckpointAsync(next,
            "Host Manager recorded a prepared window action before permission.", cancellationToken));
        gpuActionCheckpoint = new(key, record, saving);
        return saving;
    }

    private Task<HostManagerRollbackStateDocument> SaveGpuWindowResultAsync(
        HostManagerPlacementReceiptKey key,
        string recordId, GpuWindowActionResult result, CancellationToken cancellationToken)
    {
        var preparation = gpuActionCheckpoint;
        if (preparation is null || preparation.Result is not null || preparation.ActionResult is not null || preparation.Key != key
            || preparation.Record.RecordId != recordId)
            throw new InvalidOperationException("A window result must complete its single original preparation task.");
        var completed = GpuWindowActionRecord.Complete(preparation.Record, result);
        var saving = Task.Run(async () =>
        {
            var state = await preparation.Completion.ConfigureAwait(false);
            var placement = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key);
            var next = ReplaceWindowPlacement(state, placement,
                placement.Records.Select(item => item == preparation.Record ? completed : item).ToArray());
            return await PersistGpuActionCheckpointAsync(next,
                "Host Manager recorded the actual window execution result.", cancellationToken).ConfigureAwait(false);
        });
        gpuActionCheckpoint = preparation with { Completion = saving, Result = result };
        return saving;
    }

    private async Task<HostManagerRollbackStateDocument> PersistGpuActionCheckpointAsync(
        HostManagerRollbackStateDocument state, string message, CancellationToken cancellationToken)
    {
        var checkpoint = state with { LastRunAt = durableTimeSource.NextUtc(), Message = message };
        await stateStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private Task<bool> TrySettleGpuActionCheckpointAsync()
    {
        var checkpoint = gpuActionCheckpoint;
        if (checkpoint is null) return Task.FromResult(true);
        if ((GpuWindowActionRecord.IsActionFact(checkpoint.Record)
                && checkpoint.Result is null && checkpoint.ActionResult is null && checkpoint.ExitSettlement is null)
            || !checkpoint.Completion.IsCompleted) return Task.FromResult(false);
        return ObserveGpuActionCheckpointAsync(checkpoint);
    }

    private async Task DrainGpuActionCheckpointAsync()
    {
        if (gpuActionCheckpoint is { } checkpoint)
            await ObserveGpuActionCheckpointAsync(checkpoint).ConfigureAwait(false);
    }

    private async Task<bool> ObserveGpuActionCheckpointAsync(GpuActionCheckpoint checkpoint)
    {
        try
        {
            await checkpoint.Completion.ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "GPU action checkpoint persistence failed for {RecordId}; no retry. Actual result: {ActionResult}",
                checkpoint.Record.RecordId, System.Text.Json.JsonSerializer.Serialize(new { checkpoint.Result, checkpoint.ActionResult, checkpoint.ExitSettlement, checkpoint.RemoteCallId }));
            return false;
        }
        finally { gpuActionCheckpoint = null; }
    }

    private void RequireNoGpuActionCheckpoint()
    {
        if (gpuActionCheckpoint is not null)
            throw new InvalidOperationException("The original GPU action checkpoint has not been settled.");
    }

    private static HostManagerAppliedPlacementReceipt RequireWindowPolicyOwner(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key, GpuWindowActionPrepared prepared)
    {
        var placement = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key);
        var target = prepared.Window.Request;
        if (!placement.ResourceKind.Equals(OptimizationResourceKinds.Gpu, StringComparison.OrdinalIgnoreCase)
            || !placement.Records.Any(item => GpuShimPolicyRecord.TryRead(item, out _)
                && item.Metadata?.GetValueOrDefault("processId") == target.ProcessId.ToString(CultureInfo.InvariantCulture)
                && item.Metadata?.GetValueOrDefault("processStartKey") == target.CreationFileTimeUtc.ToString(CultureInfo.InvariantCulture)))
            throw new InvalidOperationException("Window permission requires the existing GPU policy owner for this exact process.");
        return placement;
    }

    private HostManagerRollbackStateDocument ReplaceWindowPlacement(HostManagerRollbackStateDocument state,
        HostManagerAppliedPlacementReceipt placement, IReadOnlyList<HostManagerAppliedRecord> records)
        => state with
        {
            AppliedPlacements = state.AppliedPlacements.Select(item => item == placement
                ? item with { Records = records, UpdatedAt = timeProvider.GetUtcNow() } : item).ToArray()
        };
}
