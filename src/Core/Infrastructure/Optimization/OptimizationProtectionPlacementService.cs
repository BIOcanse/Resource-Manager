using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationProtectionPlacementService(
    IOptimizationProtectionService protectionService,
    IResourceBreakdownSampler resourceBreakdownSampler,
    IOptimizationA2Store a2Store,
    IOptimizationProtectionPlacementStore recordStore,
    IProcessResourcePolicyWriter policyWriter,
    ILogger<OptimizationProtectionPlacementService> logger) : IOptimizationProtectionPlacementService
{
    private readonly SemaphoreSlim applyGate = new(1, 1);

    public async Task<OptimizationProtectionPlacementPreview> PreviewAsync(
        OptimizationProtectionPlacementRequest request,
        CancellationToken cancellationToken)
    {
        return await BuildPreviewAsync(cancellationToken);
    }

    public async Task<OptimizationProtectionPlacementApplyResult> ApplyAsync(
        OptimizationProtectionPlacementRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行保护避让前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var preview = await BuildPreviewAsync(cancellationToken);
            if (!preview.Eligible)
            {
                return new OptimizationProtectionPlacementApplyResult(
                    OptimizationProtectionPlacementRecordStates.NoChanges,
                    preview.DisabledReason ?? "当前没有可执行的保护避让动作。",
                    preview,
                    null);
            }

            var executableActions = preview.Actions
                .Where(static action => action.Enabled && action.WillChange)
                .OrderBy(static action => action.ProtectedTargetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static action => action.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static action => action.ProcessId)
                .ToArray();
            if (executableActions.Length == 0)
            {
                return new OptimizationProtectionPlacementApplyResult(
                    OptimizationProtectionPlacementRecordStates.NoChanges,
                    "没有需要写入的保护避让动作。",
                    preview,
                    null);
            }

            var now = DateTimeOffset.Now;
            var appliedActions = new List<OptimizationProtectionPlacementAppliedAction>();
            foreach (var action in executableActions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var applied = ApplyAction(action, now);
                if (applied is not null)
                {
                    appliedActions.Add(applied);
                }
            }

            if (appliedActions.Count == 0)
            {
                return new OptimizationProtectionPlacementApplyResult(
                    OptimizationProtectionPlacementRecordStates.NoChanges,
                    "保护避让写入失败或执行前状态已变化，未产生可恢复记录。",
                    preview,
                    null);
            }

            var enhancedMask = BuildEnhancedMask(preview.EnhancedOwners);
            var avoidanceMask = preview.AvoidancePlan.AffinityMask ?? 0;
            var record = new OptimizationProtectionPlacementRecord(
                CreateStableId($"protection-placement|{enhancedMask}|{avoidanceMask}|{now.UtcTicks}"),
                now,
                now,
                OptimizationProtectionPlacementRecordStates.Active,
                enhancedMask,
                avoidanceMask,
                CpuAffinityPlanner.FormatMask(enhancedMask),
                CpuAffinityPlanner.FormatMask(avoidanceMask),
                preview.EnhancedOwners,
                appliedActions);
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            records.RemoveAll(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationProtectionPlacementApplyResult(
                OptimizationProtectionPlacementRecordStates.Active,
                $"已应用 {appliedActions.Count} 条保护避让动作，并记录改前 affinity。",
                preview,
                record);
        }
        finally
        {
            applyGate.Release();
        }
    }

    public async Task<IReadOnlyList<OptimizationProtectionPlacementRecord>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        return await recordStore.LoadAsync(cancellationToken);
    }

    public async Task<OptimizationProtectionPlacementRestoreResult> RestoreAsync(
        OptimizationProtectionPlacementRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("恢复保护避让前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(record => record.Id.Equals(request.RecordId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("保护避让记录不存在。");
            }

            var now = DateTimeOffset.Now;
            var record = records[index];
            var restoredCount = 0;
            var skippedCount = 0;
            var actions = record.Actions
                .Select(action =>
                {
                    if (action.State == OptimizationProtectionPlacementRecordStates.Restored)
                    {
                        skippedCount++;
                        return action;
                    }

                    var restored = RestoreAction(action, now);
                    if (restored.State == OptimizationProtectionPlacementRecordStates.Restored)
                    {
                        restoredCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }

                    return restored;
                })
                .ToArray();

            var state = actions.All(static action => action.State == OptimizationProtectionPlacementRecordStates.Restored)
                ? OptimizationProtectionPlacementRecordStates.Restored
                : OptimizationProtectionPlacementRecordStates.PartiallyRestored;
            var updated = record with
            {
                UpdatedAt = now,
                State = state,
                Actions = actions
            };
            records[index] = updated;
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationProtectionPlacementRestoreResult(
                updated.Id,
                updated.State,
                $"已恢复 {restoredCount} 条，跳过 {skippedCount} 条。",
                restoredCount,
                skippedCount,
                updated);
        }
        finally
        {
            applyGate.Release();
        }
    }
}
