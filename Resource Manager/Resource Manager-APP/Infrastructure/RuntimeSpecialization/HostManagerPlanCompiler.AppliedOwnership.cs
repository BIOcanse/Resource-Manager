using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private const uint AppliedOwnershipAbiVersion = 0x0002_0000;
    private const long AppliedOwnershipImageHeaderBytes = 128;
    private const long AppliedOwnershipRecordBytes = 320;

    private static CompiledHostManagerAppliedOwnershipRecreatePlan CompileAppliedOwnershipRecreate(
        HostManagerAppliedOwnershipRecreateProfile source,
        CompiledHostManagerCapacityLimits limits)
    {
        ValidateCapacity(
            source.RecordCapacity,
            limits.AppliedOwnershipRecordCapacity,
            "host_recreate.applied_ownership.record_capacity");
        ValidateIndexCapacity(
            source.PrimaryIndexCapacity,
            source.RecordCapacity,
            limits.AppliedOwnershipPrimaryIndexCapacity,
            "host_recreate.applied_ownership.primary_index_capacity");
        ValidateIndexCapacity(
            source.PayloadIndexCapacity,
            source.RecordCapacity,
            limits.AppliedOwnershipPayloadIndexCapacity,
            "host_recreate.applied_ownership.payload_index_capacity");
        ValidatePositive(
            source.ResidentByteBudget,
            "host_recreate.applied_ownership.resident_byte_budget");
        ValidateMaximum(
            source.ResidentByteBudget,
            limits.AppliedOwnershipResidentByteBudget,
            "host_recreate.applied_ownership.resident_byte_budget");
        ValidatePositive(
            source.ImageByteBudget,
            "host_recreate.applied_ownership.image_byte_budget");
        ValidateMaximum(
            source.ImageByteBudget,
            limits.AppliedOwnershipImageByteBudget,
            "host_recreate.applied_ownership.image_byte_budget");
        ValidateImageBudget(
            source.ImageByteBudget,
            source.RecordCapacity,
            "host_recreate.applied_ownership.image_byte_budget");

        return new CompiledHostManagerAppliedOwnershipRecreatePlan(
            source.RecordCapacity,
            source.PrimaryIndexCapacity,
            source.PayloadIndexCapacity,
            source.ResidentByteBudget,
            source.ImageByteBudget,
            NormalizeStrictRelativePath(
                source.LedgerRelativePath,
                "host_recreate.applied_ownership.ledger_relative_path"));
    }

    private static CompiledHostManagerAppliedOwnershipHotPublishPlan CompileAppliedOwnershipHotPublish(
        HostManagerAppliedOwnershipHotPublishProfile source,
        int profileRevision)
    {
        ValidateNonNegative(
            source.MaximumFutureSkewMilliseconds,
            "hot_publish.applied_ownership.maximum_future_skew_ms");
        ValidatePositive(
            source.PersistenceRetryDelayMilliseconds,
            "hot_publish.applied_ownership.persistence_retry_delay_ms");
        ValidatePositive(
            source.RecoveryDeadlineMilliseconds,
            "hot_publish.applied_ownership.recovery_deadline_ms");
        ValidatePositive(
            source.ShutdownDrainTimeoutMilliseconds,
            "hot_publish.applied_ownership.shutdown_drain_timeout_ms");

        return new CompiledHostManagerAppliedOwnershipHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.MaximumFutureSkewMilliseconds,
            source.PersistenceRetryDelayMilliseconds,
            source.RecoveryDeadlineMilliseconds,
            source.ShutdownDrainTimeoutMilliseconds);
    }

    private static void ValidateAppliedOwnershipBuildLimits(
        HostManagerCapacityLimitsProfile limits)
    {
        ValidatePositive(
            limits.AppliedOwnershipRecordCapacity,
            "build_specialize.capacity_limits.applied_ownership_record_capacity");
        ValidateIndexCapacity(
            limits.AppliedOwnershipPrimaryIndexCapacity,
            limits.AppliedOwnershipRecordCapacity,
            limits.AppliedOwnershipPrimaryIndexCapacity,
            "build_specialize.capacity_limits.applied_ownership_primary_index_capacity");
        ValidateIndexCapacity(
            limits.AppliedOwnershipPayloadIndexCapacity,
            limits.AppliedOwnershipRecordCapacity,
            limits.AppliedOwnershipPayloadIndexCapacity,
            "build_specialize.capacity_limits.applied_ownership_payload_index_capacity");
        ValidatePositive(
            limits.AppliedOwnershipResidentByteBudget,
            "build_specialize.capacity_limits.applied_ownership_resident_byte_budget");
        ValidatePositive(
            limits.AppliedOwnershipImageByteBudget,
            "build_specialize.capacity_limits.applied_ownership_image_byte_budget");
        ValidateImageBudget(
            limits.AppliedOwnershipImageByteBudget,
            limits.AppliedOwnershipRecordCapacity,
            "build_specialize.capacity_limits.applied_ownership_image_byte_budget");
    }

    private static void ValidateAppliedOwnershipPathIsolation(
        CompiledHostManagerAppliedOwnershipRecreatePlan appliedOwnership,
        CompiledHostManagerTransactionJournalRecreatePlan transactionJournal)
    {
        ValidateDisjointPaths(
            appliedOwnership.LedgerRelativePath,
            transactionJournal.JournalRelativePath);
        ValidateDisjointPaths(
            appliedOwnership.LedgerRelativePath,
            transactionJournal.PayloadRelativeDirectory);
    }

    private static void ValidateIndexCapacity(
        int value,
        int recordCapacity,
        int maximum,
        string path)
    {
        ValidateCapacity(value, maximum, path);
        if (value < recordCapacity || (value & (value - 1)) != 0)
        {
            throw new InvalidDataException(
                $"{path} must be a power of two and at least the record capacity.");
        }
    }

    private static void ValidateImageBudget(long value, int recordCapacity, string path)
    {
        var minimum = checked(
            AppliedOwnershipImageHeaderBytes +
            AppliedOwnershipRecordBytes * recordCapacity);
        if (value < minimum)
        {
            throw new InvalidDataException(
                $"{path} must be at least {minimum} bytes for the configured record capacity.");
        }
    }
}
