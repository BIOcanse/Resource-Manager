using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationProtectionPlacementService
{
    private async Task<OptimizationProtectionPlacementPreview> BuildPreviewAsync(CancellationToken cancellationToken)
    {
        var logicalProcessorCount = Environment.ProcessorCount;
        if (!CpuAffinityPlanner.TryCreateFullMask(logicalProcessorCount, out var fullMask))
        {
            var reason = "逻辑处理器数量超过单 processor group affinity 安全范围，后续改用 CPU Sets。";
            return DisabledPreview(logicalProcessorCount, reason, [], []);
        }

        var protectedTargets =
            (await protectionService.GetProtectedTargetsAsync(cancellationToken))
            .Where(static target => target.State == OptimizationProtectionStates.Active && target.AllowsPlacementAvoidance)
            .ToArray();
        var enhancedOwners = await ResolveEnhancedOwnersAsync(fullMask, logicalProcessorCount, cancellationToken);
        var enhancedMask = BuildEnhancedMask(enhancedOwners);
        var avoidanceMask = fullMask & ~enhancedMask;
        var avoidancePlan = avoidanceMask > 0
            ? CpuAffinityPlanner.CreateExplicitLogicalProcessorPlan(
                logicalProcessorCount,
                avoidanceMask,
                "ProtectionAvoidance",
                "保护对象避让到逻辑处理器")
            : CpuAffinityPlanner.CreateUnavailablePlan(
                logicalProcessorCount,
                "ProtectionAvoidance",
                "增强目标已占用全部当前 processor group 逻辑处理器，无法计算保护避让核心。");

        string? disabledReason = null;
        if (protectedTargets.Length == 0)
        {
            disabledReason = "当前没有 active 保护项。";
        }
        else if (enhancedOwners.Count == 0)
        {
            disabledReason = "当前没有 active A2 核心占用记录。";
        }
        else if (!avoidancePlan.Available)
        {
            disabledReason = avoidancePlan.DisabledReason;
        }

        var actions = disabledReason is null
            ? await BuildActionPreviewsAsync(protectedTargets, avoidancePlan, enhancedMask, cancellationToken)
            : [];
        if (disabledReason is null && actions.Count == 0)
        {
            disabledReason = "当前没有找到运行中的保护进程。";
        }

        var enabledCount = actions.Count(static action => action.Enabled && action.WillChange);
        var eligible = disabledReason is null && enabledCount > 0;
        return new OptimizationProtectionPlacementPreview(
            DateTimeOffset.Now,
            eligible,
            eligible
                ? $"可对 {enabledCount} 个保护进程应用核心避让。"
                : disabledReason ?? "没有需要写入的保护避让动作。",
            disabledReason,
            avoidancePlan,
            enhancedOwners,
            actions);
    }

    private static OptimizationProtectionPlacementPreview DisabledPreview(
        int logicalProcessorCount,
        string reason,
        IReadOnlyList<OptimizationProtectionPlacementOwner> enhancedOwners,
        IReadOnlyList<OptimizationProtectionPlacementActionPreview> actions)
    {
        return new OptimizationProtectionPlacementPreview(
            DateTimeOffset.Now,
            false,
            reason,
            reason,
            CpuAffinityPlanner.CreateUnavailablePlan(logicalProcessorCount, "ProtectionAvoidance", reason),
            enhancedOwners,
            actions);
    }

    private async Task<IReadOnlyList<OptimizationProtectionPlacementOwner>> ResolveEnhancedOwnersAsync(
        long fullMask,
        int logicalProcessorCount,
        CancellationToken cancellationToken)
    {
        var records = await a2Store.LoadAsync(cancellationToken);
        var owners = new List<OptimizationProtectionPlacementOwner>();
        foreach (var record in records)
        {
            if (record.State == OptimizationA2RecordStates.Restored
                || record.State == OptimizationA2RecordStates.NoChanges)
            {
                continue;
            }

            var masks = new List<long>();
            foreach (var action in record.Actions)
            {
                if (TryResolveLiveA2AffinityMask(action, fullMask, out var mask))
                {
                    masks.Add(mask);
                }
            }

            var ownerMask = masks.Aggregate(0L, static (current, next) => current | next);
            if (ownerMask <= 0)
            {
                continue;
            }

            var ids = CpuAffinityPlanner.CreateExplicitLogicalProcessorPlan(
                logicalProcessorCount,
                ownerMask,
                "A2ActiveAffinityOwner",
                "A2 目标核心").LogicalProcessorIds;
            owners.Add(new OptimizationProtectionPlacementOwner(
                record.Id,
                record.TargetKey,
                record.DisplayName,
                ownerMask,
                CpuAffinityPlanner.FormatMask(ownerMask),
                ids,
                masks.Count));
        }

        return owners
            .OrderBy(static owner => owner.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static owner => owner.RecordId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool TryResolveLiveA2AffinityMask(
        OptimizationA2AppliedAction action,
        long fullMask,
        out long mask)
    {
        mask = 0;
        if (action.Kind != OptimizationA2ActionKinds.MoveTargetAffinity
            || action.State != OptimizationA2RecordStates.Active
            || action.RestoredAt is not null
            || action.ProcessId is null
            || !TryParseMask(action.AppliedRawValue, out var appliedMask)
            || (appliedMask & ~fullMask) != 0)
        {
            return false;
        }

        var current = policyWriter.TryReadProcess(action.ProcessId.Value);
        if (current is null
            || !MatchesA2RecordedIdentity(action, current)
            || current.ProcessorAffinityMask != appliedMask)
        {
            return false;
        }

        mask = appliedMask;
        return true;
    }

    private async Task<IReadOnlyList<OptimizationProtectionPlacementActionPreview>> BuildActionPreviewsAsync(
        IReadOnlyList<ProtectedOptimizationTarget> protectedTargets,
        CpuAffinityPlan avoidancePlan,
        long enhancedMask,
        CancellationToken cancellationToken)
    {
        var metricIds = new[]
        {
            ResourceBreakdownMetricIds.CpuUsage,
            ResourceBreakdownMetricIds.MemoryUsage,
            ResourceBreakdownMetricIds.VirtualMemoryUsage
        };
        var snapshot = await resourceBreakdownSampler.GetSnapshotAsync(
            metricIds,
            metricIds.ToDictionary(static metricId => metricId, static _ => ResourceBreakdownScaleModes.Capacity, StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        var actions = new List<OptimizationProtectionPlacementActionPreview>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in protectedTargets)
        {
            var processes = snapshot.Bars
                .SelectMany(static bar => bar.Software)
                .Where(segment => MatchesTarget(target, segment))
                .SelectMany(static segment => segment.Processes)
                .GroupBy(static process => process.ProcessId)
                .Select(static group => group.First())
                .OrderBy(static process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static process => process.ProcessId)
                .ToArray();
            foreach (var process in processes)
            {
                if (!seen.Add($"{target.Id}|{process.ProcessId}"))
                {
                    continue;
                }

                actions.Add(CreateActionPreview(target, process, avoidancePlan, enhancedMask));
            }
        }

        return actions
            .OrderBy(static action => action.ProtectedTargetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static action => action.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static action => action.ProcessId)
            .ToArray();
    }

    private OptimizationProtectionPlacementActionPreview CreateActionPreview(
        ProtectedOptimizationTarget target,
        ResourceProcessSegment process,
        CpuAffinityPlan avoidancePlan,
        long enhancedMask)
    {
        var current = policyWriter.TryReadProcess(process.ProcessId);
        if (current is null)
        {
            return DisabledAction(target, process, "进程状态不可读取或进程已经退出。");
        }

        var disabledReason = ResolveProcessDisabledReason(current);
        var currentMask = current.ProcessorAffinityMask;
        var avoidanceMask = avoidancePlan.AffinityMask ?? 0;
        var touchesEnhancedCores = (currentMask & enhancedMask) != 0;
        var proposedMask = touchesEnhancedCores ? currentMask & avoidanceMask : currentMask;
        if (touchesEnhancedCores && proposedMask <= 0)
        {
            proposedMask = avoidanceMask;
        }

        var willChange = avoidancePlan.Available
            && proposedMask > 0
            && currentMask != proposedMask
            && touchesEnhancedCores;
        var actionDisabledReason = disabledReason ?? avoidancePlan.DisabledReason;
        if (actionDisabledReason is null && !touchesEnhancedCores)
        {
            actionDisabledReason = "当前进程已经避开 A2 增强目标核心。";
        }
        else if (actionDisabledReason is null && !willChange)
        {
            actionDisabledReason = "当前进程 affinity 不需要调整。";
        }

        return new OptimizationProtectionPlacementActionPreview(
            CreateStableId($"protection-placement-preview|{target.Id}|{current.ProcessId}|{current.StartedAt}|{currentMask}"),
            OptimizationProtectionPlacementActionKinds.MoveProtectedAffinity,
            target.Id,
            target.TargetKey,
            target.DisplayName,
            current.ProcessId,
            current.ProcessName,
            current.ExecutablePath ?? process.ExecutablePath,
            current.StartedAt,
            CpuAffinityPlanner.FormatMask(currentMask),
            proposedMask > 0 ? CpuAffinityPlanner.FormatMask(proposedMask) : "--",
            currentMask.ToString(CultureInfo.InvariantCulture),
            proposedMask.ToString(CultureInfo.InvariantCulture),
            willChange,
            actionDisabledReason is null && willChange,
            actionDisabledReason,
            [
                $"保护目标：{target.DisplayName}",
                $"PID：{current.ProcessId}",
                $"进程：{current.ProcessName}",
                avoidancePlan.Summary,
                "动作：只迁移保护进程 CPU affinity，避让 A2 已记录目标核心。"
            ]);
    }

    private static OptimizationProtectionPlacementActionPreview DisabledAction(
        ProtectedOptimizationTarget target,
        ResourceProcessSegment process,
        string reason)
    {
        return new OptimizationProtectionPlacementActionPreview(
            CreateStableId($"protection-placement-disabled|{target.Id}|{process.ProcessId}|{process.Name}"),
            OptimizationProtectionPlacementActionKinds.MoveProtectedAffinity,
            target.Id,
            target.TargetKey,
            target.DisplayName,
            process.ProcessId,
            process.Name,
            process.ExecutablePath,
            null,
            "--",
            "--",
            string.Empty,
            string.Empty,
            false,
            false,
            reason,
            [$"保护目标：{target.DisplayName}", $"PID：{process.ProcessId}", $"进程：{process.Name}"]);
    }
}
