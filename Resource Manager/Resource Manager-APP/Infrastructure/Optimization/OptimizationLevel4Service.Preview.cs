using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationLevel4Service
{
    private async Task<OptimizationReportItem> ResolveReportAsync(
        string reportId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reportId))
        {
            throw new InvalidOperationException("报告 ID 不能为空。");
        }

        var overview = await reportService.GetReportsAsync(cancellationToken);
        return overview.Reports.FirstOrDefault(report => report.Id.Equals(reportId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("性能优化报告不存在或已经失效。");
    }

    private async Task<OptimizationLevel4Preview> BuildPreviewAsync(
        OptimizationReportItem report,
        CancellationToken cancellationToken)
    {
        var disabledReason = ResolveTargetDisabledReason(report);
        if (disabledReason is null
            && await protectionService.IsTargetProtectedAsync(
                report.Target,
                cancellationToken))
        {
            disabledReason = "保护级目标不允许 4 级冻结。";
        }

        var actions = disabledReason is null
            ? await BuildActionPreviewsAsync(report, cancellationToken)
            : [];
        if (disabledReason is null && actions.Count == 0)
        {
            disabledReason = "当前没有找到可冻结的运行中进程。";
        }

        var enabledCount = actions.Count(static action => action.Enabled && action.WillChange);
        var eligible = disabledReason is null && enabledCount > 0;
        return new OptimizationLevel4Preview(
            report.Id,
            DateTimeOffset.Now,
            eligible,
            eligible
                ? $"可对 {report.Target.DisplayName} 冻结 {enabledCount} 个进程。"
                : disabledReason ?? "没有需要执行的 4 级冻结动作。",
            disabledReason,
            report.Target,
            actions);
    }

    private async Task<IReadOnlyList<OptimizationLevel4ActionPreview>> BuildActionPreviewsAsync(
        OptimizationReportItem report,
        CancellationToken cancellationToken)
    {
        var metricIds = ResolveMetricIds(report);
        var snapshot = await resourceBreakdownSampler.GetSnapshotAsync(
            metricIds,
            metricIds.ToDictionary(static id => id, static _ => ResourceBreakdownScaleModes.Capacity, StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        var foregroundProcessId = TryGetForegroundProcessId();
        return snapshot.Bars
            .SelectMany(static bar => bar.Software)
            .Where(segment => MatchesTarget(report.Target, segment))
            .SelectMany(static segment => segment.Processes)
            .Where(process => MatchesProcessFilter(report.Target, process.ProcessId))
            .GroupBy(static process => process.ProcessId)
            .Select(group => CreateActionPreview(group.First(), foregroundProcessId))
            .OrderBy(static action => action.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static action => action.ProcessId)
            .ToArray();
    }

    private OptimizationLevel4ActionPreview CreateActionPreview(
        ResourceProcessSegment process,
        int? foregroundProcessId)
    {
        var target = freezeController.TryReadTarget(process.ProcessId);
        if (target is null)
        {
            return DisabledAction(process, "进程状态不可读取或进程已经退出。");
        }

        var disabledReason = ResolveProcessDisabledReason(target, foregroundProcessId);
        var willChange = target.ThreadIds.Count > 0;
        if (disabledReason is null && !willChange)
        {
            disabledReason = "没有可挂起线程。";
        }

        return new OptimizationLevel4ActionPreview(
            CreateStableId($"level4-preview|freeze|{target.Process.ProcessId}|{target.Process.StartedAt}|{target.ThreadIds.Count}"),
            OptimizationLevel4ActionKinds.FreezeProcess,
            target.Process.ProcessId,
            target.Process.ProcessName,
            target.Process.ExecutablePath,
            target.Process.StartedAt,
            target.ThreadIds.Count,
            $"运行中 / {target.ThreadIds.Count} 个线程",
            "挂起线程 + 裁剪工作集",
            willChange,
            disabledReason is null && willChange,
            disabledReason,
            BuildActionDetails(target));
    }

    private static OptimizationLevel4ActionPreview DisabledAction(
        ResourceProcessSegment process,
        string reason)
    {
        return new OptimizationLevel4ActionPreview(
            CreateStableId($"level4-disabled|freeze|{process.ProcessId}|{process.Name}"),
            OptimizationLevel4ActionKinds.FreezeProcess,
            process.ProcessId,
            process.Name,
            process.ExecutablePath,
            null,
            0,
            "--",
            "--",
            false,
            false,
            reason,
            [$"PID：{process.ProcessId}", $"进程：{process.Name}"]);
    }

    private static IReadOnlyList<string> BuildActionDetails(ProcessFreezeTarget target)
    {
        var details = new List<string>
        {
            $"PID：{target.Process.ProcessId}",
            $"进程：{target.Process.ProcessName}",
            $"线程数：{target.ThreadIds.Count}",
            "动作：挂起线程并尝试裁剪工作集。"
        };
        if (!string.IsNullOrWhiteSpace(target.Process.ExecutablePath))
        {
            details.Add($"路径：{target.Process.ExecutablePath}");
        }

        return details;
    }
}
