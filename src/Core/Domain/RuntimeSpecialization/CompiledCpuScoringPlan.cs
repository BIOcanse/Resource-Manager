using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledCpuCorePerformanceWeight(
    string PhysicalCoreId,
    int PhysicalCoreIndex,
    double ReferenceWeight);

public sealed record CompiledCpuCoreWeights(
    string Source,
    ImmutableArray<CompiledCpuCorePerformanceWeight> Cores)
{
    [JsonIgnore]
    public bool IsAvailable => !Cores.IsDefaultOrEmpty;

    public static CompiledCpuCoreWeights Empty { get; } = new("unavailable", []);
}

public sealed record CompiledCpuScoringPlan(
    double BaselineRatio,
    CompiledCpuCoreWeights CoreWeights);
