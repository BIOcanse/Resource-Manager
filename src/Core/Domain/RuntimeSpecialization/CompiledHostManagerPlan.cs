using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerPlan(
    int SchemaVersion,
    int ProfileRevision,
    string ProfileName,
    string ProfileSource,
    string ProfileSha256,
    string PlanSha256,
    string BuildSha256,
    string RecreateSha256,
    string HotPublishSha256,
    ulong PlanEpoch,
    CompiledHostManagerBindingProvenance BindingProvenance,
    CompiledHostManagerBuildSpecializePlan BuildSpecialize,
    CompiledHostManagerRecreatePlan HostRecreate,
    CompiledHostManagerHotPublishPlan HotPublish,
    CompiledHostManagerSamplingSubscriptionPlan SamplingSubscription,
    CompiledHostManagerPortableSoftwareRegistryPlan PortableSoftwareRegistry,
    CompiledHostManagerSoftwareIdentityCatalogPlan SoftwareIdentityCatalog,
    CompiledHostManagerSoftwareIdentityResolutionPlan SoftwareIdentityResolution,
    CompiledHostManagerReportCoordinatorPlan ReportCoordinator,
    CompiledHostManagerFileQueryPlan FileQuery,
    CompiledHostManagerPublicServiceCoordinatorPlan PublicServiceCoordinator,
    CompiledHostManagerDisplayCoordinatorPlan DisplayCoordinator,
    CompiledHostManagerOperationCoordinatorPlan OperationCoordinator,
    CompiledHostManagerMetricSnapshotPlan MetricSnapshot,
    CompiledHostManagerSmartCoordinatorPlan SmartCoordinator,
    CompiledHostManagerDeploymentDigests DeploymentDigests)
{
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.KernelLeakObservation)]
    public Application.KernelLeak.KernelLeakOptions? KernelLeak { get; init; }
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.Notifications)]
    public Application.Notifications.NotificationOptions? Notifications { get; init; }
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.MemoryProtection)]
    public Application.MemoryProtection.HostMemoryProtectionRecordOptions? HostMemoryProtectionRecords { get; init; }
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.KernelEtwLimits)]
    public CompiledKernelEtwLimits? KernelEtwLimits { get; init; }
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.NativeBuildPolicy)]
    public CompiledNativeBuildPolicy? NativeBuildPolicy { get; init; }
    public CompiledDataHistoryPlan DataHistory { get; init; } = CompiledDataHistoryPlan.Empty;
    public CompiledFreedomPointTree FreedomPoints { get; init; } = CompiledFreedomPointTree.Empty;
    public CompiledCpuCoreResidencyPlan CpuCoreResidency { get; init; } = CompiledCpuCoreResidencyPlan.Unpublished;
    public CompiledCpuScoringPlan? CpuScoring { get; init; }
    public CompiledBackendControlSettings BackendControlSettings { get; init; } = CompiledBackendControlSettings.Unpublished;
    public CompiledHardwareControlSettings? HardwareControl { get; init; }
    public CompiledMonitoringSettings? Monitoring { get; init; }
    public CompiledOptimizationSettings? Optimization { get; init; }
    public CompiledGpuPlacementSettings? GpuPlacement { get; init; }
    public CompiledPersistenceSettings? Persistence { get; init; }
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.SubscriptionIntervalMs)]
    public TimeSpan SchedulerSamplingInterval { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.AvailabilityCheckIntervalMs)]
    public TimeSpan HardwareBridgeAvailabilityCheckInterval { get; init; }

    /// <summary>The memory/CPU tier table (freedom-point table memory_cpu_tiers); the settings owner resolves it.</summary>
    public CompiledMemoryCpuTierTable? MemoryCpuTiers { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.CrashCounterResetSeconds)]
    public int ServiceRecoveryResetPeriodSeconds { get; init; }
    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.CrashReports)]
    public Application.CrashReports.CrashReportOptions? CrashReports { get; init; }

    [global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.RequestLifetimeMs)]
    public int TrustedAccessRequestLifetimeMilliseconds { get; init; }

    public const int CurrentSchemaVersion = 42;

    public bool IsPublished => SchemaVersion == CurrentSchemaVersion
        && ProfileRevision > 0
        && PlanEpoch > 0
        && IsSha256(ProfileSha256)
        && IsSha256(PlanSha256)
        && IsSha256(BuildSha256)
        && IsSha256(RecreateSha256)
        && IsSha256(HotPublishSha256)
        && IsSha256(FreedomPoints.DeclarationSha256)
        && !string.IsNullOrWhiteSpace(FreedomPoints.Namespace)
        && !FreedomPoints.Children.IsDefaultOrEmpty
        && CpuCoreResidency.IsPublished
        && BackendControlSettings.IsPublished
        && HardwareControl is not null
        && Monitoring is not null
        && KernelEtwLimits is { IsPublished: true }
        && CpuScoring is not null
        && SchedulerSamplingInterval > TimeSpan.Zero
        && HardwareBridgeAvailabilityCheckInterval >= TimeSpan.Zero
        && TrustedAccessRequestLifetimeMilliseconds > 0
        && BindingProvenance.IsPublished
        && DeploymentDigests.IsPublished
        && BuildSpecialize.IsPublished
        && HotPublish.ResourceScheduler.Configuration is not null
        && SmartCoordinator.IsPublished
        && HotPublish.MemoryCleanup.IsPublished
        && HostRecreate.PlacementCoordinator.IsPublished
        && HotPublish.PlacementCoordinator.IsPublished
        && HostRecreate.AppliedOwnership.IsPublished
        && HotPublish.AppliedOwnership.IsPublished
        && HostRecreate.TransactionJournal.IsPublished
        && HotPublish.TransactionJournal.IsPublished
        && SamplingSubscription.IsPublished
        && PortableSoftwareRegistry.IsPublished
        && SoftwareIdentityCatalog.IsPublished
        && SoftwareIdentityResolution.IsPublished
        && ReportCoordinator.IsPublished
        && FileQuery.IsPublished
        && PublicServiceCoordinator.IsPublished
        && DisplayCoordinator.IsPublished
        && OperationCoordinator.IsPublished
        && MetricSnapshot.IsPublished
        && string.Equals(
            DeploymentDigests.MetricSnapshot.RecreateSha256,
            MetricSnapshot.RecreateSha256,
            StringComparison.Ordinal)
        && string.Equals(
            DeploymentDigests.MetricSnapshot.HotPublishSha256,
            MetricSnapshot.HotPublishSha256,
            StringComparison.Ordinal)
        && HostRecreate.AdapterPrivateResourceLedger.StateCapacity > 0
        && HostRecreate.AdapterInstanceLease.Capacity > 0
        && HostRecreate.AdapterInstanceLease.LeaseDurationMilliseconds > 0
        && HostRecreate.AdapterInstanceLease.MaximumAttestationAgeMilliseconds > 0
        && HostRecreate.PdhCollector.BaselineResetIntervalMilliseconds > 0
        && HotPublish.AdapterPrivateResourceLedger.MaximumSnapshotAgeMilliseconds > 0
        && HotPublish.AdapterPrivateResourceLedger.MaximumFutureClockSkewMilliseconds >= 0
        && HotPublish.AdapterPrivateResourceLedger.SettlementIntervalMilliseconds > 0
        && HotPublish.AdapterPrivateResourceLedger.RequestTimeoutMilliseconds > 0
        && HotPublish.AdapterPrivateResourceLedger.MaximumResponseBytes > 0
        && HotPublish.AdapterPrivateResourceLedger.MaximumConcurrentReads > 0
        && HotPublish.AdapterPrivateResourceLedger.MaximumCycleDurationMilliseconds
            >= HotPublish.AdapterPrivateResourceLedger.RequestTimeoutMilliseconds
        && HotPublish.AdapterPrivateResourceLedger.ActiveIncrement > 0
        && HotPublish.AdapterPrivateResourceLedger.DecayDenominator > 0
        && HotPublish.AdapterPrivateResourceLedger.DecayNumerator <= HotPublish.AdapterPrivateResourceLedger.DecayDenominator
        && HotPublish.ProcessPolicyExecutor.MaximumBatchItems > 0
        && HotPublish.PdhCollector.FrameReuseWindowMilliseconds >= 0
        && HotPublish.PdhCollector.LastGoodLifetimeMilliseconds >= 0;

    public CompiledHostManagerPlan RequirePublished()
    {
        if (!IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager plan has not been compiled and published.");
        }

        return this;
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(static character => char.IsAsciiHexDigit(character));

    public static CompiledHostManagerPlan Unpublished { get; } = new(
        0,
        0,
        "unpublished",
        "unpublished",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        CompiledHostManagerBindingProvenance.Unpublished,
        CompiledHostManagerBuildSpecializePlan.Unpublished,
        new CompiledHostManagerRecreatePlan(
            new CompiledHostManagerResourceSchedulerRecreatePlan(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0),
            new CompiledHostManagerAdapterPrivateResourceRecreatePlan(0),
            new CompiledHostManagerAdapterInstanceLeaseRecreatePlan(0, 0, 0),
            CompiledHostManagerSmartCoordinatorRecreatePlan.Unpublished,
            new CompiledHostManagerMemoryCleanupRecreatePlan(0),
            CompiledHostManagerPlacementCoordinatorRecreatePlan.Unpublished,
            CompiledHostManagerAppliedOwnershipRecreatePlan.Unpublished,
            CompiledHostManagerTransactionJournalRecreatePlan.Unpublished,
            CompiledHostManagerSamplingSubscriptionRecreatePlan.Unpublished,
            CompiledHostManagerPortableSoftwareRegistryRecreatePlan.Unpublished,
            CompiledHostManagerSoftwareIdentityCatalogRecreatePlan.Unpublished,
            CompiledHostManagerSoftwareIdentityResolutionRecreatePlan.Unpublished,
            CompiledHostManagerReportCoordinatorRecreatePlan.Unpublished,
            CompiledHostManagerFileQueryRecreatePlan.Unpublished,
            CompiledHostManagerPublicServiceCoordinatorRecreatePlan.Unpublished,
            CompiledHostManagerDisplayCoordinatorRecreatePlan.Unpublished,
            CompiledHostManagerOperationCoordinatorRecreatePlan.Unpublished,
            CompiledHostManagerMetricSnapshotRecreatePlan.Unpublished,
            new CompiledHostManagerPdhCollectorRecreatePlan(0)),
        new CompiledHostManagerHotPublishPlan(
            new CompiledHostManagerResourceSchedulerHotPublishPlan(
                null,
                false,
                0,
                0,
                0,
                0),
            new CompiledHostManagerAdapterPrivateResourceHotPublishPlan(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0),
            CompiledHostManagerSmartCoordinatorHotPublishPlan.Unpublished,
            CompiledHostManagerMemoryCleanupHotPublishPlan.Unpublished,
            CompiledHostManagerPlacementCoordinatorHotPublishPlan.Unpublished,
            CompiledHostManagerAppliedOwnershipHotPublishPlan.Unpublished,
            CompiledHostManagerTransactionJournalHotPublishPlan.Unpublished,
            CompiledHostManagerSamplingSubscriptionHotPublishPlan.Unpublished,
            CompiledHostManagerPortableSoftwareRegistryHotPublishPlan.Unpublished,
            CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan.Unpublished,
            CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan.Unpublished,
            CompiledHostManagerReportCoordinatorHotPublishPlan.Unpublished,
            CompiledHostManagerFileQueryHotPublishPlan.Unpublished,
            CompiledHostManagerPublicServiceCoordinatorHotPublishPlan.Unpublished,
            CompiledHostManagerOperationCoordinatorHotPublishPlan.Unpublished,
            CompiledHostManagerMetricSnapshotHotPublishPlan.Unpublished,
            new CompiledHostManagerProcessPolicyExecutorHotPublishPlan(0),
            new CompiledHostManagerPdhCollectorHotPublishPlan(0, 0)),
        CompiledHostManagerSamplingSubscriptionPlan.Unpublished,
        CompiledHostManagerPortableSoftwareRegistryPlan.Unpublished,
        CompiledHostManagerSoftwareIdentityCatalogPlan.Unpublished,
        CompiledHostManagerSoftwareIdentityResolutionPlan.Unpublished,
        CompiledHostManagerReportCoordinatorPlan.Unpublished,
        CompiledHostManagerFileQueryPlan.Unpublished,
        CompiledHostManagerPublicServiceCoordinatorPlan.Unpublished,
        CompiledHostManagerDisplayCoordinatorPlan.Unpublished,
        CompiledHostManagerOperationCoordinatorPlan.Unpublished,
        CompiledHostManagerMetricSnapshotPlan.Unpublished,
        CompiledHostManagerSmartCoordinatorPlan.Unpublished,
        CompiledHostManagerDeploymentDigests.Unpublished);
}

