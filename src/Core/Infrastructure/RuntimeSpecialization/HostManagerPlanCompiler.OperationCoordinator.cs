using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerOperationCoordinatorBuildPlan CompileOperationCoordinatorBuild(
        uint abiVersion,
        HostManagerOperationCoordinatorCapacityProfile source,
        CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativeOperationCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.operation_coordinator_abi_version does not match the binary.");
        }

        var capacity = CompileOperationCoordinatorCapacity(
            source,
            "build_specialize.capacity_limits.operation_coordinator");
        if (!binary.Modules.Contains("operation_coordinator", StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The NativeCore binary identity does not publish operation_coordinator.");
        }

        return new CompiledHostManagerOperationCoordinatorBuildPlan(
            abiVersion,
            "operation_coordinator",
            capacity);
    }

    private static CompiledHostManagerOperationCoordinatorRecreatePlan CompileOperationCoordinatorRecreate(
        HostManagerOperationCoordinatorRecreateProfile source,
        CompiledHostManagerOperationCoordinatorBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompileOperationCoordinatorCapacity(
            source.Capacity
                ?? throw Missing("host_recreate.operation_coordinator.capacity"),
            "host_recreate.operation_coordinator.capacity");
        if (!capacity.FitsWithin(build.CapacityLimits))
        {
            throw new InvalidDataException(
                "host_recreate.operation_coordinator.capacity exceeds its build_specialize capacity limit.");
        }

        var canonicalEnvelopePath = NormalizeStrictRelativePath(
            source.CanonicalEnvelopeRelativePath,
            "host_recreate.operation_coordinator.canonical_envelope_relative_path");

        return new CompiledHostManagerOperationCoordinatorRecreatePlan(
            capacity,
            canonicalEnvelopePath);
    }

    private static CompiledHostManagerOperationCoordinatorHotPublishPlan CompileOperationCoordinatorHotPublish(
        HostManagerOperationCoordinatorHotPublishProfile source,
        CompiledHostManagerOperationCoordinatorRecreatePlan recreate,
        int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(
            source.MaximumGlobalRunningCount,
            "hot_publish.operation_coordinator.maximum_global_running_count");
        ValidatePositive(
            source.MaximumStartActionsPerPlan,
            "hot_publish.operation_coordinator.maximum_start_actions_per_plan");
        ValidatePositive(
            source.MaximumCancelActionsPerPlan,
            "hot_publish.operation_coordinator.maximum_cancel_actions_per_plan");
        ValidatePositive(
            source.MaximumRecoverActionsPerPlan,
            "hot_publish.operation_coordinator.maximum_recover_actions_per_plan");
        ValidateOperationCoordinatorPositive(
            source.MaximumFutureSkewMilliseconds,
            "hot_publish.operation_coordinator.maximum_future_skew_ms");
        if (source.MaximumGlobalRunningCount > recreate.Capacity.MaximumOperationCount
            || source.MaximumRecentTerminalCount > recreate.Capacity.MaximumOperationCount
            || source.MaximumStartActionsPerPlan > recreate.Capacity.MaximumActionCount
            || source.MaximumCancelActionsPerPlan > recreate.Capacity.MaximumActionCount
            || source.MaximumRecoverActionsPerPlan > recreate.Capacity.MaximumActionCount)
        {
            throw new InvalidDataException(
                "hot_publish.operation_coordinator action/running/terminal limits exceed recreate capacity.");
        }

        var sourceKinds = source.Kinds
            ?? throw Missing("hot_publish.operation_coordinator.kinds");
        if (sourceKinds.Length == 0)
        {
            throw new InvalidDataException(
                "hot_publish.operation_coordinator.kinds must not be empty.");
        }

        var kinds = ImmutableArray.CreateBuilder<CompiledHostManagerOperationKindPlan>(
            sourceKinds.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in sourceKinds)
        {
            ArgumentNullException.ThrowIfNull(kind);
            var name = kind.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || !string.Equals(name, kind.Name, StringComparison.Ordinal)
                || !names.Add(name))
            {
                throw new InvalidDataException(
                    "hot_publish.operation_coordinator.kinds names must be non-empty, normalized, and unique.");
            }
            ValidatePositive(
                kind.MaximumAttempts,
                $"hot_publish.operation_coordinator.kinds[{name}].maximum_attempts");
            ValidateOperationCoordinatorPositive(
                kind.ExecutionTimeoutMilliseconds,
                $"hot_publish.operation_coordinator.kinds[{name}].execution_timeout_ms");
            ValidateOperationCoordinatorPositive(
                kind.CancelGraceMilliseconds,
                $"hot_publish.operation_coordinator.kinds[{name}].cancel_grace_ms");
            ValidateOperationCoordinatorPositive(
                kind.TerminalRetentionMilliseconds,
                $"hot_publish.operation_coordinator.kinds[{name}].terminal_retention_ms");
            kinds.Add(new CompiledHostManagerOperationKindPlan(
                name,
                kind.Priority,
                kind.MaximumAttempts,
                kind.RetryDelayMilliseconds,
                kind.ExecutionTimeoutMilliseconds,
                kind.CancelGraceMilliseconds,
                kind.TerminalRetentionMilliseconds));
        }

        return new CompiledHostManagerOperationCoordinatorHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.MaximumGlobalRunningCount,
            source.MaximumRecentTerminalCount,
            source.MaximumStartActionsPerPlan,
            source.MaximumCancelActionsPerPlan,
            source.MaximumRecoverActionsPerPlan,
            source.MaximumFutureSkewMilliseconds,
            kinds.MoveToImmutable());
    }

    private static CompiledHostManagerOperationCoordinatorPlan CompileOperationCoordinatorPlan(
        CompiledHostManagerOperationCoordinatorBuildPlan build,
        CompiledHostManagerOperationCoordinatorRecreatePlan recreate,
        CompiledHostManagerOperationCoordinatorHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerOperationCoordinatorPlan(
            build,
            recreate,
            hotPublish,
            configurationSha256);
    }

    private static CompiledHostManagerOperationCoordinatorCapacityPlan CompileOperationCoordinatorCapacity(
        HostManagerOperationCoordinatorCapacityProfile source,
        string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.MaximumOperationCount, $"{path}.maximum_operation_count");
        ValidatePositive(source.MaximumDomainCount, $"{path}.maximum_domain_count");
        ValidatePositive(source.MaximumActionCount, $"{path}.maximum_action_count");
        ValidatePositive(source.OperationIndexCapacity, $"{path}.operation_index_capacity");
        ValidatePositive(source.DomainIndexCapacity, $"{path}.domain_index_capacity");
        ValidatePositive(source.MaximumReadCount, $"{path}.maximum_read_count");
        ValidateOperationCoordinatorPositive(
            source.MaximumPersistenceByteCount,
            $"{path}.maximum_persistence_byte_count");
        ValidateOperationCoordinatorPositive(
            source.ResidentByteBudget,
            $"{path}.resident_byte_budget");
        ValidatePositive(source.MaximumPayloadCount, $"{path}.maximum_payload_count");
        ValidateOperationCoordinatorPositive(
            source.MaximumPayloadByteCount,
            $"{path}.maximum_payload_byte_count");
        ValidatePositive(
            source.MaximumEffectReceiptCount,
            $"{path}.maximum_effect_receipt_count");
        ValidateOperationCoordinatorPositive(
            source.MaximumEnvelopeByteCount,
            $"{path}.maximum_envelope_byte_count");

        var capacity = new CompiledHostManagerOperationCoordinatorCapacityPlan(
            source.MaximumOperationCount,
            source.MaximumDomainCount,
            source.MaximumActionCount,
            source.OperationIndexCapacity,
            source.DomainIndexCapacity,
            source.MaximumReadCount,
            source.MaximumPersistenceByteCount,
            source.ResidentByteBudget,
            source.MaximumPayloadCount,
            source.MaximumPayloadByteCount,
            source.MaximumEffectReceiptCount,
            source.MaximumEnvelopeByteCount);
        var minimumPersistenceBytes = checked(
            200UL + (ulong)source.MaximumOperationCount * 400UL);
        var minimumEnvelopeBytes = checked(
            320UL
            + source.MaximumPersistenceByteCount
            + (ulong)source.MaximumPayloadCount * 128UL
            + source.MaximumPayloadByteCount
            + (ulong)source.MaximumEffectReceiptCount * 208UL
            + 32UL);
        if (!capacity.IsPublished
            || !IsOperationCoordinatorPowerOfTwo(source.OperationIndexCapacity)
            || !IsOperationCoordinatorPowerOfTwo(source.DomainIndexCapacity)
            || source.MaximumPersistenceByteCount < minimumPersistenceBytes
            || source.MaximumEnvelopeByteCount < minimumEnvelopeBytes)
        {
            throw new InvalidDataException(
                $"{path} violates the native operation-coordinator capacity contract.");
        }

        return capacity;
    }

    private static bool IsOperationCoordinatorPowerOfTwo(uint value)
        => value != 0 && (value & (value - 1)) == 0;

    private static void ValidateOperationCoordinatorPositive(ulong value, string path)
    {
        if (value == 0)
        {
            throw new InvalidDataException($"{path} must be greater than zero.");
        }
    }
}
