using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Windows;
namespace ResourceManager.App.Infrastructure.Optimization;
public sealed partial class OptimizationA1Service
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

    private async Task<OptimizationA1Preview> BuildPreviewAsync(
        OptimizationReportItem report,
        CancellationToken cancellationToken)
    {
        var disabledReason = ResolveTargetDisabledReason(report.Target, report.Type);
        if (disabledReason is null
            && await protectionService.IsTargetProtectedAsync(
                report.Target,
                cancellationToken))
        {
            disabledReason = "保护级目标不执行 A1 增强；如需作为增强目标，请先取消保护或使用专门策略。";
        }

        var actions = disabledReason is null
            ? await BuildActionPreviewsAsync(report, cancellationToken)
            : [];
        if (disabledReason is null && actions.Count == 0)
        {
            disabledReason = "当前没有找到可增强的运行中进程或 exe。";
        }

        var enabledCount = actions.Count(static action => action.Enabled && action.WillChange);
        var eligible = disabledReason is null && enabledCount > 0;
        return new OptimizationA1Preview(
            report.Id,
            DateTimeOffset.Now,
            eligible,
            eligible
                ? $"可对 {report.Target.DisplayName} 应用 {enabledCount} 条 A1 增强动作。"
                : disabledReason ?? "没有需要写入的 A1 增强动作。",
            disabledReason,
            report.Target,
            actions);
    }

    private async Task<IReadOnlyList<OptimizationA1ActionPreview>> BuildActionPreviewsAsync(
        OptimizationReportItem report,
        CancellationToken cancellationToken)
    {
        var metricIds = ResolveMetricIds(report);
        var snapshot = await resourceBreakdownSampler.GetSnapshotAsync(
            metricIds,
            metricIds.ToDictionary(static id => id, static _ => ResourceBreakdownScaleModes.Capacity, StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        var processes = snapshot.Bars
            .SelectMany(static bar => bar.Software)
            .Where(segment => MatchesTarget(report.Target, segment))
            .SelectMany(static segment => segment.Processes)
            .Where(process => MatchesProcessFilter(report.Target, process.ProcessId))
            .GroupBy(static process => process.ProcessId)
            .Select(static group => group.First())
            .ToArray();
        if (processes.Length == 0)
        {
            return [];
        }

        var actions = new List<OptimizationA1ActionPreview>();
        foreach (var process in processes)
        {
            var current = policyWriter.TryReadProcess(process.ProcessId);
            if (current is null)
            {
                actions.Add(DisabledProcessAction(
                    OptimizationA1ActionKinds.RaiseProcessPriority,
                    process.ProcessId,
                    process.Name,
                    process.ExecutablePath,
                    "进程状态不可读取或进程已经退出。"));
                continue;
            }

            var processDisabledReason = ResolveProcessDisabledReason(current);
            actions.Add(CreatePriorityAction(current, processDisabledReason));
            actions.Add(CreatePowerThrottlingAction(current, processDisabledReason));
        }

        return actions
            .OrderBy(static action => ActionOrder(action.Kind))
            .ThenBy(static action => action.ProcessName ?? action.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static action => action.ProcessId ?? 0)
            .ToArray();
    }

    private OptimizationA1ActionPreview CreatePriorityAction(
        ProcessResourcePolicySnapshot current,
        string? disabledReason)
    {
        var willChange = PriorityRank(current.PriorityClass) < PriorityRank(A1Priority);
        var actionDisabledReason = disabledReason;
        if (actionDisabledReason is null && PriorityRank(current.PriorityClass) > PriorityRank(A1Priority))
        {
            actionDisabledReason = "当前进程优先级已经高于 High，A1 不降级。";
        }
        else if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = "当前进程优先级已经是 High。";
        }

        return new OptimizationA1ActionPreview(
            CreateStableId($"a1-preview|priority|{current.ProcessId}|{current.StartedAt}|{current.PriorityClass}"),
            OptimizationA1ActionKinds.RaiseProcessPriority,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath,
            current.StartedAt,
            [current.ProcessId],
            [current.ProcessName],
            current.PriorityClass,
            "High / A1 目标优先级",
            current.PriorityClass,
            A1Priority,
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [$"PID：{current.ProcessId}", $"进程：{current.ProcessName}", "动作：提升进程优先级。"]);
    }

    private OptimizationA1ActionPreview CreatePowerThrottlingAction(
        ProcessResourcePolicySnapshot current,
        string? disabledReason)
    {
        var currentRaw = current.PowerThrottlingRawValue;
        if (current.PowerThrottlingControlMask is not uint currentControl
            || current.PowerThrottlingStateMask is not uint currentState
            || string.IsNullOrWhiteSpace(currentRaw))
        {
            return DisabledProcessAction(
                OptimizationA1ActionKinds.DisableExecutionSpeedThrottling,
                current.ProcessId,
                current.ProcessName,
                current.ExecutablePath,
                "当前进程节流状态不可读取。");
        }

        var currentDisplay = current.PowerThrottlingDisplayValue
            ?? WindowsProcessResourcePolicyWriter.DescribePowerThrottling(currentControl, currentState);
        var proposedControl = currentControl | NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        var proposedState = currentState & ~NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        var proposedRaw = WindowsProcessResourcePolicyWriter.FormatPowerThrottlingRaw(proposedControl, proposedState);
        var proposedDisplay = WindowsProcessResourcePolicyWriter.DescribePowerThrottling(proposedControl, proposedState);
        var willChange = !currentRaw.Equals(proposedRaw, StringComparison.OrdinalIgnoreCase);
        var actionDisabledReason = disabledReason;
        if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = "执行速度节流已经关闭。";
        }

        return new OptimizationA1ActionPreview(
            CreateStableId($"a1-preview|power|{current.ProcessId}|{current.StartedAt}|{currentRaw}"),
            OptimizationA1ActionKinds.DisableExecutionSpeedThrottling,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath,
            current.StartedAt,
            [current.ProcessId],
            [current.ProcessName],
            currentDisplay,
            proposedDisplay,
            currentRaw,
            proposedRaw,
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [$"PID：{current.ProcessId}", $"进程：{current.ProcessName}", "动作：关闭执行速度节流。"]);
    }

    private static OptimizationA1ActionPreview DisabledProcessAction(
        string kind,
        int processId,
        string processName,
        string? executablePath,
        string reason)
    {
        return new OptimizationA1ActionPreview(
            CreateStableId($"a1-disabled|{kind}|{processId}|{processName}"),
            kind,
            processId,
            processName,
            executablePath,
            null,
            [processId],
            [processName],
            "--",
            "--",
            null,
            string.Empty,
            false,
            false,
            reason,
            [$"PID：{processId}", $"进程：{processName}"]);
    }

    private OptimizationA1AppliedAction CreateAppliedAction(
        OptimizationA1ActionPreview action,
        DateTimeOffset now,
        ProcessResourcePolicySnapshot process,
        string? previousRaw,
        string appliedRaw,
        string previousDisplay,
        string appliedDisplay,
        string? message)
    {
        return new OptimizationA1AppliedAction(
            CreateStableId($"a1-applied|{action.Kind}|{process.ProcessId}|{now.UtcTicks}|{previousRaw}"),
            action.Kind,
            process.ProcessId,
            process.ProcessName,
            process.ExecutablePath ?? action.ExecutablePath,
            process.StartedAt ?? action.ProcessStartedAt,
            [process.ProcessId],
            [process.ProcessName],
            previousDisplay,
            appliedDisplay,
            previousRaw,
            appliedRaw,
            now,
            null,
            OptimizationA1RecordStates.Active,
            message);
    }
}
