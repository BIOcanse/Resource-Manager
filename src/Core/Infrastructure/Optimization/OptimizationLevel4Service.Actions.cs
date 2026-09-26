using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationLevel4Service
{
    private OptimizationLevel4AppliedAction? ApplyAction(
        OptimizationLevel4ActionPreview action,
        DateTimeOffset now)
    {
        var before = freezeController.TryReadTarget(action.ProcessId);
        if (before is null || !MatchesActionIdentity(action, before.Process))
        {
            return null;
        }

        if (before.ThreadIds.Count == 0)
        {
            return null;
        }

        var result = freezeController.TryFreezeProcess(action.ProcessId);
        if (!result.Succeeded || result.SuspendedThreads.Count == 0)
        {
            logger.LogWarning(
                "Failed to apply level 4 freeze to pid {ProcessId}: {Message}",
                action.ProcessId,
                result.Message);
            return null;
        }

        return new OptimizationLevel4AppliedAction(
            CreateStableId($"level4-applied|{action.ProcessId}|{now.UtcTicks}|{result.SuspendedThreads.Count}"),
            OptimizationLevel4ActionKinds.FreezeProcess,
            before.Process.ProcessId,
            before.Process.ProcessName,
            before.Process.ExecutablePath ?? action.ExecutablePath,
            before.Process.StartedAt ?? action.ProcessStartedAt,
            result.SuspendedThreads,
            result.WorkingSetTrimAttempted,
            result.WorkingSetTrimSucceeded,
            result.WorkingSetTrimMessage,
            now,
            null,
            OptimizationLevel4RecordStates.Active,
            result.Message);
    }

    private OptimizationLevel4AppliedAction RestoreAction(
        OptimizationLevel4AppliedAction action,
        DateTimeOffset now)
    {
        var current = freezeController.TryReadTarget(action.ProcessId);
        if (current is null)
        {
            return action with
            {
                RestoredAt = now,
                State = OptimizationLevel4RecordStates.Restored,
                Message = "进程已经退出，无需恢复。"
            };
        }

        if (!MatchesRecordedIdentity(action, current.Process))
        {
            return action with { Message = "PID 当前已不是原记录进程，跳过恢复。" };
        }

        var result = freezeController.TryResumeThreads(action.Threads);
        if (!IsFreezeRestoreComplete(result, action.Threads.Count))
        {
            return action with { Message = result.Message };
        }

        return action with
        {
            RestoredAt = now,
            State = OptimizationLevel4RecordStates.Restored,
            Message = result.Message
        };
    }
}
