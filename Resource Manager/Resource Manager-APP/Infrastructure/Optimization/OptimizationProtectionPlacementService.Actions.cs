using System.Globalization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationProtectionPlacementService
{
    private OptimizationProtectionPlacementAppliedAction? ApplyAction(
        OptimizationProtectionPlacementActionPreview action,
        DateTimeOffset now)
    {
        if (!TryParseMask(action.ProposedRawValue, out var proposedMask))
        {
            return null;
        }

        var before = policyWriter.TryReadProcess(action.ProcessId);
        if (before is null || !MatchesActionIdentity(action, before))
        {
            return null;
        }

        var previousRaw = before.ProcessorAffinityMask.ToString(CultureInfo.InvariantCulture);
        if (!previousRaw.Equals(action.CurrentRawValue, StringComparison.OrdinalIgnoreCase)
            || before.ProcessorAffinityMask == proposedMask)
        {
            return null;
        }

        var writeResult = policyWriter.TrySetProcessorAffinity(before.ProcessId, proposedMask);
        if (!writeResult.Succeeded)
        {
            logger.LogWarning(
                "Failed to apply protection placement affinity to pid {ProcessId}: {Message}",
                before.ProcessId,
                writeResult.Message);
            return null;
        }

        return new OptimizationProtectionPlacementAppliedAction(
            CreateStableId($"protection-placement-applied|{action.ProtectedTargetId}|{before.ProcessId}|{now.UtcTicks}|{previousRaw}"),
            action.Kind,
            action.ProtectedTargetId,
            action.ProtectedTargetKey,
            action.ProtectedTargetName,
            before.ProcessId,
            before.ProcessName,
            before.ExecutablePath ?? action.ExecutablePath,
            before.StartedAt ?? action.ProcessStartedAt,
            CpuAffinityPlanner.FormatMask(before.ProcessorAffinityMask),
            CpuAffinityPlanner.FormatMask(proposedMask),
            previousRaw,
            proposedMask.ToString(CultureInfo.InvariantCulture),
            now,
            null,
            OptimizationProtectionPlacementRecordStates.Active,
            writeResult.Message);
    }

    private OptimizationProtectionPlacementAppliedAction RestoreAction(
        OptimizationProtectionPlacementAppliedAction action,
        DateTimeOffset now)
    {
        var current = policyWriter.TryReadProcess(action.ProcessId);
        if (current is null)
        {
            return action with
            {
                RestoredAt = now,
                State = OptimizationProtectionPlacementRecordStates.Restored,
                Message = "进程已经退出，无需恢复运行时 affinity。"
            };
        }

        if (!MatchesRecordedIdentity(action, current))
        {
            return action with { Message = "PID 当前已不是原记录进程，跳过恢复。" };
        }

        var currentRaw = current.ProcessorAffinityMask.ToString(CultureInfo.InvariantCulture);
        if (!currentRaw.Equals(action.AppliedRawValue, StringComparison.OrdinalIgnoreCase))
        {
            return action with { Message = "当前 affinity 已被其他来源修改，跳过恢复。" };
        }

        if (!TryParseMask(action.PreviousRawValue, out var previousMask))
        {
            return action with { Message = "原始 CPU affinity mask 无效，跳过恢复。" };
        }

        var result = policyWriter.TrySetProcessorAffinity(current.ProcessId, previousMask);
        if (!result.Succeeded)
        {
            return action with { Message = result.Message };
        }

        return action with
        {
            RestoredAt = now,
            State = OptimizationProtectionPlacementRecordStates.Restored,
            Message = "已恢复保护避让前 affinity。"
        };
    }
}
