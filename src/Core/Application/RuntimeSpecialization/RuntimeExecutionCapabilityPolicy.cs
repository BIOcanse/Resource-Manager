namespace ResourceManager.App.Application.RuntimeSpecialization;

public sealed record RuntimeExecutionCapabilityPolicy(
    bool OptimizationRuntimeEnabled,
    bool PreciseGpuPlacementEnabled,
    bool PublicServiceCoordinationEnabled);
