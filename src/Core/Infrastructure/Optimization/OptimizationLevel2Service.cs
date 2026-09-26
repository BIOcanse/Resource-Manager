using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class OptimizationLevel2Service(
    IHostManagerReportService reportService,
    IOptimizationProtectionService protectionService,
    IResourceBreakdownSampler resourceBreakdownSampler,
    IOptimizationLevel2Store recordStore,
    IProcessResourcePolicyWriter policyWriter,
    ILogger<OptimizationLevel2Service> logger) : IOptimizationLevel2Service
{
    private static readonly ProcessPolicyOptimizationProfile Profile = new(
        "2级标准系统压制",
        "level2",
        OptimizationLevel2ActionKinds.LowerProcessPriority,
        "BelowNormal",
        "BelowNormal / 后台优先级",
        "降低进程 CPU priority 到 BelowNormal。",
        OptimizationLevel2ActionKinds.EnableExecutionSpeedThrottling,
        OptimizationLevel2ActionKinds.LowerMemoryPriority,
        NativeMethods.MemoryPriorityLow,
        WindowsProcessResourcePolicyWriter.DescribeMemoryPriority(NativeMethods.MemoryPriorityLow),
        true,
        OptimizationLevel2ActionKinds.MoveProcessAffinity);

    private readonly SemaphoreSlim applyGate = new(1, 1);
    private readonly OptimizationProcessPolicyEngine engine = new(
        reportService,
        protectionService,
        resourceBreakdownSampler,
        policyWriter,
        logger);

    public async Task<OptimizationLevel2Preview> PreviewAsync(
        OptimizationLevel2Request request,
        CancellationToken cancellationToken)
    {
        var report = await engine.ResolveReportAsync(request.ReportId, cancellationToken);
        return ToPreview(await engine.BuildPreviewAsync(report, Profile, cancellationToken));
    }

    public async Task<OptimizationLevel2ApplyResult> ApplyAsync(
        OptimizationLevel2Request request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行 2 级优化前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var report = await engine.ResolveReportAsync(request.ReportId, cancellationToken);
            var preview = ToPreview(await engine.BuildPreviewAsync(report, Profile, cancellationToken));
            if (!preview.Eligible)
            {
                return new OptimizationLevel2ApplyResult(
                    report.Id,
                    OptimizationLevel2RecordStates.NoChanges,
                    preview.DisabledReason ?? "当前目标不适合执行 2 级优化。",
                    preview,
                    null);
            }

            var executableActions = preview.Actions
                .Where(static action => action.Enabled && action.WillChange)
                .ToArray();
            if (executableActions.Length == 0)
            {
                return new OptimizationLevel2ApplyResult(
                    report.Id,
                    OptimizationLevel2RecordStates.NoChanges,
                    "没有需要写入的 2 级优化动作。",
                    preview,
                    null);
            }

            var now = DateTimeOffset.Now;
            cancellationToken.ThrowIfCancellationRequested();
            var appliedActions = engine.ApplyActions(
                    executableActions.Select(ToEngineAction).ToArray(),
                    now,
                    Profile.RecordPrefix)
                .Select(ToLevel2AppliedAction)
                .ToList();

            if (appliedActions.Count == 0)
            {
                return new OptimizationLevel2ApplyResult(
                    report.Id,
                    OptimizationLevel2RecordStates.NoChanges,
                    "2 级优化写入失败或执行前状态已变化，未产生可恢复记录。",
                    preview,
                    null);
            }

            var record = new OptimizationLevel2Record(
                OptimizationProcessPolicyEngine.CreateStableId($"level2|{report.Id}|{report.Target.TargetKey}|{now.UtcTicks}"),
                report.Id,
                report.Target.TargetKey,
                report.Target.DisplayName,
                now,
                now,
                OptimizationLevel2RecordStates.Active,
                appliedActions);
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            records.RemoveAll(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationLevel2ApplyResult(
                report.Id,
                OptimizationLevel2RecordStates.Active,
                $"已应用 {appliedActions.Count} 条 2 级优化动作，并记录改前状态。",
                preview,
                record);
        }
        finally
        {
            applyGate.Release();
        }
    }

    public async Task<IReadOnlyList<OptimizationLevel2Record>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        return await recordStore.LoadAsync(cancellationToken);
    }

    public async Task<OptimizationLevel2RestoreResult> RestoreAsync(
        OptimizationLevel2RestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("恢复 2 级优化前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(record => record.Id.Equals(request.RecordId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("2 级优化记录不存在。");
            }

            var now = DateTimeOffset.Now;
            var record = records[index];
            var restoredCount = 0;
            var skippedCount = 0;
            var actions = record.Actions
                .Select(action =>
                {
                    if (action.State == OptimizationLevel2RecordStates.Restored)
                    {
                        skippedCount++;
                        return action;
                    }

                    var restored = ToLevel2AppliedAction(engine.RestoreAction(ToEngineAppliedAction(action), now));
                    if (restored.State == OptimizationLevel2RecordStates.Restored)
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

            var state = actions.All(static action => action.State == OptimizationLevel2RecordStates.Restored)
                ? OptimizationLevel2RecordStates.Restored
                : OptimizationLevel2RecordStates.PartiallyRestored;
            var updated = record with
            {
                UpdatedAt = now,
                State = state,
                Actions = actions
            };
            records[index] = updated;
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationLevel2RestoreResult(
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

    private static OptimizationLevel2Preview ToPreview(ProcessPolicyOptimizationPreview preview)
    {
        return new OptimizationLevel2Preview(
            preview.ReportId,
            preview.CapturedAt,
            preview.Eligible,
            preview.Summary,
            preview.DisabledReason,
            preview.Target,
            preview.AffinityPlan,
            preview.Actions.Select(ToLevel2Action).ToArray());
    }

    private static OptimizationLevel2ActionPreview ToLevel2Action(ProcessPolicyOptimizationActionPreview action)
    {
        return new OptimizationLevel2ActionPreview(
            action.Id,
            action.Kind,
            action.ProcessId,
            action.ProcessName,
            action.ExecutablePath,
            action.ProcessStartedAt,
            action.CurrentValue,
            action.ProposedValue,
            action.CurrentRawValue,
            action.ProposedRawValue,
            action.WillChange,
            action.Enabled,
            action.DisabledReason,
            action.Details);
    }

    private static ProcessPolicyOptimizationActionPreview ToEngineAction(OptimizationLevel2ActionPreview action)
    {
        return new ProcessPolicyOptimizationActionPreview(
            action.Id,
            action.Kind,
            action.ProcessId,
            action.ProcessName,
            action.ExecutablePath,
            action.ProcessStartedAt,
            action.CurrentValue,
            action.ProposedValue,
            action.CurrentRawValue,
            action.ProposedRawValue,
            action.WillChange,
            action.Enabled,
            action.DisabledReason,
            action.Details);
    }

    private static OptimizationLevel2AppliedAction ToLevel2AppliedAction(ProcessPolicyOptimizationAppliedAction action)
    {
        return new OptimizationLevel2AppliedAction(
            action.Id,
            action.Kind,
            action.ProcessId,
            action.ProcessName,
            action.ExecutablePath,
            action.ProcessStartedAt,
            action.PreviousDisplayValue,
            action.AppliedDisplayValue,
            action.PreviousRawValue,
            action.AppliedRawValue,
            action.AppliedAt,
            action.RestoredAt,
            action.State,
            action.Message);
    }

    private static ProcessPolicyOptimizationAppliedAction ToEngineAppliedAction(OptimizationLevel2AppliedAction action)
    {
        return new ProcessPolicyOptimizationAppliedAction(
            action.Id,
            action.Kind,
            action.ProcessId,
            action.ProcessName,
            action.ExecutablePath,
            action.ProcessStartedAt,
            action.PreviousDisplayValue,
            action.AppliedDisplayValue,
            action.PreviousRawValue,
            action.AppliedRawValue,
            action.AppliedAt,
            action.RestoredAt,
            action.State,
            action.Message);
    }
}
