using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationA2Service
{
    private IReadOnlyList<OptimizationA2AppliedAction> ApplyActions(
        IReadOnlyList<OptimizationA2ActionPreview> actions,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var batchesByProcess = new Dictionary<int, PreparedA2ProcessBatch>();
        var batches = new List<PreparedA2ProcessBatch>();
        for (var index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            var field = ResolveBatchField(action.Kind);
            if (action.ProcessId is not int processId || field == ProcessResourcePolicyBatchFields.None)
            {
                continue;
            }

            if (!batchesByProcess.TryGetValue(processId, out var batch))
            {
                batch = new PreparedA2ProcessBatch(policyWriter.TryReadProcess(processId));
                batchesByProcess.Add(processId, batch);
                batches.Add(batch);
            }

            PrepareAction(batch, action, field, index);
        }

        var executableBatches = batches
            .Where(static batch => batch.Actions.Count > 0)
            .ToArray();
        if (executableBatches.Length == 0)
        {
            return [];
        }

        var requests = executableBatches
            .Select(static batch => batch.CreateRequest())
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var writeResults = policyWriter.TryApplyBatch(requests);
        var appliedActions = new List<IndexedA2AppliedAction>(actions.Count);
        for (var index = 0; index < executableBatches.Length; index++)
        {
            var batch = executableBatches[index];
            var writeResult = index < writeResults.Count ? writeResults[index] : null;
            foreach (var prepared in batch.Actions)
            {
                var fieldResult = writeResult?.Find(prepared.Field);
                if (fieldResult?.Succeeded != true)
                {
                    logger.LogWarning(
                        "Failed to apply A2 action {ActionKind} to pid {ProcessId}: {Message}",
                        prepared.Action.Kind,
                        batch.Process!.ProcessId,
                        fieldResult?.Message ?? "批处理未返回该字段的执行结果。");
                    continue;
                }

                appliedActions.Add(new IndexedA2AppliedAction(
                    prepared.OriginalIndex,
                    CreateAppliedAction(
                        prepared.Action,
                        now,
                        batch.Process!,
                        prepared.PreviousRawValue,
                        prepared.AppliedRawValue,
                        prepared.PreviousDisplayValue,
                        prepared.AppliedDisplayValue,
                        fieldResult.Message)));
            }
        }

        appliedActions.Sort(static (left, right) => left.OriginalIndex.CompareTo(right.OriginalIndex));
        return appliedActions.Select(static item => item.Action).ToArray();
    }

    private void PrepareAction(
        PreparedA2ProcessBatch batch,
        OptimizationA2ActionPreview action,
        ProcessResourcePolicyBatchFields field,
        int originalIndex)
    {
        var before = batch.Process;
        if (before is null || !MatchesActionIdentity(action, before))
        {
            return;
        }

        PreparedA2Action? prepared = action.Kind switch
        {
            OptimizationA2ActionKinds.RaiseProcessPriority => PreparePriorityAction(
                action,
                before,
                field,
                originalIndex),
            OptimizationA2ActionKinds.DisableExecutionSpeedThrottling => PreparePowerThrottlingAction(
                action,
                before,
                field,
                originalIndex),
            OptimizationA2ActionKinds.MoveTargetAffinity => PrepareAffinityAction(
                action,
                before,
                field,
                originalIndex),
            _ => null
        };
        if (prepared is null)
        {
            return;
        }

        if ((batch.Fields & field) != 0)
        {
            logger.LogWarning(
                "Duplicate A2 process policy field {Field} for pid {ProcessId}; the first action is retained.",
                field,
                before.ProcessId);
            return;
        }

        batch.Add(prepared);
    }

    private static PreparedA2Action? PreparePriorityAction(
        OptimizationA2ActionPreview action,
        ProcessResourcePolicySnapshot before,
        ProcessResourcePolicyBatchFields field,
        int originalIndex)
    {
        if (PriorityRank(before.PriorityClass) >= PriorityRank(A2Priority))
        {
            return null;
        }

        return new PreparedA2Action(
            originalIndex,
            action,
            field,
            before.PriorityClass,
            A2Priority,
            before.PriorityClass,
            "High / A2 目标优先级",
            PriorityClass: A2Priority);
    }

    private static PreparedA2Action? PreparePowerThrottlingAction(
        OptimizationA2ActionPreview action,
        ProcessResourcePolicySnapshot before,
        ProcessResourcePolicyBatchFields field,
        int originalIndex)
    {
        var previousRaw = before.PowerThrottlingRawValue;
        if (before.PowerThrottlingControlMask is not uint previousControl
            || before.PowerThrottlingStateMask is not uint previousState
            || string.IsNullOrWhiteSpace(previousRaw))
        {
            return null;
        }

        var proposedControl = previousControl | NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        var proposedState = previousState & ~NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        var proposedRaw = WindowsProcessResourcePolicyWriter.FormatPowerThrottlingRaw(proposedControl, proposedState);
        if (previousRaw.Equals(proposedRaw, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new PreparedA2Action(
            originalIndex,
            action,
            field,
            previousRaw,
            proposedRaw,
            before.PowerThrottlingDisplayValue
                ?? WindowsProcessResourcePolicyWriter.DescribePowerThrottling(previousControl, previousState),
            WindowsProcessResourcePolicyWriter.DescribePowerThrottling(proposedControl, proposedState),
            PowerControlMask: proposedControl,
            PowerStateMask: proposedState);
    }

    private static PreparedA2Action? PrepareAffinityAction(
        OptimizationA2ActionPreview action,
        ProcessResourcePolicySnapshot before,
        ProcessResourcePolicyBatchFields field,
        int originalIndex)
    {
        if (!TryParseMask(action.ProposedRawValue, out var proposedMask)
            || before.ProcessorAffinityMask == proposedMask)
        {
            return null;
        }

        var previousRaw = before.ProcessorAffinityMask.ToString(CultureInfo.InvariantCulture);
        var appliedRaw = proposedMask.ToString(CultureInfo.InvariantCulture);
        return new PreparedA2Action(
            originalIndex,
            action,
            field,
            previousRaw,
            appliedRaw,
            CpuAffinityPlanner.FormatMask(before.ProcessorAffinityMask),
            CpuAffinityPlanner.FormatMask(proposedMask),
            AffinityMask: proposedMask);
    }

    private static ProcessResourcePolicyBatchFields ResolveBatchField(string kind)
    {
        return kind switch
        {
            OptimizationA2ActionKinds.RaiseProcessPriority => ProcessResourcePolicyBatchFields.PriorityClass,
            OptimizationA2ActionKinds.DisableExecutionSpeedThrottling => ProcessResourcePolicyBatchFields.PowerThrottling,
            OptimizationA2ActionKinds.MoveTargetAffinity => ProcessResourcePolicyBatchFields.AffinityMask,
            _ => ProcessResourcePolicyBatchFields.None
        };
    }

    private sealed class PreparedA2ProcessBatch(ProcessResourcePolicySnapshot? process)
    {
        public ProcessResourcePolicySnapshot? Process { get; } = process;

        public ProcessResourcePolicyBatchFields Fields { get; private set; }

        public string? PriorityClass { get; private set; }

        public long? AffinityMask { get; private set; }

        public uint? PowerControlMask { get; private set; }

        public uint? PowerStateMask { get; private set; }

        public List<PreparedA2Action> Actions { get; } = [];

        public void Add(PreparedA2Action action)
        {
            Fields |= action.Field;
            PriorityClass ??= action.PriorityClass;
            AffinityMask ??= action.AffinityMask;
            PowerControlMask ??= action.PowerControlMask;
            PowerStateMask ??= action.PowerStateMask;
            Actions.Add(action);
        }

        public ProcessResourcePolicyBatchRequest CreateRequest()
        {
            return new ProcessResourcePolicyBatchRequest(
                Process!.ProcessId,
                Process.StartedAt,
                PriorityClass: PriorityClass,
                AffinityMask: AffinityMask,
                PowerControlMask: PowerControlMask,
                PowerStateMask: PowerStateMask);
        }
    }

    private sealed record PreparedA2Action(
        int OriginalIndex,
        OptimizationA2ActionPreview Action,
        ProcessResourcePolicyBatchFields Field,
        string PreviousRawValue,
        string AppliedRawValue,
        string PreviousDisplayValue,
        string AppliedDisplayValue,
        string? PriorityClass = null,
        long? AffinityMask = null,
        uint? PowerControlMask = null,
        uint? PowerStateMask = null);

    private sealed record IndexedA2AppliedAction(
        int OriginalIndex,
        OptimizationA2AppliedAction Action);

    private OptimizationA2AppliedAction RestoreAction(
        OptimizationA2AppliedAction action,
        DateTimeOffset now)
    {
        return action.Kind switch
        {
            OptimizationA2ActionKinds.RaiseProcessPriority => RestorePriorityAction(action, now),
            OptimizationA2ActionKinds.DisableExecutionSpeedThrottling => RestorePowerThrottlingAction(action, now),
            OptimizationA2ActionKinds.PreferHighPerformanceGpu => legacyGpuPreferenceActionRestorer.Restore(action, now),
            OptimizationA2ActionKinds.MoveTargetAffinity => RestoreAffinityAction(action, now),
            _ => action with { Message = "未知 A2 极限增强动作，跳过恢复。" }
        };
    }

    private OptimizationA2AppliedAction RestorePriorityAction(
        OptimizationA2AppliedAction action,
        DateTimeOffset now)
    {
        if (action.ProcessId is null)
        {
            return action with { Message = "记录缺少 PID，跳过恢复。" };
        }

        var current = policyWriter.TryReadProcess(action.ProcessId.Value);
        if (current is null)
        {
            return action with
            {
                RestoredAt = now,
                State = OptimizationA2RecordStates.Restored,
                Message = "进程已经退出，无需恢复运行时优先级。"
            };
        }

        if (!MatchesRecordedIdentity(action, current))
        {
            return action with { Message = "PID 当前已不是原记录进程，跳过恢复。" };
        }

        if (!string.Equals(current.PriorityClass, action.AppliedRawValue, StringComparison.OrdinalIgnoreCase))
        {
            return action with { Message = "当前优先级已被其他来源修改，跳过恢复。" };
        }

        var result = policyWriter.TrySetPriorityClass(current.ProcessId, action.PreviousRawValue ?? "Normal");
        return result.Succeeded
            ? action with { RestoredAt = now, State = OptimizationA2RecordStates.Restored, Message = "已恢复进程优先级。" }
            : action with { Message = result.Message };
    }

    private OptimizationA2AppliedAction RestorePowerThrottlingAction(
        OptimizationA2AppliedAction action,
        DateTimeOffset now)
    {
        if (action.ProcessId is null)
        {
            return action with { Message = "记录缺少 PID，跳过恢复。" };
        }

        var currentProcess = policyWriter.TryReadProcess(action.ProcessId.Value);
        if (currentProcess is null)
        {
            return action with
            {
                RestoredAt = now,
                State = OptimizationA2RecordStates.Restored,
                Message = "进程已经退出，无需恢复运行时节流状态。"
            };
        }

        if (!MatchesRecordedIdentity(action, currentProcess))
        {
            return action with { Message = "PID 当前已不是原记录进程，跳过恢复。" };
        }

        var current = policyWriter.TryReadPowerThrottling(currentProcess.ProcessId);
        if (current is null)
        {
            return action with { Message = "当前节流状态不可读取，跳过恢复。" };
        }

        if (!string.Equals(current.RawValue, action.AppliedRawValue, StringComparison.OrdinalIgnoreCase))
        {
            return action with { Message = "当前节流状态已被其他来源修改，跳过恢复。" };
        }

        if (!WindowsProcessResourcePolicyWriter.TryParsePowerThrottlingRaw(
            action.PreviousRawValue,
            out var previousControl,
            out var previousState))
        {
            return action with { Message = "原始节流状态记录无效，跳过恢复。" };
        }

        var result = policyWriter.TrySetPowerThrottling(currentProcess.ProcessId, previousControl, previousState);
        return result.Succeeded
            ? action with { RestoredAt = now, State = OptimizationA2RecordStates.Restored, Message = "已恢复进程节流状态。" }
            : action with { Message = result.Message };
    }

    private OptimizationA2AppliedAction RestoreAffinityAction(
        OptimizationA2AppliedAction action,
        DateTimeOffset now)
    {
        if (action.ProcessId is null)
        {
            return action with { Message = "记录缺少 PID，跳过恢复。" };
        }

        var current = policyWriter.TryReadProcess(action.ProcessId.Value);
        if (current is null)
        {
            return action with
            {
                RestoredAt = now,
                State = OptimizationA2RecordStates.Restored,
                Message = "进程已经退出，无需恢复运行时 CPU affinity。"
            };
        }

        if (!MatchesRecordedIdentity(action, current))
        {
            return action with { Message = "PID 当前已不是原记录进程，跳过恢复。" };
        }

        if (!TryParseMask(action.AppliedRawValue, out var appliedMask)
            || current.ProcessorAffinityMask != appliedMask)
        {
            return action with { Message = "当前 CPU affinity 已被其他来源修改，跳过恢复。" };
        }

        if (!TryParseMask(action.PreviousRawValue, out var previousMask))
        {
            return action with { Message = "原始 CPU affinity 记录无效，跳过恢复。" };
        }

        var result = policyWriter.TrySetProcessorAffinity(current.ProcessId, previousMask);
        return result.Succeeded
            ? action with { RestoredAt = now, State = OptimizationA2RecordStates.Restored, Message = "已恢复 CPU affinity。" }
            : action with { Message = result.Message };
    }
}
