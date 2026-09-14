using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Windows;
namespace ResourceManager.App.Infrastructure.Optimization;
internal sealed partial class OptimizationProcessPolicyEngine
{
    public async Task<OptimizationReportItem> ResolveReportAsync(
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

    public async Task<ProcessPolicyOptimizationPreview> BuildPreviewAsync(
        OptimizationReportItem report,
        ProcessPolicyOptimizationProfile profile,
        CancellationToken cancellationToken)
    {
        var affinityPlan = CpuAffinityPlanner.CreateRearLogicalProcessorPlan(Environment.ProcessorCount);
        var disabledReason = ResolveTargetDisabledReason(report, profile);
        if (disabledReason is null
            && await protectionService.IsTargetProtectedAsync(
                report.Target,
                cancellationToken))
        {
            disabledReason = "保护级目标不执行外部压制；保护进程只通过保护策略执行核心避让。";
        }

        var actions = disabledReason is null
            ? await BuildActionPreviewsAsync(report, profile, affinityPlan, cancellationToken)
            : [];
        if (disabledReason is null && actions.Count == 0)
        {
            disabledReason = $"当前没有找到可执行的运行中进程，暂不执行 {profile.DisplayName}。";
        }

        var enabledCount = actions.Count(static action => action.Enabled && action.WillChange);
        var eligible = disabledReason is null && enabledCount > 0;
        return new ProcessPolicyOptimizationPreview(
            report.Id,
            DateTimeOffset.Now,
            eligible,
            eligible
                ? $"可对 {report.Target.DisplayName} 应用 {enabledCount} 条 {profile.DisplayName} 动作。"
                : disabledReason ?? $"没有需要写入的 {profile.DisplayName} 动作。",
            disabledReason,
            report.Target,
            affinityPlan,
            actions);
    }
    private async Task<IReadOnlyList<ProcessPolicyOptimizationActionPreview>> BuildActionPreviewsAsync(
        OptimizationReportItem report,
        ProcessPolicyOptimizationProfile profile,
        CpuAffinityPlan affinityPlan,
        CancellationToken cancellationToken)
    {
        var metricIds = ResolveMetricIds(report)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (metricIds.Length == 0)
        {
            return [];
        }

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
            .SelectMany(group => CreateActionPreviews(group.First(), foregroundProcessId, profile, affinityPlan))
            .OrderBy(static action => action.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static action => action.ProcessId)
            .ThenBy(static action => ActionOrder(action.Kind))
            .ToArray();
    }

    private IReadOnlyList<ProcessPolicyOptimizationActionPreview> CreateActionPreviews(
        ResourceProcessSegment process,
        int? foregroundProcessId,
        ProcessPolicyOptimizationProfile profile,
        CpuAffinityPlan affinityPlan)
    {
        var current = policyWriter.TryReadProcess(process.ProcessId);
        if (current is null)
        {
            return
            [
                DisabledAction(
                    profile.PriorityActionKind,
                    process.ProcessId,
                    process.Name,
                    process.ExecutablePath,
                    "进程状态不可读取或进程已经退出。")
            ];
        }

        var disabledReason = ResolveProcessDisabledReason(current, foregroundProcessId);
        var actions = new List<ProcessPolicyOptimizationActionPreview>
        {
            CreatePriorityAction(current, disabledReason, profile),
            CreatePowerThrottlingAction(current, disabledReason, profile),
            CreateMemoryPriorityAction(current, disabledReason, profile)
        };

        if (profile.IncludeAffinity && profile.AffinityActionKind is not null)
        {
            actions.Add(CreateAffinityAction(current, disabledReason, profile.AffinityActionKind, affinityPlan));
        }

        return actions;
    }

    private static ProcessPolicyOptimizationActionPreview CreatePriorityAction(
        ProcessResourcePolicySnapshot current,
        string? disabledReason,
        ProcessPolicyOptimizationProfile profile)
    {
        var currentRaw = current.PriorityClass;
        var willChange = PriorityRank(currentRaw) > PriorityRank(profile.TargetPriorityClass);
        var actionDisabledReason = disabledReason;
        if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = $"当前进程优先级已经不高于 {profile.TargetPriorityClass}。";
        }

        return new ProcessPolicyOptimizationActionPreview(
            CreateStableId($"{profile.RecordPrefix}-preview|priority|{current.ProcessId}|{current.StartedAt}|{currentRaw}"),
            profile.PriorityActionKind,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath,
            current.StartedAt,
            FormatRawValue(profile.PriorityActionKind, currentRaw),
            profile.PriorityDisplayValue,
            currentRaw,
            profile.TargetPriorityClass,
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [
                $"PID：{current.ProcessId}",
                $"进程：{current.ProcessName}",
                $"动作：{profile.PriorityDetail}"
            ]);
    }

