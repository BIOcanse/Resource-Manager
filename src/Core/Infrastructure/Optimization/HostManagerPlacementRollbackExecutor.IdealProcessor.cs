using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed partial class HostManagerPlacementRollbackExecutor
{
    private HostManagerPlacementRecordSettlement RestoreIdealProcessor(HostManagerAppliedRecord record)
    {
        if (!ThreadIdealProcessorPlacementRecord.TryRead(record, out var value))
            return Settlement(record, HostManagerPlacementSettlementKind.InvalidReceipt, "Invalid thread ideal processor receipt.");
        var before = processPolicyWriter.ReadThreadIdealProcessor(value.ProcessId, value.ProcessStartKey, value.ThreadId);
        if (before.Status == RecoveryReadStatus.Unavailable)
            return FromUnavailable(record, before.NativeErrorCode, before.Message);
        if (before.Status == RecoveryReadStatus.NotFoundOrExited)
            return FromAbsent(record, before);
        if (before.Status != RecoveryReadStatus.Found || before.Value is not { } current || !value.Matches(current))
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Thread creation identity no longer matches.");
        if (current.IdealProcessor == value.Previous)
            return Settlement(record, HostManagerPlacementSettlementKind.AlreadyRestored, "Thread ideal processor already equals the original hint.");
        if (current.IdealProcessor != value.Applied)
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Another owner changed the ideal processor.");
        if (current.AllowedGroup != value.Previous.Group || (current.AllowedMask & (1UL << value.Previous.Number)) == 0)
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "External affinity excludes the original hint; its restriction is preserved.");
        var write = processPolicyWriter.TrySetThreadIdealProcessor(current, value.Previous);
        var after = processPolicyWriter.ReadThreadIdealProcessor(value.ProcessId, value.ProcessStartKey, value.ThreadId);
        if (after.Status == RecoveryReadStatus.Unavailable) return FromUnavailable(record, after.NativeErrorCode, after.Message);
        if (after.Status == RecoveryReadStatus.NotFoundOrExited)
            return FromAbsent(record, after);
        if (after.Status != RecoveryReadStatus.Found || after.Value is not { } observed || !value.Matches(observed))
            return Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Thread disappeared during restoration.");
        if (observed.IdealProcessor == value.Previous)
            return Settlement(record, HostManagerPlacementSettlementKind.Restored, "Original ideal processor restored and read back.");
        return observed.IdealProcessor == value.Applied
            ? Settlement(record, HostManagerPlacementSettlementKind.RetryableFailure, write.Message)
            : Settlement(record, HostManagerPlacementSettlementKind.OwnershipLost, "Another owner changed the ideal processor during restoration.");
    }
}
