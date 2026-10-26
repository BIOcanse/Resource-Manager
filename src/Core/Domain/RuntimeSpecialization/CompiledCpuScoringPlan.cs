using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledCpuScoringPlan(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.MachineBaselineRatio)]
    double BaselineRatio,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.CorePerformanceWeights)]
    CompiledCpuCoreWeights CoreWeights);
