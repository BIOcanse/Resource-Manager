using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed record HostManagerProfileSource(
    string ManifestResourceName,
    string DisplayName);

internal sealed record LoadedHostManagerProfile(
    HostManagerConfigurationProfile Profile,
    string SourceDisplayName,
    string SourceSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerConfigurationProfile
{
    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("profile_revision")]
    public required int ProfileRevision { get; init; }

    [JsonPropertyName("profile_name")]
    public required string ProfileName { get; init; }

    [JsonPropertyName("build_specialize")]
    public required HostManagerBuildSpecializeProfile BuildSpecialize { get; init; }

    [JsonPropertyName("host_recreate")]
    public required HostManagerRecreateProfile HostRecreate { get; init; }

    [JsonPropertyName("hot_publish")]
    public required HostManagerHotPublishProfile HotPublish { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerRecreateProfile
{
    [JsonPropertyName("resource_scheduler")]
    public required HostManagerResourceSchedulerRecreateProfile ResourceScheduler { get; init; }

    [JsonPropertyName("adapter_private_resource_ledger")]
    public required HostManagerAdapterPrivateResourceRecreateProfile AdapterPrivateResourceLedger { get; init; }

    [JsonPropertyName("adapter_instance_lease")]
    public required HostManagerAdapterInstanceLeaseRecreateProfile AdapterInstanceLease { get; init; }

    [JsonPropertyName("smart_coordinator")]
    public required HostManagerSmartCoordinatorRecreateProfile SmartCoordinator { get; init; }

    [JsonPropertyName("memory_cleanup")]
    public required HostManagerMemoryCleanupRecreateProfile MemoryCleanup { get; init; }

    [JsonPropertyName("placement_coordinator")]
    public required HostManagerPlacementCoordinatorRecreateProfile PlacementCoordinator { get; init; }

    [JsonPropertyName("applied_ownership")]
    public required HostManagerAppliedOwnershipRecreateProfile AppliedOwnership { get; init; }

    [JsonPropertyName("transaction_journal")]
    public required HostManagerTransactionJournalRecreateProfile TransactionJournal { get; init; }

    [JsonPropertyName("sampling_subscription")]
    public required HostManagerSamplingSubscriptionRecreateProfile SamplingSubscription { get; init; }

    [JsonPropertyName("portable_software_registry")]
    public required HostManagerPortableSoftwareRegistryRecreateProfile PortableSoftwareRegistry { get; init; }

    [JsonPropertyName("software_identity_catalog")]
    public required HostManagerSoftwareIdentityCatalogRecreateProfile SoftwareIdentityCatalog { get; init; }

    [JsonPropertyName("software_identity_resolution")]
    public required HostManagerSoftwareIdentityResolutionRecreateProfile SoftwareIdentityResolution { get; init; }

    [JsonPropertyName("report_coordinator")]
    public required HostManagerReportCoordinatorRecreateProfile ReportCoordinator { get; init; }

    [JsonPropertyName("file_query")]
    public required HostManagerFileQueryRecreateProfile FileQuery { get; init; }

    [JsonPropertyName("public_service_coordinator")]
    public required HostManagerPublicServiceCoordinatorRecreateProfile PublicServiceCoordinator { get; init; }

    [JsonPropertyName("display_coordinator")]
    public required HostManagerDisplayCoordinatorRecreateProfile DisplayCoordinator { get; init; }

    [JsonPropertyName("operation_coordinator")]
    public required HostManagerOperationCoordinatorRecreateProfile OperationCoordinator { get; init; }

    [JsonPropertyName("metric_snapshot")]
    public required HostManagerMetricSnapshotRecreateProfile MetricSnapshot { get; init; }

    [JsonPropertyName("pdh_collector")]
    public required HostManagerPdhCollectorRecreateProfile PdhCollector { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerAdapterInstanceLeaseRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required int Capacity { get; init; }

    [JsonPropertyName("lease_duration_ms")]
    public required int LeaseDurationMilliseconds { get; init; }

    [JsonPropertyName("maximum_attestation_age_ms")]
    public required int MaximumAttestationAgeMilliseconds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerBuildSpecializeProfile
{
    [JsonPropertyName("capacity_limits")]
    public required HostManagerCapacityLimitsProfile CapacityLimits { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerCapacityLimitsProfile
{
    [JsonPropertyName("scheduler_target_capacity")]
    public required int SchedulerTargetCapacity { get; init; }

    [JsonPropertyName("scheduler_resource_capacity")]
    public required int SchedulerResourceCapacity { get; init; }

    [JsonPropertyName("scheduler_pending_capacity")]
    public required int SchedulerPendingCapacity { get; init; }

    [JsonPropertyName("adapter_private_resource_state_capacity")]
    public required int AdapterPrivateResourceStateCapacity { get; init; }

    [JsonPropertyName("adapter_instance_lease_capacity")]
    public required int AdapterInstanceLeaseCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_process_capacity")]
    public required int SmartCoordinatorProcessCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_software_group_capacity")]
    public required int SmartCoordinatorSoftwareGroupCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_gpu_state_capacity")]
    public required int SmartCoordinatorGpuStateCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_input_row_capacity")]
    public required int SmartCoordinatorInputRowCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_action_capacity")]
    public required int SmartCoordinatorActionCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_reservation_capacity")]
    public required int SmartCoordinatorReservationCapacity { get; init; }

    [JsonPropertyName("smart_coordinator_atomic_group_capacity")]
    public required int SmartCoordinatorAtomicGroupCapacity { get; init; }

    [JsonPropertyName("process_policy_batch_capacity")]
    public required int ProcessPolicyBatchCapacity { get; init; }

    [JsonPropertyName("memory_cleanup_state_capacity")]
    public required int MemoryCleanupStateCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_maximum_desired_capacity")]
    public required int PlacementCoordinatorMaximumDesiredCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_maximum_applied_capacity")]
    public required int PlacementCoordinatorMaximumAppliedCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_maximum_action_capacity")]
    public required int PlacementCoordinatorMaximumActionCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_maximum_state_capacity")]
    public required int PlacementCoordinatorMaximumStateCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_core_capacity")]
    public required int PlacementCoordinatorCoreCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_ccd_capacity")]
    public required int PlacementCoordinatorCcdCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_target_capacity")]
    public required int PlacementCoordinatorTargetCapacity { get; init; }

    [JsonPropertyName("placement_coordinator_reservation_capacity")]
    public required int PlacementCoordinatorReservationCapacity { get; init; }

    [JsonPropertyName("applied_ownership_record_capacity")]
    public required int AppliedOwnershipRecordCapacity { get; init; }

    [JsonPropertyName("applied_ownership_primary_index_capacity")]
    public required int AppliedOwnershipPrimaryIndexCapacity { get; init; }

    [JsonPropertyName("applied_ownership_payload_index_capacity")]
    public required int AppliedOwnershipPayloadIndexCapacity { get; init; }

    [JsonPropertyName("applied_ownership_resident_byte_budget")]
    public required long AppliedOwnershipResidentByteBudget { get; init; }

    [JsonPropertyName("applied_ownership_image_byte_budget")]
    public required long AppliedOwnershipImageByteBudget { get; init; }

    [JsonPropertyName("transaction_journal_record_capacity")]
    public required int TransactionJournalRecordCapacity { get; init; }

    [JsonPropertyName("transaction_journal_resident_byte_budget")]
    public required long TransactionJournalResidentByteBudget { get; init; }

    [JsonPropertyName("transaction_journal_payload_count")]
    public required int TransactionJournalPayloadCount { get; init; }

    [JsonPropertyName("transaction_journal_payload_byte_budget")]
    public required long TransactionJournalPayloadByteBudget { get; init; }

    [JsonPropertyName("sampling_subscription_roles")]
    public required HostManagerSamplingSubscriptionRoleCapacityProfile[] SamplingSubscriptionRoles { get; init; }

    [JsonPropertyName("portable_software_registry")]
    public required HostManagerPortableSoftwareRegistryCapacityProfile PortableSoftwareRegistry { get; init; }

    [JsonPropertyName("software_identity_catalog")]
    public required HostManagerSoftwareIdentityCatalogCapacityProfile SoftwareIdentityCatalog { get; init; }

    [JsonPropertyName("software_identity_resolution")]
    public required HostManagerSoftwareIdentityResolutionCapacityProfile SoftwareIdentityResolution { get; init; }

    [JsonPropertyName("report_coordinator")]
    public required HostManagerReportCoordinatorCapacityProfile ReportCoordinator { get; init; }

    [JsonPropertyName("file_query")]
    public required HostManagerFileQueryBuildCapacityProfile FileQuery { get; init; }

    [JsonPropertyName("public_service_coordinator")]
    public required HostManagerPublicServiceCoordinatorCapacityProfile PublicServiceCoordinator { get; init; }

    [JsonPropertyName("display_coordinator")]
    public required HostManagerDisplayCoordinatorCapacityProfile DisplayCoordinator { get; init; }

    [JsonPropertyName("operation_coordinator")]
    public required HostManagerOperationCoordinatorCapacityProfile OperationCoordinator { get; init; }

    [JsonPropertyName("metric_snapshot")]
    public required HostManagerMetricSnapshotCapacityProfile MetricSnapshot { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerHotPublishProfile
{
    [JsonPropertyName("resource_scheduler")]
    public required HostManagerResourceSchedulerHotPublishProfile ResourceScheduler { get; init; }

    [JsonPropertyName("adapter_private_resource_ledger")]
    public required HostManagerAdapterPrivateResourceHotPublishProfile AdapterPrivateResourceLedger { get; init; }

    [JsonPropertyName("smart_coordinator")]
    public required HostManagerSmartCoordinatorHotPublishProfile SmartCoordinator { get; init; }

    [JsonPropertyName("memory_cleanup")]
    public required HostManagerMemoryCleanupProfile MemoryCleanup { get; init; }

    [JsonPropertyName("placement_coordinator")]
    public required HostManagerPlacementCoordinatorHotPublishProfile PlacementCoordinator { get; init; }

    [JsonPropertyName("applied_ownership")]
    public required HostManagerAppliedOwnershipHotPublishProfile AppliedOwnership { get; init; }

    [JsonPropertyName("transaction_journal")]
    public required HostManagerTransactionJournalHotPublishProfile TransactionJournal { get; init; }

    [JsonPropertyName("sampling_subscription")]
    public required HostManagerSamplingSubscriptionHotPublishProfile SamplingSubscription { get; init; }

    [JsonPropertyName("portable_software_registry")]
    public required HostManagerPortableSoftwareRegistryHotPublishProfile PortableSoftwareRegistry { get; init; }

    [JsonPropertyName("software_identity_catalog")]
    public required HostManagerSoftwareIdentityCatalogHotPublishProfile SoftwareIdentityCatalog { get; init; }

    [JsonPropertyName("software_identity_resolution")]
    public required HostManagerSoftwareIdentityResolutionHotPublishProfile SoftwareIdentityResolution { get; init; }

    [JsonPropertyName("report_coordinator")]
    public required HostManagerReportCoordinatorHotPublishProfile ReportCoordinator { get; init; }

    [JsonPropertyName("file_query")]
    public required HostManagerFileQueryHotPublishProfile FileQuery { get; init; }

    [JsonPropertyName("public_service_coordinator")]
    public required HostManagerPublicServiceCoordinatorHotPublishProfile PublicServiceCoordinator { get; init; }

    [JsonPropertyName("operation_coordinator")]
    public required HostManagerOperationCoordinatorHotPublishProfile OperationCoordinator { get; init; }

    [JsonPropertyName("metric_snapshot")]
    public required HostManagerMetricSnapshotHotPublishProfile MetricSnapshot { get; init; }

    [JsonPropertyName("process_policy_executor")]
    public required HostManagerProcessPolicyExecutorHotPublishProfile ProcessPolicyExecutor { get; init; }

    [JsonPropertyName("pdh_collector")]
    public required HostManagerPdhCollectorHotPublishProfile PdhCollector { get; init; }
}
