using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerAdapterPrivateResourceRecreatePlan CompileAdapterPrivateResourceRecreate(
        HostManagerAdapterPrivateResourceRecreateProfile source,
        CompiledHostManagerCapacityLimits limits)
    {
        ValidatePositive(source.StateCapacity, "host_recreate.adapter_private_resource_ledger.state_capacity");
        ValidateMaximum(
            source.StateCapacity,
            limits.AdapterPrivateResourceStateCapacity,
            "host_recreate.adapter_private_resource_ledger.state_capacity");
        return new CompiledHostManagerAdapterPrivateResourceRecreatePlan(source.StateCapacity);
    }

    private static CompiledHostManagerAdapterPrivateResourceHotPublishPlan CompileAdapterPrivateResourceHotPublish(
        HostManagerAdapterPrivateResourceHotPublishProfile source)
    {
        ValidatePositive(
            source.MaximumSnapshotAgeMilliseconds,
            "hot_publish.adapter_private_resource_ledger.maximum_snapshot_age_ms");
        ValidateNonNegative(
            source.MaximumFutureClockSkewMilliseconds,
            "hot_publish.adapter_private_resource_ledger.maximum_future_clock_skew_ms");
        ValidatePositive(
            source.SettlementIntervalMilliseconds,
            "hot_publish.adapter_private_resource_ledger.settlement_interval_ms");
        ValidatePositive(
            source.RequestTimeoutMilliseconds,
            "hot_publish.adapter_private_resource_ledger.request_timeout_ms");
        ValidatePositive(
            source.MaximumResponseBytes,
            "hot_publish.adapter_private_resource_ledger.maximum_response_bytes");
        ValidatePositive(
            source.MaximumConcurrentReads,
            "hot_publish.adapter_private_resource_ledger.maximum_concurrent_reads");
        ValidatePositive(
            source.MaximumCycleDurationMilliseconds,
            "hot_publish.adapter_private_resource_ledger.maximum_cycle_duration_ms");
        if (source.MaximumCycleDurationMilliseconds < source.RequestTimeoutMilliseconds)
        {
            throw new InvalidDataException(
                "hot_publish.adapter_private_resource_ledger.maximum_cycle_duration_ms must cover at least one request timeout.");
        }
        if (source.ActiveIncrement == 0)
        {
            throw new InvalidDataException(
                "hot_publish.adapter_private_resource_ledger.active_increment must be greater than zero.");
        }
        if (source.DecayDenominator == 0 || source.DecayNumerator > source.DecayDenominator)
        {
            throw new InvalidDataException(
                "hot_publish.adapter_private_resource_ledger decay fraction must have a non-zero denominator and numerator not exceeding it.");
        }
        return new CompiledHostManagerAdapterPrivateResourceHotPublishPlan(
            source.MaximumSnapshotAgeMilliseconds,
            source.MaximumFutureClockSkewMilliseconds,
            source.SettlementIntervalMilliseconds,
            source.RequestTimeoutMilliseconds,
            source.MaximumResponseBytes,
            source.MaximumConcurrentReads,
            source.MaximumCycleDurationMilliseconds,
            source.ActiveIncrement,
            source.DecayNumerator,
            source.DecayDenominator);
    }

    private static CompiledHostManagerProcessPolicyExecutorHotPublishPlan CompileProcessPolicyExecutorHotPublish(
        HostManagerProcessPolicyExecutorHotPublishProfile source,
        CompiledHostManagerCapacityLimits limits)
    {
        ValidatePositive(
            source.MaximumBatchItems,
            "hot_publish.process_policy_executor.maximum_batch_items");
        ValidateMaximum(
            source.MaximumBatchItems,
            limits.ProcessPolicyBatchCapacity,
            "hot_publish.process_policy_executor.maximum_batch_items");
        return new CompiledHostManagerProcessPolicyExecutorHotPublishPlan(source.MaximumBatchItems);
    }

    private static CompiledHostManagerPdhCollectorRecreatePlan CompilePdhCollectorRecreate(
        HostManagerPdhCollectorRecreateProfile source)
    {
        ValidatePositive(
            source.BaselineResetIntervalMilliseconds,
            "host_recreate.pdh_collector.baseline_reset_interval_ms");
        if (source.BaselineResetIntervalMilliseconds < 1000)
        {
            throw new InvalidDataException(
                "host_recreate.pdh_collector.baseline_reset_interval_ms must be at least 1000 for the native ABI.");
        }
        return new CompiledHostManagerPdhCollectorRecreatePlan(
            source.BaselineResetIntervalMilliseconds);
    }

    private static CompiledHostManagerPdhCollectorHotPublishPlan CompilePdhCollectorHotPublish(
        HostManagerPdhCollectorHotPublishProfile source)
    {
        ValidateNonNegative(
            source.FrameReuseWindowMilliseconds,
            "hot_publish.pdh_collector.frame_reuse_window_ms");
        ValidateNonNegative(
            source.LastGoodLifetimeMilliseconds,
            "hot_publish.pdh_collector.last_good_lifetime_ms");
        return new CompiledHostManagerPdhCollectorHotPublishPlan(
            source.FrameReuseWindowMilliseconds,
            source.LastGoodLifetimeMilliseconds);
    }

    private static void ValidatePercent(double value, string path)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100)
        {
            throw new InvalidDataException($"{path} must be finite and between 0 and 100.");
        }
    }
}
