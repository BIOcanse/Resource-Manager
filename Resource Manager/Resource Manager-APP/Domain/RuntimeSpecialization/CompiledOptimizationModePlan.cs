using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

[Flags]
public enum OptimizationModeCapabilities : byte
{
    None = 0,
    ExternalAdaptedMemoryActions = 1 << 0,
    AutomaticMemoryCleanup = 1 << 1,
    VramResourceActions = 1 << 2,
    SoftwareScheduling = 1 << 3,
    AutomaticProcessPolicies = 1 << 4,
    HardwarePlacement = 1 << 5,
    NonAdaptedMemoryPriority = 1 << 6,
    ResourceManagerSelfMemoryActions = 1 << 7
}

public sealed record CompiledOptimizationModePlan(
    string Mode,
    OptimizationModeCapabilities Capabilities)
{
    private const OptimizationModeCapabilities MemoryOnlyCapabilities =
        OptimizationModeCapabilities.ResourceManagerSelfMemoryActions
        | OptimizationModeCapabilities.AutomaticMemoryCleanup
        | OptimizationModeCapabilities.NonAdaptedMemoryPriority;
    private const OptimizationModeCapabilities SmartCapabilities =
        MemoryOnlyCapabilities
        | OptimizationModeCapabilities.VramResourceActions
        | OptimizationModeCapabilities.SoftwareScheduling
        | OptimizationModeCapabilities.AutomaticProcessPolicies
        | OptimizationModeCapabilities.HardwarePlacement;

    public static CompiledOptimizationModePlan Default { get; } = Compile(AppOptimizationModes.Normal);

    public bool SchedulingEnabled => Capabilities != OptimizationModeCapabilities.None;
    public bool ExternalAdaptedMemoryActionsEnabled =>
        Has(OptimizationModeCapabilities.ExternalAdaptedMemoryActions);
    public bool ResourceManagerSelfMemoryActionsEnabled =>
        Has(OptimizationModeCapabilities.ResourceManagerSelfMemoryActions);
    public bool AutomaticMemoryCleanupEnabled => Has(OptimizationModeCapabilities.AutomaticMemoryCleanup);
    public bool VramResourceActionsEnabled => Has(OptimizationModeCapabilities.VramResourceActions);
    public bool SoftwareSchedulingEnabled => Has(OptimizationModeCapabilities.SoftwareScheduling);
    public bool AutomaticProcessPoliciesEnabled => Has(OptimizationModeCapabilities.AutomaticProcessPolicies);
    public bool HardwarePlacementEnabled => Has(OptimizationModeCapabilities.HardwarePlacement);
    public bool NonAdaptedMemoryPriorityEnabled =>
        Has(OptimizationModeCapabilities.NonAdaptedMemoryPriority);
    public static CompiledOptimizationModePlan Compile(string? mode)
    {
        var normalized = mode switch
        {
            AppOptimizationModes.MemoryOnly => AppOptimizationModes.MemoryOnly,
            AppOptimizationModes.Smart => AppOptimizationModes.Smart,
            _ => AppOptimizationModes.Normal
        };

        var capabilities = normalized switch
        {
            AppOptimizationModes.MemoryOnly => MemoryOnlyCapabilities,
            AppOptimizationModes.Smart => SmartCapabilities,
            _ => OptimizationModeCapabilities.None
        };

        return new CompiledOptimizationModePlan(normalized, capabilities);
    }

    private bool Has(OptimizationModeCapabilities capability)
    {
        return (Capabilities & capability) != 0;
    }
}
