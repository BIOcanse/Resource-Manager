using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationLevel4Service(
    IHostManagerReportService reportService,
    IOptimizationProtectionService protectionService,
    IResourceBreakdownSampler resourceBreakdownSampler,
    IOptimizationLevel4Store recordStore,
    IProcessFreezeController freezeController,
    ILogger<OptimizationLevel4Service> logger) : IOptimizationLevel4Service
{
    private readonly SemaphoreSlim applyGate = new(1, 1);

    public async Task<OptimizationLevel4Preview> PreviewAsync(
        OptimizationLevel4Request request,
        CancellationToken cancellationToken)
    {
        var report = await ResolveReportAsync(request.ReportId, cancellationToken);
        return await BuildPreviewAsync(report, cancellationToken);
    }

    public async Task<OptimizationLevel4ApplyResult> ApplyAsync(
        OptimizationLevel4Request request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("执行 4 级冻结前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var report = await ResolveReportAsync(request.ReportId, cancellationToken);
            var preview = await BuildPreviewAsync(report, cancellationToken);
            if (!preview.Eligible)
            {
                return new OptimizationLevel4ApplyResult(
                    report.Id,
                    OptimizationLevel4RecordStates.NoChanges,
                    preview.DisabledReason ?? "当前目标不适合执行 4 级冻结。",
                    preview,
                    null);
            }

            var executableActions = preview.Actions
                .Where(static action => action.Enabled && action.WillChange)
                .OrderBy(static action => action.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static action => action.ProcessId)
                .ToArray();
            if (executableActions.Length == 0)
            {
                return new OptimizationLevel4ApplyResult(
                    report.Id,
                    OptimizationLevel4RecordStates.NoChanges,
                    "没有需要执行的 4 级冻结动作。",
                    preview,
                    null);
            }

            var now = DateTimeOffset.Now;
            var appliedActions = new List<OptimizationLevel4AppliedAction>();
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
                return new OptimizationLevel4ApplyResult(
                    report.Id,
                    OptimizationLevel4RecordStates.NoChanges,
                    "4 级冻结失败或执行前状态已变化，未产生可恢复记录。",
                    preview,
                    null);
            }

            var record = new OptimizationLevel4Record(
                CreateStableId($"level4|{report.Id}|{report.Target.TargetKey}|{now.UtcTicks}"),
                report.Id,
                report.Target.TargetKey,
                report.Target.DisplayName,
                now,
                now,
                OptimizationLevel4RecordStates.Active,
                appliedActions);
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            records.RemoveAll(item => item.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationLevel4ApplyResult(
                report.Id,
                OptimizationLevel4RecordStates.Active,
                $"已冻结 {appliedActions.Count} 个进程，并记录可恢复线程状态。",
                preview,
                record);
        }
        finally
        {
            applyGate.Release();
        }
    }

    public async Task<IReadOnlyList<OptimizationLevel4Record>> GetRecordsAsync(CancellationToken cancellationToken)
    {
        return await recordStore.LoadAsync(cancellationToken);
    }

    public async Task<OptimizationLevel4RestoreResult> RestoreAsync(
        OptimizationLevel4RestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.ConfirmOperation)
        {
            throw new InvalidOperationException("恢复 4 级冻结前需要确认操作。");
        }

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            var records = (await recordStore.LoadAsync(cancellationToken)).ToList();
            var index = records.FindIndex(record => record.Id.Equals(request.RecordId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new InvalidOperationException("4 级冻结记录不存在。");
            }

            var now = DateTimeOffset.Now;
            var record = records[index];
            var restoredCount = 0;
            var skippedCount = 0;
            var actions = record.Actions
                .Select(action =>
                {
                    if (action.State == OptimizationLevel4RecordStates.Restored)
                    {
                        skippedCount++;
                        return action;
                    }

                    var restored = RestoreAction(action, now);
                    if (restored.State == OptimizationLevel4RecordStates.Restored)
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

            var state = actions.All(static action => action.State == OptimizationLevel4RecordStates.Restored)
                ? OptimizationLevel4RecordStates.Restored
                : OptimizationLevel4RecordStates.PartiallyRestored;
            var updated = record with
            {
                UpdatedAt = now,
                State = state,
                Actions = actions
            };
            records[index] = updated;
            await recordStore.SaveAsync(records, cancellationToken);

            return new OptimizationLevel4RestoreResult(
                updated.Id,
                updated.State,
                $"已恢复 {restoredCount} 个进程，跳过 {skippedCount} 个进程。",
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
