using System.Globalization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private readonly Dictionary<Guid, OwnedGpuRemoteCall> gpuRemoteCalls = [];

    private sealed class OwnedGpuRemoteCall(GpuRemoteCallExecution execution, HostManagerPlacementReceiptKey key)
    {
        internal GpuRemoteCallExecution Execution { get; } = execution;
        internal HostManagerPlacementReceiptKey Key { get; } = key;
        internal GpuRemoteCallRecord Fact { get; set; } = new(execution.Request);
    }

    private Task<(HostManagerRollbackStateDocument State, GpuRemoteCallSnapshot Result)> ExecuteOwnedGpuRemoteCallAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key,
        GpuRemoteCallExecution execution, CancellationToken cancellationToken)
        => ExecuteOwnedGpuRemoteCallCoreAsync(state, key, execution, null, cancellationToken);

    private async Task<(HostManagerRollbackStateDocument State, GpuRemoteCallSnapshot Result)> ExecuteOwnedGpuRemoteCallCoreAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key,
        GpuRemoteCallExecution execution, HostManagerAutomaticPlacementProcess? observationProcess,
        CancellationToken cancellationToken, Func<bool>? tryStart = null)
    {
        OwnedGpuRemoteCall owner;
        HostManagerAppliedRecord initial;
        try
        {
            RequireNoGpuActionCheckpoint();
            var target = execution.Request.Process;
            if (observationProcess is not null)
                state = PrepareGpuApiObservationLedger(state, key, observationProcess, execution.Request);
            var placement = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key);
            var allowed = observationProcess is not null ||
                (execution.Request.Kind is GpuRemoteCallKind.LoadProvider or GpuRemoteCallKind.ConfigureProvider or GpuRemoteCallKind.ReadDevices
                && placement.Records.Any(record => GpuShimPolicyRecord.TryRead(record, out _)
                    && record.Metadata?.GetValueOrDefault("processId") == target.ProcessId.ToString(CultureInfo.InvariantCulture)
                    && record.Metadata?.GetValueOrDefault("processStartKey") == target.ProcessStartKey.ToString(CultureInfo.InvariantCulture)));
            if (!allowed || GpuActionFacts.BlocksProcess(state.AppliedPlacements, placement.TargetId, target.ProcessId, target.ProcessStartKey)
                || gpuRemoteCalls.Values.Any(item => item.Fact.Request.Process.ProcessId == target.ProcessId
                    && item.Fact.Request.Process.ProcessStartKey == target.ProcessStartKey))
                throw new InvalidOperationException("The GPU call must have its matching action permission and an unblocked exact target.");
            owner = new(execution, key);
            initial = owner.Fact.Encode();
            gpuRemoteCalls.Add(execution.Request.CallId, owner);
        }
        catch { execution.Dispose(); throw; }

        var saving = Task.Run(async () =>
        {
            var saved = await SaveRemoteFactAsync(state, key, initial, add: true).ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested
                && (observationProcess is null || IsGpuApiObservationPermitted(
                    saved.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key),
                    observationProcess, execution.Request)))
            {
                var started = true;
                if (tryStart is null) execution.Start();
                else started = tryStart();
                if (started)
                {
                    owner.Fact = owner.Fact with { Started = execution.Snapshot };
                    saved = await SaveRemoteFactAsync(saved, key, owner.Fact.Encode()).ConfigureAwait(false);
                    await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            owner.Fact = owner.Fact with { Result = execution.Snapshot };
            return await SaveRemoteFactAsync(saved, key, owner.Fact.Encode()).ConfigureAwait(false);
        });
        gpuActionCheckpoint = new(key, initial, saving, RemoteCallId: execution.Request.CallId);
        // The original checkpoint continues if the caller's deadline stops waiting for its save.
        state = await saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = execution.Snapshot;
        if (!await TrySettleGpuActionCheckpointAsync().ConfigureAwait(false))
            throw new IOException("The original GPU remote-call checkpoint was not saved.");
        if (result.ResourcesReleased)
        {
            gpuRemoteCalls.Remove(execution.Request.CallId);
            execution.Dispose();
        }
        return (state, result);
    }

    private Task<HostManagerRollbackStateDocument> SaveRemoteFactAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementReceiptKey key, HostManagerAppliedRecord record, bool add = false)
    {
        var placement = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == key);
        var matches = placement.Records.Count(item => item.Kind == record.Kind && item.RecordId == record.RecordId);
        if (matches != (add ? 0 : 1)) throw new InvalidDataException("The remote call must keep its single original ledger record.");
        var records = add ? [.. placement.Records, record]
            : placement.Records.Select(item => item.Kind == record.Kind && item.RecordId == record.RecordId ? record : item).ToArray();
        return PersistGpuActionCheckpointAsync(ReplaceWindowPlacement(state, placement, records),
            "Host Manager retained the original GPU remote call and its actual resource state.", CancellationToken.None);
    }

    private async Task<bool> ReconcileGpuRemoteCallsAsync(HostManagerCycleEffectAdmission admission, CancellationToken cancellationToken)
    {
        if (!admission.TryAcquire(HostManagerCycleEffectKind.ExitedOwnershipReconciliation, out var permit)) return true;
        RequireNoGpuWindowOwnerWork();
        var state = await LoadRollbackStateAsync(cancellationToken);
        ReleaseUnstartedRemoteCallsWithoutFacts(state);
        foreach (var placement in state.AppliedPlacements)
        foreach (var record in placement.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (permit.RecoveryRemaining == 0) return true;
            if (!GpuRemoteCallRecord.TryRead(record, out var fact)) continue;
            gpuRemoteCalls.TryGetValue(fact.Request.CallId, out var owner);
            if (!fact.BlocksProcess)
            {
                if (owner is not null) { owner.Execution.Dispose(); gpuRemoteCalls.Remove(fact.Request.CallId); }
                continue;
            }
            GpuRemoteCallRecord? completed = null;
            if (owner is not null)
            {
                var actual = owner.Execution.Observe();
                if (actual.ResourcesReleased) completed = CompleteRemoteFact(fact, owner.Fact, actual);
            }
            else
            {
                // After restart the saved address is not allocation ownership. Never free or replay it.
                var target = fact.Request.Process;
                var read = processPolicyWriter.ReadProcessInstanceForRecovery(target.ProcessId);
                if (ProcessInstanceRecovery.IsConfirmedExited(read, checked((uint)target.ProcessId), target.ProcessStartKey))
                    completed = fact with { ExitSettlement = new(timeProvider.GetUtcNow(), read) };
            }
            if (completed is null || !permit.TryReserveRecovery(1, out _)) continue;
            var encoded = completed.Encode();
            var key = HostManagerPlacementReceiptKey.Create(placement);
            var saving = Task.Run(() => SaveRemoteFactAsync(state, key, encoded));
            var checkpoint = new GpuActionCheckpoint(key, encoded, saving, RemoteCallId: fact.Request.CallId);
            gpuActionCheckpoint = checkpoint;
            try { state = await saving.WaitAsync(cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return await ObserveGpuActionCheckpointAsync(checkpoint);
            }
            if (!await ObserveGpuActionCheckpointAsync(checkpoint)) return false;
            if (owner is not null) { owner.Execution.Dispose(); gpuRemoteCalls.Remove(fact.Request.CallId); }
        }
        return true;
    }

    private static GpuRemoteCallRecord CompleteRemoteFact(
        GpuRemoteCallRecord saved, GpuRemoteCallRecord observed, GpuRemoteCallSnapshot actual)
    {
        var fact = saved with { Started = saved.Started ?? observed.Started, Result = saved.Result ?? observed.Result };
        return fact.Result is null ? fact with { Result = actual }
            : fact.Result.ResourcesReleased ? fact : fact with { Settlement = actual };
    }

    private void ReleaseUnstartedRemoteCallsWithoutFacts(HostManagerRollbackStateDocument state)
    {
        foreach (var owner in gpuRemoteCalls.Values.ToArray())
        {
            if (state.AppliedPlacements.Any(placement => placement.Records.Any(record =>
                    GpuRemoteCallRecord.IsActionFact(record) && record.RecordId == owner.Fact.RecordId))) continue;
            if (owner.Fact.Started is not null || owner.Execution.Snapshot is not
                { Status: "not-started", ResourcesReleased: true, ParameterAddress: 0, ThreadId: null })
                throw new InvalidDataException("A started GPU call has lost its original durable fact.");
            owner.Execution.Dispose();
            gpuRemoteCalls.Remove(owner.Fact.Request.CallId);
        }
    }

    private async Task CloseOwnedGpuRemoteCallsAsync()
    {
        if (gpuRemoteCalls.Count == 0) return;
        try
        {
            var state = await LoadRollbackStateAsync(CancellationToken.None).ConfigureAwait(false);
            ReleaseUnstartedRemoteCallsWithoutFacts(state);
            foreach (var owner in gpuRemoteCalls.Values)
            {
                var record = state.AppliedPlacements.Single(item => HostManagerPlacementReceiptKey.Create(item) == owner.Key)
                    .Records.Single(item => item.RecordId == owner.Fact.RecordId && GpuRemoteCallRecord.IsActionFact(item));
                if (!GpuRemoteCallRecord.TryRead(record, out var fact)) throw new InvalidDataException("Invalid original GPU call.");
                var actual = owner.Execution.Observe();
                if (!fact.BlocksProcess || !actual.ResourcesReleased) continue;
                state = await SaveRemoteFactAsync(state, owner.Key, CompleteRemoteFact(fact, owner.Fact, actual).Encode()).ConfigureAwait(false);
            }
        }
        finally
        {
            // Unsettled calls stay on the original disk ledger; closing a handle is not cancelling a target thread.
            foreach (var owner in gpuRemoteCalls.Values) owner.Execution.Dispose();
            gpuRemoteCalls.Clear();
        }
    }
}
