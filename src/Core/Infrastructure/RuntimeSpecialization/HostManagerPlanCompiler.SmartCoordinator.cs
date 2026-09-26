using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerSmartCoordinatorRecreatePlan CompileSmartCoordinatorRecreate(
        HostManagerSmartCoordinatorRecreateProfile source,
        CompiledHostManagerCapacityLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateCapacity(source.MaximumProcesses, limits.SmartCoordinatorProcessCapacity, "host_recreate.smart_coordinator.maximum_processes");
        ValidateCapacity(source.MaximumSoftwareGroups, limits.SmartCoordinatorSoftwareGroupCapacity, "host_recreate.smart_coordinator.maximum_software_groups");
        ValidateCapacity(source.MaximumGpuStates, limits.SmartCoordinatorGpuStateCapacity, "host_recreate.smart_coordinator.maximum_gpu_states");
        ValidateCapacity(source.MaximumInputRows, limits.SmartCoordinatorInputRowCapacity, "host_recreate.smart_coordinator.maximum_input_rows");
        ValidateCapacity(source.MaximumActions, limits.SmartCoordinatorActionCapacity, "host_recreate.smart_coordinator.maximum_actions");
        ValidateCapacity(source.MaximumReservations, limits.SmartCoordinatorReservationCapacity, "host_recreate.smart_coordinator.maximum_reservations");
        ValidateCapacity(source.MaximumAtomicGroups, limits.SmartCoordinatorAtomicGroupCapacity, "host_recreate.smart_coordinator.maximum_atomic_groups");

        var minimumActions = checked(source.MaximumProcesses + source.MaximumSoftwareGroups);
        var minimumReservations = checked(source.MaximumProcesses + (2 * source.MaximumSoftwareGroups));
        if (source.MaximumActions < minimumActions)
        {
            throw new InvalidDataException("smart_coordinator maximum_actions must cover all process and software targets.");
        }
        if (source.MaximumReservations < minimumReservations)
        {
            throw new InvalidDataException("smart_coordinator maximum_reservations must cover process and independent CPU/GPU software reservations.");
        }
        if (source.MaximumAtomicGroups < source.MaximumProcesses)
        {
            throw new InvalidDataException("smart_coordinator maximum_atomic_groups must cover all process targets.");
        }
        if (source.MaximumGpuStates < source.MaximumInputRows)
        {
            throw new InvalidDataException(
                "smart_coordinator maximum_gpu_states must cover every possible GPU identity in the input-row envelope.");
        }

        return new CompiledHostManagerSmartCoordinatorRecreatePlan(
            source.MaximumProcesses,
            source.MaximumSoftwareGroups,
            source.MaximumGpuStates,
            source.MaximumInputRows,
            source.MaximumActions,
            source.MaximumReservations,
            source.MaximumAtomicGroups);
    }

    private static CompiledHostManagerSmartCoordinatorHotPublishPlan CompileSmartCoordinatorHotPublish(
        HostManagerSmartCoordinatorHotPublishProfile source,
        CompiledHostManagerSmartCoordinatorRecreatePlan recreate,
        FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recreate);
        const string consumer = $"{nameof(HostManagerPlanCompiler)}.{nameof(CompileSmartCoordinatorHotPublish)}";
        var gameStartGrace = freedom.Consume<int>(BackendFreedomPointPaths.GameStartGrace, consumer);
        var consecutiveDecisions = freedom.Consume<int>(BackendFreedomPointPaths.ConsecutiveDecisions, consumer);
        var failureRetry = freedom.Consume<int>(BackendFreedomPointPaths.FailureRetry, consumer);
        var welfare = freedom.Consume<SoftwareWelfareDeclaration>(
            BackendFreedomPointPaths.SoftwareWelfare,
            consumer);
        if (welfare.Kind != "software_base_mean_utilization_bonus")
            throw new InvalidDataException("The software welfare formula is not implemented by this backend.");
        if (!double.IsFinite(welfare.UtilizationBaselinePercent)
            || welfare.UtilizationBaselinePercent is <= 0 or > 100)
            throw new InvalidDataException("The software welfare utilization_baseline_percent must be finite and in (0, 100].");
        ValidatePositive(source.NormalIntervalMilliseconds, "hot_publish.smart_coordinator.normal_interval_ms");
        ValidatePositive(source.EventIntervalMilliseconds, "hot_publish.smart_coordinator.event_interval_ms");
        ValidatePositive(source.EventBoostMilliseconds, "hot_publish.smart_coordinator.event_boost_ms");
        ValidatePositive(source.ReservationTimeoutMilliseconds, "hot_publish.smart_coordinator.reservation_timeout_ms");
        ValidatePositive(source.MaximumActionsPerRealtimeTick, "hot_publish.smart_coordinator.maximum_actions_per_realtime_tick");
        if (source.MaximumActionsPerRealtimeTick > recreate.MaximumActions)
        {
            throw new InvalidDataException(
                "hot_publish.smart_coordinator.maximum_actions_per_realtime_tick must not exceed host_recreate.smart_coordinator.maximum_actions.");
        }

        var process = source.ProcessPolicy
            ?? throw Missing("hot_publish.smart_coordinator.process_policy");
        var stateMultipliers = ValidateStateMultipliers(
            freedom.Consume<double[]>(BackendFreedomPointPaths.CpuStateMultipliers, consumer),
            "hot_publish.smart_coordinator.process_policy.state_multipliers");
        ValidateProcessPolicy(process);
        var baseScoreTiers = CompileBaseScoreTiers(
            freedom.Consume<HostManagerBaseScoreTierProfile>(BackendFreedomPointPaths.BaseScoreTiers, consumer));
        var cpuAdapterPolicy = CompileAdapterPolicy(
            source.CpuAdapterPolicy
                ?? throw Missing("hot_publish.smart_coordinator.cpu_adapter_policy"),
            "hot_publish.smart_coordinator.cpu_adapter_policy",
            stateMultipliers);
        var gpuAdapterProfile = source.GpuAdapterPolicy
            ?? throw Missing("hot_publish.smart_coordinator.gpu_adapter_policy");
        var gpuAdapterPolicy = CompileAdapterPolicy(
            gpuAdapterProfile,
            "hot_publish.smart_coordinator.gpu_adapter_policy",
            ValidateStateMultipliers(
                gpuAdapterProfile.StateMultipliers,
                "hot_publish.smart_coordinator.gpu_adapter_policy.state_multipliers"));
        ValidateSmartCoordinatorScoreRange(
            stateMultipliers,
            recreate.MaximumProcesses,
            gpuAdapterPolicy);

        return new CompiledHostManagerSmartCoordinatorHotPublishPlan(
            CompileSmartCoordinatorFeatureFlags(source.FeatureFlags),
            source.NormalIntervalMilliseconds,
            source.EventIntervalMilliseconds,
            source.EventBoostMilliseconds,
            gameStartGrace,
            consecutiveDecisions,
            failureRetry,
            source.ReservationTimeoutMilliseconds,
            source.MaximumActionsPerRealtimeTick,
            welfare.UtilizationBaselinePercent,
            stateMultipliers,
            baseScoreTiers,
            process.A1MinimumCpuScore,
            process.DefaultMinimumCpuScoreScale,
            process.Level1MaximumCpuScoreScale,
            process.Level2MaximumCpuScoreScale,
            process.Level3MaximumCpuScoreScale,
            process.LowTierLevel4MaximumCpuScoreScale,
            cpuAdapterPolicy,
            gpuAdapterPolicy,
            CompileMemoryModePolicy(
                source.MemoryModePolicy
                ?? throw Missing(
                    "hot_publish.smart_coordinator.memory_mode_policy"),
                recreate));
    }

    private sealed record SoftwareWelfareDeclaration(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("utilization_baseline_percent")] double UtilizationBaselinePercent);

    private static CompiledHostManagerMemoryModePolicyPlan
        CompileMemoryModePolicy(
            HostManagerMemoryModePolicyProfile source,
            CompiledHostManagerSmartCoordinatorRecreatePlan recreate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recreate);
        const string path = "hot_publish.smart_coordinator.memory_mode_policy";
        string sourceKind;
        if (!source.Enabled)
        {
            if (!string.IsNullOrEmpty(source.SourceKind)
                || source.RatioUnitsMaximum != 0
                || source.StrongBeginFreeRatioUnits != 0
                || source.NormalMinimumFreeRatioUnits != 0
                || source.UnrestrictedMinimumFreeRatioUnits != 0
                || source.AllowUnrestricted
                || !string.IsNullOrEmpty(source.ForeignMemoryPriorityDisposition)
                || source.OwnedStateVerificationIntervalCycles != 0)
            {
                throw new InvalidDataException(
                    $"{path} must be completely empty while disabled.");
            }
            sourceKind = string.Empty;
        }
        else
        {
            sourceKind = source.SourceKind;
            if (!string.Equals(
                    sourceKind,
                    HostManagerMemoryModePolicySourceKinds.ProductBaseline,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{path}.source_kind must be product-baseline.");
            }
            if (source.RatioUnitsMaximum != 10_000
                || source.StrongBeginFreeRatioUnits == 0
                || source.StrongBeginFreeRatioUnits >= source.NormalMinimumFreeRatioUnits
                || source.NormalMinimumFreeRatioUnits >=
                    source.UnrestrictedMinimumFreeRatioUnits
                || source.UnrestrictedMinimumFreeRatioUnits > source.RatioUnitsMaximum)
            {
                throw new InvalidDataException(
                    $"{path} fixed-point thresholds must be strictly ordered inside 1..10000.");
            }
            if (!string.Equals(
                    source.ForeignMemoryPriorityDisposition,
                    HostManagerMemoryPriorityForeignDispositions.Reject,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{path}.foreign_memory_priority_disposition must be reject.");
            }
            if (source.OwnedStateVerificationIntervalCycles != 1)
            {
                throw new InvalidDataException(
                    $"{path}.owned_state_verification_interval_cycles must be exactly one in the production baseline.");
            }
        }
        if (source.OptimizeMemoryPriority is < 1 or > 5)
        {
            throw new InvalidDataException(
                $"{path}.optimize_memory_priority must be between 1 and 5.");
        }
        if (source.PagedFrozenMemoryPriority is < 1 or > 5
            || source.PagedFrozenMemoryPriority > source.OptimizeMemoryPriority)
        {
            throw new InvalidDataException(
                $"{path}.paged_frozen_memory_priority must be between 1 and optimize_memory_priority.");
        }

        var canonical = string.Join(
            '\n',
            "rm-host-memory-mode-policy-config-v5",
            source.Enabled ? "1" : "0",
            sourceKind,
            source.RatioUnitsMaximum.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            source.StrongBeginFreeRatioUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            source.NormalMinimumFreeRatioUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            source.UnrestrictedMinimumFreeRatioUnits.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            source.AllowUnrestricted ? "1" : "0",
            source.OptimizeMemoryPriority.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            source.PagedFrozenMemoryPriority.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            source.ForeignMemoryPriorityDisposition,
            source.OwnedStateVerificationIntervalCycles.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            recreate.MaximumSoftwareGroups.ToString(
                System.Globalization.CultureInfo.InvariantCulture)) + "\n";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        ulong generation = 0;
        var offset = 0;
        while (offset <= digest.Length - sizeof(ulong))
        {
            generation = BinaryPrimitives.ReadUInt64LittleEndian(
                digest.AsSpan(offset, sizeof(ulong)));
            if (generation != 0)
            {
                break;
            }
            offset += sizeof(ulong);
        }
        if (generation == 0)
        {
            throw new InvalidDataException(
                $"{path} produced an invalid zero configuration generation.");
        }

        return new CompiledHostManagerMemoryModePolicyPlan(
            source.Enabled,
            sourceKind,
            source.RatioUnitsMaximum,
            source.StrongBeginFreeRatioUnits,
            source.NormalMinimumFreeRatioUnits,
            source.UnrestrictedMinimumFreeRatioUnits,
            source.AllowUnrestricted,
            source.OptimizeMemoryPriority,
            source.PagedFrozenMemoryPriority,
            source.ForeignMemoryPriorityDisposition,
            source.OwnedStateVerificationIntervalCycles,
            generation,
            Convert.ToHexString(digest));
    }

    private static CompiledHostManagerSmartCoordinatorPlan CompileSmartCoordinatorPlan(
        CompiledHostManagerSmartCoordinatorBuildPlan build,
        CompiledHostManagerSmartCoordinatorRecreatePlan recreate,
        CompiledHostManagerSmartCoordinatorHotPublishPlan hotPublish,
        CompiledDataHistoryPlan dataHistory,
        int profileRevision)
    {
        ValidatePositive(profileRevision, "profile_revision");
        var generation = (ulong)checked((uint)profileRevision) << 32;
        var projection = NativeSmartCoordinatorConfigurationWriter.Create(
            build,
            recreate,
            hotPublish,
            generation);
        return new CompiledHostManagerSmartCoordinatorPlan(
            build,
            recreate,
            hotPublish,
            generation,
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(
                in projection,
                checked((uint)hotPublish.MaximumActionsPerRealtimeTick)))
        {
            DataHistory = dataHistory
        };
    }

    private static CompiledHostManagerSmartCoordinatorAdapterPolicyPlan CompileAdapterPolicy(
        HostManagerSmartCoordinatorAdapterPolicyProfile source,
        string path,
        ImmutableArray<double> stateMultipliers)
    {
        ValidateNonNegativeFinite(source.ExtremeMinimumScore, $"{path}.extreme_minimum_score");
        ValidateNonNegativeFinite(source.NormalMinimumScore, $"{path}.normal_minimum_score");
        ValidateNonNegativeFinite(source.OptimizeMinimumScore, $"{path}.optimize_minimum_score");
        if (source.OptimizeMinimumScore > source.NormalMinimumScore
            || source.NormalMinimumScore > source.ExtremeMinimumScore)
        {
            throw new InvalidDataException($"{path} threshold ordering is invalid.");
        }

        return new CompiledHostManagerSmartCoordinatorAdapterPolicyPlan(
            stateMultipliers,
            source.ExtremeMinimumScore,
            source.NormalMinimumScore,
            source.OptimizeMinimumScore);
    }

    private static CompiledHostManagerBaseScoreTierPlan CompileBaseScoreTiers(
        HostManagerBaseScoreTierProfile source)
    {
        const string path = "hot_publish.smart_coordinator.base_score_tiers";
        ValidateNonNegativeFinite(
            source.HighMinimumBaseScore,
            $"{path}.high_minimum_base_score");
        ValidateNonNegativeFinite(
            source.MiddleMinimumBaseScore,
            $"{path}.middle_minimum_base_score");
        if (source.MiddleMinimumBaseScore <= 0
            || source.MiddleMinimumBaseScore >= source.HighMinimumBaseScore
            || source.HighMinimumBaseScore > 100)
        {
            throw new InvalidDataException(
                $"{path} must preserve 0 < middle_minimum_base_score < high_minimum_base_score <= 100.");
        }

        return new CompiledHostManagerBaseScoreTierPlan(
            source.HighMinimumBaseScore,
            source.MiddleMinimumBaseScore);
    }

    private static ImmutableArray<double> ValidateStateMultipliers(double[]? values, string path)
    {
        var result = RequireCount(
                values,
                NativeSmartCoordinatorAbi.RuntimeStateCount,
                path)
            .ToArray();
        foreach (var value in result)
        {
            ValidateNonNegativeFinite(value, path);
        }
        if (result[^1] != 0)
        {
            throw new InvalidDataException($"{path} not-running multiplier must be exactly zero.");
        }
        return result.ToImmutableArray();
    }

    private static void ValidateSmartCoordinatorScoreRange(
        ImmutableArray<double> processStateMultipliers,
        int maximumProcesses,
        CompiledHostManagerSmartCoordinatorAdapterPolicyPlan gpuAdapterPolicy)
    {
        var maximumCpuProcessScore = 100d * processStateMultipliers.Max()
            + Optimization.HostManagerComputeScoringConfigurationFactory.MaximumBaseImportance;
        var maximumGpuProcessScore = 100d * gpuAdapterPolicy.StateMultipliers.Max()
            + Optimization.HostManagerComputeScoringConfigurationFactory.MaximumBaseImportance;
        var maximumCpuSoftwareScore = maximumCpuProcessScore * maximumProcesses;
        var maximumGpuSoftwareScore = maximumGpuProcessScore * maximumProcesses;
        if (!double.IsFinite(maximumCpuProcessScore)
            || !double.IsFinite(maximumGpuProcessScore)
            || !double.IsFinite(maximumCpuSoftwareScore)
            || !double.IsFinite(maximumGpuSoftwareScore))
        {
            throw new InvalidDataException(
                "smart coordinator score configuration must remain finite at its declared process capacity.");
        }
    }

    private static void ValidateProcessPolicy(HostManagerSmartCoordinatorProcessPolicyProfile source)
    {
        ValidatePositiveFinite(source.A1MinimumCpuScore, "hot_publish.smart_coordinator.process_policy.a1_minimum_cpu_score");
        var scales = new[]
        {
            source.DefaultMinimumCpuScoreScale,
            source.Level1MaximumCpuScoreScale,
            source.Level2MaximumCpuScoreScale,
            source.Level3MaximumCpuScoreScale,
            source.LowTierLevel4MaximumCpuScoreScale
        };
        foreach (var scale in scales)
        {
            ValidateNonNegativeFinite(scale, "hot_publish.smart_coordinator.process_policy.cpu_score_scales");
        }
        if (source.DefaultMinimumCpuScoreScale < source.Level1MaximumCpuScoreScale
            || source.Level1MaximumCpuScoreScale < source.Level2MaximumCpuScoreScale
            || source.Level2MaximumCpuScoreScale < source.Level3MaximumCpuScoreScale
            || source.Level3MaximumCpuScoreScale < source.LowTierLevel4MaximumCpuScoreScale)
        {
            throw new InvalidDataException("smart coordinator CPU score scales must preserve grade ordering.");
        }

    }

    private static ulong CompileSmartCoordinatorFeatureFlags(string[]? names)
    {
        var values = names ?? throw Missing("hot_publish.smart_coordinator.feature_flags");
        ulong result = 0;
        foreach (var name in values)
        {
            var flag = name switch
            {
                "process_policy" => (ulong)NativeSmartCoordinatorFeatures.ProcessPolicy,
                "adapter_cpu" => (ulong)NativeSmartCoordinatorFeatures.AdapterCpu,
                "adapter_gpu" => (ulong)NativeSmartCoordinatorFeatures.AdapterGpu,
                "critical_events" => (ulong)NativeSmartCoordinatorFeatures.CriticalEvents,
                _ => throw new InvalidDataException($"Unknown smart coordinator feature flag '{name}'.")
            };
            if ((result & flag) != 0)
            {
                throw new InvalidDataException($"Duplicate smart coordinator feature flag '{name}'.");
            }
            result |= flag;
        }
        if (result == 0)
        {
            throw new InvalidDataException("smart coordinator feature_flags must not be empty.");
        }
        return result;
    }

    private static void ValidateCapacity(int value, int maximum, string path)
    {
        ValidatePositive(value, path);
        ValidateMaximum(value, maximum, path);
    }

    private static void ValidatePercentage(double value, string path)
    {
        ValidateNonNegativeFinite(value, path);
        if (value > 100)
        {
            throw new InvalidDataException($"{path} must not exceed 100.");
        }
    }
}
