using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.Optimization;

/// <summary>
/// Build-time optimization choices (freedom-point tables scheduling, scoring, scoring/cpu and reports). The stores,
/// workspaces, coordinators and scoring defaults are constructed before the first runtime plan publication, so they
/// read <see cref="ProductDefaults"/>; the plan compiler consumes the same declarations through <see cref="Compile"/>.
/// </summary>
internal static class OptimizationSettings
{
    private const string Consumer = "OptimizationSettings.Compile";

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static CompiledOptimizationSettings ProductDefaults { get; } = Compile(EmbeddedFreedomPointDefaults.Begin());

    internal static CompiledOptimizationSettings Compile(FreedomPointCompilation freedom)
    {
        ArgumentNullException.ThrowIfNull(freedom);
        var reportSampleInterval = freedom.Consume<int>(BackendFreedomPointPaths.ReportSampleInterval, Consumer);
        // The report status sends the cadence to the frontend in whole seconds.
        if (reportSampleInterval < 1000 || reportSampleInterval % 1000 != 0)
            throw new InvalidDataException("The optimization report sample interval must be a whole number of seconds.");
        return new(
            freedom.Consume<RollbackStateStoreOptions>(BackendFreedomPointPaths.RollbackStateStore, Consumer).Validate(),
            freedom.Consume<AuthorityRetirementOptions>(BackendFreedomPointPaths.AuthorityRetirement, Consumer).Validate(),
            freedom.Consume<IdealProcessorWorkspaceOptions>(BackendFreedomPointPaths.IdealProcessorWorkspace, Consumer).Validate(),
            freedom.Consume<NormalRestorationOptions>(BackendFreedomPointPaths.NormalRestoration, Consumer).Validate(),
            freedom.Consume<EffectValidationLeaseOptions>(BackendFreedomPointPaths.EffectValidationLease, Consumer).Validate(),
            freedom.Consume<MemoryCleanupValidationEvidenceOptions>(BackendFreedomPointPaths.MemoryCleanupValidationEvidence, Consumer).Validate(),
            freedom.Consume<SmartRecordHistoryOptions>(BackendFreedomPointPaths.SmartRecordHistory, Consumer).Validate(),
            freedom.Consume<SoftwareKindBaseScores>(BackendFreedomPointPaths.SoftwareKindBaseScores, Consumer).Validate(),
            freedom.Consume<CpuCoreClassScores>(BackendFreedomPointPaths.CoreClassScores, Consumer).Validate(),
            TimeSpan.FromMilliseconds(reportSampleInterval),
            freedom.Consume<ReportRuleLimits>(BackendFreedomPointPaths.ReportRuleLimits, Consumer).Validate(),
            freedom.Consume<OptimizationStorageLimits>(BackendFreedomPointPaths.OptimizationStorageLimits, Consumer).Validate(),
            freedom.Consume<LegacyAffinityOptions>(BackendFreedomPointPaths.LegacyAffinity, Consumer).Validate(),
            freedom.Consume<OptimizationRuntimeLimits>(BackendFreedomPointPaths.OptimizationRuntimeLimits, Consumer).Validate());
    }
}