public enum HostManagerBindingId : ushort
{
    PhysicalMemoryOptimizationTargetUsagePercent = 1,
    VirtualMemoryOptimizationTargetUsagePercent = 2,
    GpuOverflowUsagePercent = 3,
    GpuOverflowDedicatedMemoryPercent = 4,
    PhysicalMemoryAutomaticCleanupPercent = 5,
    VirtualMemoryAutomaticCleanupPercent = 6,
    PublicServiceEnabled = 7,
    PublicServiceFileIndexEnabled = 8,
    PublicServiceDatabaseEnabled = 9,
    PublicServiceAiModelCatalogEnabled = 10,
    AiModelProvider = 11,
    AiModelEndpoint = 12,
    AiModelAutoStart = 13,
    ReportRuleOverrides = 14,
    RealtimeActionBudgetMode = 15,
    RealtimeActionBudgetCount = 16,
    RealtimeActionBudgetPercent = 17
}

public sealed record CompiledHostManagerBindingProvenance(
    AppSettingsSourceKind SettingsSourceKind,
    string SettingsStoragePath,
    string SettingsSourceVersion,
    string SettingsInputSha256,
    string SettingsEffectiveSha256,
    DateTimeOffset SettingsUpdatedAtUtc,
    bool SettingsRewritePerformed,
    ImmutableArray<HostManagerBindingId> AppliedBindingIds)
{
    public bool IsPublished => !string.IsNullOrWhiteSpace(SettingsStoragePath)
        && !string.IsNullOrWhiteSpace(SettingsSourceVersion)
        && SettingsInputSha256.Length == 64
        && SettingsEffectiveSha256.Length == 64
        && SettingsInputSha256.All(static value => char.IsAsciiHexDigit(value))
        && SettingsEffectiveSha256.All(static value => char.IsAsciiHexDigit(value))
        && AppliedBindingIds.Length == 17
        && AppliedBindingIds.Distinct().Count() == AppliedBindingIds.Length;

    public static CompiledHostManagerBindingProvenance Unpublished { get; } = new(
        AppSettingsSourceKind.BundledFirstRun,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        DateTimeOffset.MinValue,
        false,
        []);
}

