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
    CompiledSelfLogicPlan SelfLogic,
    CompiledDiagnosticsPlan Diagnostics,
    CompiledMonitoringPlan Monitoring)
{
    public IReadOnlyList<string> RuntimeCapabilityConstrainedPaths { get; init; } = [];

    public bool AutoStartEnabled { get; init; }

    public CpuBaselineRatioSettings? CpuBaseline { get; init; }

    public CpuTopologySnapshot? CpuPlacementTopology { get; init; }

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
        CompiledSelfLogicPlan.Default,
        CompiledDiagnosticsPlan.Default,
        CompiledMonitoringPlan.Default);
}
