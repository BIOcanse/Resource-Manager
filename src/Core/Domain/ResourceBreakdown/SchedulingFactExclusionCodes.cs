using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.ResourceBreakdown;

// These codes describe producer facts, never display text or adapter identities.
[JsonConverter(typeof(SchedulingFactCodeConverter))]
public enum SchedulingFactCode
{
    [JsonStringEnumMemberName("inventory")] Inventory = 1,
    [JsonStringEnumMemberName("identity")] Identity,
    [JsonStringEnumMemberName("attribution")] Attribution,
    [JsonStringEnumMemberName("base-score")] BaseScore,
    [JsonStringEnumMemberName("cpu-usage")] CpuUsage,
    [JsonStringEnumMemberName("memory-usage")] MemoryUsage,
    [JsonStringEnumMemberName("gpu-usage")] GpuUsage,
    [JsonStringEnumMemberName("gpu-dedicated-memory")] GpuDedicatedMemory,
    [JsonStringEnumMemberName("gpu-private-memory-bytes")] GpuPrivateMemoryBytes,
    [JsonStringEnumMemberName("gpu-shared-memory-bytes")] GpuSharedMemoryBytes,
    [JsonStringEnumMemberName("runtime-state")] RuntimeState,
    [JsonStringEnumMemberName("cpu-core-residency")] CpuCoreResidency,
    [JsonStringEnumMemberName("compute-score")] ComputeScore,
    [JsonStringEnumMemberName("cpu-score")] CpuScore,
    [JsonStringEnumMemberName("gpu-score")] GpuScore,
    [JsonStringEnumMemberName("decision-target")] DecisionTarget,
    [JsonStringEnumMemberName("memory-cleanup")] MemoryCleanup,
    [JsonStringEnumMemberName("native-plan")] NativePlan
}

[JsonConverter(typeof(SchedulingFactExclusionReasonConverter))]
public enum SchedulingFactExclusionReason
{
    [JsonStringEnumMemberName("unidentified-live-process")] UnidentifiedLiveProcess = 1,
    [JsonStringEnumMemberName("dataset-not-current")] DatasetNotCurrent,
    [JsonStringEnumMemberName("inventory-overflow")] InventoryOverflow,
    [JsonStringEnumMemberName("emitted-count-mismatch")] EmittedCountMismatch,
    [JsonStringEnumMemberName("inventory-count-mismatch")] InventoryCountMismatch,
    [JsonStringEnumMemberName("capture-count-mismatch")] CaptureCountMismatch,
    [JsonStringEnumMemberName("invalid-or-duplicate-inventory-identity")] InvalidOrDuplicateInventoryIdentity,
    [JsonStringEnumMemberName("invalid-or-duplicate-process")] InvalidOrDuplicateProcess,
    [JsonStringEnumMemberName("row-missing-or-invalid")] RowMissingOrInvalid,
    [JsonStringEnumMemberName("gpu-row-missing-or-invalid")] GpuRowMissingOrInvalid,
    [JsonStringEnumMemberName("target-process-count-not-one")] TargetProcessCountNotOne,
    [JsonStringEnumMemberName("target-process-identity-missing")] TargetProcessIdentityMissing,
    [JsonStringEnumMemberName("target-process-identity-mismatch")] TargetProcessIdentityMismatch,
    [JsonStringEnumMemberName("duplicate-target-process-identity")] DuplicateTargetProcessIdentity,
    [JsonStringEnumMemberName("target-runtime-state-invalid")] TargetRuntimeStateInvalid,
    [JsonStringEnumMemberName("compute-runtime-state-missing")] ComputeRuntimeStateMissing,
    [JsonStringEnumMemberName("cpu-residency-not-covered")] CpuResidencyNotCovered,
    [JsonStringEnumMemberName("cpu-residency-invalid")] CpuResidencyInvalid,
    [JsonStringEnumMemberName("ownership-software-attribution-mismatch")] OwnershipSoftwareAttributionMismatch,
    [JsonStringEnumMemberName("canonical-process-membership-mismatch")] CanonicalProcessMembershipMismatch,
    [JsonStringEnumMemberName("canonical-software-score-missing")] CanonicalSoftwareScoreMissing,
    [JsonStringEnumMemberName("live-identity-without-decision-facts")] LiveIdentityWithoutDecisionFacts,
    [JsonStringEnumMemberName("canonical-process-not-projected")] CanonicalProcessNotProjected,
    [JsonStringEnumMemberName("canonical-software-membership-incomplete")] CanonicalSoftwareMembershipIncomplete,
    [JsonStringEnumMemberName("cleanup-baseline-score-missing")] CleanupBaselineScoreMissing,
    [JsonStringEnumMemberName("cleanup-current-process-facts-missing")] CleanupCurrentProcessFactsMissing,
    [JsonStringEnumMemberName("cleanup-software-maximum-base-score-missing")] CleanupSoftwareMaximumBaseScoreMissing,
    [JsonStringEnumMemberName("invalid-argument")] InvalidArgument,
    [JsonStringEnumMemberName("abi-mismatch")] AbiMismatch,
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("no-data")] NoData,
    [JsonStringEnumMemberName("buffer-too-small")] BufferTooSmall,
    [JsonStringEnumMemberName("stale-generation")] StaleGeneration,
    [JsonStringEnumMemberName("out-of-memory")] OutOfMemory,
    [JsonStringEnumMemberName("capacity-exceeded")] CapacityExceeded,
    [JsonStringEnumMemberName("conflicting-facts")] ConflictingFacts,
    [JsonStringEnumMemberName("feedback-mismatch")] FeedbackMismatch,
    [JsonStringEnumMemberName("recreate-required")] RecreateRequired
}

// Numeric/undefined enum values cannot escape as wire codes.
public sealed class SchedulingFactCodeConverter() : JsonStringEnumConverter<SchedulingFactCode>(allowIntegerValues: false) { }
public sealed class SchedulingFactExclusionReasonConverter() : JsonStringEnumConverter<SchedulingFactExclusionReason>(allowIntegerValues: false) { }
