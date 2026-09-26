using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed class ProductBaselineHostManagerMemoryModePolicySource
    : IHostManagerMemoryModePolicySource
{
    public HostManagerMemoryModePolicyCapture Capture(
        HostManagerSchedulingPlanBinding planBinding,
        CompiledHostManagerSmartCoordinatorPlan smartCoordinatorPlan)
    {
        ArgumentNullException.ThrowIfNull(planBinding);
        ArgumentNullException.ThrowIfNull(smartCoordinatorPlan);
        ValidateRequest(planBinding, smartCoordinatorPlan);

        var policy = smartCoordinatorPlan.HotPublish.MemoryModePolicy;
        if (!policy.Enabled)
        {
            return HostManagerMemoryModePolicyCapture.Unavailable(
                "memory-mode-policy-disabled");
        }

        var configuration = NativeMemoryModeConfigurationWriter.Create(
            policy.ConfigurationGeneration,
            checked((uint)smartCoordinatorPlan.Recreate.MaximumSoftwareGroups),
            policy.RatioUnitsMaximum,
            policy.UnrestrictedMinimumFreeRatioUnits,
            policy.NormalMinimumFreeRatioUnits,
            policy.StrongBeginFreeRatioUnits,
            smartCoordinatorPlan.HotPublish.BaseScoreTiers.MiddleMinimumBaseScore,
            smartCoordinatorPlan.HotPublish.BaseScoreTiers.HighMinimumBaseScore);
        var evidence = HostManagerMemoryModePolicyEvidence.Create(
            planBinding,
            policy.SourceKind,
            policy.ConfigurationSha256,
            policy.AllowUnrestricted);
        return HostManagerMemoryModePolicyCapture.Available(new(
            configuration,
            policy.SourceKind,
            policy.AllowUnrestricted,
            policy.OptimizeMemoryPriority,
            policy.PagedFrozenMemoryPriority,
            policy.ForeignMemoryPriorityDisposition,
            policy.OwnedStateVerificationIntervalCycles,
            planBinding,
            evidence));
    }

    private static void ValidateRequest(
        HostManagerSchedulingPlanBinding binding,
        CompiledHostManagerSmartCoordinatorPlan plan)
    {
        var policy = plan.HotPublish.MemoryModePolicy;
        if (!binding.IsPublished
            || !plan.IsPublished
            || !policy.IsPublished
            || binding.SmartConfigurationGeneration != plan.ConfigurationGeneration
            || !string.Equals(
                binding.SmartConfigurationSha256,
                plan.ConfigurationSha256,
                StringComparison.Ordinal)
            || binding.MemoryModePolicyEnabled != policy.Enabled
            || !string.Equals(
                binding.MemoryModePolicySourceKind,
                policy.SourceKind,
                StringComparison.Ordinal)
            || binding.MemoryModeConfigurationGeneration
                != policy.ConfigurationGeneration
            || !string.Equals(
                binding.MemoryModeConfigurationSha256,
                policy.ConfigurationSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The product memory-mode baseline is not bound to the current Host plan.");
        }
    }
}
