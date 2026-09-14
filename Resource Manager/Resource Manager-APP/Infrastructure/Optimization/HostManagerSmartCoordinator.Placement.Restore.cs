using System.Globalization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed class HostManagerPlacementRollbackExecutor(
    IProcessResourcePolicyWriter processPolicyWriter,
    IWindowsGraphicsPreferenceStore graphicsPreferenceStore,
    D3d11ProxyShimRuntime gpuShimRuntime,
    Microsoft.Extensions.Logging.ILogger logger)
{
    internal HostManagerPlacementRecordSettlement RestoreRecord(HostManagerAppliedRecord record)
    {
        var settlement = record.Kind switch
        {
            HostManagerAppliedRecordKinds.GpuPreference => RestoreGpuPreferenceRecord(record),
            HostManagerAppliedRecordKinds.GpuShimPolicy => gpuShimRuntime.RestorePolicyRecord(record),
            HostManagerAppliedRecordKinds.GpuRuntimeRebuildTrigger => SettleRuntimeRebuildRecord(record),
            HostManagerAppliedRecordKinds.GpuRemoteCall => new(record, HostManagerPlacementSettlementKind.RetainedActionFact,
                0, "Remote-call facts remain with their original action; never replay them as restoration."),
            HostManagerAppliedRecordKinds.CpuThreadCpuSets => RestoreCpuThreadCpuSetRecord(record),
            HostManagerAppliedRecordKinds.CpuAffinity => RestoreCpuAffinityRecord(record),
            HostManagerAppliedRecordKinds.ProcessPriority => RestoreProcessPriorityRecord(record),
            HostManagerAppliedRecordKinds.ProcessMemoryPriority => RestoreProcessMemoryPriorityRecord(record),
            _ => Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "未知 placement record kind。")
        };
        if (!settlement.CanRemoveReceipt)
        {
            logger.LogWarning(
                "Host Manager retained placement record {Kind} {RecordId}: {Settlement} {Message}",
                settlement.Record.Kind,
                settlement.Record.RecordId,
                settlement.Kind,
                settlement.Message);
        }
        return settlement;
    }

    private HostManagerPlacementRecordSettlement RestoreGpuPreferenceRecord(HostManagerAppliedRecord record)
    {
        var path = ReadMetadata(record, "path");
        var hadValueToken = ReadMetadata(record, "hadValue");
        var previousValue = ReadMetadata(record, "previousValue");
        var appliedValue = ReadMetadata(record, "appliedValue");
        if (string.IsNullOrWhiteSpace(path)
            || (hadValueToken is not "true" and not "false")
            || previousValue is null
            || string.IsNullOrWhiteSpace(appliedValue))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "GPU preference receipt metadata 无效。");
        }

        var hadValue = hadValueToken == "true";
        var initial = graphicsPreferenceStore.ReadValueForRecovery(path);
        if (initial.Status == RecoveryReadStatus.Unavailable)
        {
            return FromUnavailable(record, initial.NativeErrorCode, initial.Message);
        }
        if (initial.Status == RecoveryReadStatus.NotFoundOrExited)
        {
            return Settlement(
                record,
                hadValue
                    ? HostManagerPlacementSettlementKind.OwnershipLost
                    : HostManagerPlacementSettlementKind.AlreadyRestored,
                initial.Message,
                initial.NativeErrorCode);
        }

        var current = initial.Value!;
        if (hadValue && string.Equals(current, previousValue, StringComparison.Ordinal))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "GPU preference 已是 previous 值。");
        }
        if (!string.Equals(current, appliedValue, StringComparison.Ordinal))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "GPU preference 已被其他来源修改。");
        }

        try
        {
            if (hadValue)
            {
                graphicsPreferenceStore.WriteValue(path, previousValue);
            }
            else
            {
                graphicsPreferenceStore.DeleteValue(path);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Host Manager GPU preference restore write reported failure for {ExecutablePath}.", path);
        }

        var readBack = graphicsPreferenceStore.ReadValueForRecovery(path);
        if (readBack.Status == RecoveryReadStatus.Unavailable)
        {
            return FromUnavailable(record, readBack.NativeErrorCode, readBack.Message);
        }
        if (readBack.Status == RecoveryReadStatus.NotFoundOrExited)
        {
            return Settlement(
                record,
                hadValue
                    ? HostManagerPlacementSettlementKind.OwnershipLost
                    : HostManagerPlacementSettlementKind.Restored,
                readBack.Message,
                readBack.NativeErrorCode);
        }
        if (hadValue && string.Equals(readBack.Value, previousValue, StringComparison.Ordinal))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.Restored, "GPU preference 已恢复并读回确认。");
        }
        return string.Equals(readBack.Value, appliedValue, StringComparison.Ordinal)
            ? Settlement(record, HostManagerPlacementSettlementKind.RetryableFailure, "GPU preference 写后仍是 applied 值。")
            : Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "GPU preference 写后出现第三方值。");
    }

    private HostManagerPlacementRecordSettlement RestoreCpuThreadCpuSetRecord(HostManagerAppliedRecord record)
    {
        if (!TryReadIntMetadata(record, "processId", out var processId)
            || processId <= 0
            || !TryReadIntMetadata(record, "threadId", out var threadId)
            || threadId <= 0
            || !long.TryParse(ReadMetadata(record, "threadCreatedAt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var createdAtMilliseconds)
            || createdAtMilliseconds <= 0
            || !TryParseCpuSetIds(ReadMetadata(record, "appliedCpuSetIds"), out var appliedIds)
            || !TryParseCpuSetIds(ReadMetadata(record, "previousCpuSetIds"), out var previousIds))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "Thread CPU Sets receipt metadata 无效。");
        }

        var initial = processPolicyWriter.ReadThreadPlacementStateForRecovery(processId, threadId);
        if (initial.Status == RecoveryReadStatus.Unavailable)
        {
            return FromUnavailable(record, initial.NativeErrorCode, initial.Message);
        }
        if (initial.Status == RecoveryReadStatus.NotFoundOrExited)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, initial.Message, initial.NativeErrorCode);
        }

        var current = initial.Value!;
        if (current.CreatedAt.ToUnixTimeMilliseconds() != createdAtMilliseconds)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Thread creation identity 已变化。");
        }
        if (CpuSetIdsEqual(current.CpuSetIds, previousIds))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "Thread CPU Sets 已是 previous 值。");
        }
        if (!CpuSetIdsEqual(current.CpuSetIds, appliedIds))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Thread CPU Sets 已被其他来源修改。");
        }

        _ = processPolicyWriter.TrySetThreadSelectedCpuSets(
            processId,
            threadId,
            current.CreatedAt,
            previousIds);
        var readBack = processPolicyWriter.ReadThreadPlacementStateForRecovery(processId, threadId);
        return SettleThreadReadBack(record, readBack, createdAtMilliseconds, appliedIds, previousIds);
    }

    private HostManagerPlacementRecordSettlement RestoreCpuAffinityRecord(HostManagerAppliedRecord record)
    {
        var affinityKind = ReadMetadata(record, "affinityKind");
        var fields = affinityKind?.Equals("cpu-sets", StringComparison.OrdinalIgnoreCase) == true
            ? ProcessPlacementReadFields.DefaultCpuSets
            : ProcessPlacementReadFields.Affinity;
        if (!TryReadProcessIdentity(
                record,
                out var processId,
                out var expectedStartedAt,
                out var exactStartIdentity))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "CPU affinity receipt 缺少严格 PID/start identity。");
        }

        var initial = processPolicyWriter.ReadPlacementStateForRecovery(processId, fields);
        var gate = SettleProcessReadGate(
            record,
            initial,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        var current = initial.Value!;
        if (!MatchesProcessReceiptIdentity(record, current))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "CPU affinity process identity 已变化。");
        }

        return fields == ProcessPlacementReadFields.DefaultCpuSets
            ? RestoreCpuSetRecord(
                record,
                processId,
                expectedStartedAt,
                exactStartIdentity,
                current)
            : RestoreAffinityMaskRecord(
                record,
                processId,
                expectedStartedAt,
                exactStartIdentity,
                current);
    }

    private HostManagerPlacementRecordSettlement RestoreCpuSetRecord(
        HostManagerAppliedRecord record,
        int processId,
        DateTimeOffset expectedStartedAt,
        bool exactStartIdentity,
        ProcessPlacementRecoverySnapshot current)
    {
        if (!TryParseCpuSetIds(ReadMetadata(record, "appliedCpuSetIds"), out var appliedIds)
            || !TryParseCpuSetIds(ReadMetadata(record, "previousCpuSetIds"), out var previousIds)
            || current.DefaultCpuSetIds is null)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "Process CPU Sets receipt metadata 无效。");
        }
        if (CpuSetIdsEqual(current.DefaultCpuSetIds, previousIds))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "Process CPU Sets 已是 previous 值。");
        }
        if (!CpuSetIdsEqual(current.DefaultCpuSetIds, appliedIds))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Process CPU Sets 已被其他来源修改。");
        }

        _ = processPolicyWriter.TrySetProcessDefaultCpuSets(processId, previousIds);
        var readBack = processPolicyWriter.ReadPlacementStateForRecovery(processId, ProcessPlacementReadFields.DefaultCpuSets);
        var gate = SettleProcessReadGate(
            record,
            readBack,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        return SettleValueReadBack(
            record,
            CpuSetIdsEqual(readBack.Value!.DefaultCpuSetIds!, previousIds),
            CpuSetIdsEqual(readBack.Value!.DefaultCpuSetIds!, appliedIds),
            "Process CPU Sets");
    }

    private HostManagerPlacementRecordSettlement RestoreAffinityMaskRecord(
        HostManagerAppliedRecord record,
        int processId,
        DateTimeOffset expectedStartedAt,
        bool exactStartIdentity,
        ProcessPlacementRecoverySnapshot current)
    {
        if (!TryReadMaskMetadata(record, "appliedAffinityMask", out var appliedMask)
            || !TryReadMaskMetadata(record, "previousAffinityMask", out var previousMask)
            || current.ProcessorAffinityMask is null)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "CPU affinity mask receipt metadata 无效。");
        }
        if (current.ProcessorAffinityMask == previousMask)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "CPU affinity 已是 previous 值。");
        }
        if (current.ProcessorAffinityMask != appliedMask)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "CPU affinity 已被其他来源修改。");
        }

        _ = processPolicyWriter.TrySetProcessorAffinity(processId, previousMask);
        var readBack = processPolicyWriter.ReadPlacementStateForRecovery(processId, ProcessPlacementReadFields.Affinity);
        var gate = SettleProcessReadGate(
            record,
            readBack,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        return SettleValueReadBack(
            record,
            readBack.Value!.ProcessorAffinityMask == previousMask,
            readBack.Value!.ProcessorAffinityMask == appliedMask,
            "CPU affinity");
    }

    private HostManagerPlacementRecordSettlement RestoreProcessPriorityRecord(HostManagerAppliedRecord record)
    {
        if (!TryReadProcessIdentity(
                record,
                out var processId,
                out var expectedStartedAt,
                out var exactStartIdentity)
            || string.IsNullOrWhiteSpace(ReadMetadata(record, "appliedPriorityClass"))
            || string.IsNullOrWhiteSpace(ReadMetadata(record, "previousPriorityClass")))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "Process priority receipt metadata 无效。");
        }
        var applied = ReadMetadata(record, "appliedPriorityClass")!;
        var previous = ReadMetadata(record, "previousPriorityClass")!;
        var initial = processPolicyWriter.ReadPlacementStateForRecovery(processId, ProcessPlacementReadFields.PriorityClass);
        var gate = SettleProcessReadGate(
            record,
            initial,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        if (!MatchesProcessReceiptIdentity(record, initial.Value!))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Priority process identity 已变化。");
        }
        if (string.Equals(initial.Value!.PriorityClass, previous, StringComparison.OrdinalIgnoreCase))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "Process priority 已是 previous 值。");
        }
        if (!string.Equals(initial.Value!.PriorityClass, applied, StringComparison.OrdinalIgnoreCase))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Process priority 已被其他来源修改。");
        }

        _ = processPolicyWriter.TrySetPriorityClass(processId, previous);
        var readBack = processPolicyWriter.ReadPlacementStateForRecovery(processId, ProcessPlacementReadFields.PriorityClass);
        gate = SettleProcessReadGate(
            record,
            readBack,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        return SettleValueReadBack(
            record,
            string.Equals(readBack.Value!.PriorityClass, previous, StringComparison.OrdinalIgnoreCase),
            string.Equals(readBack.Value!.PriorityClass, applied, StringComparison.OrdinalIgnoreCase),
            "Process priority");
    }

    private HostManagerPlacementRecordSettlement RestoreProcessMemoryPriorityRecord(HostManagerAppliedRecord record)
    {
        if (!TryReadProcessIdentity(
                record,
                out var processId,
                out var expectedStartedAt,
                out var exactStartIdentity)
            || !uint.TryParse(ReadMetadata(record, "appliedMemoryPriority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var applied)
            || !uint.TryParse(ReadMetadata(record, "previousMemoryPriority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var previous))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "Process memory priority receipt metadata 无效。");
        }
        var initial = processPolicyWriter.ReadPlacementStateForRecovery(processId, ProcessPlacementReadFields.MemoryPriority);
        var gate = SettleProcessReadGate(
            record,
            initial,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        if (!MatchesProcessReceiptIdentity(record, initial.Value!))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Memory priority process identity 已变化。");
        }
        if (initial.Value!.MemoryPriority == previous)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "Process memory priority 已是 previous 值。");
        }
        if (initial.Value!.MemoryPriority != applied)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Process memory priority 已被其他来源修改。");
        }

        _ = processPolicyWriter.TrySetMemoryPriority(
            processId,
            initial.Value.StartedAt,
            applied,
            previous);
        var readBack = processPolicyWriter.ReadPlacementStateForRecovery(processId, ProcessPlacementReadFields.MemoryPriority);
        gate = SettleProcessReadGate(
            record,
            readBack,
            expectedStartedAt,
            exactStartIdentity);
        if (gate is not null)
        {
            return gate;
        }
        return SettleValueReadBack(
            record,
            readBack.Value!.MemoryPriority == previous,
            readBack.Value!.MemoryPriority == applied,
            "Process memory priority");
    }

    private static HostManagerPlacementRecordSettlement? SettleProcessReadGate(
        HostManagerAppliedRecord record,
        RecoveryReadResult<ProcessPlacementRecoverySnapshot> read,
        DateTimeOffset expectedStartedAt,
        bool exactStartIdentity)
    {
        if (read.Status == RecoveryReadStatus.Unavailable)
        {
            return FromUnavailable(record, read.NativeErrorCode, read.Message);
        }
        if (read.Status == RecoveryReadStatus.NotFoundOrExited)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, read.Message, read.NativeErrorCode);
        }
        if (read.Value is null
            || (exactStartIdentity
                ? read.Value.StartedAt.ToFileTime() != expectedStartedAt.ToFileTime()
                : read.Value.StartedAt.ToUnixTimeMilliseconds()
                    != expectedStartedAt.ToUnixTimeMilliseconds()))
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Process start identity 已变化。");
        }
        return null;
    }

    private static HostManagerPlacementRecordSettlement SettleThreadReadBack(
        HostManagerAppliedRecord record,
        RecoveryReadResult<ThreadCpuSetPolicySnapshot> read,
        long expectedCreatedAtMilliseconds,
        IReadOnlyList<uint> applied,
        IReadOnlyList<uint> previous)
    {
        if (read.Status == RecoveryReadStatus.Unavailable)
        {
            return FromUnavailable(record, read.NativeErrorCode, read.Message);
        }
        if (read.Status == RecoveryReadStatus.NotFoundOrExited
            || read.Value is null
            || read.Value.CreatedAt.ToUnixTimeMilliseconds() != expectedCreatedAtMilliseconds)
        {
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, read.Message, read.NativeErrorCode);
        }
        return SettleValueReadBack(
            record,
            CpuSetIdsEqual(read.Value.CpuSetIds, previous),
            CpuSetIdsEqual(read.Value.CpuSetIds, applied),
            "Thread CPU Sets");
    }

    private static HostManagerPlacementRecordSettlement SettleValueReadBack(
        HostManagerAppliedRecord record,
        bool isPrevious,
        bool isApplied,
        string label)
    {
        return isPrevious
            ? Settlement(record, HostManagerPlacementSettlementKind.Restored, $"{label} 已恢复并读回确认。")
            : isApplied
                ? Settlement(record, HostManagerPlacementSettlementKind.RetryableFailure, $"{label} 写后仍是 applied 值。")
                : Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, $"{label} 写后出现第三方值。");
    }

    private static HostManagerPlacementRecordSettlement SettleRuntimeRebuildRecord(HostManagerAppliedRecord record)
    {
        if (GpuWindowActionRecord.IsActionFact(record))
            return Settlement(record, HostManagerPlacementSettlementKind.RetainedActionFact,
                "A recorded window action is not a repeatable restore command; its facts remain owned.");
        return record.Metadata is not null
            && !string.IsNullOrWhiteSpace(ReadMetadata(record, "assignedPositionId"))
            ? Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "GPU runtime rebuild trigger 没有持久可逆 OS 值。")
            : Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "GPU runtime rebuild trigger receipt metadata 无效。");
    }

    private static bool TryReadProcessIdentity(
        HostManagerAppliedRecord record,
        out int processId,
        out DateTimeOffset expectedStartedAt,
        out bool exactStartIdentity)
    {
        processId = 0;
        expectedStartedAt = default;
        exactStartIdentity = false;
        if (!TryReadIntMetadata(record, "processId", out processId)
            || processId <= 0)
        {
            return false;
        }

        if (ulong.TryParse(
                ReadMetadata(record, "processStartKey"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processStartKey)
            && processStartKey is > 0 and <= long.MaxValue)
        {
            try
            {
                expectedStartedAt = DateTimeOffset.FromFileTime(
                    checked((long)processStartKey));
                exactStartIdentity = true;
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        if (!long.TryParse(
                ReadMetadata(record, "processStartedAt"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var startedAtMilliseconds)
            || startedAtMilliseconds <= 0)
        {
            return false;
        }
        try
        {
            expectedStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                startedAtMilliseconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool MatchesProcessReceiptIdentity(
        HostManagerAppliedRecord record,
        ProcessPlacementRecoverySnapshot current)
    {
        var processName = ReadMetadata(record, "processName");
        if (string.IsNullOrWhiteSpace(processName)
            || !processName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var executablePath = ReadMetadata(record, "executablePath");
        return string.IsNullOrWhiteSpace(executablePath)
            || executablePath.Equals(current.ExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadMetadata(HostManagerAppliedRecord record, string key)
        => record.Metadata is not null && record.Metadata.TryGetValue(key, out var value)
            ? value
            : null;

    private static bool TryReadIntMetadata(
        HostManagerAppliedRecord record,
        string key,
        out int value)
        => int.TryParse(
            ReadMetadata(record, key),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);

    private static bool TryReadMaskMetadata(
        HostManagerAppliedRecord record,
        string key,
        out long value)
    {
        var rawValue = ReadMetadata(record, key);
        value = 0;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        if (rawValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(
                rawValue[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value)
                && value > 0;
        }

        return long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }

    private static bool TryParseCpuSetIds(string? value, out IReadOnlyList<uint> ids)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ids = [];
            return true;
        }

        var parsed = new List<uint>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                ids = [];
                return false;
            }

            parsed.Add(id);
        }

        ids = parsed.Distinct().Order().ToArray();
        return true;
    }

    private static bool CpuSetIdsEqual(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
        => left.Order().SequenceEqual(right.Order());

    private static HostManagerPlacementRecordSettlement FromUnavailable(
        HostManagerAppliedRecord record,
        int nativeErrorCode,
        string message)
        => Settlement(
            record,
            HostManagerPlacementSettlementKind.RetryableFailure,
            message,
            nativeErrorCode);

    private static HostManagerPlacementRecordSettlement Settlement(
        HostManagerAppliedRecord record,
        HostManagerPlacementSettlementKind kind,
        string message,
        int nativeErrorCode = 0)
        => new(record, kind, nativeErrorCode, message);
}

public sealed partial class HostManagerSmartCoordinator
{
    internal static string? ReadMetadata(HostManagerAppliedRecord record, string key)
        => record.Metadata is not null && record.Metadata.TryGetValue(key, out var value)
            ? value
            : null;

    private HostManagerPlacementRecordSettlement RestorePlacementRecord(HostManagerAppliedRecord record)
        => new HostManagerPlacementRollbackExecutor(
            processPolicyWriter,
            graphicsPreferenceStore,
            gpuShimRuntime,
            logger).RestoreRecord(record);

}
