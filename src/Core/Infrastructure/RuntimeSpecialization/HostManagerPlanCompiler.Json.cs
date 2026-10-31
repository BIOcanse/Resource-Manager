using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    internal static System.Text.Json.JsonSerializerOptions CreateFreedomPointSerializerOptions(System.Text.Json.JsonSerializerOptions options)
        => new FreedomPointJsonContext(options).Options;

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(HostManagerConfigurationProfile))]
    [JsonSerializable(typeof(FreedomPointRegistry.CpuScoringInput))]
    [JsonSerializable(typeof(CompiledCpuCoreWeights))]
    [JsonSerializable(typeof(CompiledKernelEtwLimits))]
    [JsonSerializable(typeof(CompiledNativeBuildPolicy))]
    [JsonSerializable(typeof(CpuExecutionTimeSourceDeclaration))]
    [JsonSerializable(typeof(CpuSmtAccountingDeclaration))]
    [JsonSerializable(typeof(CompiledCpuAutomaticExclusivityPolicy))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(double[]))]
    [JsonSerializable(typeof(GpuWindowExecutionLimitsDeclaration))]
    [JsonSerializable(typeof(HostManagerBaseScoreTierProfile))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(SoftwareWelfareDeclaration))]
    [JsonSerializable(typeof(Application.KernelLeak.KernelLeakOptions))]
    [JsonSerializable(typeof(Application.Notifications.NotificationOptions))]
    [JsonSerializable(typeof(Application.CrashReports.CrashReportOptions))]
    [JsonSerializable(typeof(Application.MemoryProtection.HostMemoryProtectionRecordOptions))]
    [JsonSerializable(typeof(HardwareHelperProcessTiming))]
    [JsonSerializable(typeof(CompiledGpuVfCurveSettings))]
    [JsonSerializable(typeof(FanCurveFeedTiming))]
    [JsonSerializable(typeof(UniwillEcChannelOptions))]
    [JsonSerializable(typeof(TargetedRecordingOptions))]
    [JsonSerializable(typeof(PerformanceOverlayOptions))]
    [JsonSerializable(typeof(EtwSessionBufferPoolOptions))]
    [JsonSerializable(typeof(FramePipelineOptions))]
    [JsonSerializable(typeof(CompiledMemoryCpuTierCell[]))]
    [JsonSerializable(typeof(RollbackStateStoreOptions))]
    [JsonSerializable(typeof(AuthorityRetirementOptions))]
    [JsonSerializable(typeof(IdealProcessorWorkspaceOptions))]
    [JsonSerializable(typeof(NormalRestorationOptions))]
    [JsonSerializable(typeof(EffectValidationLeaseOptions))]
    [JsonSerializable(typeof(MemoryCleanupValidationEvidenceOptions))]
    [JsonSerializable(typeof(SmartRecordHistoryOptions))]
    [JsonSerializable(typeof(SoftwareKindBaseScores))]
    [JsonSerializable(typeof(CpuCoreClassScores))]
    [JsonSerializable(typeof(ReportRuleLimits))]
    [JsonSerializable(typeof(GpuPlacementProcessHistoryOptions))]
    [JsonSerializable(typeof(ExternalGpuRuntimeOptions))]
    [JsonSerializable(typeof(GpuInjectionOptions))]
    [JsonSerializable(typeof(GpuLaunchReportOptions))]
    [JsonSerializable(typeof(SqliteVacuumOptions))]
    [JsonSerializable(typeof(OptimizationStorageLimits))]
    [JsonSerializable(typeof(LegacyAffinityOptions))]
    [JsonSerializable(typeof(OptimizationRuntimeLimits))]
    private partial class FreedomPointJsonContext : JsonSerializerContext;
}
