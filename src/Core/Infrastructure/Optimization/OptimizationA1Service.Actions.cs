using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.Windows;
namespace ResourceManager.App.Infrastructure.Optimization;
public sealed partial class OptimizationA1Service
{
    private IReadOnlyList<OptimizationA1AppliedAction> ApplyActions(
        IReadOnlyList<OptimizationA1ActionPreview> actions,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var batchesByProcess = new Dictionary<int, PreparedA1ProcessBatch>();
        var batches = new List<PreparedA1ProcessBatch>();
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
                batch = new PreparedA1ProcessBatch(policyWriter.TryReadProcess(processId));
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
        var appliedActions = new List<IndexedA1AppliedAction>(actions.Count);
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
                        "Failed to apply A1 action {ActionKind} to pid {ProcessId}: {Message}",
                        prepared.Action.Kind,
                        batch.Process!.ProcessId,
                        fieldResult?.Message ?? "批处理未返回该字段的执行结果。");
                    continue;
                }

                appliedActions.Add(new IndexedA1AppliedAction(
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
        PreparedA1ProcessBatch batch,
        OptimizationA1ActionPreview action,
        ProcessResourcePolicyBatchFields field,
        int originalIndex)
    {
        var before = batch.Process;
        if (before is null || !MatchesActionIdentity(action, before))
        {
            return;
        }

        PreparedA1Action? prepared = action.Kind switch
        {
            OptimizationA1ActionKinds.RaiseProcessPriority => PreparePriorityAction(
                action,
                before,
                field,
                originalIndex),
            OptimizationA1ActionKinds.DisableExecutionSpeedThrottling => PreparePowerThrottlingAction(
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
                "Duplicate A1 process policy field {Field} for pid {ProcessId}; the first action is retained.",
                field,
                before.ProcessId);
            return;
        }

        batch.Add(prepared);
    }

    private static PreparedA1Action? PreparePriorityAction(
        OptimizationA1ActionPreview action,
        ProcessResourcePolicySnapshot before,
        ProcessResourcePolicyBatchFields field,
        int originalIndex)
    {
        if (PriorityRank(before.PriorityClass) >= PriorityRank(A1Priority))
        {
            return null;
        }

        return new PreparedA1Action(
            originalIndex,
            action,
            field,
            before.PriorityClass,
            A1Priority,
            before.PriorityClass,
            "High / A1 目标优先级",
            PriorityClass: A1Priority);
    }

    private static PreparedA1Action? PreparePowerThrottlingAction(
        OptimizationA1ActionPreview action,
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

        return new PreparedA1Action(
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

    private static ProcessResourcePolicyBatchFields ResolveBatchField(string kind)
    {
        return kind switch
        {
            OptimizationA1ActionKinds.RaiseProcessPriority => ProcessResourcePolicyBatchFields.PriorityClass,
            OptimizationA1ActionKinds.DisableExecutionSpeedThrottling => ProcessResourcePolicyBatchFields.PowerThrottling,
            _ => ProcessResourcePolicyBatchFields.None
        };
    }

    private sealed class PreparedA1ProcessBatch(ProcessResourcePolicySnapshot? process)
    {
        public ProcessResourcePolicySnapshot? Process { get; } = process;

        public ProcessResourcePolicyBatchFields Fields { get; private set; }

        public string? PriorityClass { get; private set; }

        public uint? PowerControlMask { get; private set; }

        public uint? PowerStateMask { get; private set; }

        public List<PreparedA1Action> Actions { get; } = [];

        public void Add(PreparedA1Action action)
        {
            Fields |= action.Field;
            PriorityClass ??= action.PriorityClass;
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
                PowerControlMask: PowerControlMask,
                PowerStateMask: PowerStateMask);
        }
    }

    private sealed record PreparedA1Action(
        int OriginalIndex,
        OptimizationA1ActionPreview Action,
        ProcessResourcePolicyBatchFields Field,
        string PreviousRawValue,
        string AppliedRawValue,
        string PreviousDisplayValue,
        string AppliedDisplayValue,
        string? PriorityClass = null,
        uint? PowerControlMask = null,
        uint? PowerStateMask = null);

    private sealed record IndexedA1AppliedAction(
        int OriginalIndex,
        OptimizationA1AppliedAction Action);

    private OptimizationA1AppliedAction RestoreAction(
        OptimizationA1AppliedAction action,
        DateTimeOffset now)
    {
        return action.Kind switch
        {
            OptimizationA1ActionKinds.RaiseProcessPriority => RestorePriorityAction(action, now),
            OptimizationA1ActionKinds.DisableExecutionSpeedThrottling => RestorePowerThrottlingAction(action, now),
            OptimizationA1ActionKinds.PreferHighPerformanceGpu => legacyGpuPreferenceActionRestorer.Restore(action, now),
            _ => action with { Message = "未知 A1 增强动作，跳过恢复。" }
        };
    }

    private OptimizationA1AppliedAction RestorePriorityAction(
        OptimizationA1AppliedAction action,
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
                State = OptimizationA1RecordStates.Restored,
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
            ? action with { RestoredAt = now, State = OptimizationA1RecordStates.Restored, Message = "已恢复进程优先级。" }
            : action with { Message = result.Message };
    }

    private OptimizationA1AppliedAction RestorePowerThrottlingAction(
        OptimizationA1AppliedAction action,
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
                State = OptimizationA1RecordStates.Restored,
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
            ? action with { RestoredAt = now, State = OptimizationA1RecordStates.Restored, Message = "已恢复进程节流状态。" }
            : action with { Message = result.Message };
    }
}
