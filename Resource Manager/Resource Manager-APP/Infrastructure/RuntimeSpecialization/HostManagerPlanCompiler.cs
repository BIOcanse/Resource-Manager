using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private const int SupportedSchemaVersion = CompiledHostManagerPlan.CurrentSchemaVersion;

    internal CompiledHostManagerPlan Compile(
        LoadedHostManagerProfile loaded,
        AppSettingsUpdateResult settingsInput,
        ulong planEpoch,
        CpuTopologySnapshot cpuTopology,
        CompiledCpuScoringPlan cpuScoring,
        FreedomPointRegistry? freedomPointRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(settingsInput);
        ArgumentNullException.ThrowIfNull(settingsInput.Settings);
        ArgumentNullException.ThrowIfNull(settingsInput.Source);
        var performance = settingsInput.Settings.Performance;
        var bindingProvenance = CompileBindingProvenance(settingsInput);
        if (planEpoch == 0)
        {
            throw new InvalidDataException("Host Manager plan epoch must be greater than zero.");
        }
        var profile = loaded.Profile;
        if (profile.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Host Manager profile schema_version {profile.SchemaVersion}; expected {SupportedSchemaVersion}.");
        }

        if (profile.ProfileRevision <= 0)
        {
            throw new InvalidDataException("Host Manager profile_revision must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileName))
        {
            throw new InvalidDataException("Host Manager profile_name must not be empty.");
        }

        var freedom = (freedomPointRegistry ?? FreedomPointRegistry.LoadEmbedded()).BeginCompilation(profile, cpuScoring);
        var compiledCpuScoring = CompileCpuScoring(freedom);
        var cpuCoreResidency = CompileCpuCoreResidency(freedom, cpuTopology);
        var schedulerSamplingInterval = CompileSchedulerSamplingInterval(freedom);

        var buildSpecialize = CompileBuildSpecialize(
            profile.BuildSpecialize ?? throw Missing("build_specialize"));

        var recreate = profile.HostRecreate
            ?? throw Missing("host_recreate");
        var recreateSharedResources = recreate.SharedResources
            ?? throw Missing("host_recreate.shared_resources");
        ValidatePositive(
            recreateSharedResources.ResourceCapacity,
            "host_recreate.shared_resources.resource_capacity");
        ValidateMaximum(
            recreateSharedResources.ResourceCapacity,
            buildSpecialize.CapacityLimits.SharedResourceCapacity,
            "host_recreate.shared_resources.resource_capacity");
        ValidateNonNegative(
            recreateSharedResources.SubscriptionCapacity,
            "host_recreate.shared_resources.subscription_capacity");
        ValidateMaximum(
            recreateSharedResources.SubscriptionCapacity,
            buildSpecialize.CapacityLimits.SharedSubscriptionCapacity,
            "host_recreate.shared_resources.subscription_capacity");
        ValidatePositive(
            recreateSharedResources.TaskCapacity,
            "host_recreate.shared_resources.task_capacity");
        ValidateMaximum(
            recreateSharedResources.TaskCapacity,
            buildSpecialize.CapacityLimits.SharedTaskCapacity,
            "host_recreate.shared_resources.task_capacity");
        ValidatePositive(
            recreateSharedResources.MaximumSubscriptionLeaseDurationMilliseconds,
            "host_recreate.shared_resources.maximum_subscription_lease_duration_ms");
        ValidatePositive(
            recreateSharedResources.MaximumQueueDurationMilliseconds,
            "host_recreate.shared_resources.maximum_queue_duration_ms");
        ValidatePositive(
            recreateSharedResources.MaximumGrantDurationMilliseconds,
            "host_recreate.shared_resources.maximum_grant_duration_ms");
        var recreateResourceScheduler = recreate.ResourceScheduler
            ?? throw Missing("host_recreate.resource_scheduler");
        ValidatePositive(
            recreateResourceScheduler.TargetCapacity,
            "host_recreate.resource_scheduler.target_capacity");
        ValidateMaximum(
            recreateResourceScheduler.TargetCapacity,
            buildSpecialize.CapacityLimits.SchedulerTargetCapacity,
            "host_recreate.resource_scheduler.target_capacity");
        ValidatePositive(
            recreateResourceScheduler.PrivateResourceCapacity,
            "host_recreate.resource_scheduler.private_resource_capacity");
        ValidateMaximum(
            recreateResourceScheduler.PrivateResourceCapacity,
            buildSpecialize.CapacityLimits.SchedulerResourceCapacity,
            "host_recreate.resource_scheduler.private_resource_capacity");
        ValidatePositive(
            recreateResourceScheduler.PendingCapacity,
            "host_recreate.resource_scheduler.pending_capacity");
        ValidateMaximum(
            recreateResourceScheduler.PendingCapacity,
            buildSpecialize.CapacityLimits.SchedulerPendingCapacity,
            "host_recreate.resource_scheduler.pending_capacity");
        ValidatePositive(
            recreateResourceScheduler.JournalPendingCapacity,
            "host_recreate.resource_scheduler.journal_pending_capacity");
        ValidateMaximum(
            recreateResourceScheduler.JournalPendingCapacity,
            buildSpecialize.CapacityLimits.SchedulerPendingCapacity,
            "host_recreate.resource_scheduler.journal_pending_capacity");
        ValidatePositive(
            recreateResourceScheduler.AuthorityCapacity,
            "host_recreate.resource_scheduler.authority_capacity");
        ValidatePositive(
            recreateResourceScheduler.FeedbackCapacity,
            "host_recreate.resource_scheduler.feedback_capacity");
        ValidatePositive(
            recreateResourceScheduler.PrivateLedgerCapacity,
            "host_recreate.resource_scheduler.private_ledger_capacity");
        ValidateMaximum(
            recreateResourceScheduler.PrivateLedgerCapacity,
            buildSpecialize.CapacityLimits.SchedulerTargetCapacity,
            "host_recreate.resource_scheduler.private_ledger_capacity");
        if (recreateResourceScheduler.MaximumResidentBytes == 0)
        {
            throw new InvalidDataException(
                "host_recreate.resource_scheduler.maximum_resident_bytes must be greater than zero.");
        }
        var requiredAuthorityCapacity = checked(
            recreateResourceScheduler.PrivateResourceCapacity * 3);
        if (recreateResourceScheduler.AuthorityCapacity < requiredAuthorityCapacity)
        {
            throw new InvalidDataException(
                "host_recreate.resource_scheduler.authority_capacity is smaller than the private authority set.");
        }
        var recreateAdapterPrivateResource = CompileAdapterPrivateResourceRecreate(
            recreate.AdapterPrivateResourceLedger
            ?? throw Missing("host_recreate.adapter_private_resource_ledger"),
            buildSpecialize.CapacityLimits);
        var recreateAdapterInstanceLease = recreate.AdapterInstanceLease
            ?? throw Missing("host_recreate.adapter_instance_lease");
        ValidatePositive(
            recreateAdapterInstanceLease.Capacity,
            "host_recreate.adapter_instance_lease.capacity");
        ValidateMaximum(
            recreateAdapterInstanceLease.Capacity,
            buildSpecialize.CapacityLimits.AdapterInstanceLeaseCapacity,
            "host_recreate.adapter_instance_lease.capacity");
        ValidatePositive(
            recreateAdapterInstanceLease.LeaseDurationMilliseconds,
            "host_recreate.adapter_instance_lease.lease_duration_ms");
        ValidatePositive(
            recreateAdapterInstanceLease.MaximumAttestationAgeMilliseconds,
            "host_recreate.adapter_instance_lease.maximum_attestation_age_ms");
        var recreateSmartCoordinator = CompileSmartCoordinatorRecreate(
            recreate.SmartCoordinator
            ?? throw Missing("host_recreate.smart_coordinator"),
            buildSpecialize.CapacityLimits);
        var recreateMemoryCleanup = recreate.MemoryCleanup
            ?? throw Missing("host_recreate.memory_cleanup");
        ValidatePositive(
            recreateMemoryCleanup.StateCapacity,
            "host_recreate.memory_cleanup.state_capacity");
        ValidateMaximum(
            recreateMemoryCleanup.StateCapacity,
            buildSpecialize.CapacityLimits.MemoryCleanupStateCapacity,
            "host_recreate.memory_cleanup.state_capacity");
        var recreatePlacementCoordinator = CompilePlacementCoordinatorRecreate(
            recreate.PlacementCoordinator
            ?? throw Missing("host_recreate.placement_coordinator"),
            buildSpecialize.CapacityLimits);
        var recreateAppliedOwnership = CompileAppliedOwnershipRecreate(
            recreate.AppliedOwnership
            ?? throw Missing("host_recreate.applied_ownership"),
            buildSpecialize.CapacityLimits);
        var recreateTransactionJournal = CompileTransactionJournalRecreate(
            recreate.TransactionJournal
            ?? throw Missing("host_recreate.transaction_journal"),
            buildSpecialize.CapacityLimits);
        var recreateSamplingSubscription = CompileSamplingSubscriptionRecreate(
            recreate.SamplingSubscription
            ?? throw Missing("host_recreate.sampling_subscription"),
            buildSpecialize.SamplingSubscription);
        var recreatePortableSoftwareRegistry = CompilePortableSoftwareRegistryRecreate(
            recreate.PortableSoftwareRegistry
            ?? throw Missing("host_recreate.portable_software_registry"),
            buildSpecialize.PortableSoftwareRegistry);
        var recreateSoftwareIdentityCatalog = CompileSoftwareIdentityCatalogRecreate(
            recreate.SoftwareIdentityCatalog
            ?? throw Missing("host_recreate.software_identity_catalog"),
            buildSpecialize.SoftwareIdentityCatalog);
        var recreateSoftwareIdentityResolution = CompileSoftwareIdentityResolutionRecreate(
            recreate.SoftwareIdentityResolution
            ?? throw Missing("host_recreate.software_identity_resolution"),
            buildSpecialize.SoftwareIdentityResolution);
        var recreateReportCoordinator = CompileReportCoordinatorRecreate(
            recreate.ReportCoordinator
                ?? throw Missing("host_recreate.report_coordinator"),
            buildSpecialize.ReportCoordinator);
        var recreateFileQuery = CompileFileQueryRecreate(
            recreate.FileQuery ?? throw Missing("host_recreate.file_query"),
            buildSpecialize.FileQuery);
        var recreatePublicServiceCoordinator = CompilePublicServiceCoordinatorRecreate(
            recreate.PublicServiceCoordinator
                ?? throw Missing("host_recreate.public_service_coordinator"),
            buildSpecialize.PublicServiceCoordinator);
        var recreateDisplayCoordinator = CompileDisplayCoordinatorRecreate(
            recreate.DisplayCoordinator
                ?? throw Missing("host_recreate.display_coordinator"),
            buildSpecialize.DisplayCoordinator,
            profile.ProfileRevision);
        var displayCoordinator = CompileDisplayCoordinatorPlan(
            buildSpecialize.DisplayCoordinator,
            recreateDisplayCoordinator);
        var recreateOperationCoordinator = CompileOperationCoordinatorRecreate(
            recreate.OperationCoordinator
                ?? throw Missing("host_recreate.operation_coordinator"),
            buildSpecialize.OperationCoordinator);
        var recreateMetricSnapshot = CompileMetricSnapshotRecreate(
            recreate.MetricSnapshot
                ?? throw Missing("host_recreate.metric_snapshot"),
            buildSpecialize.MetricSnapshot);
        ValidateAppliedOwnershipPathIsolation(
            recreateAppliedOwnership,
            recreateTransactionJournal);
        var recreatePdhCollector = CompilePdhCollectorRecreate(
            recreate.PdhCollector
            ?? throw Missing("host_recreate.pdh_collector"));

        var hotPublish = profile.HotPublish
            ?? throw Missing("hot_publish");
        var hotSharedResources = hotPublish.SharedResources
            ?? throw Missing("hot_publish.shared_resources");
        ValidateNonNegativeFinite(
            hotSharedResources.SubscriptionCoefficient,
            "hot_publish.shared_resources.subscription_coefficient");
        ValidatePositive(
            hotSharedResources.MaintenanceIntervalMilliseconds,
            "hot_publish.shared_resources.maintenance_interval_ms");
        var hotResourceScheduler = hotPublish.ResourceScheduler
            ?? throw Missing("hot_publish.resource_scheduler");
        var adapterPrivateResource = CompileAdapterPrivateResourceHotPublish(
            hotPublish.AdapterPrivateResourceLedger
            ?? throw Missing("hot_publish.adapter_private_resource_ledger"));
        var smartCoordinatorHotPublish = CompileSmartCoordinatorHotPublish(
            hotPublish.SmartCoordinator
            ?? throw Missing("hot_publish.smart_coordinator"),
            recreateSmartCoordinator,
            freedom);
        var memoryCleanup = CompileMemoryCleanup(
            hotPublish.MemoryCleanup
            ?? throw Missing("hot_publish.memory_cleanup"),
            smartCoordinatorHotPublish,
            recreateMemoryCleanup.StateCapacity,
            performance);
        var placementCoordinator = CompilePlacementCoordinatorHotPublish(
            hotPublish.PlacementCoordinator
            ?? throw Missing("hot_publish.placement_coordinator"),
            profile.ProfileRevision,
            freedom);
        var appliedOwnership = CompileAppliedOwnershipHotPublish(
            hotPublish.AppliedOwnership
            ?? throw Missing("hot_publish.applied_ownership"),
            profile.ProfileRevision);
        var transactionJournal = CompileTransactionJournalHotPublish(
            hotPublish.TransactionJournal
            ?? throw Missing("hot_publish.transaction_journal"),
            profile.ProfileRevision);
        var samplingSubscriptionHotPublish = CompileSamplingSubscriptionHotPublish(
            hotPublish.SamplingSubscription
            ?? throw Missing("hot_publish.sampling_subscription"),
            profile.ProfileRevision);
        var samplingSubscription = CompileSamplingSubscriptionPlan(
            buildSpecialize.SamplingSubscription,
            recreateSamplingSubscription,
            samplingSubscriptionHotPublish);
        var dataHistory = CompileHistory();
        var smartCoordinator = CompileSmartCoordinatorPlan(
            buildSpecialize.SmartCoordinator,
            recreateSmartCoordinator,
            smartCoordinatorHotPublish,
            dataHistory,
            profile.ProfileRevision);
        var portableSoftwareRegistryHotPublish = CompilePortableSoftwareRegistryHotPublish(
            hotPublish.PortableSoftwareRegistry
            ?? throw Missing("hot_publish.portable_software_registry"),
            profile.ProfileRevision);
        var portableSoftwareRegistry = CompilePortableSoftwareRegistryPlan(
            buildSpecialize.PortableSoftwareRegistry,
            recreatePortableSoftwareRegistry,
            portableSoftwareRegistryHotPublish);
        var softwareIdentityCatalogHotPublish = CompileSoftwareIdentityCatalogHotPublish(
            hotPublish.SoftwareIdentityCatalog
            ?? throw Missing("hot_publish.software_identity_catalog"),
            profile.ProfileRevision);
        var softwareIdentityCatalog = CompileSoftwareIdentityCatalogPlan(
            buildSpecialize.SoftwareIdentityCatalog,
            recreateSoftwareIdentityCatalog,
            softwareIdentityCatalogHotPublish);
        var softwareIdentityResolutionHotPublish = CompileSoftwareIdentityResolutionHotPublish(
            hotPublish.SoftwareIdentityResolution
            ?? throw Missing("hot_publish.software_identity_resolution"),
            recreateSoftwareIdentityResolution,
            profile.ProfileRevision);
        var softwareIdentityResolution = CompileSoftwareIdentityResolutionPlan(
            buildSpecialize.SoftwareIdentityResolution,
            recreateSoftwareIdentityResolution,
            softwareIdentityResolutionHotPublish);
        var reportCoordinatorHotPublish = CompileReportCoordinatorHotPublish(
            hotPublish.ReportCoordinator
            ?? throw Missing("hot_publish.report_coordinator"),
            profile.ProfileRevision);
        var reportCoordinator = CompileReportCoordinatorPlan(
            buildSpecialize.ReportCoordinator,
            recreateReportCoordinator,
            reportCoordinatorHotPublish);
        var fileQueryHotPublish = CompileFileQueryHotPublish(
            hotPublish.FileQuery ?? throw Missing("hot_publish.file_query"),
            buildSpecialize.FileQuery,
            recreateFileQuery,
            profile.ProfileRevision);
        var fileQuery = CompileFileQueryPlan(
            buildSpecialize.FileQuery,
            recreateFileQuery,
            fileQueryHotPublish);
        var publicServiceCoordinatorHotPublish =
            CompilePublicServiceCoordinatorHotPublish(
                hotPublish.PublicServiceCoordinator
                    ?? throw Missing("hot_publish.public_service_coordinator"),
                settingsInput.Settings,
                profile.ProfileRevision);
        var publicServiceCoordinator = CompilePublicServiceCoordinatorPlan(
            buildSpecialize.PublicServiceCoordinator,
            recreatePublicServiceCoordinator,
            publicServiceCoordinatorHotPublish);
        var operationCoordinatorHotPublish = CompileOperationCoordinatorHotPublish(
            hotPublish.OperationCoordinator
                ?? throw Missing("hot_publish.operation_coordinator"),
            recreateOperationCoordinator,
            profile.ProfileRevision);
        var operationCoordinator = CompileOperationCoordinatorPlan(
            buildSpecialize.OperationCoordinator,
            recreateOperationCoordinator,
            operationCoordinatorHotPublish);
        var metricSnapshotHotPublish = CompileMetricSnapshotHotPublish(
            hotPublish.MetricSnapshot
                ?? throw Missing("hot_publish.metric_snapshot"),
            recreateMetricSnapshot,
            profile.ProfileRevision);
        var metricSnapshot = CompileMetricSnapshotPlan(
            buildSpecialize.MetricSnapshot,
            recreateMetricSnapshot,
            metricSnapshotHotPublish);
        var processPolicyExecutor = CompileProcessPolicyExecutorHotPublish(
            hotPublish.ProcessPolicyExecutor
            ?? throw Missing("hot_publish.process_policy_executor"),
            buildSpecialize.CapacityLimits);
        var pdhCollector = CompilePdhCollectorHotPublish(
            hotPublish.PdhCollector
            ?? throw Missing("hot_publish.pdh_collector"));
        var resourceSchedulerConfigurationGeneration = HostManagerPlanIdentity.CreateGeneration(new
        {
            loaded.SourceSha256,
            profile.ProfileRevision,
            performance.PhysicalMemoryOptimizationTargetUsagePercent,
            performance.VirtualMemoryOptimizationTargetUsagePercent,
            performance.VramMoveDownPhysicalMemoryDangerPercent,
            performance.PhysicalMemoryMoveDownVirtualMemoryDangerPercent
        });
        var resourceSchedulerConfiguration = CompileResourceSchedulerConfiguration(
            hotResourceScheduler.Configuration
            ?? throw Missing("hot_publish.resource_scheduler.configuration"),
            performance,
            resourceSchedulerConfigurationGeneration);
        var resourceSchedulerDispatch = hotResourceScheduler.Dispatch
            ?? throw Missing("hot_publish.resource_scheduler.dispatch");
        ValidatePositive(
            resourceSchedulerDispatch.PerActionTimeoutMilliseconds,
            "hot_publish.resource_scheduler.dispatch.per_action_timeout_ms");
        ValidatePositive(
            resourceSchedulerDispatch.PendingActionTtlMilliseconds,
            "hot_publish.resource_scheduler.dispatch.pending_action_ttl_ms");
        ValidatePositive(
            resourceSchedulerDispatch.MaximumInFlightActions,
            "hot_publish.resource_scheduler.dispatch.maximum_in_flight_actions");
        ValidatePositive(
            resourceSchedulerDispatch.MaximumInFlightActionsPerTarget,
            "hot_publish.resource_scheduler.dispatch.maximum_in_flight_actions_per_target");
        if (resourceSchedulerDispatch.MaximumInFlightActionsPerTarget
            > resourceSchedulerDispatch.MaximumInFlightActions)
        {
            throw new InvalidDataException(
                "maximum_in_flight_actions_per_target must not exceed maximum_in_flight_actions.");
        }
        var recreatePlan = new CompiledHostManagerRecreatePlan(
            new CompiledHostManagerSharedResourceRecreatePlan(
                recreateSharedResources.ResourceCapacity,
                recreateSharedResources.SubscriptionCapacity,
                recreateSharedResources.TaskCapacity,
                recreateSharedResources.MaximumSubscriptionLeaseDurationMilliseconds,
                recreateSharedResources.MaximumQueueDurationMilliseconds,
                recreateSharedResources.MaximumGrantDurationMilliseconds),
            new CompiledHostManagerResourceSchedulerRecreatePlan(
                recreateResourceScheduler.TargetCapacity,
                recreateResourceScheduler.PrivateResourceCapacity,
                recreateResourceScheduler.PendingCapacity,
                recreateResourceScheduler.JournalPendingCapacity,
                recreateResourceScheduler.AuthorityCapacity,
                recreateResourceScheduler.FeedbackCapacity,
                recreateResourceScheduler.PrivateLedgerCapacity,
                recreateResourceScheduler.MaximumResidentBytes),
            recreateAdapterPrivateResource,
            new CompiledHostManagerAdapterInstanceLeaseRecreatePlan(
                recreateAdapterInstanceLease.Capacity,
                recreateAdapterInstanceLease.LeaseDurationMilliseconds,
                recreateAdapterInstanceLease.MaximumAttestationAgeMilliseconds),
            recreateSmartCoordinator,
            new CompiledHostManagerMemoryCleanupRecreatePlan(
                recreateMemoryCleanup.StateCapacity),
            recreatePlacementCoordinator,
            recreateAppliedOwnership,
            recreateTransactionJournal,
            recreateSamplingSubscription,
            recreatePortableSoftwareRegistry,
            recreateSoftwareIdentityCatalog,
            recreateSoftwareIdentityResolution,
            recreateReportCoordinator,
            recreateFileQuery,
            recreatePublicServiceCoordinator,
            recreateDisplayCoordinator,
            recreateOperationCoordinator,
            recreateMetricSnapshot,
            recreatePdhCollector);
        var hotPublishPlan = new CompiledHostManagerHotPublishPlan(
            new CompiledHostManagerSharedResourceHotPublishPlan(
                hotSharedResources.SubscriptionCoefficient,
                hotSharedResources.MaintenanceIntervalMilliseconds),
            new CompiledHostManagerResourceSchedulerHotPublishPlan(
                resourceSchedulerConfiguration,
                resourceSchedulerDispatch.ExecutionCapabilityEnabled,
                resourceSchedulerDispatch.PerActionTimeoutMilliseconds,
                resourceSchedulerDispatch.PendingActionTtlMilliseconds,
                resourceSchedulerDispatch.MaximumInFlightActions,
                resourceSchedulerDispatch.MaximumInFlightActionsPerTarget),
            adapterPrivateResource,
            smartCoordinatorHotPublish,
            memoryCleanup,
            placementCoordinator,
            appliedOwnership,
            transactionJournal,
            samplingSubscriptionHotPublish,
            portableSoftwareRegistryHotPublish,
            softwareIdentityCatalogHotPublish,
            softwareIdentityResolutionHotPublish,
            reportCoordinatorHotPublish,
            fileQueryHotPublish,
            publicServiceCoordinatorHotPublish,
            operationCoordinatorHotPublish,
            metricSnapshotHotPublish,
            processPolicyExecutor,
            pdhCollector);
        var freedomPoints = freedom.Complete();
        var buildFreedomPoints = freedomPoints.EnumeratePoints()
            .Where(static point => point.Status == "active"
                && point.UpdateClass == "rebuild_backend")
            .OrderBy(static point => point.Address, StringComparer.Ordinal)
            .Select(static point => new
            {
                point.Address,
                Value = point.Value!.Value
            })
            .ToArray();
        var buildDigest = HostManagerPlanIdentity.ComputeDigest(new
        {
            Plan = buildSpecialize,
            FreedomPoints = buildFreedomPoints,
            CpuCoreMapping = cpuCoreResidency.PhysicalCoreByLogicalProcessor
                .OrderBy(static pair => pair.Key)
                .Select(static pair => new { LogicalProcessorId = pair.Key, PhysicalCore = pair.Value })
                .ToArray()
        });
        var deploymentDigests = new CompiledHostManagerDeploymentDigests(
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(recreatePlan.SharedResources),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.SharedResources)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(recreatePlan.ResourceScheduler),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.ResourceScheduler)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "adapter_private_resource_ledger",
                    buildSpecialize.AdapterPrivateResourceLedgerAbiVersion,
                    recreatePlan.AdapterPrivateResourceLedger
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.AdapterPrivateResourceLedger)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "smart_coordinator",
                    Build = buildSpecialize.SmartCoordinator,
                    Recreate = recreatePlan.SmartCoordinator
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    smartCoordinator.ConfigurationGeneration,
                    smartCoordinator.ConfigurationSha256,
                    hotPublishPlan.SmartCoordinator
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "memory_cleanup_planner",
                    buildSpecialize.MemoryCleanupAbiVersion,
                    recreatePlan.MemoryCleanup
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.MemoryCleanup)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "placement_coordinator",
                    buildSpecialize.PlacementCoordinatorAbiVersion,
                    recreatePlan.PlacementCoordinator
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.PlacementCoordinator)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "applied_ownership",
                    buildSpecialize.AppliedOwnershipAbiVersion,
                    BuildLimits = new
                    {
                        buildSpecialize.CapacityLimits.AppliedOwnershipRecordCapacity,
                        buildSpecialize.CapacityLimits.AppliedOwnershipPrimaryIndexCapacity,
                        buildSpecialize.CapacityLimits.AppliedOwnershipPayloadIndexCapacity,
                        buildSpecialize.CapacityLimits.AppliedOwnershipResidentByteBudget,
                        buildSpecialize.CapacityLimits.AppliedOwnershipImageByteBudget
                    },
                    recreatePlan.AppliedOwnership
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.AppliedOwnership)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "transaction_journal",
                    buildSpecialize.TransactionJournalAbiVersion,
                    BuildLimits = new
                    {
                        buildSpecialize.CapacityLimits.TransactionJournalRecordCapacity,
                        buildSpecialize.CapacityLimits.TransactionJournalResidentByteBudget,
                        buildSpecialize.CapacityLimits.TransactionJournalPayloadCount,
                        buildSpecialize.CapacityLimits.TransactionJournalPayloadByteBudget
                    },
                    recreatePlan.TransactionJournal
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.TransactionJournal)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "sampling_subscription",
                    Build = buildSpecialize.SamplingSubscription,
                    Recreate = recreatePlan.SamplingSubscription
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    samplingSubscription.ConfigurationGeneration,
                    samplingSubscription.ConfigurationSha256,
                    hotPublishPlan.SamplingSubscription
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "software_identity_portable_registry",
                    Build = buildSpecialize.PortableSoftwareRegistry,
                    Recreate = recreatePlan.PortableSoftwareRegistry
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    portableSoftwareRegistry.ConfigurationGeneration,
                    portableSoftwareRegistry.ConfigurationSha256,
                    hotPublishPlan.PortableSoftwareRegistry
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "software_identity_catalog",
                    Build = buildSpecialize.SoftwareIdentityCatalog,
                    Recreate = recreatePlan.SoftwareIdentityCatalog
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    softwareIdentityCatalog.ConfigurationGeneration,
                    softwareIdentityCatalog.ConfigurationSha256,
                    hotPublishPlan.SoftwareIdentityCatalog
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "software_identity_resolution",
                    Build = buildSpecialize.SoftwareIdentityResolution,
                    Recreate = recreatePlan.SoftwareIdentityResolution
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    softwareIdentityResolution.ConfigurationGeneration,
                    softwareIdentityResolution.ConfigurationSha256,
                    hotPublishPlan.SoftwareIdentityResolution
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "report_coordinator",
                    Build = buildSpecialize.ReportCoordinator,
                    Recreate = recreatePlan.ReportCoordinator
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    reportCoordinator.ConfigurationGeneration,
                    reportCoordinator.ConfigurationSha256,
                    hotPublishPlan.ReportCoordinator
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "file_query",
                    Build = buildSpecialize.FileQuery,
                    Recreate = recreatePlan.FileQuery
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    fileQuery.ConfigurationGeneration,
                    fileQuery.ConfigurationSha256,
                    hotPublishPlan.FileQuery
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "public_service_coordinator",
                    Build = buildSpecialize.PublicServiceCoordinator,
                    Recreate = recreatePlan.PublicServiceCoordinator
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    publicServiceCoordinator.ConfigurationSha256,
                    hotPublishPlan.PublicServiceCoordinator
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "display_coordinator",
                    Build = buildSpecialize.DisplayCoordinator,
                    Recreate = recreatePlan.DisplayCoordinator
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "display_coordinator",
                    HotPublishCapability = "not_supported"
                })),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "operation_coordinator",
                    Build = buildSpecialize.OperationCoordinator,
                    Recreate = recreatePlan.OperationCoordinator
                }),
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    operationCoordinator.ConfigurationSha256,
                    hotPublishPlan.OperationCoordinator
                })),
            new CompiledHostManagerModuleDigests(
                metricSnapshot.RecreateSha256,
                metricSnapshot.HotPublishSha256),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "process_policy_executor",
                    buildSpecialize.ProcessPolicyExecutorAbiVersion
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.ProcessPolicyExecutor)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new
                {
                    Module = "pdh_collector",
                    buildSpecialize.PdhCollectorAbiVersion,
                    recreatePlan.PdhCollector
                }),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.PdhCollector)));
        var recreateDigest = HostManagerPlanIdentity.ComputeDigest(recreatePlan);
        var hotPublishDigest = HostManagerPlanIdentity.ComputeDigest(new { Plan = hotPublishPlan, CpuScoring = compiledCpuScoring });
        var planDigest = HostManagerPlanIdentity.ComputeDigest(new
        {
            profile.SchemaVersion,
            profile.ProfileRevision,
            ProfileSha256 = loaded.SourceSha256,
            BuildSha256 = buildDigest,
            RecreateSha256 = recreateDigest,
            HotPublishSha256 = hotPublishDigest,
            DataHistory = dataHistory.Requirements,
            FreedomPoints = freedomPoints
        });

        return new CompiledHostManagerPlan(
            profile.SchemaVersion,
            profile.ProfileRevision,
            profile.ProfileName,
            loaded.SourceDisplayName,
            loaded.SourceSha256,
            planDigest,
            buildDigest,
            recreateDigest,
            hotPublishDigest,
            planEpoch,
            bindingProvenance,
            buildSpecialize,
            recreatePlan,
            hotPublishPlan,
            samplingSubscription,
            portableSoftwareRegistry,
            softwareIdentityCatalog,
            softwareIdentityResolution,
            reportCoordinator,
            fileQuery,
            publicServiceCoordinator,
            displayCoordinator,
            operationCoordinator,
            metricSnapshot,
            smartCoordinator,
            deploymentDigests)
        {
            DataHistory = dataHistory,
            CpuCoreResidency = cpuCoreResidency,
            CpuScoring = compiledCpuScoring,
            SchedulerSamplingInterval = schedulerSamplingInterval,
            FreedomPoints = freedomPoints
        };
    }

    private static CompiledHostManagerBindingProvenance CompileBindingProvenance(
        AppSettingsUpdateResult settingsInput)
    {
        if (string.IsNullOrWhiteSpace(settingsInput.StoragePath))
        {
            throw new InvalidDataException("The Host Manager settings binding source path is empty.");
        }
        if (string.IsNullOrWhiteSpace(settingsInput.Source.SourceVersion)
            || !IsSha256(settingsInput.Source.InputSha256)
            || !IsSha256(settingsInput.Source.EffectiveSha256))
        {
            throw new InvalidDataException(
                "The Host Manager settings binding source identity is invalid.");
        }

        return new CompiledHostManagerBindingProvenance(
            settingsInput.Source.Kind,
            settingsInput.StoragePath,
            settingsInput.Source.SourceVersion,
            settingsInput.Source.InputSha256,
            settingsInput.Source.EffectiveSha256,
            settingsInput.UpdatedAt.ToUniversalTime(),
            settingsInput.Source.RewritePerformed,
            [
                HostManagerBindingId.PhysicalMemoryOptimizationTargetUsagePercent,
                HostManagerBindingId.VirtualMemoryOptimizationTargetUsagePercent,
                HostManagerBindingId.VramMoveDownPhysicalMemoryDangerPercent,
                HostManagerBindingId.PhysicalMemoryMoveDownVirtualMemoryDangerPercent,
                HostManagerBindingId.PhysicalMemoryAutomaticCleanupPercent,
                HostManagerBindingId.VirtualMemoryAutomaticCleanupPercent,
                HostManagerBindingId.PublicServiceEnabled,
                HostManagerBindingId.PublicServiceFileIndexEnabled,
                HostManagerBindingId.PublicServiceDatabaseEnabled,
                HostManagerBindingId.PublicServiceAiModelCatalogEnabled,
                HostManagerBindingId.AiModelProvider,
                HostManagerBindingId.AiModelEndpoint,
                HostManagerBindingId.AiModelAutoStart
            ]);
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(static character => char.IsAsciiHexDigit(character));

    private static CompiledHostManagerBuildSpecializePlan CompileBuildSpecialize(
        HostManagerBuildSpecializeProfile source)
    {
        var targetOperatingSystem = source.TargetOperatingSystem;
        var targetArchitecture = source.TargetArchitecture;
        if (targetOperatingSystem != "windows")
        {
            throw new InvalidDataException("build_specialize.target_os must be 'windows'.");
        }

        if (targetArchitecture != "x64")
        {
            throw new InvalidDataException("build_specialize.target_architecture must be 'x64'.");
        }

        if (source.SharedResourceAbiVersion != SharedResourceProtocol.Version)
        {
            throw new InvalidDataException("build_specialize.shared_resource_abi_version does not match the binary.");
        }

        if (source.ResourceSchedulerAbiVersion != ResourceSchedulerProtocol.Version)
        {
            throw new InvalidDataException("build_specialize.resource_scheduler_abi_version does not match the binary.");
        }

        if (source.AdapterPrivateResourceLedgerAbiVersion != AdapterPrivateResourceLedgerProtocol.Version)
        {
            throw new InvalidDataException("build_specialize.adapter_private_resource_ledger_abi_version does not match the binary.");
        }

        if (source.AdapterInstanceLeaseAbiVersion != NativeAdapterInstanceLeaseAbi.Version)
        {
            throw new InvalidDataException("build_specialize.adapter_instance_lease_abi_version does not match the binary.");
        }

        if (source.SmartCoordinatorAbiVersion != NativeSmartCoordinatorAbi.Version)
        {
            throw new InvalidDataException("build_specialize.smart_coordinator_abi_version does not match the binary.");
        }

        if (source.MemoryCleanupAbiVersion != NativeMemoryCleanupAbi.Version)
        {
            throw new InvalidDataException("build_specialize.memory_cleanup_abi_version does not match the binary.");
        }

        if (source.PlacementCoordinatorAbiVersion != NativePlacementCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.placement_coordinator_abi_version does not match the binary.");
        }

        if (source.AppliedOwnershipAbiVersion != AppliedOwnershipAbiVersion)
        {
            throw new InvalidDataException(
                "build_specialize.applied_ownership_abi_version does not match the configured ABI contract.");
        }

        if (source.TransactionJournalAbiVersion != TransactionJournalAbiVersion)
        {
            throw new InvalidDataException(
                "build_specialize.transaction_journal_abi_version does not match the configured ABI contract.");
        }

        if (source.SamplingSubscriptionAbiVersion != NativeSamplingSubscriptionAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.sampling_subscription_abi_version does not match the binary.");
        }

        if (source.PortableSoftwareRegistryAbiVersion != NativePortableSoftwareRegistryAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.portable_software_registry_abi_version does not match the binary.");
        }

        if (source.SoftwareIdentityCatalogAbiVersion != NativeSoftwareIdentityCatalogAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.software_identity_catalog_abi_version does not match the binary.");
        }

        if (source.SoftwareIdentityResolutionAbiVersion != NativeSoftwareIdentityResolutionAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.software_identity_resolution_abi_version does not match the binary.");
        }

        if (source.ReportCoordinatorAbiVersion != NativeReportCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.report_coordinator_abi_version does not match the binary.");
        }

        if (source.FileQueryAbiVersion != NativeFileQueryAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.file_query_abi_version does not match the binary.");
        }

        if (source.PublicServiceCoordinatorAbiVersion
            != NativePublicServiceCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.public_service_coordinator_abi_version does not match the binary.");
        }

        if (source.DisplayCoordinatorAbiVersion
            != NativeDisplayCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.display_coordinator_abi_version does not match the binary.");
        }

        if (source.OperationCoordinatorAbiVersion
            != NativeOperationCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.operation_coordinator_abi_version does not match the binary.");
        }

        if (source.ProcessPolicyExecutorAbiVersion != NativeProcessPolicyBatchAbi.Version)
        {
            throw new InvalidDataException("build_specialize.process_policy_executor_abi_version does not match the binary.");
        }

        if (source.PdhCollectorAbiVersion != NativeCoreAbi.Version)
        {
            throw new InvalidDataException("build_specialize.pdh_collector_abi_version does not match the binary.");
        }

        var modules = (source.NativeModules ?? throw Missing("build_specialize.native_modules"))
            .ToImmutableArray();
        if (!modules.SequenceEqual(
                [
                    "adapter_private_resource_ledger",
                    "adapter_instance_lease",
                    "applied_ownership",
                    "display_coordinator",
                    "file_query",
                    "memory_cleanup_planner",
                    "metric_snapshot",
                    "operation_coordinator",
                    "placement_coordinator",
                    "public_service_coordinator",
                    "report_coordinator",
                    "sampling_subscription",
                    "smart_coordinator",
                    "pdh_collector",
                    "process_policy_executor",
                    "resource_scheduler",
                    "shared_resource",
                    "software_identity_catalog",
                    "software_identity_portable_registry",
                    "software_identity_resolution",
                    "transaction_journal"
                ],
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "build_specialize.native_modules must contain exactly the configured Zig Host modules.");
        }

        var limits = source.CapacityLimits ?? throw Missing("build_specialize.capacity_limits");
        ValidatePositive(limits.SharedResourceCapacity, "build_specialize.capacity_limits.shared_resource_capacity");
        ValidatePositive(limits.SharedSubscriptionCapacity, "build_specialize.capacity_limits.shared_subscription_capacity");
        ValidatePositive(limits.SharedTaskCapacity, "build_specialize.capacity_limits.shared_task_capacity");
        ValidatePositive(limits.SchedulerTargetCapacity, "build_specialize.capacity_limits.scheduler_target_capacity");
        ValidatePositive(limits.SchedulerResourceCapacity, "build_specialize.capacity_limits.scheduler_resource_capacity");
        ValidatePositive(limits.SchedulerPendingCapacity, "build_specialize.capacity_limits.scheduler_pending_capacity");
        ValidatePositive(limits.AdapterPrivateResourceStateCapacity, "build_specialize.capacity_limits.adapter_private_resource_state_capacity");
        ValidatePositive(limits.AdapterInstanceLeaseCapacity, "build_specialize.capacity_limits.adapter_instance_lease_capacity");
        if (limits.AdapterInstanceLeaseCapacity != NativeAdapterInstanceLeaseAbi.HardCapacityLimit)
        {
            throw new InvalidDataException(
                "build_specialize.capacity_limits.adapter_instance_lease_capacity must match the native hard capacity limit.");
        }
        ValidatePositive(limits.SmartCoordinatorProcessCapacity, "build_specialize.capacity_limits.smart_coordinator_process_capacity");
        ValidatePositive(limits.SmartCoordinatorSoftwareGroupCapacity, "build_specialize.capacity_limits.smart_coordinator_software_group_capacity");
        ValidatePositive(limits.SmartCoordinatorGpuStateCapacity, "build_specialize.capacity_limits.smart_coordinator_gpu_state_capacity");
        ValidatePositive(limits.SmartCoordinatorInputRowCapacity, "build_specialize.capacity_limits.smart_coordinator_input_row_capacity");
        ValidatePositive(limits.SmartCoordinatorActionCapacity, "build_specialize.capacity_limits.smart_coordinator_action_capacity");
        ValidatePositive(limits.SmartCoordinatorReservationCapacity, "build_specialize.capacity_limits.smart_coordinator_reservation_capacity");
        ValidatePositive(limits.SmartCoordinatorAtomicGroupCapacity, "build_specialize.capacity_limits.smart_coordinator_atomic_group_capacity");
        ValidatePositive(limits.ProcessPolicyBatchCapacity, "build_specialize.capacity_limits.process_policy_batch_capacity");
        ValidatePositive(limits.MemoryCleanupStateCapacity, "build_specialize.capacity_limits.memory_cleanup_state_capacity");
        ValidatePositive(limits.PlacementCoordinatorMaximumDesiredCapacity, "build_specialize.capacity_limits.placement_coordinator_maximum_desired_capacity");
        ValidatePositive(limits.PlacementCoordinatorMaximumAppliedCapacity, "build_specialize.capacity_limits.placement_coordinator_maximum_applied_capacity");
        ValidatePositive(limits.PlacementCoordinatorMaximumActionCapacity, "build_specialize.capacity_limits.placement_coordinator_maximum_action_capacity");
        ValidatePositive(limits.PlacementCoordinatorMaximumStateCapacity, "build_specialize.capacity_limits.placement_coordinator_maximum_state_capacity");
        ValidatePositive(limits.PlacementCoordinatorCoreCapacity, "build_specialize.capacity_limits.placement_coordinator_core_capacity");
        ValidatePositive(limits.PlacementCoordinatorCcdCapacity, "build_specialize.capacity_limits.placement_coordinator_ccd_capacity");
        ValidatePositive(limits.PlacementCoordinatorTargetCapacity, "build_specialize.capacity_limits.placement_coordinator_target_capacity");
        ValidatePositive(limits.PlacementCoordinatorReservationCapacity, "build_specialize.capacity_limits.placement_coordinator_reservation_capacity");
        if (limits.PlacementCoordinatorReservationCapacity < limits.PlacementCoordinatorTargetCapacity)
        {
            throw new InvalidDataException(
                "build_specialize.capacity_limits.placement_coordinator_reservation_capacity must be at least placement_coordinator_target_capacity.");
        }
        ValidateAppliedOwnershipBuildLimits(limits);
        ValidatePositive(limits.TransactionJournalRecordCapacity, "build_specialize.capacity_limits.transaction_journal_record_capacity");
        ValidatePositive(limits.TransactionJournalResidentByteBudget, "build_specialize.capacity_limits.transaction_journal_resident_byte_budget");
        ValidatePositive(limits.TransactionJournalPayloadCount, "build_specialize.capacity_limits.transaction_journal_payload_count");
        ValidatePositive(limits.TransactionJournalPayloadByteBudget, "build_specialize.capacity_limits.transaction_journal_payload_byte_budget");

        var compatible = OperatingSystem.IsWindows()
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        if (!compatible)
        {
            throw new InvalidDataException("The current Host binary does not match build_specialize.");
        }

        var nativeBinaries = CompileNativeBinaryIdentities(modules);
        var smartCoordinatorBinary = nativeBinaries.Single(
            static binary => binary.Modules.Contains("smart_coordinator", StringComparer.Ordinal));
        var smartCoordinatorBuild = new CompiledHostManagerSmartCoordinatorBuildPlan(
            source.SmartCoordinatorAbiVersion,
            "smart_coordinator",
            smartCoordinatorBinary.FileName,
            smartCoordinatorBinary.Sha256);
        var nativeCoreBinary = nativeBinaries.Single(
            static binary => binary.Modules.Contains("sampling_subscription", StringComparer.Ordinal));
        var samplingSubscriptionBuild = CompileSamplingSubscriptionBuild(
            source.SamplingSubscriptionAbiVersion,
            limits.SamplingSubscriptionRoles,
            nativeCoreBinary);
        var portableSoftwareRegistryBuild = CompilePortableSoftwareRegistryBuild(
            source.PortableSoftwareRegistryAbiVersion,
            limits.PortableSoftwareRegistry
            ?? throw Missing("build_specialize.capacity_limits.portable_software_registry"),
            nativeCoreBinary);
        var softwareIdentityCatalogBuild = CompileSoftwareIdentityCatalogBuild(
            source.SoftwareIdentityCatalogAbiVersion,
            limits.SoftwareIdentityCatalog
            ?? throw Missing("build_specialize.capacity_limits.software_identity_catalog"),
            nativeCoreBinary);
        var softwareIdentityResolutionBuild = CompileSoftwareIdentityResolutionBuild(
            source.SoftwareIdentityResolutionAbiVersion,
            limits.SoftwareIdentityResolution
            ?? throw Missing("build_specialize.capacity_limits.software_identity_resolution"),
            nativeCoreBinary);
        var reportCoordinatorBuild = CompileReportCoordinatorBuild(
            source.ReportCoordinatorAbiVersion,
            limits.ReportCoordinator
            ?? throw Missing("build_specialize.capacity_limits.report_coordinator"),
            nativeCoreBinary);
        var fileQueryBuild = CompileFileQueryBuild(
            source.FileQueryAbiVersion,
            limits.FileQuery
            ?? throw Missing("build_specialize.capacity_limits.file_query"),
            nativeCoreBinary);
        var publicServiceCoordinatorBuild = CompilePublicServiceCoordinatorBuild(
            source.PublicServiceCoordinatorAbiVersion,
            limits.PublicServiceCoordinator
                ?? throw Missing(
                    "build_specialize.capacity_limits.public_service_coordinator"),
            nativeCoreBinary);
        var displayCoordinatorBuild = CompileDisplayCoordinatorBuild(
            source.DisplayCoordinatorAbiVersion,
            limits.DisplayCoordinator
                ?? throw Missing(
                    "build_specialize.capacity_limits.display_coordinator"),
            modules.ToHashSet(StringComparer.Ordinal));
        var operationCoordinatorBuild = CompileOperationCoordinatorBuild(
            source.OperationCoordinatorAbiVersion,
            limits.OperationCoordinator
                ?? throw Missing(
                    "build_specialize.capacity_limits.operation_coordinator"),
            nativeCoreBinary);
        var metricSnapshotBuild = CompileMetricSnapshotBuild(
            source.MetricSnapshotAbiVersion,
            limits.MetricSnapshot
                ?? throw Missing(
                    "build_specialize.capacity_limits.metric_snapshot"),
            modules.ToHashSet(StringComparer.Ordinal));

        return new CompiledHostManagerBuildSpecializePlan(
            targetOperatingSystem,
            targetArchitecture,
            source.SharedResourceAbiVersion,
            source.ResourceSchedulerAbiVersion,
            source.AdapterPrivateResourceLedgerAbiVersion,
            source.AdapterInstanceLeaseAbiVersion,
            smartCoordinatorBuild,
            source.MemoryCleanupAbiVersion,
            source.PlacementCoordinatorAbiVersion,
            source.AppliedOwnershipAbiVersion,
            source.TransactionJournalAbiVersion,
            samplingSubscriptionBuild,
            portableSoftwareRegistryBuild,
            softwareIdentityCatalogBuild,
            softwareIdentityResolutionBuild,
            reportCoordinatorBuild,
            fileQueryBuild,
            publicServiceCoordinatorBuild,
            displayCoordinatorBuild,
            operationCoordinatorBuild,
            metricSnapshotBuild,
            source.ProcessPolicyExecutorAbiVersion,
            source.PdhCollectorAbiVersion,
            modules,
            nativeBinaries,
            new CompiledHostManagerCapacityLimits(
                limits.SharedResourceCapacity,
                limits.SharedSubscriptionCapacity,
                limits.SharedTaskCapacity,
                limits.SchedulerTargetCapacity,
                limits.SchedulerResourceCapacity,
                limits.SchedulerPendingCapacity,
                limits.AdapterPrivateResourceStateCapacity,
                limits.AdapterInstanceLeaseCapacity,
                limits.SmartCoordinatorProcessCapacity,
                limits.SmartCoordinatorSoftwareGroupCapacity,
                limits.SmartCoordinatorGpuStateCapacity,
                limits.SmartCoordinatorInputRowCapacity,
                limits.SmartCoordinatorActionCapacity,
                limits.SmartCoordinatorReservationCapacity,
                limits.SmartCoordinatorAtomicGroupCapacity,
                limits.ProcessPolicyBatchCapacity,
                limits.MemoryCleanupStateCapacity,
                limits.PlacementCoordinatorMaximumDesiredCapacity,
                limits.PlacementCoordinatorMaximumAppliedCapacity,
                limits.PlacementCoordinatorMaximumActionCapacity,
                limits.PlacementCoordinatorMaximumStateCapacity,
                limits.PlacementCoordinatorCoreCapacity,
                limits.PlacementCoordinatorCcdCapacity,
                limits.PlacementCoordinatorTargetCapacity,
                limits.PlacementCoordinatorReservationCapacity,
                limits.AppliedOwnershipRecordCapacity,
                limits.AppliedOwnershipPrimaryIndexCapacity,
                limits.AppliedOwnershipPayloadIndexCapacity,
                limits.AppliedOwnershipResidentByteBudget,
                limits.AppliedOwnershipImageByteBudget,
                limits.TransactionJournalRecordCapacity,
                limits.TransactionJournalResidentByteBudget,
                limits.TransactionJournalPayloadCount,
                limits.TransactionJournalPayloadByteBudget),
            true);
    }

    private static ImmutableArray<CompiledHostManagerNativeBinaryIdentity> CompileNativeBinaryIdentities(
        ImmutableArray<string> configuredModules)
    {
        ImmutableArray<string> adapterModules =
        [
            "adapter_private_resource_ledger",
            "resource_scheduler",
            "shared_resource"
        ];
        ImmutableArray<string> nativeCoreModules =
        [
            "adapter_instance_lease",
            "applied_ownership",
            "display_coordinator",
            "file_query",
            "memory_cleanup_planner",
            "metric_snapshot",
            "operation_coordinator",
            "placement_coordinator",
            "public_service_coordinator",
            "report_coordinator",
            "sampling_subscription",
            "smart_coordinator",
            "pdh_collector",
            "process_policy_executor",
            "software_identity_catalog",
            "software_identity_portable_registry",
            "software_identity_resolution",
            "transaction_journal"
        ];
        var assignedModules = adapterModules
            .Concat(nativeCoreModules)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!assignedModules.SequenceEqual(
                configuredModules.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "build_specialize.native_modules must be fully assigned to the deployed Zig binaries.");
        }

        return
        [
            ReadNativeBinaryIdentity("ResourceManager.Adapter.Native.dll", adapterModules),
            ReadNativeBinaryIdentity("ResourceManager.NativeCore.dll", nativeCoreModules)
        ];
    }

    private static CompiledHostManagerNativeBinaryIdentity ReadNativeBinaryIdentity(
        string fileName,
        ImmutableArray<string> modules)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
        {
            throw new InvalidDataException(
                $"The configured Zig binary '{fileName}' is missing or empty at '{path}'.");
        }

        using var stream = file.OpenRead();
        return new CompiledHostManagerNativeBinaryIdentity(
            fileName,
            Convert.ToHexString(SHA256.HashData(stream)),
            file.Length,
            modules);
    }


    private static void ValidatePositive(int value, string path)
    {
        if (value <= 0)
        {
            throw new InvalidDataException($"{path} must be greater than zero.");
        }
    }

    private static void ValidateNonNegative(int value, string path)
    {
        if (value < 0)
        {
            throw new InvalidDataException($"{path} must be greater than or equal to zero.");
        }
    }

    private static void ValidateMaximum(int value, int maximum, string path)
    {
        if (value > maximum)
        {
            throw new InvalidDataException($"{path} must not exceed its build_specialize capacity limit {maximum}.");
        }
    }

    private static void ValidatePositiveFinite(double value, string path)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new InvalidDataException(
                $"{path} must be a finite number greater than zero.");
        }
    }

    private static void ValidateNonNegativeFinite(double value, string path)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new InvalidDataException(
                $"{path} must be a finite number greater than or equal to zero.");
        }
    }

    private static InvalidDataException Missing(string path)
        => new($"Required Host Manager profile object '{path}' is missing or null.");
}
