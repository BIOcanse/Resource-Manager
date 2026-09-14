using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.Windows;
namespace ResourceManager.App.Infrastructure.Optimization;
internal sealed partial class OptimizationProcessPolicyEngine
{
    public ProcessPolicyOptimizationAppliedAction RestoreAction(
        ProcessPolicyOptimizationAppliedAction action,
        DateTimeOffset now)
    {
        var current = policyWriter.TryReadProcess(action.ProcessId);
        if (current is null)
        {
            return action with { Message = "进程已经退出，跳过恢复。" };
        }

        if (!MatchesRecordedIdentity(action, current))
        {
            return action with { Message = "PID 当前已不是原记录进程，跳过恢复。" };
        }

        var currentRaw = ReadRawValue(action.Kind, current);
        if (!string.Equals(currentRaw, action.AppliedRawValue, StringComparison.OrdinalIgnoreCase))
        {
            return action with { Message = "当前状态已被其他来源修改，跳过恢复。" };
        }

        var result = WriteAction(
            action.Kind,
            action.ProcessId,
            current.StartedAt,
            currentRaw,
            action.PreviousRawValue);
        if (!result.Succeeded)
        {
            return action with { Message = result.Message };
        }

        return action with
        {
            RestoredAt = now,
            State = ProcessPolicyOptimizationRecordStates.Restored,
            Message = "已恢复改前状态。"
        };
    }
    private ProcessResourcePolicyWriteResult WriteAction(
        string kind,
        int processId,
        DateTimeOffset? expectedStartedAt,
        string? expectedRawValue,
        string rawValue)
    {
        if (IsProcessPriorityAction(kind))
        {
            return policyWriter.TrySetPriorityClass(processId, rawValue);
        }

        if (IsPowerThrottlingAction(kind))
        {
            return WindowsProcessResourcePolicyWriter.TryParsePowerThrottlingRaw(rawValue, out var controlMask, out var stateMask)
                ? policyWriter.TrySetPowerThrottling(processId, controlMask, stateMask)
                : new ProcessResourcePolicyWriteResult(false, "进程节流状态无效。");
        }

        if (IsMemoryPriorityAction(kind))
        {
            return expectedStartedAt is not null
                && expectedRawValue is not null
                && uint.TryParse(
                    expectedRawValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var expectedMemoryPriority)
                && uint.TryParse(
                    rawValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var memoryPriority)
                    ? policyWriter.TrySetMemoryPriority(
                        processId,
                        expectedStartedAt.Value,
                        expectedMemoryPriority,
                        memoryPriority)
                    : new ProcessResourcePolicyWriteResult(false, "内存优先级写入条件无效。");
        }

        if (IsAffinityAction(kind))
        {
            return TryParseMask(rawValue, out var mask)
                ? policyWriter.TrySetProcessorAffinity(processId, mask)
                : new ProcessResourcePolicyWriteResult(false, "CPU affinity mask 无效。");
        }

        return new ProcessResourcePolicyWriteResult(false, "未知进程策略动作。");
    }

    private static bool CanApplyNow(
        ProcessPolicyOptimizationActionPreview action,
        ProcessResourcePolicySnapshot current,
        string currentRaw)
    {
        if (IsProcessPriorityAction(action.Kind))
        {
            return PriorityRank(currentRaw) > PriorityRank(action.ProposedRawValue);
        }

        if (IsPowerThrottlingAction(action.Kind))
        {
            return !string.Equals(currentRaw, action.ProposedRawValue, StringComparison.OrdinalIgnoreCase);
        }

        if (IsMemoryPriorityAction(action.Kind))
        {
            return uint.TryParse(action.ProposedRawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
                && current.MemoryPriority is not null
                && current.MemoryPriority.Value > target;
        }

        if (IsAffinityAction(action.Kind))
        {
            if (!TryParseMask(action.ProposedRawValue, out var proposedMask))
            {
                return false;
            }

            return current.ProcessorAffinityMask != proposedMask
                && (proposedMask & ~current.ProcessorAffinityMask) == 0;
        }

        return false;
    }

    private static string? ReadRawValue(
        string kind,
        ProcessResourcePolicySnapshot snapshot)
    {
        if (IsProcessPriorityAction(kind))
        {
            return snapshot.PriorityClass;
        }

        if (IsPowerThrottlingAction(kind))
        {
            return snapshot.PowerThrottlingRawValue;
        }

        if (IsMemoryPriorityAction(kind))
        {
            return snapshot.MemoryPriorityRawValue;
        }

        if (IsAffinityAction(kind))
        {
            return snapshot.ProcessorAffinityMask.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static string FormatRawValue(string kind, string rawValue)
    {
        if (IsPowerThrottlingAction(kind)
            && WindowsProcessResourcePolicyWriter.TryParsePowerThrottlingRaw(rawValue, out var controlMask, out var stateMask))
        {
            return WindowsProcessResourcePolicyWriter.DescribePowerThrottling(controlMask, stateMask);
        }

        if (IsMemoryPriorityAction(kind)
            && uint.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryPriority))
        {
            return WindowsProcessResourcePolicyWriter.DescribeMemoryPriority(memoryPriority);
        }

        if (IsAffinityAction(kind)
            && TryParseMask(rawValue, out var mask))
        {
            return CpuAffinityPlanner.FormatMask(mask);
        }

        return rawValue;
    }
}
