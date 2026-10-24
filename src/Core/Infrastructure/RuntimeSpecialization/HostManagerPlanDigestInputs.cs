// Ordered inputs to the existing JSON digest contract.
namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;
internal sealed record HostManagerPlanDigestInput(
    int SchemaVersion,
    int ProfileRevision,
    string ProfileSha256,
    string BuildSha256,
    string RecreateSha256,
    string HotPublishSha256,
    global::System.Collections.Immutable.ImmutableArray<global::ResourceManager.App.Domain.Monitoring.HistoryRequirement> DataHistory,
    global::ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints.CompiledFreedomPointTree FreedomPoints);

internal sealed record HostManagerHotPublishDigestInput(
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerHotPublishPlan Plan,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuScoringPlan CpuScoring);

internal sealed record PdhCollectorRecreateDigestInput(
    string Module,
    uint PdhCollectorAbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPdhCollectorRecreatePlan PdhCollector);

internal sealed record ProcessPolicyExecutorDigestInput(
    string Module,
    uint ProcessPolicyExecutorAbiVersion);

internal sealed record OperationCoordinatorHotPublishDigestInput(
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorHotPublishPlan OperationCoordinator);

internal sealed record OperationCoordinatorRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorRecreatePlan Recreate);

internal sealed record ModuleCapabilityDigestInput(
    string Module,
    string HotPublishCapability);

internal sealed record DisplayCoordinatorRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerDisplayCoordinatorBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerDisplayCoordinatorRecreatePlan Recreate);

internal sealed record PublicServiceCoordinatorHotPublishDigestInput(
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPublicServiceCoordinatorHotPublishPlan PublicServiceCoordinator);

internal sealed record PublicServiceCoordinatorRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPublicServiceCoordinatorBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPublicServiceCoordinatorRecreatePlan Recreate);

internal sealed record FileQueryHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerFileQueryHotPublishPlan FileQuery);

internal sealed record FileQueryRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerFileQueryBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerFileQueryRecreatePlan Recreate);

internal sealed record ReportCoordinatorHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportCoordinatorHotPublishPlan ReportCoordinator);

internal sealed record ReportCoordinatorRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportCoordinatorBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportCoordinatorRecreatePlan Recreate);

internal sealed record SoftwareIdentityResolutionHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan SoftwareIdentityResolution);

internal sealed record SoftwareIdentityResolutionRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityResolutionBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityResolutionRecreatePlan Recreate);

internal sealed record SoftwareIdentityCatalogHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan SoftwareIdentityCatalog);

internal sealed record SoftwareIdentityCatalogRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogRecreatePlan Recreate);

internal sealed record PortableSoftwareRegistryHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPortableSoftwareRegistryHotPublishPlan PortableSoftwareRegistry);

internal sealed record PortableSoftwareRegistryRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPortableSoftwareRegistryBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPortableSoftwareRegistryRecreatePlan Recreate);

internal sealed record SamplingSubscriptionHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSamplingSubscriptionHotPublishPlan SamplingSubscription);

internal sealed record SamplingSubscriptionRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSamplingSubscriptionBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSamplingSubscriptionRecreatePlan Recreate);

internal sealed record TransactionJournalBuildDigestInput(
    int TransactionJournalRecordCapacity,
    long TransactionJournalResidentByteBudget,
    int TransactionJournalPayloadCount,
    long TransactionJournalPayloadByteBudget);

internal sealed record TransactionJournalRecreateDigestInput(
    string Module,
    uint TransactionJournalAbiVersion,
    TransactionJournalBuildDigestInput BuildLimits,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerTransactionJournalRecreatePlan TransactionJournal);

internal sealed record AppliedOwnershipBuildDigestInput(
    int AppliedOwnershipRecordCapacity,
    int AppliedOwnershipPrimaryIndexCapacity,
    int AppliedOwnershipPayloadIndexCapacity,
    long AppliedOwnershipResidentByteBudget,
    long AppliedOwnershipImageByteBudget);