    private static ProcessPolicyOptimizationActionPreview CreatePowerThrottlingAction(
        ProcessResourcePolicySnapshot current,
        string? disabledReason,
        ProcessPolicyOptimizationProfile profile)
    {
        var bit = NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        var currentRaw = current.PowerThrottlingRawValue;
        var control = current.PowerThrottlingControlMask ?? 0;
        var state = current.PowerThrottlingStateMask ?? 0;
        var proposedControl = control | bit;
        var proposedState = state | bit;
        var proposedRaw = WindowsProcessResourcePolicyWriter.FormatPowerThrottlingRaw(proposedControl, proposedState);
        var willChange = currentRaw is not null && ((control & bit) == 0 || (state & bit) == 0);
        var actionDisabledReason = disabledReason;
        if (actionDisabledReason is null && currentRaw is null)
        {
            actionDisabledReason = "执行速度节流状态不可读取。";
        }
        else if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = "执行速度节流已经开启。";
        }

        return new ProcessPolicyOptimizationActionPreview(
            CreateStableId($"{profile.RecordPrefix}-preview|power|{current.ProcessId}|{current.StartedAt}|{currentRaw}"),
            profile.PowerThrottlingActionKind,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath,
            current.StartedAt,
            current.PowerThrottlingDisplayValue ?? "--",
            WindowsProcessResourcePolicyWriter.DescribePowerThrottling(proposedControl, proposedState),
            currentRaw,
            proposedRaw,
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [
                $"PID：{current.ProcessId}",
                $"进程：{current.ProcessName}",
                "动作：开启执行速度节流 / EcoQoS 倾向。"
            ]);
    }

    private static ProcessPolicyOptimizationActionPreview CreateMemoryPriorityAction(
        ProcessResourcePolicySnapshot current,
        string? disabledReason,
        ProcessPolicyOptimizationProfile profile)
    {
        var currentRaw = current.MemoryPriorityRawValue;
        var willChange = current.MemoryPriority is not null && current.MemoryPriority.Value > profile.TargetMemoryPriority;
        var actionDisabledReason = disabledReason;
        if (actionDisabledReason is null && current.MemoryPriority is null)
        {
            actionDisabledReason = "内存优先级状态不可读取。";
        }
        else if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = $"当前内存优先级已经不高于 {profile.MemoryPriorityDisplayValue}。";
        }

        return new ProcessPolicyOptimizationActionPreview(
            CreateStableId($"{profile.RecordPrefix}-preview|memory|{current.ProcessId}|{current.StartedAt}|{currentRaw}"),
            profile.MemoryPriorityActionKind,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath,
            current.StartedAt,
            current.MemoryPriorityDisplayValue ?? "--",
            profile.MemoryPriorityDisplayValue,
            currentRaw,
            profile.TargetMemoryPriority.ToString(CultureInfo.InvariantCulture),
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [
                $"PID：{current.ProcessId}",
                $"进程：{current.ProcessName}",
                "动作：降低进程内存优先级，让 Windows 在内存紧张时优先裁剪该进程页面。"
            ]);
    }

    private static ProcessPolicyOptimizationActionPreview CreateAffinityAction(
        ProcessResourcePolicySnapshot current,
        string? disabledReason,
        string actionKind,
        CpuAffinityPlan affinityPlan)
    {
        var currentRaw = current.ProcessorAffinityMask.ToString(CultureInfo.InvariantCulture);
        var proposedMask = affinityPlan.AffinityMask ?? 0;
        var willChange = affinityPlan.Available && current.ProcessorAffinityMask != proposedMask;
        var actionDisabledReason = disabledReason ?? affinityPlan.DisabledReason;
        if (actionDisabledReason is null && (proposedMask & ~current.ProcessorAffinityMask) != 0)
        {
            actionDisabledReason = "当前进程已有更窄 CPU affinity，避免扩大可用核心范围。";
        }

        if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = "当前进程已经在目标 CPU affinity 上。";
        }

        return new ProcessPolicyOptimizationActionPreview(
            CreateStableId($"policy-preview|affinity|{current.ProcessId}|{current.StartedAt}|{currentRaw}"),
            actionKind,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath,
            current.StartedAt,
            CpuAffinityPlanner.FormatMask(current.ProcessorAffinityMask),
            proposedMask > 0
                ? $"{CpuAffinityPlanner.FormatMask(proposedMask)} / 逻辑处理器 {CpuAffinityPlanner.FormatProcessorIds(affinityPlan.LogicalProcessorIds)}"
                : "--",
            currentRaw,
            proposedMask.ToString(CultureInfo.InvariantCulture),
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [
                $"PID：{current.ProcessId}",
                $"进程：{current.ProcessName}",
                affinityPlan.Summary,
                "动作：迁移后台进程 CPU affinity。"
            ]);
    }

    private static ProcessPolicyOptimizationActionPreview DisabledAction(
        string kind,
        int processId,
        string processName,
        string? executablePath,
        string reason)
    {
        return new ProcessPolicyOptimizationActionPreview(
            CreateStableId($"policy-disabled|{kind}|{processId}|{processName}"),
            kind,
            processId,
            processName,
            executablePath,
            null,
            "--",
            "--",
            null,
            string.Empty,
            false,
            false,
            reason,
            [$"PID：{processId}", $"进程：{processName}"]);
    }
}
