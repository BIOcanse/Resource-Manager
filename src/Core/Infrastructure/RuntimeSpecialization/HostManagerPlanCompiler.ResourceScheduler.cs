using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Domain.Settings;
using System.Collections.Immutable;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static ResourceSchedulerConfig CompileResourceSchedulerConfiguration(
        HostManagerResourceSchedulerConfigurationProfile source,
        AppPerformanceSettings performance,
        ulong configurationGeneration)
    {
        if (source.BytesPerMegabyte == 0)
        {
            throw new InvalidDataException("resource_scheduler.bytes_per_megabyte must be greater than zero.");
        }

        ValidatePositiveFinite(source.SizeImportanceMinimum, "resource_scheduler.size_importance_minimum");
        ValidatePositiveFinite(source.SizeImportanceMaximum, "resource_scheduler.size_importance_maximum");
        ValidatePositiveFinite(source.SizeLogDivisor, "resource_scheduler.size_log_divisor");
        if (source.SizeImportanceMaximum < source.SizeImportanceMinimum)
        {
            throw new InvalidDataException(
                "resource_scheduler.size_importance_maximum must not be smaller than size_importance_minimum.");
        }

        var resourceKindMultipliers = ValidateMultiplierArray(
            source.ResourceKindMultipliers,
            ResourceSchedulerConfig.ResourceKindCount,
            "resource_scheduler.resource_kind_multipliers");
        var surfaceMultipliers = ValidateMultiplierArray(
            source.SurfaceMultipliers,
            ResourceSchedulerConfig.SurfaceCount,
            "resource_scheduler.surface_multipliers");
        var policyGradeMultipliers = ValidateMultiplierArray(
            source.PolicyGradeMultipliers,
            ResourceSchedulerConfig.PolicyGradeCount,
            "resource_scheduler.policy_grade_multipliers");
        var policyGradePressure = ValidateByteArray(
            source.PolicyGradePressure,
            ResourceSchedulerConfig.PolicyGradeCount,
            0,
            4,
            "resource_scheduler.policy_grade_pressure");

        var schedulingGradeMultipliers = ValidateMultiplierArray(
            source.SchedulingGradeMultipliers,
            ResourceSchedulerConfig.SchedulingGradeCount,
            "resource_scheduler.scheduling_grade_multipliers");
        ValidatePositiveFinite(
            source.AbsentSchedulingGradeMultiplier,
            "resource_scheduler.absent_scheduling_grade_multiplier");
        var discardKindMultipliers = ValidateMultiplierArray(
            source.DiscardKindMultipliers,
            ResourceSchedulerConfig.ResourceKindCount,
            "resource_scheduler.discard_kind_multipliers");
        var trimKindMultipliers = ValidateMultiplierArray(
            source.TrimKindMultipliers,
            ResourceSchedulerConfig.ResourceKindCount,
            "resource_scheduler.trim_kind_multipliers");
        var moveDownKindMultipliers = ValidateMultiplierArray(
            source.MoveDownKindMultipliers,
            ResourceSchedulerConfig.ResourceKindCount,
            "resource_scheduler.move_down_kind_multipliers");
        var moveUpKindMultipliers = ValidateMultiplierArray(
            source.MoveUpKindMultipliers,
            ResourceSchedulerConfig.ResourceKindCount,
            "resource_scheduler.move_up_kind_multipliers");

        ValidatePositiveFinite(source.ActivityBaseMultiplier, "resource_scheduler.activity_base_multiplier");
        ValidateNonNegativeFinite(source.ActivityQuadraticScale, "resource_scheduler.activity_quadratic_scale");
        ValidatePositiveFinite(source.ActivityNormalizer, "resource_scheduler.activity_normalizer");
        var demandMultipliers = ValidateMultiplierArray(
            source.DemandMultipliers,
            ResourceSchedulerConfig.DemandMultiplierCount,
            "resource_scheduler.demand_multipliers");
        if (source.LargeResourceBytes == 0)
        {
            throw new InvalidDataException("resource_scheduler.large_resource_bytes must be greater than zero.");
        }

        if (source.PhysicalToVirtualDesperatePressureLevel is < 1 or > 4)
        {
            throw new InvalidDataException(
                "resource_scheduler.physical_to_virtual_desperate_pressure_level must be between 1 and 4.");
        }

        ValidateRatio(source.PhysicalToVirtualMinimumFreeRatio, "resource_scheduler.physical_to_virtual_minimum_free_ratio");
        ValidateRatio(
            source.PhysicalToVirtualDesperateMinimumFreeRatio,
            "resource_scheduler.physical_to_virtual_desperate_minimum_free_ratio");
        if (source.PhysicalToVirtualDesperateMinimumFreeRatio > source.PhysicalToVirtualMinimumFreeRatio)
        {
            throw new InvalidDataException(
                "resource_scheduler.physical_to_virtual_desperate_minimum_free_ratio must not exceed physical_to_virtual_minimum_free_ratio.");
        }

        ValidateRatio(source.VramToPhysicalMinimumFreeRatio, "resource_scheduler.vram_to_physical_minimum_free_ratio");
        var pressureThresholds = ValidateRatioArray(
            source.PressureFreeRatioThresholds,
            ResourceSchedulerConfig.PressureThresholdCount,
            "resource_scheduler.pressure_free_ratio_thresholds");
        if (!(pressureThresholds[0] < pressureThresholds[1]
            && pressureThresholds[1] < pressureThresholds[2]))
        {
            throw new InvalidDataException(
                "resource_scheduler.pressure_free_ratio_thresholds must be strictly increasing.");
        }

        ValidateRatio(source.VramTargetFreeRatio, "resource_scheduler.vram_target_free_ratio");
        var targetFreeRatios = new[]
        {
            source.VramTargetFreeRatio,
            1 - performance.PhysicalMemoryOptimizationTargetUsagePercent / 100d,
            1 - performance.VirtualMemoryOptimizationTargetUsagePercent / 100d
        };
        ValidateRatio(targetFreeRatios[1], "performance.physical_memory_optimization_target_usage_percent");
        ValidateRatio(targetFreeRatios[2], "performance.virtual_memory_optimization_target_usage_percent");

        var desiredFreeRatios = ValidateRatioArray(
            source.DesiredFreeRatiosLevel2To4,
            ResourceSchedulerConfig.TierCount,
            "resource_scheduler.desired_free_ratios_level_2_to_4");
        if (desiredFreeRatios.Any(static value => value <= 0))
        {
            throw new InvalidDataException(
                "resource_scheduler.desired_free_ratios_level_2_to_4 values must be greater than zero.");
        }

        var minimumReleaseBytes = RequireCount(
                source.MinimumReleaseBytes,
                ResourceSchedulerConfig.PressureLevelCount,
                "resource_scheduler.minimum_release_bytes")
            .ToArray();
        if (minimumReleaseBytes.Any(static value => value == 0))
        {
            throw new InvalidDataException("resource_scheduler.minimum_release_bytes values must be greater than zero.");
        }

        var maximumReleaseShares = ValidateMultiplierArray(
            source.MaximumReleaseShares,
            ResourceSchedulerConfig.PressureLevelCount,
            "resource_scheduler.maximum_release_shares");
        if (maximumReleaseShares.Any(static value => value > 1))
        {
            throw new InvalidDataException("resource_scheduler.maximum_release_shares values must not exceed 1.");
        }

        var baseScoreThresholds = RequireCount(
                source.BaseScoreThresholds,
                ResourceSchedulerConfig.PressureThresholdCount,
                "resource_scheduler.base_score_thresholds")
            .ToArray();
        foreach (var value in baseScoreThresholds)
        {
            ValidateNonNegativeFinite(value, "resource_scheduler.base_score_thresholds");
        }

        ValidateNonNegativeFinite(source.BaseScoreMinimum, "resource_scheduler.base_score_minimum");
        ValidatePositiveFinite(source.BaseScoreMaximum, "resource_scheduler.base_score_maximum");
        if (source.BaseScoreMaximum <= source.BaseScoreMinimum
            || !(baseScoreThresholds[0] > baseScoreThresholds[1]
                && baseScoreThresholds[1] > baseScoreThresholds[2])
            || baseScoreThresholds[0] > source.BaseScoreMaximum
            || baseScoreThresholds[2] < source.BaseScoreMinimum)
        {
            throw new InvalidDataException("resource_scheduler base score bounds and thresholds are inconsistent.");
        }

        var baseReleaseMultipliers = ValidateMultiplierArray(
            source.BaseReleaseMultipliers,
            ResourceSchedulerConfig.PressureLevelCount,
            "resource_scheduler.base_release_multipliers");
        if (source.TrimReleaseNumerator == 0
            || source.TrimReleaseDenominator == 0
            || source.TrimReleaseNumerator > source.TrimReleaseDenominator)
        {
            throw new InvalidDataException("resource_scheduler trim release fraction is invalid.");
        }

        if (source.MaximumActionsPerTarget == 0)
        {
            throw new InvalidDataException("resource_scheduler.maximum_actions_per_target must be greater than zero.");
        }

        ValidateRatio(source.StrongPressureFreeRatio, "resource_scheduler.strong_pressure_free_ratio");
        var dangerPhysicalRatio = performance.VramMoveDownPhysicalMemoryDangerPercent / 100d;
        var dangerVirtualRatio = performance.PhysicalMemoryMoveDownVirtualMemoryDangerPercent / 100d;
        ValidateRatio(dangerPhysicalRatio, "performance.vram_move_down_physical_memory_danger_percent");
        ValidateRatio(dangerVirtualRatio, "performance.physical_memory_move_down_virtual_memory_danger_percent");
        var dangerWeights = ValidateByteArray(
            source.DangerSeverityWeights,
            ResourceSchedulerConfig.DangerFlagCount,
            1,
            byte.MaxValue,
            "resource_scheduler.danger_severity_weights");

        var compiled = new ResourceSchedulerConfig(
            configurationGeneration,
            source.BytesPerMegabyte,
            source.SizeImportanceMinimum,
            source.SizeImportanceMaximum,
            source.SizeLogDivisor,
            resourceKindMultipliers.ToImmutableArray(),
            surfaceMultipliers.ToImmutableArray(),
            policyGradeMultipliers.ToImmutableArray(),
            policyGradePressure.ToImmutableArray(),
            schedulingGradeMultipliers.ToImmutableArray(),
            source.AbsentSchedulingGradeMultiplier,
            discardKindMultipliers.ToImmutableArray(),
            trimKindMultipliers.ToImmutableArray(),
            moveDownKindMultipliers.ToImmutableArray(),
            moveUpKindMultipliers.ToImmutableArray(),
            source.ActivityBaseMultiplier,
            source.ActivityQuadraticScale,
            source.ActivityNormalizer,
            demandMultipliers.ToImmutableArray(),
            source.LargeResourceBytes,
            source.HighActivityScore,
            source.PhysicalToVirtualDesperatePressureLevel,
            source.PhysicalToVirtualMinimumFreeRatio,
            source.PhysicalToVirtualDesperateMinimumFreeRatio,
            source.VramToPhysicalMinimumFreeRatio,
            pressureThresholds.ToImmutableArray(),
            targetFreeRatios.ToImmutableArray(),
            desiredFreeRatios.ToImmutableArray(),
            minimumReleaseBytes.ToImmutableArray(),
            maximumReleaseShares.ToImmutableArray(),
            baseScoreThresholds.ToImmutableArray(),
            baseReleaseMultipliers.ToImmutableArray(),
            source.BaseScoreMinimum,
            source.BaseScoreMaximum,
            source.TrimReleaseNumerator,
            source.TrimReleaseDenominator,
            source.MaximumActionsPerTarget,
            source.StrongPressureFreeRatio,
            dangerPhysicalRatio,
            dangerVirtualRatio,
            dangerWeights.ToImmutableArray());
        NativeResourceSchedulerSession.ValidateConfiguration(compiled);
        return compiled;
    }

    private static double[] ValidateRatioArray(double[]? values, int expectedCount, string path)
    {
        var result = RequireCount(values, expectedCount, path).ToArray();
        foreach (var value in result)
        {
            ValidateRatio(value, path);
        }

        return result;
    }

    private static byte[] ValidateByteArray(
        int[]? values,
        int expectedCount,
        int minimum,
        int maximum,
        string path)
    {
        var source = RequireCount(values, expectedCount, path);
        var result = new byte[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var value = source[index];
            if (value < minimum || value > maximum)
            {
                throw new InvalidDataException(
                    $"{path} values must be between {minimum} and {maximum}.");
            }

            result[index] = checked((byte)value);
        }

        return result;
    }

    private static IReadOnlyList<T> RequireCount<T>(
        IReadOnlyList<T>? values,
        int expectedCount,
        string path)
    {
        if (values is null || values.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"{path} must contain exactly {expectedCount} values.");
        }
        return values;
    }

    private static double[] ValidateMultiplierArray(
        double[]? values,
        int expectedCount,
        string path)
    {
        var result = RequireCount(values, expectedCount, path).ToArray();
        foreach (var value in result)
        {
            ValidateNonNegativeFinite(value, path);
        }
        return result;
    }

    private static void ValidateRatio(double value, string path)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new InvalidDataException($"{path} must be a finite number between 0 and 1.");
        }
    }
}
