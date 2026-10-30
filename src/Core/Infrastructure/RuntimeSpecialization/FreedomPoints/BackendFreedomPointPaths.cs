namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

internal static class BackendFreedomPointPaths
{
    public const string ReportObservationGrowthLimit = "resource-manager/backend/reports/build/simple/0";
    public const string SingleRequestStopBuffer = "resource-manager/backend/monitoring/build/simple/0";
    public const string KernelLeak = "resource-manager/backend/monitoring/build/complex/0";
    public const string Notifications = "resource-manager/backend/monitoring/build/complex/1";
    public const string HostMemoryProtectionRecords = "resource-manager/backend/monitoring/build/complex/2";
    public const string ServiceIdentityCacheLifetime = "resource-manager/backend/monitoring/build/simple/1";
    public const string OverlayFrameWindow = "resource-manager/backend/monitoring/build/simple/2";
    public const string TargetedRecording = "resource-manager/backend/monitoring/build/complex/3";
    public const string KernelEtwLimits = "resource-manager/backend/monitoring/build/complex/4";
    public const string ServiceRecoveryCounterReset = "resource-manager/backend/service_recovery/build/simple/0";
    public const string CpuResidencyObservationWindow = "resource-manager/backend/monitoring/cpu_residency/build/simple/0";
    public const string CpuResidencyExecutionTimeSource = "resource-manager/backend/monitoring/cpu_residency/build/complex/0";
    public const string SchedulerSamplingInterval = "resource-manager/backend/scheduling/build/simple/0";
    public const string ConsecutiveDecisions = "resource-manager/backend/scheduling/transitions/runtime/simple/0";
    public const string GameStartGrace = "resource-manager/backend/scheduling/transitions/runtime/simple/1";
    public const string FailureRetry = "resource-manager/backend/scheduling/transitions/runtime/simple/2";
    public const string PlacementActionTimeout = "resource-manager/backend/scheduling/placement/runtime/simple/0";
    public const string CpuAutomaticExclusivity = "resource-manager/backend/scheduling/placement/runtime/complex/0";
    public const string GpuWindowExecutionLimits = "resource-manager/backend/scheduling/placement/build/complex/0";
    public const string GpuApiObservationWindow = "resource-manager/backend/scheduling/placement/build/simple/0";
    public const string GpuNoPresentExpiry = "resource-manager/backend/scheduling/placement/build/simple/1";
    public const string SoftwareWelfare = "resource-manager/backend/scoring/build/complex/0";
    public const string BaseScoreTiers = "resource-manager/backend/scoring/runtime/complex/0";
    public const string CpuStateMultipliers = "resource-manager/backend/scoring/cpu/runtime/complex/0";
    public const string CpuSmtAccounting = "resource-manager/backend/scoring/cpu/build/complex/0";
    public const string CpuBaselineRatio = "resource-manager/backend/scoring/cpu/runtime/simple/0";
    public const string CpuCorePerformanceWeights = "resource-manager/backend/scoring/cpu/runtime/complex/1";
    public const string TrustedAccessRequestLifetime = "resource-manager/backend/trusted_access/build/simple/0";
    public const string HardwareBridgeAvailabilityCheckInterval = "resource-manager/backend/hardware_bridge/build/simple/0";
    public const string HardwareHelperRequestTimeout = "resource-manager/backend/hardware_bridge/build/simple/1";
    public const string HardwareHelperProcessTiming = "resource-manager/backend/hardware_bridge/build/complex/0";
    public const string FanCurveFeed = "resource-manager/backend/hardware_bridge/build/complex/1";
    public const string UniwillEcChannel = "resource-manager/backend/hardware_bridge/build/complex/2";
    public const string BusinessBlockWorkerCount = "resource-manager/backend/business_blocks/build/simple/0";
    public const string BusinessBlockHistoryCapacity = "resource-manager/backend/business_blocks/build/simple/1";
    public const string BusinessBlockCompletionCapacity = "resource-manager/backend/business_blocks/build/simple/2";
    public const string BusinessBlockReaderCapacity = "resource-manager/backend/business_blocks/build/simple/3";
    public const string BusinessBlockReaderBytes = "resource-manager/backend/business_blocks/build/simple/4";
    public const string BusinessBlockWorkerArenaBytes = "resource-manager/backend/business_blocks/build/simple/5";
    public const string BusinessBlockReasonTextCapacity = "resource-manager/backend/business_blocks/build/simple/6";
}
