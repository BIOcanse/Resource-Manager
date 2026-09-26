using System.Collections.Immutable;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

internal static class CpuScoringPlanCompiler
{
    internal static CompiledCpuScoringPlan Compile(
        CpuTopologySnapshot topology,
        IReadOnlyDictionary<int, double> manualWeights,
        double baselineRatio)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(manualWeights);
        CpuBaselineRatio.Validate(baselineRatio);

        var cores = topology.PhysicalCores.OrderBy(static core => core.Index).ToArray();
        if (cores.Length == 0 || cores.Length != topology.PhysicalCoreCount
            || cores.Select(static core => core.Index).Distinct().Count() != cores.Length
            || cores.Any(static core => core.Index < 0 || string.IsNullOrWhiteSpace(core.Id))
            || cores.Select(static core => core.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cores.Length)
        {
            throw new InvalidDataException("CPU scoring requires distinct physical cores from the startup topology.");
        }

        string source;
        double[] weights;
        if (manualWeights.Count != 0)
        {
            if (manualWeights.Count != cores.Length || cores.Any(core =>
                    !manualWeights.TryGetValue(core.Index, out var value) || !double.IsFinite(value) || value <= 0))
            {
                return new(baselineRatio, CompiledCpuCoreWeights.Empty);
            }
            source = "manual";
            weights = cores.Select(core => manualWeights[core.Index]).ToArray();
        }
        else
        {
            var preset = CpuCorePerformancePresetResolver.Classify(topology.CpuName);
            if (!preset.KnownHomogeneous || preset.KnownHybrid
                || preset.Source == "model-preset:intel-default"
                || (preset.HasExpectedCoreLayout && preset.ExpectedPhysicalCoreCount != cores.Length))
            {
                return new(baselineRatio, CompiledCpuCoreWeights.Empty);
            }
            // A common per-core reference cancels from W; these are relative units, not CPU-Z measurements.
            source = $"homogeneous-relative:{preset.Source}";
            weights = Enumerable.Repeat(1d, cores.Length).ToArray();
        }

        var compiled = ImmutableArray.CreateBuilder<CompiledCpuCorePerformanceWeight>(cores.Length);
        for (var index = 0; index < cores.Length; index++)
        {
            compiled.Add(new(cores[index].Id, cores[index].Index, weights[index]));
        }
        return new(baselineRatio, new(source, compiled.MoveToImmutable()));
    }
}
