using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class OptimizationLevel1Service(
    IHostManagerReportService reportService,
    IOptimizationProtectionService protectionService,
    IResourceBreakdownSampler resourceBreakdownSampler,
    IOptimizationLevel1Store recordStore,
    IProcessResourcePolicyWriter policyWriter,
    ILogger<OptimizationLevel1Service> logger) : IOptimizationLevel1Service
{
    private static readonly ProcessPolicyOptimizationProfile Profile = new(
        "1级轻量礼貌优化",
        "level1",
        OptimizationLevel1ActionKinds.NormalizeProcessPriority,
        "Normal",
        "Normal / 默认后台优先级",
        "后台高优先级进程归一到 Normal。",
        OptimizationLevel1ActionKinds.EnableExecutionSpeedThrottling,
        OptimizationLevel1ActionKinds.LowerMemoryPriority,
        NativeMethods.MemoryPriorityBelowNormal,
        WindowsProcessResourcePolicyWriter.DescribeMemoryPriority(NativeMethods.MemoryPriorityBelowNormal),
        false,
        null);

    private readonly SemaphoreSlim applyGate = new(1, 1);
    private readonly OptimizationProcessPolicyEngine engine = new(
        reportService,
        protectionService,
        resourceBreakdownSampler,
        policyWriter,
        logger);

    public async Task<OptimizationLevel1Preview> PreviewAsync(
        OptimizationLevel1Request request,
        CancellationToken cancellationToken)
    {
        var report = await engine.ResolveReportAsync(request.ReportId, cancellationToken);
        return ToPreview(await engine.BuildPreviewAsync(report, Profile, cancellationToken));
    }

    public async Task<OptimizationLevel1ApplyResult> ApplyAsync(
        OptimizationLevel1Request request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行 1 级优化前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var report = await engine.ResolveReportAsync(request.ReportId, cancellationToken);
            var preview = ToPreview(await engine.BuildPreviewAsync(report, Profile, cancellationToken));
            if (!preview.Eligible)
            {
                return new OptimizationLevel1ApplyResult(
                    report.Id,
                    OptimizationLevel1RecordStates.NoChanges,
                    preview.DisabledReason ?? "当前目标不适合执行 1 级优化。",
                    preview,
                    null);
            }

            var executableActions = preview.Actions
                .Where(static action => action.Enabled && action.WillChange)
                .ToArray();
            if (executableActions.Length == 0)
            {
                return new OptimizationLevel1ApplyResult(
                    report.Id,
                    OptimizationLevel1RecordStates.NoChanges,
                    "没有需要写入的 1 级优化动作。",
                    preview,
                    null);
            }

            var now = DateTimeOffset.Now;
            cancellationToken.ThrowIfCancellationRequested();
            var appliedActions = engine.ApplyActions(
                    executableActions.Select(ToEngineAction).ToArray(),
                    now,
                    Profile.RecordPrefix)
                .Select(ToLevel1AppliedAction)
                .ToList();

            if (appliedActions.Count == 0)
            {
                return new OptimizationLevel1ApplyResult(
                    report.Id,
                    OptimizationLevel1RecordStates.NoChanges,
                    "1 级优化写入失败或执行前状态已变化，未产生可恢复记录。",
                    preview,
                    null);
            }

            var record = new OptimizationLevel1Record(
                OptimizationProcessPolicyEngine.CreateStableId($"level1|{report.Id}|{report.Target.TargetKey}|{now.UtcTicks}"),
                report.Id,
                report.Target.TargetKey,
                report.Target.DisplayName,
                now,
                now,
                OptimizationLevel1RecordStates.Active,
                appliedActions);
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            records.RemoveAll(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationLevel1ApplyResult(
                report.Id,
                OptimizationLevel1RecordStates.Active,
                $"已应用 {appliedActions.Count} 条 1 级优化动作，并记录改前状态。",
                preview,
                record);
        }
        finally
        {
            applyGate.Release();
        }
    }

    public async Task<IReadOnlyList<OptimizationLevel1Record>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        return await recordStore.LoadAsync(cancellationToken);
    }

    public async Task<OptimizationLevel1RestoreResult> RestoreAsync(
        OptimizationLevel1RestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("恢复 1 级优化前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(record => record.Id.Equals(request.RecordId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("1 级优化记录不存在。");
            }

            var now = DateTimeOffset.Now;
            var record = records[index];
            var restoredCount = 0;
            var skippedCount = 0;
            var actions = record.Actions
                .Select(action =>
                {
                    if (action.State == OptimizationLevel1RecordStates.Restored)
                    {
                        skippedCount++;
                        return action;
                    }

                    var restored = ToLevel1AppliedAction(engine.RestoreAction(ToEngineAppliedAction(action), now));
                    if (restored.State == OptimizationLevel1RecordStates.Restored)
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

            var state = actions.All(static action => action.State == OptimizationLevel1RecordStates.Restored)
                ? OptimizationLevel1RecordStates.Restored
                : OptimizationLevel1RecordStates.PartiallyRestored;
            var updated = record with
            {
                UpdatedAt = now,
                State = state,
                Actions = actions
            };
            records[index] = updated;
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationLevel1RestoreResult(
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

    private static OptimizationLevel1Preview ToPreview(ProcessPolicyOptimizationPreview preview)
    {
        return new OptimizationLevel1Preview(
            preview.ReportId,
            preview.CapturedAt,
            preview.Eligible,
            preview.Summary,
            preview.DisabledReason,
            preview.Target,
            preview.Actions.Select(ToLevel1Action).ToArray());
    }

    private static OptimizationLevel1ActionPreview ToLevel1Action(ProcessPolicyOptimizationActionPreview action)
    {
        return new OptimizationLevel1ActionPreview(
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

    private static ProcessPolicyOptimizationActionPreview ToEngineAction(OptimizationLevel1ActionPreview action)
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

    private static OptimizationLevel1AppliedAction ToLevel1AppliedAction(ProcessPolicyOptimizationAppliedAction action)
    {
        return new OptimizationLevel1AppliedAction(
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

    private static ProcessPolicyOptimizationAppliedAction ToEngineAppliedAction(OptimizationLevel1AppliedAction action)
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
