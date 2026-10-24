using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledRuntimePlan(
    long Version,
    DateTimeOffset CompiledAt,
    string Reason,
    CompiledHostManagerPlan HostManager,
    CompiledAdapterDispatchPlan AdapterDispatch,
    CompiledGpuPlacementPlan GpuPlacement,
    CompiledBaseScorePlan BaseScore,
    CompiledHardwareScorePlan HardwareScores,
    CompiledOptimizationModePlan OptimizationMode,
    CompiledDiagnosticsPlan Diagnostics,
    CompiledMonitoringPlan Monitoring)
{
    public IReadOnlyList<string> RuntimeCapabilityConstrainedPaths { get; init; } = [];

    public bool AutoStartEnabled { get; init; }

    public Settings.AppSystemIntegrationSettings ServiceRecovery { get; init; } = new(false, []);

    public CpuBaselineRatioSettings? CpuBaseline { get; init; }

    public CpuTopologySnapshot? CpuPlacementTopology { get; init; }

    internal global::FreedomPoint.ConfigurationChange? FreedomPointPreview { get; init; }

    public static CompiledRuntimePlan Default { get; } = new(
        0,
        DateTimeOffset.MinValue,
        "default",
        CompiledHostManagerPlan.Unpublished,
        CompiledAdapterDispatchPlan.Default,
        CompiledGpuPlacementPlan.Default,
        CompiledBaseScorePlan.Empty,
        CompiledHardwareScorePlan.Default,
        CompiledOptimizationModePlan.Default,
        CompiledDiagnosticsPlan.Default,
        CompiledMonitoringPlan.Default);
}
