using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed partial class OptimizationProcessPolicyEngine
{
    public IReadOnlyList<ProcessPolicyOptimizationAppliedAction> ApplyActions(
        IReadOnlyList<ProcessPolicyOptimizationActionPreview> actions,
        DateTimeOffset now,
        string recordPrefix)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            return [];
        }

        var batchesByProcess = new Dictionary<int, PreparedProcessBatch>();
        var batches = new List<PreparedProcessBatch>();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (!batchesByProcess.TryGetValue(action.ProcessId, out var batch))
            {
                batch = new PreparedProcessBatch(
                    policyWriter.TryReadProcess(action.ProcessId));
                batchesByProcess.Add(action.ProcessId, batch);
                batches.Add(batch);
            }

            PrepareAction(batch, action, index);
        }

        var executableBatches = batches
            .Where(static batch => batch.Actions.Count > 0)
            .ToArray();
        if (executableBatches.Length == 0)
        {
            return [];
        }

        var requests = new ProcessResourcePolicyBatchRequest[executableBatches.Length];
        for (var index = 0; index < executableBatches.Length; index++)
        {
            requests[index] = executableBatches[index].CreateRequest();
        }

        var writeResults = policyWriter.TryApplyBatch(requests);
        var applied = new List<IndexedAppliedAction>(actions.Count);
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
                        "Failed to apply process policy action {ActionKind} to pid {ProcessId}: {Message}",
                        prepared.Action.Kind,
                        prepared.Action.ProcessId,
                        fieldResult?.Message ?? "批处理未返回该字段的执行结果。");
                    continue;
                }

                applied.Add(new IndexedAppliedAction(
                    prepared.OriginalIndex,
                    CreateAppliedAction(
                        batch.Process!,
                        prepared,
                        now,
                        recordPrefix,
                        fieldResult.Message)));
            }
        }

        applied.Sort(static (left, right) => left.OriginalIndex.CompareTo(right.OriginalIndex));
        return applied.Select(static item => item.Action).ToArray();
    }

    private void PrepareAction(
        PreparedProcessBatch batch,
        ProcessPolicyOptimizationActionPreview action,
        int originalIndex)
    {
        var before = batch.Process;
        if (before is null || !MatchesActionIdentity(action, before))
        {
            return;
        }

        var currentRaw = ReadRawValue(action.Kind, before);
        if (currentRaw is null || !CanApplyNow(action, before, currentRaw))
        {
            return;
        }

        var field = ResolveBatchField(action.Kind);
        if (field == ProcessResourcePolicyBatchFields.None)
        {
            logger.LogWarning(
                "Unknown process policy action {ActionKind} for pid {ProcessId}.",
                action.Kind,
                action.ProcessId);
            return;
        }

        if ((batch.Fields & field) != 0)
        {
            logger.LogWarning(
                "Duplicate process policy field {Field} for pid {ProcessId}; the first action is retained.",
                field,
                action.ProcessId);
            return;
        }

        if (!batch.TrySetValue(field, action.ProposedRawValue))
        {
            logger.LogWarning(
                "Invalid process policy value {Value} for action {ActionKind} on pid {ProcessId}.",
                action.ProposedRawValue,
                action.Kind,
                action.ProcessId);
            return;
        }

        batch.Fields |= field;
        batch.Actions.Add(new PreparedBatchAction(originalIndex, action, currentRaw, field));
    }

    private static ProcessPolicyOptimizationAppliedAction CreateAppliedAction(
        ProcessResourcePolicySnapshot before,
        PreparedBatchAction prepared,
        DateTimeOffset now,
        string recordPrefix,
        string message)
    {
        var action = prepared.Action;
        return new ProcessPolicyOptimizationAppliedAction(
            CreateStableId($"{recordPrefix}-applied|{action.Kind}|{action.ProcessId}|{now.UtcTicks}|{prepared.PreviousRawValue}"),
            action.Kind,
            before.ProcessId,
            before.ProcessName,
            before.ExecutablePath ?? action.ExecutablePath,
            before.StartedAt ?? action.ProcessStartedAt,
            FormatRawValue(action.Kind, prepared.PreviousRawValue),
            action.ProposedValue,
            prepared.PreviousRawValue,
            action.ProposedRawValue,
            now,
            null,
            ProcessPolicyOptimizationRecordStates.Active,
            message);
    }

    private static ProcessResourcePolicyBatchFields ResolveBatchField(string kind)
    {
        if (IsProcessPriorityAction(kind))
        {
            return ProcessResourcePolicyBatchFields.PriorityClass;
        }

        if (IsPowerThrottlingAction(kind))
        {
            return ProcessResourcePolicyBatchFields.PowerThrottling;
        }

        if (IsMemoryPriorityAction(kind))
        {
            return ProcessResourcePolicyBatchFields.MemoryPriority;
        }

        if (IsAffinityAction(kind))
        {
            return ProcessResourcePolicyBatchFields.AffinityMask;
        }

        return ProcessResourcePolicyBatchFields.None;
    }

    private sealed class PreparedProcessBatch(ProcessResourcePolicySnapshot? process)
    {
        public ProcessResourcePolicySnapshot? Process { get; } = process;

        public ProcessResourcePolicyBatchFields Fields { get; set; }

        public string? PriorityClass { get; private set; }

        public long? AffinityMask { get; private set; }

        public uint? MemoryPriority { get; private set; }

        public uint? PowerControlMask { get; private set; }

        public uint? PowerStateMask { get; private set; }

        public List<PreparedBatchAction> Actions { get; } = [];

        public bool TrySetValue(ProcessResourcePolicyBatchFields field, string value)
        {
            switch (field)
            {
                case ProcessResourcePolicyBatchFields.PriorityClass:
                    PriorityClass = value;
                    return true;
                case ProcessResourcePolicyBatchFields.AffinityMask:
                    if (TryParseMask(value, out var mask))
                    {
                        AffinityMask = mask;
                        return true;
                    }

                    return false;
                case ProcessResourcePolicyBatchFields.MemoryPriority:
                    if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryPriority))
                    {
                        MemoryPriority = memoryPriority;
                        return true;
                    }

                    return false;
                case ProcessResourcePolicyBatchFields.PowerThrottling:
                    if (WindowsProcessResourcePolicyWriter.TryParsePowerThrottlingRaw(
                            value,
                            out var controlMask,
                            out var stateMask))
                    {
                        PowerControlMask = controlMask;
                        PowerStateMask = stateMask;
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        public ProcessResourcePolicyBatchRequest CreateRequest()
        {
            return new ProcessResourcePolicyBatchRequest(
                ProcessId: Process!.ProcessId,
                ExpectedStartedAt: Process.StartedAt,
                PriorityClass: PriorityClass,
                AffinityMask: AffinityMask,
                MemoryPriority: MemoryPriority,
                PowerControlMask: PowerControlMask,
                PowerStateMask: PowerStateMask,
                ExpectedMemoryPriority: MemoryPriority is null
                    ? null
                    : Process.MemoryPriority);
        }
    }

    private sealed record PreparedBatchAction(
        int OriginalIndex,
        ProcessPolicyOptimizationActionPreview Action,
        string PreviousRawValue,
        ProcessResourcePolicyBatchFields Field);

    private sealed record IndexedAppliedAction(
        int OriginalIndex,
        ProcessPolicyOptimizationAppliedAction Action);
}
