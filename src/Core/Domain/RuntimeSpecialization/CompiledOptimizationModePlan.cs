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

[Flags]
public enum OptimizationDomains : byte
{
    None = 0,
    Memory = 1 << 0,
    Cpu = 1 << 1,
    Gpu = 1 << 2
}

public sealed record CompiledOptimizationModePlan(
    string Mode,
    OptimizationModeCapabilities Capabilities,
    OptimizationDomains Domains)
{
    private const OptimizationModeCapabilities MemoryOnlyCapabilities =
        OptimizationModeCapabilities.ResourceManagerSelfMemoryActions
        | OptimizationModeCapabilities.AutomaticMemoryCleanup
        | OptimizationModeCapabilities.NonAdaptedMemoryPriority;
    private const OptimizationModeCapabilities CpuCapabilities =
        OptimizationModeCapabilities.SoftwareScheduling
        | OptimizationModeCapabilities.AutomaticProcessPolicies
        | OptimizationModeCapabilities.HardwarePlacement;
    private const OptimizationModeCapabilities GpuCapabilities =
        OptimizationModeCapabilities.VramResourceActions
        | OptimizationModeCapabilities.SoftwareScheduling
        | OptimizationModeCapabilities.HardwarePlacement;

    public static CompiledOptimizationModePlan Default { get; } = Compile(AppOptimizationModes.Normal);

    public bool SchedulingEnabled => Domains != OptimizationDomains.None;
    public bool MemorySchedulingEnabled => HasDomain(OptimizationDomains.Memory);
    public bool CpuSchedulingEnabled => HasDomain(OptimizationDomains.Cpu);
    public bool GpuSchedulingEnabled => HasDomain(OptimizationDomains.Gpu);
    public bool CpuPlacementEnabled => CpuSchedulingEnabled && HardwarePlacementEnabled;
    public bool GpuPlacementEnabled => GpuSchedulingEnabled && HardwarePlacementEnabled;
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
        var normalized = AppOptimizationModes.Normalize(mode);
        var domains = normalized switch
        {
            AppOptimizationModes.MemoryOnly => OptimizationDomains.Memory,
            AppOptimizationModes.CpuOnly => OptimizationDomains.Cpu,
            AppOptimizationModes.GpuOnly => OptimizationDomains.Gpu,
            AppOptimizationModes.MemoryCpu => OptimizationDomains.Memory | OptimizationDomains.Cpu,
            AppOptimizationModes.MemoryGpu => OptimizationDomains.Memory | OptimizationDomains.Gpu,
            AppOptimizationModes.CpuGpu => OptimizationDomains.Cpu | OptimizationDomains.Gpu,
            AppOptimizationModes.Smart => OptimizationDomains.Memory | OptimizationDomains.Cpu | OptimizationDomains.Gpu,
            _ => OptimizationDomains.None
        };
        var capabilities = OptimizationModeCapabilities.None;
        if (domains.HasFlag(OptimizationDomains.Memory))
        {
            capabilities |= MemoryOnlyCapabilities;
        }
        if (domains.HasFlag(OptimizationDomains.Cpu))
        {
            capabilities |= CpuCapabilities;
        }
        if (domains.HasFlag(OptimizationDomains.Gpu))
        {
            capabilities |= GpuCapabilities;
        }
        return new CompiledOptimizationModePlan(normalized, capabilities, domains);
    }

    private bool Has(OptimizationModeCapabilities capability)
    {
        return (Capabilities & capability) != 0;
    }

    private bool HasDomain(OptimizationDomains domain)
        => (Domains & domain) != 0;
}
