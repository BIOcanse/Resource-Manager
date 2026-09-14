using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private bool WantsGpuShim(HostManagerAutomaticPlacementProcess process)
        => process.CanApplyPhysicalPlacement && !string.IsNullOrWhiteSpace(process.ExecutablePath)
            && runtimePlanProvider.Current.GpuPlacement.GlobalPreciseProviderEnabled
            && process.Policy.AcceptsRuntimeGpuScheduling()
            && GpuPlacementTargets.Normalize(process.Policy.TargetGpu) is GpuPlacementTargets.AutoIdleGpu
                or GpuPlacementTargets.IntegratedGpu or GpuPlacementTargets.HighPerformanceGpu;

    private Task<HostManagerPlacementDesired?> TryCreateGpuShimPlacementDesiredAsync(
        HostManagerAutomaticGpuPreferencePlacement planned,
        HostManagerAppliedPlacementReceipt? existing,
        CancellationToken cancellationToken)
        => PrepareGpuShimPlacementDesiredAsync(planned, existing, null, cancellationToken);

    private async Task<HostManagerPlacementDesired?> PrepareGpuShimPlacementDesiredAsync(
        HostManagerAutomaticGpuPreferencePlacement planned,
        HostManagerAppliedPlacementReceipt? existing,
        List<HostManagerAutomaticGpuPreferencePlacement>? firstUse,
        CancellationToken cancellationToken)
    {
        var process = planned.Process;
        if (!WantsGpuShim(process) || existing?.Records.Any(record => !IsAutomaticPlacementRecord(record)) == true)
            return null;

        var preparation = await runningGpuPlacementActions.PrepareAsync(
            CreateAutomaticGpuActionRequest(planned), cancellationToken);
        if (preparation.ApiObservationProcesses.Any(identity => identity.ProcessId == process.ProcessId
                && identity.ProcessStartKey == process.ProcessStartKey
                && StringComparer.OrdinalIgnoreCase.Equals(identity.ExecutablePath, process.ExecutablePath)))
            firstUse?.Add(planned);
        if (preparation.Plan is not { } action) return null;
        return CreatePreparedGpuShimPlacementDesired(planned, existing, action);
    }

    private static RunningGpuPlacementActionRequest CreateAutomaticGpuActionRequest(
        HostManagerAutomaticGpuPreferencePlacement planned)
    {
        var process = planned.Process;
        return new(process.TargetId, process.SoftwareId, process.DisplayName,
            [new(process.ProcessId, process.ProcessStartKey, process.ProcessName, process.ExecutablePath!)],
            "automatic-placement", planned.TargetAdapterKey, process.Policy.PreferredRuntimeSwitchMethod);
    }

    private HostManagerPlacementDesired? CreatePreparedGpuShimPlacementDesired(
        HostManagerAutomaticGpuPreferencePlacement planned,
        HostManagerAppliedPlacementReceipt? existing, RunningGpuPlacementActionPlan action)
    {
        var process = planned.Process;
        var previousRecord = TryGetAutomaticRecord(existing, HostManagerAppliedRecordKinds.GpuShimPolicy);
        HostManagerAppliedRecord? record;
        if (previousRecord is null)
        {
            record = gpuShimRuntime.CapturePolicyRecord(process.TargetId, action.PolicyValue);
        }
        else
        {
            if (!GpuShimPolicyRecord.TryRead(previousRecord, out var previous)
                || previous.TargetId != process.TargetId) return null;
            // Keep the original baseline across a changed target, not the currently applied bytes.
            if (previous.PreviousValue is { } original && original.AsSpan().SequenceEqual(action.PolicyValue)) return null;
            record = GpuShimPolicyRecord.Create(process.TargetId, previous.PreviousValue, action.PolicyValue);
        }
        if (record is null) return null;
        var metadata = CreateProcessPlacementMetadata(process, DateTimeOffset.FromFileTime(checked((long)process.ProcessStartKey)),
            process.ProcessName, process.ExecutablePath!);
        foreach (var item in record.Metadata!) metadata[item.Key] = item.Value;
        metadata["assignedPositionId"] = action.Request.AssignedPositionId;
        metadata["targetAdapterKey"] = planned.TargetAdapterKey.ToString(CultureInfo.InvariantCulture);
        metadata["runtimeActionResult"] = JsonSerializer.Serialize(new RunningGpuPlacementActionResult(
            [], "动作结果尚未结算；不代表动作没有发生。", RunningGpuPlacementActionStatuses.Unresolved));
        record = record with { Metadata = metadata };
        var now = timeProvider.GetUtcNow();
        var placement = new HostManagerAppliedPlacementReceipt(process.TargetId, process.DisplayName,
            process.SoftwareId, OptimizationResourceKinds.Gpu, [record], existing?.AppliedAt ?? now, now);
        return new(placement, record, ToPlacementPriority(planned.CanonicalProcessScore)) { RuntimeGpuAction = action };
    }

    private HostManagerPlacementObservation ObserveGpuShimPolicy(
        HostManagerAppliedPlacementReceipt placement, HostManagerAppliedRecord record,
        ulong receiptDigest, ulong previousDigest)
    {
        if (!GpuShimPolicyRecord.TryRead(record, out var policy)) return HostManagerPlacementObservation.Unchecked;
        try
        {
            var current = gpuShimRuntime.ReadPolicy(policy.TargetId);
            if (current is not null && current.AsSpan().SequenceEqual(policy.AppliedValue))
                return HostManagerPlacementObservation.FoundReceipt(receiptDigest);
            if (current is null ? policy.PreviousValue is null
                : policy.PreviousValue is not null && current.AsSpan().SequenceEqual(policy.PreviousValue))
                return HostManagerPlacementObservation.FoundPrevious(previousDigest);
            return HostManagerPlacementObservation.FoundForeign(CreateForeignPlacementDigest(placement, record,
                current is null ? "missing" : Convert.ToBase64String(current), receiptDigest, previousDigest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HostManagerPlacementObservation.Unavailable(exception.HResult);
        }
    }

    private static HostManagerRollbackStateDocument AddAutomaticPlacementRecord(
        HostManagerRollbackStateDocument state, HostManagerPlacementProjectedDesired desired)
    {
        var key = HostManagerPlacementReceiptKey.Create(desired.Placement);
        var existing = state.AppliedPlacements.SingleOrDefault(placement => HostManagerPlacementReceiptKey.Create(placement) == key);
        if (existing is null)
            return state with { AppliedPlacements = [.. state.AppliedPlacements, desired.Placement with { Records = [desired.Record] }] };
        if (existing.Records.Any(record => HostManagerPlacementCoordinatorProjection.CreateIdentity(existing, record) == desired.Identity))
            throw new InvalidDataException("An automatic placement apply collided with an existing durable record.");
        return state with
        {
            AppliedPlacements = state.AppliedPlacements.Select(placement => placement == existing
                ? placement with { Records = [.. placement.Records, desired.Record], UpdatedAt = desired.Placement.UpdatedAt }
                : placement).ToArray()
        };
    }

    private async Task<bool> SaveAutomaticGpuPreparationAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementProjectedDesired desired,
        ulong deadline, CancellationToken cancellationToken)
    {
        RequireNoGpuActionCheckpoint();
        var saving = Task.Run(() => PersistGpuActionCheckpointAsync(state,
            "Host Manager prepared an automatic GPU placement write.", CancellationToken.None));
        var checkpoint = new GpuActionCheckpoint(HostManagerPlacementReceiptKey.Create(desired.Placement), desired.Record, saving);
        gpuActionCheckpoint = checkpoint;
        try
        {
            var remaining = RemainingGpuActionTime(deadline);
            if (remaining <= TimeSpan.Zero) return false;
            await saving.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            return false;
        }
        return await ObserveGpuActionCheckpointAsync(checkpoint).ConfigureAwait(false);
    }

    private async Task<(HostManagerRollbackStateDocument State, bool CanContinue)> ExecuteGpuShimActionAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementProjectedDesired desired,
        ulong deadline, CancellationToken cancellationToken)
    {
        var key = HostManagerPlacementReceiptKey.Create(desired.Placement);
        var limits = appliedPlacementCoordinatorPlan?.HotPublish.WindowExecution
            ?? runtimePlanProvider.Current.HostManager.HotPublish.PlacementCoordinator.WindowExecution;
        var cleanup = TimeSpan.FromMilliseconds(limits.CleanupReserveMilliseconds);
        var remaining = RemainingGpuActionTime(deadline);
        var stopPlan = cancellationToken.IsCancellationRequested || remaining <= cleanup;
        RunningGpuPlacementActionResult result;
        using var workStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!stopPlan) workStop.CancelAfter(remaining - cleanup);
        try
        {
            result = stopPlan
                ? new([], "原动作期限内已无可执行窗口的时间；未启动运行期动作。", RunningGpuPlacementActionStatuses.Skipped)
                : await runningGpuPlacementActions.TryApplyAsync(desired.RuntimeGpuAction!,
                    new(limits.MaximumWindowCount, async request =>
                    {
                        var window = await ExecuteOwnedGpuWindowAsync(state, key, request, limits, deadline, workStop.Token);
                        var saved = gpuActionCheckpoint?.Completion;
                        if (window.CanContinue)
                        {
                            var released = TrySettleGpuWindowExecution();
                            var settled = await TrySettleGpuActionCheckpointAsync();
                            if (released && settled)
                            {
                                if (saved is not null) state = await saved;
                            }
                            else window = window with { ContinueAllowed = false };
                        }
                        stopPlan |= !window.CanContinue;
                        return window;
                    }, async (call, token) =>
                    {
                        var completed = await ExecuteOwnedGpuRemoteCallAsync(state, key, call, token);
                        state = completed.State;
                        stopPlan |= !completed.Result.Completed;
                        return completed.Result;
                    }, async (process, adapter, token) =>
                    {
                        var prepared = await ExecuteOwnedOpenGlPreparationAsync(state, key, process, adapter, limits, deadline, token);
                        state = prepared.State;
                        stopPlan |= prepared.Source is null;
                        return prepared.Source;
                    }), workStop.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "GPU runtime action failed after policy publication for {Target}.", desired.Placement.TargetId);
            result = new([], exception.Message, RunningGpuPlacementActionStatuses.Unresolved);
            stopPlan = true;
        }
        stopPlan |= workStop.IsCancellationRequested;
        var saving = SaveGpuActionSummaryAsync(state, desired, result);
        remaining = RemainingGpuActionTime(deadline);
        try
        {
            if (remaining > TimeSpan.Zero)
                await saving.WaitAsync(remaining, cancellationToken);
        }
        catch (Exception exception) when (!saving.IsCompleted
            && exception is TimeoutException or OperationCanceledException)
        {
            return (state, false);
        }
        catch
        {
            _ = TrySettleGpuWindowExecution();
            _ = await TrySettleGpuActionCheckpointAsync();
            throw;
        }
        if (!saving.IsCompleted) return (state, false);
        state = await saving;
        var resourcesReleased = TrySettleGpuCallbackPreparation() && TrySettleGpuWindowExecution();
        var checkpointSettled = await TrySettleGpuActionCheckpointAsync();
        return (state, !stopPlan && resourcesReleased && checkpointSettled);
    }

    private Task<HostManagerRollbackStateDocument> SaveGpuActionSummaryAsync(
        HostManagerRollbackStateDocument state, HostManagerPlacementProjectedDesired desired,
        RunningGpuPlacementActionResult result)
    {
        var original = gpuActionCheckpoint;
        if (original?.ActionResult is not null)
            throw new InvalidOperationException("The original GPU action already submitted its final summary.");
        var previous = original?.Completion ?? Task.FromResult(state);
        var saving = Task.Run(async () =>
        {
            var saved = await previous.ConfigureAwait(false);
            var next = saved with
            {
                AppliedPlacements = saved.AppliedPlacements.Select(placement => placement with
                {
                    Records = placement.Records.Select(record =>
                        HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, record) == desired.Identity
                            ? record with { Metadata = new Dictionary<string, string>(record.Metadata!, StringComparer.Ordinal)
                                { ["runtimeActionResult"] = JsonSerializer.Serialize(result) } } : record).ToArray()
                }).ToArray()
            };
            return await PersistGpuActionCheckpointAsync(next,
                "Host Manager retained GPU policy ownership and runtime action facts.", CancellationToken.None).ConfigureAwait(false);
        });
        gpuActionCheckpoint = original is null
            ? new(HostManagerPlacementReceiptKey.Create(desired.Placement), desired.Record, saving, ActionResult: result)
            : original with { Completion = saving, ActionResult = result };
        return saving;
    }
}
