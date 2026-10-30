using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using System.Collections.Immutable;
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
        var kernelLeak = CompileKernelLeak(freedom);
        var notifications = CompileNotifications(freedom);
        var memoryProtectionRecords = CompileHostMemoryProtectionRecords(freedom);
        var schedulerSamplingInterval = CompileSchedulerSamplingInterval(freedom);
        var serviceRecoveryReset = CompileServiceRecovery(freedom);
        var trustedAccessRequestLifetime = CompileTrustedAccessRequestLifetime(freedom);
        var hardwareBridgeAvailabilityCheckInterval = CompileHardwareBridgeAvailabilityCheckInterval(freedom);
        var backendControlSettings = BackendControlSettings.Compile(freedom);

        var buildSpecialize = CompileBuildSpecialize(
            profile.BuildSpecialize ?? throw Missing("build_specialize"));
        var reportObservationGrowthLimit = freedom.Consume<int>(BackendFreedomPointPaths.ReportObservationGrowthLimit,
            "HostManagerPlanCompiler.Compile");
        if (reportObservationGrowthLimit <= 0
            || reportObservationGrowthLimit > (1 << 29))
            throw new InvalidDataException("The report observation growth limit must cover the initial observation limit and fit the native index.");
        buildSpecialize = buildSpecialize with
        {
            ReportCoordinator = buildSpecialize.ReportCoordinator with { ObservationGrowthLimit = reportObservationGrowthLimit }
        };

        var recreate = profile.HostRecreate
            ?? throw Missing("host_recreate");
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
        var (recreateReportCoordinator, reportRuleSettings) = CompileReportCoordinatorRecreate(
            recreate.ReportCoordinator
                ?? throw Missing("host_recreate.report_coordinator"),
            buildSpecialize.ReportCoordinator,
            settingsInput.Settings.Reports);
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
        var hotResourceScheduler = hotPublish.ResourceScheduler
            ?? throw Missing("hot_publish.resource_scheduler");
        var adapterPrivateResource = CompileAdapterPrivateResourceHotPublish(
            hotPublish.AdapterPrivateResourceLedger
            ?? throw Missing("hot_publish.adapter_private_resource_ledger"));
        var smartCoordinatorHotPublish = CompileSmartCoordinatorHotPublish(
            hotPublish.SmartCoordinator
            ?? throw Missing("hot_publish.smart_coordinator"),
            recreateSmartCoordinator,
            performance,
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
            freedom,
            performance);
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
            reportCoordinatorHotPublish,
            reportRuleSettings);
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
        var resourceSchedulerConfigurationGeneration = HostManagerPlanIdentity.CreateGeneration(new ResourceSchedulerGenerationInput(loaded.SourceSha256, profile.ProfileRevision, performance.PhysicalMemoryOptimizationTargetUsagePercent, performance.VirtualMemoryOptimizationTargetUsagePercent));
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
            .Select(static point => new FreedomPointDigestInput(point.Address, point.Value!.Value))
            .ToArray();
        var buildDigest = HostManagerPlanIdentity.ComputeDigest(new HostManagerBuildDigestInput(buildSpecialize, buildFreedomPoints, cpuCoreResidency.PhysicalCoreByLogicalProcessor.OrderBy(static pair => pair.Key).Select(static pair => new CpuCoreMappingDigestInput(pair.Key, pair.Value)).ToArray()));
        var deploymentDigests = new CompiledHostManagerDeploymentDigests(
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(recreatePlan.ResourceScheduler),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.ResourceScheduler)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new AdapterPrivateResourceRecreateDigestInput("adapter_private_resource_ledger", AdapterPrivateResourceLedgerProtocol.Version, recreatePlan.AdapterPrivateResourceLedger)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.AdapterPrivateResourceLedger)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new SmartCoordinatorRecreateDigestInput("smart_coordinator", buildSpecialize.SmartCoordinator, recreatePlan.SmartCoordinator)),
                HostManagerPlanIdentity.ComputeDigest(new SmartCoordinatorHotPublishDigestInput(smartCoordinator.ConfigurationGeneration, smartCoordinator.ConfigurationSha256, hotPublishPlan.SmartCoordinator))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new MemoryCleanupRecreateDigestInput("memory_cleanup_planner", NativeMemoryCleanupAbi.Version, recreatePlan.MemoryCleanup)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.MemoryCleanup)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new PlacementCoordinatorRecreateDigestInput("placement_coordinator", NativePlacementCoordinatorAbi.Version, recreatePlan.PlacementCoordinator)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.PlacementCoordinator)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new AppliedOwnershipRecreateDigestInput("applied_ownership", NativeAppliedOwnershipAbi.Version, new AppliedOwnershipBuildDigestInput(buildSpecialize.CapacityLimits.AppliedOwnershipRecordCapacity, buildSpecialize.CapacityLimits.AppliedOwnershipPrimaryIndexCapacity, buildSpecialize.CapacityLimits.AppliedOwnershipPayloadIndexCapacity, buildSpecialize.CapacityLimits.AppliedOwnershipResidentByteBudget, buildSpecialize.CapacityLimits.AppliedOwnershipImageByteBudget), recreatePlan.AppliedOwnership)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.AppliedOwnership)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new TransactionJournalRecreateDigestInput("transaction_journal", NativeTransactionJournalAbi.Version, new TransactionJournalBuildDigestInput(buildSpecialize.CapacityLimits.TransactionJournalRecordCapacity, buildSpecialize.CapacityLimits.TransactionJournalResidentByteBudget, buildSpecialize.CapacityLimits.TransactionJournalPayloadCount, buildSpecialize.CapacityLimits.TransactionJournalPayloadByteBudget), recreatePlan.TransactionJournal)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.TransactionJournal)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new SamplingSubscriptionRecreateDigestInput("sampling_subscription", buildSpecialize.SamplingSubscription, recreatePlan.SamplingSubscription)),
                HostManagerPlanIdentity.ComputeDigest(new SamplingSubscriptionHotPublishDigestInput(samplingSubscription.ConfigurationGeneration, samplingSubscription.ConfigurationSha256, hotPublishPlan.SamplingSubscription))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new PortableSoftwareRegistryRecreateDigestInput("software_identity_portable_registry", buildSpecialize.PortableSoftwareRegistry, recreatePlan.PortableSoftwareRegistry)),
                HostManagerPlanIdentity.ComputeDigest(new PortableSoftwareRegistryHotPublishDigestInput(portableSoftwareRegistry.ConfigurationGeneration, portableSoftwareRegistry.ConfigurationSha256, hotPublishPlan.PortableSoftwareRegistry))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new SoftwareIdentityCatalogRecreateDigestInput("software_identity_catalog", buildSpecialize.SoftwareIdentityCatalog, recreatePlan.SoftwareIdentityCatalog)),
                HostManagerPlanIdentity.ComputeDigest(new SoftwareIdentityCatalogHotPublishDigestInput(softwareIdentityCatalog.ConfigurationGeneration, softwareIdentityCatalog.ConfigurationSha256, hotPublishPlan.SoftwareIdentityCatalog))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new SoftwareIdentityResolutionRecreateDigestInput("software_identity_resolution", buildSpecialize.SoftwareIdentityResolution, recreatePlan.SoftwareIdentityResolution)),
                HostManagerPlanIdentity.ComputeDigest(new SoftwareIdentityResolutionHotPublishDigestInput(softwareIdentityResolution.ConfigurationGeneration, softwareIdentityResolution.ConfigurationSha256, hotPublishPlan.SoftwareIdentityResolution))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new ReportCoordinatorRecreateDigestInput("report_coordinator", buildSpecialize.ReportCoordinator, recreatePlan.ReportCoordinator)),
                HostManagerPlanIdentity.ComputeDigest(new ReportCoordinatorHotPublishDigestInput(reportCoordinator.ConfigurationGeneration, reportCoordinator.ConfigurationSha256, hotPublishPlan.ReportCoordinator))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new FileQueryRecreateDigestInput("file_query", buildSpecialize.FileQuery, recreatePlan.FileQuery)),
                HostManagerPlanIdentity.ComputeDigest(new FileQueryHotPublishDigestInput(fileQuery.ConfigurationGeneration, fileQuery.ConfigurationSha256, hotPublishPlan.FileQuery))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new PublicServiceCoordinatorRecreateDigestInput("public_service_coordinator", buildSpecialize.PublicServiceCoordinator, recreatePlan.PublicServiceCoordinator)),
                HostManagerPlanIdentity.ComputeDigest(new PublicServiceCoordinatorHotPublishDigestInput(publicServiceCoordinator.ConfigurationSha256, hotPublishPlan.PublicServiceCoordinator))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new DisplayCoordinatorRecreateDigestInput("display_coordinator", buildSpecialize.DisplayCoordinator, recreatePlan.DisplayCoordinator)),
                HostManagerPlanIdentity.ComputeDigest(new ModuleCapabilityDigestInput("display_coordinator", "not_supported"))),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new OperationCoordinatorRecreateDigestInput("operation_coordinator", buildSpecialize.OperationCoordinator, recreatePlan.OperationCoordinator)),
                HostManagerPlanIdentity.ComputeDigest(new OperationCoordinatorHotPublishDigestInput(operationCoordinator.ConfigurationSha256, hotPublishPlan.OperationCoordinator))),
            new CompiledHostManagerModuleDigests(
                metricSnapshot.RecreateSha256,
                metricSnapshot.HotPublishSha256),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new ProcessPolicyExecutorDigestInput("process_policy_executor", NativeProcessPolicyBatchAbi.Version)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.ProcessPolicyExecutor)),
            new CompiledHostManagerModuleDigests(
                HostManagerPlanIdentity.ComputeDigest(new PdhCollectorRecreateDigestInput("pdh_collector", NativeCoreAbi.Version, recreatePlan.PdhCollector)),
                HostManagerPlanIdentity.ComputeDigest(hotPublishPlan.PdhCollector)));
        var recreateDigest = HostManagerPlanIdentity.ComputeDigest(recreatePlan);
        var hotPublishDigest = HostManagerPlanIdentity.ComputeDigest(new HostManagerHotPublishDigestInput(hotPublishPlan, compiledCpuScoring));
        var planDigest = HostManagerPlanIdentity.ComputeDigest(new HostManagerPlanDigestInput(profile.SchemaVersion, profile.ProfileRevision, loaded.SourceSha256, buildDigest, recreateDigest, hotPublishDigest, dataHistory.Requirements, freedomPoints));

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
            KernelLeak = kernelLeak,
            Notifications = notifications,
            HostMemoryProtectionRecords = memoryProtectionRecords,
            CpuScoring = compiledCpuScoring,
            SchedulerSamplingInterval = schedulerSamplingInterval,
            HardwareBridgeAvailabilityCheckInterval = hardwareBridgeAvailabilityCheckInterval,
            ServiceRecoveryResetPeriodSeconds = serviceRecoveryReset,
            TrustedAccessRequestLifetimeMilliseconds = trustedAccessRequestLifetime,
            BackendControlSettings = backendControlSettings,
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
                HostManagerBindingId.GpuOverflowUsagePercent,
                HostManagerBindingId.GpuOverflowDedicatedMemoryPercent,
                HostManagerBindingId.PhysicalMemoryAutomaticCleanupPercent,
                HostManagerBindingId.VirtualMemoryAutomaticCleanupPercent,
                HostManagerBindingId.PublicServiceEnabled,
                HostManagerBindingId.PublicServiceFileIndexEnabled,
                HostManagerBindingId.PublicServiceDatabaseEnabled,
                HostManagerBindingId.PublicServiceAiModelCatalogEnabled,
                HostManagerBindingId.AiModelProvider,
                HostManagerBindingId.AiModelEndpoint,
                HostManagerBindingId.AiModelAutoStart,
                HostManagerBindingId.ReportRuleOverrides,
                HostManagerBindingId.RealtimeActionBudgetMode,
                HostManagerBindingId.RealtimeActionBudgetCount,
                HostManagerBindingId.RealtimeActionBudgetPercent
            ]);
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(static character => char.IsAsciiHexDigit(character));

    private static CompiledHostManagerBuildSpecializePlan CompileBuildSpecialize(
        HostManagerBuildSpecializeProfile source)
    {
        var limits = source.CapacityLimits ?? throw Missing("build_specialize.capacity_limits");
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

        var nativeBinaries = CompileNativeBinaryIdentities();
        var smartCoordinatorBinary = nativeBinaries.Single(
            static binary => binary.Modules.Contains("smart_coordinator", StringComparer.Ordinal));
        var smartCoordinatorBuild = new CompiledHostManagerSmartCoordinatorBuildPlan(
            NativeSmartCoordinatorAbi.Version,
            "smart_coordinator",
            smartCoordinatorBinary.FileName,
            smartCoordinatorBinary.Sha256);
        var nativeCoreBinary = nativeBinaries.Single(
            static binary => binary.Modules.Contains("sampling_subscription", StringComparer.Ordinal));
        var samplingSubscriptionBuild = CompileSamplingSubscriptionBuild(
            limits.SamplingSubscriptionRoles,
            nativeCoreBinary);
        var portableSoftwareRegistryBuild = CompilePortableSoftwareRegistryBuild(
            limits.PortableSoftwareRegistry
            ?? throw Missing("build_specialize.capacity_limits.portable_software_registry"),
            nativeCoreBinary);
        var softwareIdentityCatalogBuild = CompileSoftwareIdentityCatalogBuild(
            limits.SoftwareIdentityCatalog
            ?? throw Missing("build_specialize.capacity_limits.software_identity_catalog"),
            nativeCoreBinary);
        var softwareIdentityResolutionBuild = CompileSoftwareIdentityResolutionBuild(
            limits.SoftwareIdentityResolution
            ?? throw Missing("build_specialize.capacity_limits.software_identity_resolution"),
            nativeCoreBinary);
        var reportCoordinatorBuild = CompileReportCoordinatorBuild(
            limits.ReportCoordinator
            ?? throw Missing("build_specialize.capacity_limits.report_coordinator"),
            nativeCoreBinary);
        var fileQueryBuild = CompileFileQueryBuild(
            limits.FileQuery
            ?? throw Missing("build_specialize.capacity_limits.file_query"),
            nativeCoreBinary);
        var publicServiceCoordinatorBuild = CompilePublicServiceCoordinatorBuild(
            limits.PublicServiceCoordinator
                ?? throw Missing(
                    "build_specialize.capacity_limits.public_service_coordinator"),
            nativeCoreBinary);
        var displayCoordinatorBuild = CompileDisplayCoordinatorBuild(
            limits.DisplayCoordinator
                ?? throw Missing(
                    "build_specialize.capacity_limits.display_coordinator"));
        var operationCoordinatorBuild = CompileOperationCoordinatorBuild(
            limits.OperationCoordinator
                ?? throw Missing(
                    "build_specialize.capacity_limits.operation_coordinator"),
            nativeCoreBinary);
        var metricSnapshotBuild = CompileMetricSnapshotBuild(
            limits.MetricSnapshot
                ?? throw Missing(
                    "build_specialize.capacity_limits.metric_snapshot"));

        return new CompiledHostManagerBuildSpecializePlan(
            smartCoordinatorBuild,
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
            nativeBinaries,
            new CompiledHostManagerCapacityLimits(
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
                limits.TransactionJournalPayloadByteBudget));
    }

    private static ImmutableArray<CompiledHostManagerNativeBinaryIdentity> CompileNativeBinaryIdentities()
    {
        ImmutableArray<string> adapterModules =
        [
            "adapter_private_resource_ledger",
            "resource_scheduler"
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