public sealed record CompiledHostManagerDeploymentDigests(
    CompiledHostManagerModuleDigests ResourceScheduler,
    CompiledHostManagerModuleDigests AdapterPrivateResourceLedger,
    CompiledHostManagerModuleDigests SmartCoordinator,
    CompiledHostManagerModuleDigests MemoryCleanup,
    CompiledHostManagerModuleDigests PlacementCoordinator,
    CompiledHostManagerModuleDigests AppliedOwnership,
    CompiledHostManagerModuleDigests TransactionJournal,
    CompiledHostManagerModuleDigests SamplingSubscription,
    CompiledHostManagerModuleDigests PortableSoftwareRegistry,
    CompiledHostManagerModuleDigests SoftwareIdentityCatalog,
    CompiledHostManagerModuleDigests SoftwareIdentityResolution,
    CompiledHostManagerModuleDigests ReportCoordinator,
    CompiledHostManagerModuleDigests FileQuery,
    CompiledHostManagerModuleDigests PublicServiceCoordinator,
    CompiledHostManagerModuleDigests DisplayCoordinator,
    CompiledHostManagerModuleDigests OperationCoordinator,
    CompiledHostManagerModuleDigests MetricSnapshot,
    CompiledHostManagerModuleDigests ProcessPolicyExecutor,
    CompiledHostManagerModuleDigests PdhCollector)
{
    public bool IsPublished => ResourceScheduler.IsPublished
        && AdapterPrivateResourceLedger.IsPublished
        && SmartCoordinator.IsPublished
        && MemoryCleanup.IsPublished
        && PlacementCoordinator.IsPublished
        && AppliedOwnership.IsPublished
        && TransactionJournal.IsPublished
        && SamplingSubscription.IsPublished
        && PortableSoftwareRegistry.IsPublished
        && SoftwareIdentityCatalog.IsPublished
        && SoftwareIdentityResolution.IsPublished
        && ReportCoordinator.IsPublished
        && FileQuery.IsPublished
        && PublicServiceCoordinator.IsPublished
        && DisplayCoordinator.IsPublished
        && OperationCoordinator.IsPublished
        && MetricSnapshot.IsPublished
        && ProcessPolicyExecutor.IsPublished
        && PdhCollector.IsPublished;

    public static CompiledHostManagerDeploymentDigests Unpublished { get; } = new(
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished,
        CompiledHostManagerModuleDigests.Unpublished);
}

