using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class HostManagerComputeScoringConfigurationFactory
{
    internal const double MaximumBaseImportance = 100;

    internal static NativeComputeScoringConfiguration Create(
        CompiledHostManagerSmartCoordinatorPlan plan,
        CompiledCpuScoringPlan cpuScoring)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidDataException(
                "The compute scoring authority requires a published smart coordinator plan.");
        }

        var maximumProcessCount = checked((uint)plan.Recreate.MaximumProcesses);
        var maximumGpuRowCount = checked((uint)plan.Recreate.MaximumInputRows);
        var maximumOutputCount = checked(
            checked(3U * maximumProcessCount) + checked(2U * maximumGpuRowCount));
        var cpuStateMultipliers = plan.HotPublish.ProcessStateMultipliers.AsSpan();
        var gpuStateMultipliers = plan.HotPublish.GpuAdapterPolicy.StateMultipliers.AsSpan();
        var maximumPolicyMultiplier = 1D;
        foreach (var multiplier in cpuStateMultipliers)
        {
            maximumPolicyMultiplier = Math.Max(maximumPolicyMultiplier, multiplier);
        }
        foreach (var multiplier in gpuStateMultipliers)
        {
            maximumPolicyMultiplier = Math.Max(maximumPolicyMultiplier, multiplier);
        }

        return NativeComputeScoringConfigurationWriter.Create(
            plan.ConfigurationGeneration,
            maximumProcessCount,
            maximumGpuRowCount,
            maximumOutputCount,
            cpuStateMultipliers,
            gpuStateMultipliers,
            MaximumBaseImportance,
            maximumPolicyMultiplier,
            cpuScoring.BaselineRatio,
            checked((uint)cpuScoring.CoreWeights.Cores.Length),
            plan.HotPublish.WelfareUtilizationBaselinePercent);
    }
}
