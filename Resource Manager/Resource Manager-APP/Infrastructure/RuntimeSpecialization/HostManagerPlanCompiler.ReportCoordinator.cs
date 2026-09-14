using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerReportCoordinatorBuildPlan CompileReportCoordinatorBuild(
        uint abiVersion,
        HostManagerReportCoordinatorCapacityProfile source,
        CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativeReportCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.report_coordinator_abi_version does not match the binary.");
        }

        return new CompiledHostManagerReportCoordinatorBuildPlan(
            abiVersion,
            "report_coordinator",
            binary.FileName,
            binary.Sha256,
            CompileReportCoordinatorCapacity(
                source,
                "build_specialize.capacity_limits.report_coordinator"));
    }

    private static CompiledHostManagerReportCoordinatorRecreatePlan CompileReportCoordinatorRecreate(
        HostManagerReportCoordinatorRecreateProfile source,
        CompiledHostManagerReportCoordinatorBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompileReportCoordinatorCapacity(
            source.Capacity ?? throw Missing("host_recreate.report_coordinator.capacity"),
            "host_recreate.report_coordinator.capacity");
        var limit = build.CapacityLimits;
        ValidateMaximum(capacity.MaximumSourceCount, limit.MaximumSourceCount, "host_recreate.report_coordinator.capacity.maximum_source_count");
        ValidateMaximum(capacity.MaximumRuleCount, limit.MaximumRuleCount, "host_recreate.report_coordinator.capacity.maximum_rule_count");
        ValidateMaximum(capacity.MaximumObservationCount, limit.MaximumObservationCount, "host_recreate.report_coordinator.capacity.maximum_observation_count");
        ValidateMaximum(capacity.MaximumReportCount, limit.MaximumReportCount, "host_recreate.report_coordinator.capacity.maximum_report_count");
        ValidateMaximum(capacity.MaximumTrustCount, limit.MaximumTrustCount, "host_recreate.report_coordinator.capacity.maximum_trust_count");
        ValidateMaximum(capacity.MaximumBucketCount, limit.MaximumBucketCount, "host_recreate.report_coordinator.capacity.maximum_bucket_count");
        ValidateMaximum(capacity.MaximumPersistenceOperationCount, limit.MaximumPersistenceOperationCount, "host_recreate.report_coordinator.capacity.maximum_persistence_operation_count");
        ValidateMaximum(capacity.MaximumReportOutputCount, limit.MaximumReportOutputCount, "host_recreate.report_coordinator.capacity.maximum_report_output_count");
        ValidateMaximum(capacity.MaximumRollingObservationCount, limit.MaximumRollingObservationCount, "host_recreate.report_coordinator.capacity.maximum_rolling_observation_count");
        ValidateMaximum(capacity.SourceIndexCapacity, limit.SourceIndexCapacity, "host_recreate.report_coordinator.capacity.source_index_capacity");
        ValidateMaximum(capacity.RuleIndexCapacity, limit.RuleIndexCapacity, "host_recreate.report_coordinator.capacity.rule_index_capacity");
        ValidateMaximum(capacity.ObservationIndexCapacity, limit.ObservationIndexCapacity, "host_recreate.report_coordinator.capacity.observation_index_capacity");
        ValidateMaximum(capacity.ReportIndexCapacity, limit.ReportIndexCapacity, "host_recreate.report_coordinator.capacity.report_index_capacity");
        ValidateMaximum(capacity.TrustIndexCapacity, limit.TrustIndexCapacity, "host_recreate.report_coordinator.capacity.trust_index_capacity");
        ValidateMaximum(capacity.BucketIndexCapacity, limit.BucketIndexCapacity, "host_recreate.report_coordinator.capacity.bucket_index_capacity");
        ValidateMaximum(capacity.PlannedPersistenceIndexCapacity, limit.PlannedPersistenceIndexCapacity, "host_recreate.report_coordinator.capacity.planned_persistence_index_capacity");
        var rules = CompileReportCoordinatorRules(
            source.Rules
                ?? throw Missing("host_recreate.report_coordinator.rules"),
            capacity.MaximumRuleCount);
        return new CompiledHostManagerReportCoordinatorRecreatePlan(capacity, rules);
    }

    private static CompiledHostManagerReportCoordinatorHotPublishPlan CompileReportCoordinatorHotPublish(
        HostManagerReportCoordinatorHotPublishProfile source,
        int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.BucketWidthMilliseconds, "hot_publish.report_coordinator.bucket_width_ms");
        ValidatePositive(source.Window24HoursMilliseconds, "hot_publish.report_coordinator.window_24h_ms");
        ValidatePositive(source.Window7DaysMilliseconds, "hot_publish.report_coordinator.window_7d_ms");
        if (source.MaximumFutureSkewMilliseconds < 0)
        {
            throw new InvalidDataException(
                "hot_publish.report_coordinator.maximum_future_skew_ms must be non-negative.");
        }

        ValidatePositive(source.DefaultStaleAfterMilliseconds, "hot_publish.report_coordinator.default_stale_after_ms");
        ValidatePositive(source.DefaultRetentionMilliseconds, "hot_publish.report_coordinator.default_retention_ms");
        ValidatePositive(source.MetadataCheckpointIntervalMilliseconds, "hot_publish.report_coordinator.metadata_checkpoint_interval_ms");
        ValidatePositive(source.ResidentByteBudget, "hot_publish.report_coordinator.resident_byte_budget");
        if (source.Window7DaysMilliseconds <= source.Window24HoursMilliseconds
            || source.Window24HoursMilliseconds % source.BucketWidthMilliseconds != 0
            || source.Window7DaysMilliseconds % source.BucketWidthMilliseconds != 0
            || source.DefaultRetentionMilliseconds < source.DefaultStaleAfterMilliseconds)
        {
            throw new InvalidDataException(
                "hot_publish.report_coordinator violates the published time-window relations.");
        }

        return new CompiledHostManagerReportCoordinatorHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.BucketWidthMilliseconds,
            source.Window24HoursMilliseconds,
            source.Window7DaysMilliseconds,
            source.MaximumFutureSkewMilliseconds,
            source.DefaultStaleAfterMilliseconds,
            source.DefaultRetentionMilliseconds,
            source.MetadataCheckpointIntervalMilliseconds,
            source.ResidentByteBudget);
    }

    private static CompiledHostManagerReportCoordinatorPlan CompileReportCoordinatorPlan(
        CompiledHostManagerReportCoordinatorBuildPlan build,
        CompiledHostManagerReportCoordinatorRecreatePlan recreate,
        CompiledHostManagerReportCoordinatorHotPublishPlan hotPublish)
    {
        var bucketsPerRollingObservation = checked(
            hotPublish.Window7DaysMilliseconds / hotPublish.BucketWidthMilliseconds + 1);
        if (checked((long)recreate.Capacity.MaximumRollingObservationCount
                * bucketsPerRollingObservation) > recreate.Capacity.MaximumBucketCount)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.capacity.maximum_bucket_count cannot cover every rolling observation window.");
        }

        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerReportCoordinatorPlan(
            build,
            recreate,
            hotPublish,
            hotPublish.ConfigurationGeneration,
            configurationSha256);
    }

    private static CompiledHostManagerReportCoordinatorCapacityPlan CompileReportCoordinatorCapacity(
        HostManagerReportCoordinatorCapacityProfile source,
        string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.MaximumSourceCount, $"{path}.maximum_source_count");
        ValidatePositive(source.MaximumRuleCount, $"{path}.maximum_rule_count");
        ValidatePositive(source.MaximumObservationCount, $"{path}.maximum_observation_count");
        ValidatePositive(source.MaximumReportCount, $"{path}.maximum_report_count");
        ValidatePositive(source.MaximumTrustCount, $"{path}.maximum_trust_count");
        ValidatePositive(source.MaximumBucketCount, $"{path}.maximum_bucket_count");
        ValidatePositive(source.MaximumPersistenceOperationCount, $"{path}.maximum_persistence_operation_count");
        ValidatePositive(source.MaximumReportOutputCount, $"{path}.maximum_report_output_count");
        ValidatePositive(source.MaximumRollingObservationCount, $"{path}.maximum_rolling_observation_count");
        ValidatePositive(source.SourceIndexCapacity, $"{path}.source_index_capacity");
        ValidatePositive(source.RuleIndexCapacity, $"{path}.rule_index_capacity");
        ValidatePositive(source.ObservationIndexCapacity, $"{path}.observation_index_capacity");
        ValidatePositive(source.ReportIndexCapacity, $"{path}.report_index_capacity");
        ValidatePositive(source.TrustIndexCapacity, $"{path}.trust_index_capacity");
        ValidatePositive(source.BucketIndexCapacity, $"{path}.bucket_index_capacity");
        ValidatePositive(source.PlannedPersistenceIndexCapacity, $"{path}.planned_persistence_index_capacity");

        var result = new CompiledHostManagerReportCoordinatorCapacityPlan(
            source.MaximumSourceCount,
            source.MaximumRuleCount,
            source.MaximumObservationCount,
            source.MaximumReportCount,
            source.MaximumTrustCount,
            source.MaximumBucketCount,
            source.MaximumPersistenceOperationCount,
            source.MaximumReportOutputCount,
            source.MaximumRollingObservationCount,
            source.SourceIndexCapacity,
            source.RuleIndexCapacity,
            source.ObservationIndexCapacity,
            source.ReportIndexCapacity,
            source.TrustIndexCapacity,
            source.BucketIndexCapacity,
            source.PlannedPersistenceIndexCapacity);
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                $"{path} violates the published report coordinator capacity relations.");
        }

        return result;
    }

    private static ImmutableArray<CompiledHostManagerReportCoordinatorRulePlan> CompileReportCoordinatorRules(
        IReadOnlyList<HostManagerReportCoordinatorRuleProfile> source,
        int maximumRuleCount)
    {
        if (source.Count is 0 || source.Count > maximumRuleCount)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules must be non-empty and fit maximum_rule_count.");
        }

        var result = ImmutableArray.CreateBuilder<CompiledHostManagerReportCoordinatorRulePlan>(
            source.Count);
        var handles = new HashSet<ulong>();
        ulong previousGroupHandle = 0;
        uint expectedPredicateIndex = 0;
        CompiledHostManagerReportCoordinatorRulePlan? groupPrimary = null;
        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item);
            var rule = new CompiledHostManagerReportCoordinatorRulePlan(
                item.FactKind,
                item.RuleHandle,
                HostManagerPlanIdentity.CreateGeneration(item),
                item.SourceHandle,
                item.CoverageScopeHandle,
                item.FamilyHandle,
                item.ReportTypeHandle,
                item.ResourceKindHandle,
                item.PayloadHandle,
                item.MetricSelector,
                item.Comparison,
                item.Priority,
                item.Severity,
                item.RequiredConsecutiveHits,
                item.RequiredConsecutiveMisses,
                item.MinimumSampleDurationMilliseconds,
                item.ActivationThreshold,
                item.ClearThreshold,
                item.StaleAfterMilliseconds,
                item.RetentionMilliseconds,
                item.Rolling,
                item.PredicateGroupHandle,
                item.PredicateIndex,
                item.PredicateCount);
            ValidateReportCoordinatorRule(rule);
            if (!handles.Add(rule.RuleHandle))
            {
                throw new InvalidDataException(
                    "host_recreate.report_coordinator.rules contains a duplicate rule_handle.");
            }

            if (rule.PredicateGroupHandle != previousGroupHandle)
            {
                if (groupPrimary is not null
                    && expectedPredicateIndex != groupPrimary.PredicateCount)
                {
                    throw new InvalidDataException(
                        "host_recreate.report_coordinator.rules contains an incomplete predicate group.");
                }

                if (rule.PredicateGroupHandle <= previousGroupHandle || rule.PredicateIndex != 0)
                {
                    throw new InvalidDataException(
                        "host_recreate.report_coordinator.rules predicate groups must be strictly ordered and begin at index zero.");
                }

                previousGroupHandle = rule.PredicateGroupHandle;
                expectedPredicateIndex = 0;
                groupPrimary = rule;
            }

            if (groupPrimary is null
                || rule.PredicateIndex != expectedPredicateIndex
                || rule.PredicateCount != groupPrimary.PredicateCount
                || rule.FamilyHandle != groupPrimary.FamilyHandle
                || rule.ReportTypeHandle != groupPrimary.ReportTypeHandle
                || rule.ResourceKindHandle != groupPrimary.ResourceKindHandle
                || rule.PayloadHandle != groupPrimary.PayloadHandle
                || rule.Priority != groupPrimary.Priority
                || rule.Severity != groupPrimary.Severity)
            {
                throw new InvalidDataException(
                    "host_recreate.report_coordinator.rules contains a non-canonical predicate group.");
            }

            expectedPredicateIndex++;
            result.Add(rule);
        }

        if (groupPrimary is null || expectedPredicateIndex != groupPrimary.PredicateCount)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules contains an incomplete predicate group.");
        }

        return result.MoveToImmutable();
    }

    private static void ValidateReportCoordinatorRule(
        CompiledHostManagerReportCoordinatorRulePlan rule)
    {
        if (!rule.IsPublished)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules contains an invalid fixed rule shape.");
        }

        var rolling = rule.MetricSelector is >= 4 and <= 13;
        if (rule.Rolling != rolling)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules rolling must match metric_selector.");
        }

        if (rule.Comparison is 6 or 7 && rule.MetricSelector != 1)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules relative comparisons require the current metric.");
        }

        if (rule.Comparison is 3 or 4 or 5
            && rule.ActivationThreshold != rule.ClearThreshold)
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules exact comparisons require identical thresholds.");
        }

        if (rule.Comparison == 5
            && (rule.ActivationThreshold <= 0
                || rule.ActivationThreshold > uint.MaxValue
                || rule.ActivationThreshold != Math.Truncate(rule.ActivationThreshold)))
        {
            throw new InvalidDataException(
                "host_recreate.report_coordinator.rules bit masks must be positive UInt32 values.");
        }
    }
}