public sealed record CompiledHostManagerModuleDigests(
    string RecreateSha256,
    string HotPublishSha256)
{
    public bool IsPublished => IsSha256(RecreateSha256) && IsSha256(HotPublishSha256);

    public static CompiledHostManagerModuleDigests Unpublished { get; } = new(
        string.Empty,
        string.Empty);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(static character => char.IsAsciiHexDigit(character));
}

public sealed record CompiledHostManagerBuildSpecializePlan(
    CompiledHostManagerSmartCoordinatorBuildPlan SmartCoordinator,
    CompiledHostManagerSamplingSubscriptionBuildPlan SamplingSubscription,
    CompiledHostManagerPortableSoftwareRegistryBuildPlan PortableSoftwareRegistry,
    CompiledHostManagerSoftwareIdentityCatalogBuildPlan SoftwareIdentityCatalog,
    CompiledHostManagerSoftwareIdentityResolutionBuildPlan SoftwareIdentityResolution,
    CompiledHostManagerReportCoordinatorBuildPlan ReportCoordinator,
    CompiledHostManagerFileQueryBuildPlan FileQuery,
    CompiledHostManagerPublicServiceCoordinatorBuildPlan PublicServiceCoordinator,
    CompiledHostManagerDisplayCoordinatorBuildPlan DisplayCoordinator,
    CompiledHostManagerOperationCoordinatorBuildPlan OperationCoordinator,
    CompiledHostManagerMetricSnapshotBuildPlan MetricSnapshot,
    ImmutableArray<CompiledHostManagerNativeBinaryIdentity> NativeBinaries,
    CompiledHostManagerCapacityLimits CapacityLimits)
{
    public bool IsPublished => SmartCoordinator.IsPublished
        && SamplingSubscription.IsPublished
        && PortableSoftwareRegistry.IsPublished
        && SoftwareIdentityCatalog.IsPublished
        && SoftwareIdentityResolution.IsPublished
        && ReportCoordinator.IsPublished
        && FileQuery.IsPublished
        && PublicServiceCoordinator.IsPublished
        && DisplayCoordinator.IsPublished
        && OperationCoordinator.IsPublished
        && MetricSnapshot.IsPublished
        && NativeBinaries.Length == 2
        && NativeBinaries.All(static binary => binary.IsPublished);

    public static CompiledHostManagerBuildSpecializePlan Unpublished { get; } = new(
        CompiledHostManagerSmartCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerSamplingSubscriptionBuildPlan.Unpublished,
        CompiledHostManagerPortableSoftwareRegistryBuildPlan.Unpublished,
        CompiledHostManagerSoftwareIdentityCatalogBuildPlan.Unpublished,
        CompiledHostManagerSoftwareIdentityResolutionBuildPlan.Unpublished,
        CompiledHostManagerReportCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerFileQueryBuildPlan.Unpublished,
        CompiledHostManagerPublicServiceCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerDisplayCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerOperationCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerMetricSnapshotBuildPlan.Unpublished,
        [],
        new CompiledHostManagerCapacityLimits(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
}

public sealed record CompiledHostManagerNativeBinaryIdentity(
    string FileName,
    string Sha256,
    long LengthBytes,
    ImmutableArray<string> Modules)
{
    public bool IsPublished => !string.IsNullOrWhiteSpace(FileName)
        && Sha256.Length == 64
        && Sha256.All(static character => char.IsAsciiHexDigit(character))
        && LengthBytes > 0
        && !Modules.IsDefaultOrEmpty;
}

public sealed record CompiledHostManagerCapacityLimits(
    int SchedulerTargetCapacity,
    int SchedulerResourceCapacity,
    int SchedulerPendingCapacity,
    int AdapterPrivateResourceStateCapacity,
    int AdapterInstanceLeaseCapacity,
    int SmartCoordinatorProcessCapacity,
    int SmartCoordinatorSoftwareGroupCapacity,
    int SmartCoordinatorGpuStateCapacity,
    int SmartCoordinatorInputRowCapacity,
    int SmartCoordinatorActionCapacity,
    int SmartCoordinatorReservationCapacity,
    int SmartCoordinatorAtomicGroupCapacity,
    int ProcessPolicyBatchCapacity,
    int MemoryCleanupStateCapacity,
    int PlacementCoordinatorMaximumDesiredCapacity,
    int PlacementCoordinatorMaximumAppliedCapacity,
    int PlacementCoordinatorMaximumActionCapacity,
    int PlacementCoordinatorMaximumStateCapacity,
    int PlacementCoordinatorCoreCapacity,
    int PlacementCoordinatorCcdCapacity,
    int PlacementCoordinatorTargetCapacity,
    int PlacementCoordinatorReservationCapacity,
    int AppliedOwnershipRecordCapacity,
    int AppliedOwnershipPrimaryIndexCapacity,
    int AppliedOwnershipPayloadIndexCapacity,
    long AppliedOwnershipResidentByteBudget,
    long AppliedOwnershipImageByteBudget,
    int TransactionJournalRecordCapacity,
    long TransactionJournalResidentByteBudget,
    int TransactionJournalPayloadCount,
    long TransactionJournalPayloadByteBudget);

public sealed record CompiledHostManagerRecreatePlan(
    CompiledHostManagerResourceSchedulerRecreatePlan ResourceScheduler,
    CompiledHostManagerAdapterPrivateResourceRecreatePlan AdapterPrivateResourceLedger,
    CompiledHostManagerAdapterInstanceLeaseRecreatePlan AdapterInstanceLease,
    CompiledHostManagerSmartCoordinatorRecreatePlan SmartCoordinator,
    CompiledHostManagerMemoryCleanupRecreatePlan MemoryCleanup,
    CompiledHostManagerPlacementCoordinatorRecreatePlan PlacementCoordinator,
    CompiledHostManagerAppliedOwnershipRecreatePlan AppliedOwnership,
    CompiledHostManagerTransactionJournalRecreatePlan TransactionJournal,
    CompiledHostManagerSamplingSubscriptionRecreatePlan SamplingSubscription,
    CompiledHostManagerPortableSoftwareRegistryRecreatePlan PortableSoftwareRegistry,
    CompiledHostManagerSoftwareIdentityCatalogRecreatePlan SoftwareIdentityCatalog,
    CompiledHostManagerSoftwareIdentityResolutionRecreatePlan SoftwareIdentityResolution,
    CompiledHostManagerReportCoordinatorRecreatePlan ReportCoordinator,
    CompiledHostManagerFileQueryRecreatePlan FileQuery,
    CompiledHostManagerPublicServiceCoordinatorRecreatePlan PublicServiceCoordinator,
    CompiledHostManagerDisplayCoordinatorRecreatePlan DisplayCoordinator,
    CompiledHostManagerOperationCoordinatorRecreatePlan OperationCoordinator,
    CompiledHostManagerMetricSnapshotRecreatePlan MetricSnapshot,
    CompiledHostManagerPdhCollectorRecreatePlan PdhCollector);

public sealed record CompiledHostManagerResourceSchedulerRecreatePlan(
    int TargetCapacity,
    int PrivateResourceCapacity,
    int PendingCapacity,
    int JournalPendingCapacity,
    int AuthorityCapacity,
    int FeedbackCapacity,
    int PrivateLedgerCapacity,
    ulong MaximumResidentBytes);

public sealed record CompiledHostManagerAdapterInstanceLeaseRecreatePlan(
    int Capacity,
    int LeaseDurationMilliseconds,
    int MaximumAttestationAgeMilliseconds);

public sealed record CompiledHostManagerHotPublishPlan(
    CompiledHostManagerResourceSchedulerHotPublishPlan ResourceScheduler,
    CompiledHostManagerAdapterPrivateResourceHotPublishPlan AdapterPrivateResourceLedger,
    CompiledHostManagerSmartCoordinatorHotPublishPlan SmartCoordinator,
    CompiledHostManagerMemoryCleanupHotPublishPlan MemoryCleanup,
    CompiledHostManagerPlacementCoordinatorHotPublishPlan PlacementCoordinator,
    CompiledHostManagerAppliedOwnershipHotPublishPlan AppliedOwnership,
    CompiledHostManagerTransactionJournalHotPublishPlan TransactionJournal,
    CompiledHostManagerSamplingSubscriptionHotPublishPlan SamplingSubscription,
    CompiledHostManagerPortableSoftwareRegistryHotPublishPlan PortableSoftwareRegistry,
    CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan SoftwareIdentityCatalog,
    CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan SoftwareIdentityResolution,
    CompiledHostManagerReportCoordinatorHotPublishPlan ReportCoordinator,
    CompiledHostManagerFileQueryHotPublishPlan FileQuery,
    CompiledHostManagerPublicServiceCoordinatorHotPublishPlan PublicServiceCoordinator,
    CompiledHostManagerOperationCoordinatorHotPublishPlan OperationCoordinator,
    CompiledHostManagerMetricSnapshotHotPublishPlan MetricSnapshot,
    CompiledHostManagerProcessPolicyExecutorHotPublishPlan ProcessPolicyExecutor,
    CompiledHostManagerPdhCollectorHotPublishPlan PdhCollector);

public sealed record CompiledHostManagerResourceSchedulerHotPublishPlan(
    ResourceSchedulerConfig? Configuration,
    bool ExecutionCapabilityEnabled,
    int PerActionTimeoutMilliseconds,
    int PendingActionTtlMilliseconds,
    int MaximumInFlightActions,
    int MaximumInFlightActionsPerTarget);
