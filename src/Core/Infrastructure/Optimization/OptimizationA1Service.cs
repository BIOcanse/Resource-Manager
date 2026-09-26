using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationA1Service(
    IHostManagerReportService reportService,
    IOptimizationProtectionService protectionService,
    IResourceBreakdownSampler resourceBreakdownSampler,
    IOptimizationA1Store recordStore,
    IProcessResourcePolicyWriter policyWriter,
    LegacyGpuPreferenceActionRestorer legacyGpuPreferenceActionRestorer,
    ILogger<OptimizationA1Service> logger) : IOptimizationA1Service
{
    private const string A1Priority = "High";
    private readonly SemaphoreSlim applyGate = new(1, 1);

    public async Task<OptimizationA1Preview> PreviewAsync(
        OptimizationA1Request request,
        CancellationToken cancellationToken)
    {
        var report = await ResolveReportAsync(request.ReportId, cancellationToken);
        return await BuildPreviewAsync(report, cancellationToken);
    }

    public async Task<OptimizationA1ApplyResult> ApplyAsync(
        OptimizationA1Request request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行 A1 增强前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var report = await ResolveReportAsync(request.ReportId, cancellationToken);
            var preview = await BuildPreviewAsync(report, cancellationToken);
            if (!preview.Eligible)
            {
                return new OptimizationA1ApplyResult(
                    report.Id,
                    OptimizationA1RecordStates.NoChanges,
                    preview.DisabledReason ?? "当前目标不适合执行 A1 增强。",
                    preview,
                    null);
            }

            var executableActions = preview.Actions
                .Where(static action => action.Enabled && action.WillChange)
                .OrderBy(static action => ActionOrder(action.Kind))
                .ThenBy(static action => action.ProcessName ?? action.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static action => action.ProcessId ?? 0)
                .ToArray();
            if (executableActions.Length == 0)
            {
                return new OptimizationA1ApplyResult(
                    report.Id,
                    OptimizationA1RecordStates.NoChanges,
                    "没有需要写入的 A1 增强动作。",
                    preview,
                    null);
            }

            var now = DateTimeOffset.Now;
            var appliedActions = ApplyActions(executableActions, now, cancellationToken);

            if (appliedActions.Count == 0)
            {
                return new OptimizationA1ApplyResult(
                    report.Id,
                    OptimizationA1RecordStates.NoChanges,
                    "A1 增强写入失败或执行前状态已变化，未产生可恢复记录。",
                    preview,
                    null);
            }

            var record = new OptimizationA1Record(
                CreateStableId($"a1|{report.Id}|{report.Target.TargetKey}|{now.UtcTicks}"),
                report.Id,
                report.Target.TargetKey,
                report.Target.DisplayName,
                now,
                now,
                OptimizationA1RecordStates.Active,
                appliedActions);
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            records.RemoveAll(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationA1ApplyResult(
                report.Id,
                OptimizationA1RecordStates.Active,
                $"已应用 {appliedActions.Count} 条 A1 增强动作，并记录改前状态。",
                preview,
                record);
        }
        finally
        {
            applyGate.Release();
        }
    }

    public async Task<IReadOnlyList<OptimizationA1Record>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        return await recordStore.LoadAsync(cancellationToken);
    }

    public async Task<OptimizationA1RestoreResult> RestoreAsync(
        OptimizationA1RestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("恢复 A1 增强前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(record => record.Id.Equals(request.RecordId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("A1 增强记录不存在。");
            }

            var now = DateTimeOffset.Now;
            var record = records[index];
            var restoredCount = 0;
            var skippedCount = 0;
            var actions = record.Actions
                .Select(action =>
                {
                    if (action.State == OptimizationA1RecordStates.Restored)
                    {
                        skippedCount++;
                        return action;
                    }

                    var restored = RestoreAction(action, now);
                    if (restored.State == OptimizationA1RecordStates.Restored)
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

            var state = actions.All(static action => action.State == OptimizationA1RecordStates.Restored)
                ? OptimizationA1RecordStates.Restored
                : OptimizationA1RecordStates.PartiallyRestored;
            var updated = record with
            {
                UpdatedAt = now,
                State = state,
                Actions = actions
            };
            records[index] = updated;
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationA1RestoreResult(
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
