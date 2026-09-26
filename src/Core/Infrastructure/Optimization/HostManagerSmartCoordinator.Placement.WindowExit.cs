using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<bool> ReconcileExitedGpuWindowActionsAsync(
        HostManagerCycleEffectAdmission admission, CancellationToken cancellationToken)
    {
        if (!admission.TryAcquire(HostManagerCycleEffectKind.ExitedOwnershipReconciliation, out var permit))
            return true;
        RequireNoGpuWindowOwnerWork();
        var state = await LoadRollbackStateAsync(cancellationToken);
        for (var placementIndex = 0; placementIndex < state.AppliedPlacements.Count; placementIndex++)
        {
            for (var recordIndex = 0; recordIndex < state.AppliedPlacements[placementIndex].Records.Count; recordIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (permit.RecoveryRemaining == 0) return true;
                var placement = state.AppliedPlacements[placementIndex];
                var record = placement.Records[recordIndex];
                if (!GpuWindowActionRecord.TryRead(record, out var fact) || !fact.BlocksAutomaticAction) continue;
                var target = fact.Prepared.Window.Request;
                var targetRead = processPolicyWriter.ReadProcessInstanceForRecovery(target.ProcessId);
                if (!ProcessInstanceRecovery.IsConfirmedExited(targetRead,
                    checked((uint)target.ProcessId), checked((ulong)target.CreationFileTimeUtc))) continue;
                var worker = fact.Prepared.Worker;
                var workerRead = processPolicyWriter.ReadProcessInstanceForRecovery(worker.ProcessId);
                if (!ProcessInstanceRecovery.IsConfirmedExited(workerRead,
                    checked((uint)worker.ProcessId), checked((ulong)worker.CreationFileTimeUtc))) continue;
                if (!permit.TryReserveRecovery(1, out _)) return true;
                var settlement = new GpuWindowActionExitSettlement(timeProvider.GetUtcNow(), targetRead, workerRead);
                var completed = fact.SettleExited(settlement);
                var records = placement.Records.ToArray();
                records[recordIndex] = completed;
                var next = ReplaceWindowPlacement(state, placement, records);
                var saving = Task.Run(() => PersistGpuActionCheckpointAsync(next,
                    "Host Manager confirmed the original window target and helper exited; prior window outcome retained.",
                    CancellationToken.None));
                var checkpoint = new GpuActionCheckpoint(HostManagerPlacementReceiptKey.Create(placement),
                    completed, saving, ExitSettlement: settlement);
                gpuActionCheckpoint = checkpoint;
                // Caller cancellation does not cancel or replace the original durable save.
                try { state = await saving.WaitAsync(cancellationToken); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return await ObserveGpuActionCheckpointAsync(checkpoint);
                }
                if (!await ObserveGpuActionCheckpointAsync(checkpoint)) return false;
            }
        }
        return true;
    }
}
