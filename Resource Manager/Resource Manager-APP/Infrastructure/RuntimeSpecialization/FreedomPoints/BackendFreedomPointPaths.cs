namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

internal static class BackendFreedomPointPaths
{
    public const string CpuResidencyObservationWindow = "resource-manager/backend/monitoring/cpu_residency/build/simple/0";
    public const string CpuResidencyExecutionTimeSource = "resource-manager/backend/monitoring/cpu_residency/build/complex/0";
    public const string SchedulerSamplingInterval = "resource-manager/backend/scheduling/build/simple/0";
    public const string ConsecutiveDecisions = "resource-manager/backend/scheduling/transitions/runtime/simple/0";
    public const string GameStartGrace = "resource-manager/backend/scheduling/transitions/runtime/simple/1";
    public const string FailureRetry = "resource-manager/backend/scheduling/transitions/runtime/simple/2";
    public const string PlacementActionTimeout = "resource-manager/backend/scheduling/placement/runtime/simple/0";
    public const string GpuOverflowThresholds = "resource-manager/backend/scheduling/placement/runtime/complex/1";
    public const string GpuWindowExecutionLimits = "resource-manager/backend/scheduling/placement/build/complex/0";
    public const string GpuApiObservationWindow = "resource-manager/backend/scheduling/placement/build/simple/0";
    public const string SoftwareWelfare = "resource-manager/backend/scoring/build/complex/0";
    public const string BaseScoreTiers = "resource-manager/backend/scoring/runtime/complex/0";
    public const string CpuStateMultipliers = "resource-manager/backend/scoring/cpu/runtime/complex/0";
    public const string CpuSmtAccounting = "resource-manager/backend/scoring/cpu/build/complex/0";
    public const string CpuBaselineRatio = "resource-manager/backend/scoring/cpu/runtime/simple/0";
    public const string CpuCorePerformanceWeights = "resource-manager/backend/scoring/cpu/runtime/complex/1";
}