internal sealed record AppliedOwnershipRecreateDigestInput(
    string Module,
    uint AppliedOwnershipAbiVersion,
    AppliedOwnershipBuildDigestInput BuildLimits,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerAppliedOwnershipRecreatePlan AppliedOwnership);

internal sealed record PlacementCoordinatorRecreateDigestInput(
    string Module,
    uint PlacementCoordinatorAbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlacementCoordinatorRecreatePlan PlacementCoordinator);

internal sealed record MemoryCleanupRecreateDigestInput(
    string Module,
    uint MemoryCleanupAbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerMemoryCleanupRecreatePlan MemoryCleanup);

internal sealed record SmartCoordinatorHotPublishDigestInput(
    ulong ConfigurationGeneration,
    string ConfigurationSha256,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorHotPublishPlan SmartCoordinator);

internal sealed record SmartCoordinatorRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSmartCoordinatorRecreatePlan Recreate);

internal sealed record AdapterPrivateResourceRecreateDigestInput(
    string Module,
    uint AdapterPrivateResourceLedgerAbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerAdapterPrivateResourceRecreatePlan AdapterPrivateResourceLedger);

internal sealed record CpuCoreMappingDigestInput(
    int LogicalProcessorId,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledCpuPhysicalCoreAccounting PhysicalCore);

internal sealed record HostManagerBuildDigestInput(
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerBuildSpecializePlan Plan,
    FreedomPointDigestInput[] FreedomPoints,
    CpuCoreMappingDigestInput[] CpuCoreMapping);

internal sealed record FreedomPointDigestInput(
    string Address,
    global::System.Text.Json.JsonElement Value);

internal sealed record ResourceSchedulerGenerationInput(
    string SourceSha256,
    int ProfileRevision,
    byte PhysicalMemoryOptimizationTargetUsagePercent,
    byte VirtualMemoryOptimizationTargetUsagePercent);

internal sealed record FileQueryConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerFileQueryRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerFileQueryHotPublishPlan hotPublish);

internal sealed record MemoryCleanupGenerationInput(
    double CriticalFreeRatio,
    double VeryLowFreeRatio,
    double LowFreeRatio,
    double GuardedFreeRatio,
    double PhysicalEmergencyFreeRatio,
    double VirtualEmergencyFreeRatio,
    double HighTierMinimumBaseScore,
    int CriticalBatchCount,
    int VeryLowBatchCount,
    int LowBatchCount,
    int GuardedBatchCount,
    int EmergencyBatchCount);

internal sealed record MetricSnapshotHotPublishDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerMetricSnapshotHotPublishPlan HotPublish);

internal sealed record MetricSnapshotRecreateDigestInput(
    string Module,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerMetricSnapshotBuildPlan Build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerMetricSnapshotRecreatePlan Recreate);

internal sealed record OperationCoordinatorConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorHotPublishPlan hotPublish);

internal sealed record PortableSoftwareRegistryConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPortableSoftwareRegistryRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPortableSoftwareRegistryHotPublishPlan hotPublish);

internal sealed record PublicServiceCoordinatorConfigurationDigestInput(
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPublicServiceCoordinatorBuildPlan build,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPublicServiceCoordinatorRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPublicServiceCoordinatorHotPublishPlan hotPublish);

internal sealed record ReportCoordinatorConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportCoordinatorRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerReportCoordinatorHotPublishPlan hotPublish);

internal sealed record SamplingSubscriptionConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSamplingSubscriptionRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSamplingSubscriptionHotPublishPlan hotPublish);

internal sealed record SoftwareIdentityCatalogConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan hotPublish);

internal sealed record SoftwareIdentityResolutionConfigurationDigestInput(
    uint AbiVersion,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityResolutionRecreatePlan recreate,
    global::ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan hotPublish);

