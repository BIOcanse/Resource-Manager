using System.ComponentModel;
using ResourceManager.App.Domain.Optimization;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class D3d11ProxyShimRuntime
{
    // Capture has no write side effect. The placement owner persists the record before apply.
    internal HostManagerAppliedRecord? CapturePolicyRecord(string targetId, byte[] appliedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(appliedValue);
        var previous = ReadPolicy(targetId);
        return EqualPolicyBytes(previous, appliedValue)
            ? null
            : GpuShimPolicyRecord.Create(targetId, previous, appliedValue);
    }

    internal bool TryApplyPolicyRecord(HostManagerAppliedRecord record)
    {
        if (!GpuShimPolicyRecord.TryRead(record, out var policy))
            throw new InvalidDataException("GPU shim policy receipt is invalid.");
        return TryWritePolicy(policy.TargetId, policy.PreviousValue, policy.AppliedValue);
    }

    internal HostManagerPlacementRecordSettlement RestorePolicyRecord(HostManagerAppliedRecord record)
    {
        if (!GpuShimPolicyRecord.TryRead(record, out var policy))
            return new(record, HostManagerPlacementSettlementKind.InvalidReceipt, 0,
                "GPU shim policy receipt is invalid.");

        try
        {
            var current = ReadPolicy(policy.TargetId);
            if (EqualPolicyBytes(current, policy.PreviousValue))
                return new(record, HostManagerPlacementSettlementKind.AlreadyRestored, 0,
                    "GPU shim policy already has its previous value.");
            if (!EqualPolicyBytes(current, policy.AppliedValue))
                return new(record, HostManagerPlacementSettlementKind.OwnershipLost, 0,
                    "GPU shim policy was changed outside this record.");

            var wrote = TryWritePolicy(policy.TargetId, policy.AppliedValue, policy.PreviousValue);
            var readBack = ReadPolicy(policy.TargetId);
            if (EqualPolicyBytes(readBack, policy.PreviousValue))
                return new(record, wrote ? HostManagerPlacementSettlementKind.Restored
                    : HostManagerPlacementSettlementKind.AlreadyRestored, 0,
                    "GPU shim policy previous value was read back.");
            return EqualPolicyBytes(readBack, policy.AppliedValue)
                ? new(record, HostManagerPlacementSettlementKind.RetryableFailure, 0,
                    "GPU shim policy still has the applied value.")
                : new(record, HostManagerPlacementSettlementKind.OwnershipLost, 0,
                    "GPU shim policy read-back found another value.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(record, HostManagerPlacementSettlementKind.RetryableFailure,
                ex.InnerException is Win32Exception native ? native.NativeErrorCode : 0, ex.Message);
        }
    }
}
