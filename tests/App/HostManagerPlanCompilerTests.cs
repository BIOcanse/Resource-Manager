using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerPlanCompilerTests
{
    [Theory]
    [InlineData(10000)]
    [InlineData(25000)]
    [InlineData(0)]
    [InlineData(-1)]
    public void PhysicalMemoryResidualMinimumIsConfiguredAndPositive(long milliseconds)
    {
        var json = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        json["hot_publish"]!["sampling_subscription"]!["physical_memory_residual_minimum_interval_ms"] = milliseconds;
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        if (milliseconds <= 0)
            Assert.Throws<InvalidDataException>(() => Compile(StrictHostManagerProfileLoader.LoadBytes(bytes,"pfn-minimum")));
        else
            Assert.Equal(milliseconds, Compile(StrictHostManagerProfileLoader.LoadBytes(bytes,"pfn-minimum"))
                .SamplingSubscription.HotPublish.PhysicalMemoryResidualMinimumIntervalMilliseconds);
    }

    [Fact]
    public void DefaultProfilePublishesTheOperationFactPayloadAbi()
    {
        var plan = Compile(StrictHostManagerProfileLoader.LoadBytes(ReadDefaultProfile(), "test/default.json"));

        Assert.Equal(0x0002_0000U, plan.BuildSpecialize.OperationCoordinator.AbiVersion);
        Assert.Equal(NativeOperationCoordinatorAbi.Version, plan.OperationCoordinator.Build.AbiVersion);
        Assert.True(plan.BuildSpecialize.OperationCoordinator.IsPublished);
        Assert.True(plan.BuildSpecialize.IsPublished);
        Assert.True(plan.OperationCoordinator.Build.IsPublished);
        Assert.True(plan.OperationCoordinator.IsPublished);
        Assert.True(plan.IsPublished);

        var legacyBuild = plan.OperationCoordinator.Build with { AbiVersion = 0x0001_0000U };
        Assert.False(legacyBuild.IsPublished);
        Assert.False((plan.OperationCoordinator with { Build = legacyBuild }).IsPublished);
        Assert.False((plan with { OperationCoordinator = plan.OperationCoordinator with { Build = legacyBuild } }).IsPublished);
    }

    [Fact]
    public void DefaultProfile_CompilesCompleteHostPublicAndPrivateResourcePlans()
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "test/default.json");

        var plan = Compile(loaded);

        Assert.Equal(42, plan.SchemaVersion);
        Assert.Equal(68, plan.ProfileRevision);
        Assert.True(plan.IsPublished);
        Assert.Equal("default", plan.ProfileName);
        Assert.Equal(64, plan.PlanSha256.Length);
        Assert.Equal(64, plan.BuildSha256.Length);
        Assert.Equal(64, plan.RecreateSha256.Length);
        Assert.Equal(64, plan.HotPublishSha256.Length);
        Assert.True(plan.BuildSpecialize.IsCompatible);
        Assert.Equal("windows", plan.BuildSpecialize.TargetOperatingSystem);
        Assert.Equal("x64", plan.BuildSpecialize.TargetArchitecture);
        Assert.Equal(SharedResourceProtocol.Version, plan.BuildSpecialize.SharedResourceAbiVersion);
        Assert.Equal(ResourceSchedulerProtocol.Version, plan.BuildSpecialize.ResourceSchedulerAbiVersion);
        Assert.Equal(AdapterPrivateResourceLedgerProtocol.Version, plan.BuildSpecialize.AdapterPrivateResourceLedgerAbiVersion);
        Assert.Equal(NativeAdapterInstanceLeaseAbi.Version, plan.BuildSpecialize.AdapterInstanceLeaseAbiVersion);
        Assert.Equal(NativeSmartCoordinatorAbi.Version, plan.BuildSpecialize.SmartCoordinator.AbiVersion);
        Assert.Equal("smart_coordinator", plan.BuildSpecialize.SmartCoordinator.NativeModule);
        Assert.Equal(0x0001_0004U, plan.BuildSpecialize.MemoryCleanupAbiVersion);
        Assert.Equal(NativePlacementCoordinatorAbi.Version, plan.BuildSpecialize.PlacementCoordinatorAbiVersion);
        Assert.Equal(0x0002_0000U, plan.BuildSpecialize.AppliedOwnershipAbiVersion);
        Assert.Equal(0x0005_0000U, plan.BuildSpecialize.TransactionJournalAbiVersion);
        Assert.Equal(NativeSamplingSubscriptionAbi.Version, plan.BuildSpecialize.SamplingSubscription.AbiVersion);
        Assert.Equal(NativePortableSoftwareRegistryAbi.Version, plan.BuildSpecialize.PortableSoftwareRegistry.AbiVersion);
        Assert.Equal(NativeReportCoordinatorAbi.Version, plan.BuildSpecialize.ReportCoordinator.AbiVersion);
        Assert.Equal(
            NativePublicServiceCoordinatorAbi.Version,
            plan.BuildSpecialize.PublicServiceCoordinator.AbiVersion);
        Assert.Equal(
            NativeDisplayCoordinatorAbi.Version,
            plan.BuildSpecialize.DisplayCoordinator.AbiVersion);
        Assert.Equal(
            NativeMetricSnapshotAbi.Version,
            plan.BuildSpecialize.MetricSnapshot.AbiVersion);
        Assert.Equal("metric_snapshot", plan.BuildSpecialize.MetricSnapshot.NativeModule);
        Assert.Equal(0x0002_0000U, plan.BuildSpecialize.ProcessPolicyExecutorAbiVersion);
        Assert.Equal(0x0002_0000U, plan.BuildSpecialize.PdhCollectorAbiVersion);
        Assert.Equal<string>(
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
            plan.BuildSpecialize.NativeModules);
        Assert.Equal(2, plan.BuildSpecialize.NativeBinaries.Length);
        Assert.All(plan.BuildSpecialize.NativeBinaries, static binary =>
        {
            Assert.True(binary.IsPublished);
            Assert.Equal(64, binary.Sha256.Length);
            Assert.True(binary.LengthBytes > 0);
        });
        Assert.Equal(
            plan.BuildSpecialize.NativeModules.Order(StringComparer.Ordinal),
            plan.BuildSpecialize.NativeBinaries
                .SelectMany(static binary => binary.Modules)
                .Order(StringComparer.Ordinal));
        Assert.Equal(256, plan.HostRecreate.SharedResources.ResourceCapacity);
        Assert.Equal(1024, plan.HostRecreate.SharedResources.SubscriptionCapacity);
        Assert.Equal(2048, plan.HostRecreate.SharedResources.TaskCapacity);
        Assert.Equal(
            30_000,
            plan.HostRecreate.SharedResources.MaximumSubscriptionLeaseDurationMilliseconds);
        Assert.Equal(
            30_000,
            plan.HostRecreate.SharedResources.MaximumQueueDurationMilliseconds);
        Assert.Equal(
            30_000,
            plan.HostRecreate.SharedResources.MaximumGrantDurationMilliseconds);
        Assert.Equal(512, plan.HostRecreate.ResourceScheduler.TargetCapacity);
        Assert.Equal(8192, plan.HostRecreate.ResourceScheduler.PrivateResourceCapacity);
        Assert.Equal(2048, plan.HostRecreate.ResourceScheduler.PendingCapacity);
        Assert.Equal(4096, plan.HostRecreate.ResourceScheduler.JournalPendingCapacity);
        Assert.Equal(32768, plan.HostRecreate.ResourceScheduler.AuthorityCapacity);
        Assert.Equal(8192, plan.HostRecreate.ResourceScheduler.FeedbackCapacity);
        Assert.Equal(512, plan.HostRecreate.ResourceScheduler.PrivateLedgerCapacity);
        Assert.Equal(268435456UL, plan.HostRecreate.ResourceScheduler.MaximumResidentBytes);
        Assert.Equal(4096, plan.HostRecreate.AdapterInstanceLease.Capacity);
        Assert.Equal(30_000, plan.HostRecreate.AdapterInstanceLease.LeaseDurationMilliseconds);
        Assert.Equal(5_000, plan.HostRecreate.AdapterInstanceLease.MaximumAttestationAgeMilliseconds);
        Assert.Equal(65536, plan.BuildSpecialize.CapacityLimits.AdapterInstanceLeaseCapacity);
        Assert.Equal(262144, plan.BuildSpecialize.CapacityLimits.SmartCoordinatorGpuStateCapacity);
        Assert.Equal(4096, plan.HostRecreate.SmartCoordinator.MaximumProcesses);
        Assert.Equal(2048, plan.HostRecreate.SmartCoordinator.MaximumSoftwareGroups);
        Assert.Equal(32768, plan.HostRecreate.SmartCoordinator.MaximumGpuStates);
        Assert.Equal(32768, plan.HostRecreate.SmartCoordinator.MaximumInputRows);
        Assert.Equal(8192, plan.HostRecreate.SmartCoordinator.MaximumActions);
        Assert.Equal(8192, plan.HostRecreate.SmartCoordinator.MaximumReservations);
        Assert.Equal(4096, plan.HostRecreate.SmartCoordinator.MaximumAtomicGroups);
        Assert.Equal(8192, plan.HostRecreate.MemoryCleanup.StateCapacity);
        Assert.Equal(8192, plan.HostRecreate.PlacementCoordinator.MaximumDesiredCount);
        Assert.Equal(8192, plan.HostRecreate.PlacementCoordinator.MaximumAppliedCount);
        Assert.Equal(8192, plan.HostRecreate.PlacementCoordinator.MaximumActionCount);
        Assert.Equal(8192, plan.HostRecreate.PlacementCoordinator.MaximumStateCount);
        Assert.Equal(256, plan.HostRecreate.PlacementCoordinator.CoreCapacity);
        Assert.Equal(32, plan.HostRecreate.PlacementCoordinator.CcdCapacity);
        Assert.Equal(2048, plan.HostRecreate.PlacementCoordinator.TargetCapacity);
        Assert.Equal(2048, plan.HostRecreate.PlacementCoordinator.ReservationCapacity);
        Assert.Equal(8192, plan.HostRecreate.TransactionJournal.RecordCapacity);
        Assert.Equal(33_554_432L, plan.HostRecreate.TransactionJournal.ResidentByteBudget);
        Assert.Equal(8192, plan.HostRecreate.TransactionJournal.PayloadCount);
        Assert.Equal(268_435_456L, plan.HostRecreate.TransactionJournal.PayloadByteBudget);
        Assert.Equal("host-manager/transaction-journal/journal.bin", plan.HostRecreate.TransactionJournal.JournalRelativePath);
        Assert.Equal("host-manager/transaction-journal/payloads", plan.HostRecreate.TransactionJournal.PayloadRelativeDirectory);
        Assert.Equal(16, plan.HostRecreate.ReportCoordinator.Capacity.MaximumSourceCount);
        Assert.Equal(64, plan.HostRecreate.ReportCoordinator.Capacity.MaximumRuleCount);
        Assert.Equal(2048, plan.HostRecreate.ReportCoordinator.Capacity.MaximumObservationCount);
        Assert.Equal(1024, plan.HostRecreate.ReportCoordinator.Capacity.MaximumReportCount);
        Assert.Equal(32768, plan.HostRecreate.ReportCoordinator.Capacity.MaximumPersistenceOperationCount);
        Assert.Equal(
            (uint)(
                NativePublicServiceTaskOutcomeMask.ProviderUnavailable
                | NativePublicServiceTaskOutcomeMask.Timeout
                | NativePublicServiceTaskOutcomeMask.TransportFailure),
            plan.HostRecreate.PublicServiceCoordinator.RetryableTaskOutcomeMask);
        Assert.Equal(
            3,
            plan.HostRecreate.PublicServiceCoordinator.MaximumTaskAttemptCount);
        Assert.Equal(
            (uint)NativePublicServiceTaskHttpRetryPolicyMask.Known,
            plan.HostRecreate.PublicServiceCoordinator.RetryableHttpStatusPolicyMask);
        Assert.Equal(41UL, plan.PlanEpoch);

        var metricSnapshot = plan.MetricSnapshot;
        Assert.True(metricSnapshot.IsPublished);
        Assert.Equal(68UL << 32, metricSnapshot.HotPublish.ConfigurationGeneration);
        Assert.Equal(
            (68UL << 32) | 1UL,
            metricSnapshot.HotPublish.CatalogGenerationBase);
        Assert.Equal(1U, metricSnapshot.HotPublish.CatalogManifestVersion);
        Assert.Equal(64, metricSnapshot.HotPublish.CatalogManifestSha256.Length);
        Assert.Equal(24, metricSnapshot.HotPublish.SourcePolicies.Length);
        Assert.Equal(305, metricSnapshot.HotPublish.RuleTemplates.Length);
        Assert.Equal(10, metricSnapshot.HotPublish.RuleTemplates.Count(static rule => rule.ValueKind == 4));
        Assert.Equal(3, metricSnapshot.HotPublish.RuleTemplates.Count(static rule => rule.ValueKind == 5));
        Assert.Single(
            metricSnapshot.HotPublish.RuleTemplates,
            static rule => string.Equals(
                rule.MetricIdTemplate,
                "cpu.frequency",
                StringComparison.Ordinal)
                && string.Equals(
                    rule.SourceId,
                    "pdh.cpu-frequency",
                    StringComparison.Ordinal));
        Assert.Single(
            metricSnapshot.HotPublish.RuleTemplates,
            static rule => string.Equals(
                rule.MetricIdTemplate,
                "cpu.frequencyPercent",
                StringComparison.Ordinal)
                && string.Equals(
                    rule.SourceId,
                    "pdh.cpu-frequency",
                    StringComparison.Ordinal));
        Assert.Contains(
            metricSnapshot.HotPublish.SourcePolicies,
            static policy => string.Equals(
                policy.SourceId,
                "pdh.system-io",
                StringComparison.Ordinal)
                && policy.SourceRole == 1
                && policy.CapabilityMask == 8192);

        var shared = plan.HotPublish.SharedResources;
        Assert.Equal(0.25, shared.SubscriptionCoefficient);
        Assert.Equal(1000, shared.MaintenanceIntervalMilliseconds);

        var resourceScheduler = plan.HotPublish.ResourceScheduler;
        var configuration = Assert.IsType<ResourceSchedulerConfig>(resourceScheduler.Configuration);
        Assert.NotEqual(0UL, configuration.Generation);
        Assert.Equal(ResourceSchedulerConfig.ResourceKindCount, configuration.ResourceKindMultipliers.Length);
        Assert.Equal(ResourceSchedulerConfig.TierCount, configuration.TargetFreeRatios.Length);
        Assert.Equal(0.30, configuration.TargetFreeRatios[0], 12);
        Assert.Equal(0.30, configuration.TargetFreeRatios[1], 12);
        Assert.Equal(0.30, configuration.TargetFreeRatios[2], 12);
        Assert.Equal(1500, resourceScheduler.PerActionTimeoutMilliseconds);
        Assert.Equal(30000, resourceScheduler.PendingActionTtlMilliseconds);
        Assert.Equal(8, resourceScheduler.MaximumInFlightActions);
        Assert.Equal(1, resourceScheduler.MaximumInFlightActionsPerTarget);
        Assert.False(resourceScheduler.ExecutionCapabilityEnabled);

        var reportCoordinator = plan.ReportCoordinator;
        Assert.True(reportCoordinator.IsPublished);
        Assert.Equal(68UL << 32, reportCoordinator.ConfigurationGeneration);
        Assert.Equal(3_600_000, reportCoordinator.HotPublish.BucketWidthMilliseconds);
        Assert.Equal(86_400_000, reportCoordinator.HotPublish.Window24HoursMilliseconds);
        Assert.Equal(604_800_000, reportCoordinator.HotPublish.Window7DaysMilliseconds);
        Assert.Equal(134_217_728, reportCoordinator.HotPublish.ResidentByteBudget);

        var privateResource = plan.HotPublish.AdapterPrivateResourceLedger;
        Assert.Equal(30000, privateResource.MaximumSnapshotAgeMilliseconds);
        Assert.Equal(5000, privateResource.MaximumFutureClockSkewMilliseconds);
        Assert.Equal(5000, privateResource.SettlementIntervalMilliseconds);
        Assert.Equal(1500, privateResource.RequestTimeoutMilliseconds);
        Assert.Equal(4 * 1024 * 1024, privateResource.MaximumResponseBytes);
        Assert.Equal(8, privateResource.MaximumConcurrentReads);
        Assert.Equal(5000, privateResource.MaximumCycleDurationMilliseconds);
        Assert.Equal(64, privateResource.ActiveIncrement);
        Assert.Equal(75, privateResource.DecayNumerator);
        Assert.Equal(100, privateResource.DecayDenominator);

        var smartCoordinator = plan.SmartCoordinator;
        Assert.True(smartCoordinator.IsPublished);
        Assert.NotEqual(0UL, smartCoordinator.ConfigurationGeneration);
        Assert.Equal(64, smartCoordinator.ConfigurationSha256.Length);
        Assert.Equal(
            (ulong)(NativeSmartCoordinatorFeatures.ProcessPolicy
                | NativeSmartCoordinatorFeatures.AdapterCpu
                | NativeSmartCoordinatorFeatures.AdapterGpu
                | NativeSmartCoordinatorFeatures.CriticalEvents),
            smartCoordinator.HotPublish.FeatureFlags);
        Assert.Equal(30000, smartCoordinator.HotPublish.NormalIntervalMilliseconds);
        Assert.Equal(5000, smartCoordinator.HotPublish.EventIntervalMilliseconds);
        Assert.Equal(10, smartCoordinator.HotPublish.EventBoostRounds);
        Assert.Equal(10000, smartCoordinator.HotPublish.GameStartGraceMilliseconds);
        Assert.Equal(3, smartCoordinator.HotPublish.RequiredConsecutiveDecisions);
        Assert.Equal(5000, smartCoordinator.HotPublish.FailureRetryMilliseconds);
        Assert.Equal(30000, smartCoordinator.HotPublish.ReservationTimeoutMilliseconds);
        Assert.Equal(new CompiledHostManagerRealtimeActionBudgetPlan("fixed", 32, 5),
            smartCoordinator.HotPublish.RealtimeActionBudget);
        Assert.Equal<double>([1, 1.35, 1.12, 0.75, 0.55, 0.45, 0], smartCoordinator.HotPublish.ProcessStateMultipliers);
        Assert.Equal(120, smartCoordinator.HotPublish.A1MinimumCpuScore);
        Assert.Equal(81, smartCoordinator.HotPublish.BaseScoreTiers.HighMinimumBaseScore);
        Assert.Equal(21, smartCoordinator.HotPublish.BaseScoreTiers.MiddleMinimumBaseScore);
        var defaultSoftwareKinds = typeof(SoftwareKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static field => field.IsLiteral && !field.IsInitOnly)
            .Select(static field => Assert.IsType<string>(field.GetRawConstantValue()))
            .Append("UnknownFutureKind")
            .ToArray();
        Assert.All(defaultSoftwareKinds, kind => Assert.True(
            OptimizationRuntimeScoringDefaults.BaseScoreForKind(kind) >=
                smartCoordinator.HotPublish.BaseScoreTiers.MiddleMinimumBaseScore,
            $"Default software kind '{kind}' must not enter the configured low tier."));
        Assert.Equal(120, smartCoordinator.HotPublish.CpuAdapterPolicy.ExtremeMinimumScore);
        Assert.Equal(
            smartCoordinator.HotPublish.ProcessStateMultipliers,
            smartCoordinator.HotPublish.CpuAdapterPolicy.StateMultipliers);

        var memoryCleanup = plan.HotPublish.MemoryCleanup;
        Assert.True(memoryCleanup.IsPublished);
        Assert.Equal(0.06, memoryCleanup.CriticalFreeRatio);
        Assert.Equal(0.12, memoryCleanup.VeryLowFreeRatio);
        Assert.Equal(0.20, memoryCleanup.LowFreeRatio);
        Assert.Equal(0.30, memoryCleanup.GuardedFreeRatio);
        Assert.Equal(0.06, memoryCleanup.PhysicalEmergencyFreeRatio);
        Assert.Equal(0.08, memoryCleanup.VirtualEmergencyFreeRatio);
        Assert.Equal(81, memoryCleanup.HighTierMinimumBaseScore);
        Assert.Equal(6, memoryCleanup.CriticalBatchCount);
        Assert.Equal(4, memoryCleanup.VeryLowBatchCount);
        Assert.Equal(2, memoryCleanup.LowBatchCount);
        Assert.Equal(1, memoryCleanup.GuardedBatchCount);
        Assert.Equal(6, memoryCleanup.EmergencyBatchCount);

        var placement = plan.HotPublish.PlacementCoordinator;
        Assert.Equal(68UL << 32, placement.ConfigurationGeneration);
        Assert.Equal(5000, placement.RetryDelayMilliseconds);
        Assert.Equal(30000, placement.ActionTimeoutMilliseconds);
        Assert.Equal(5000, placement.MaximumFutureSkewMilliseconds);

        var transactionJournal = plan.HotPublish.TransactionJournal;
        Assert.Equal(68UL << 32, transactionJournal.ConfigurationGeneration);
        Assert.Equal(3, transactionJournal.MaximumRecoveryAttempts);
        Assert.Equal(5000, transactionJournal.RetryDelayMilliseconds);
        Assert.Equal(300000, transactionJournal.RecoveryDeadlineMilliseconds);
        Assert.Equal(5000, transactionJournal.MaximumFutureSkewMilliseconds);
        Assert.Equal(30000, transactionJournal.ShutdownDrainTimeoutMilliseconds);
    }

    [Fact]
    public void Loader_RejectsUnknownMissingDuplicateAndWrongJsonTypes()
    {
        var source = Encoding.UTF8.GetString(ReadDefaultProfile());
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(source.Replace("{", "{\"unknown\":1,", StringComparison.Ordinal)),
            "unknown.json"));

        var missing = JsonNode.Parse(source)!.AsObject();
        missing.Remove("profile_name");
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(missing.ToJsonString()),
            "missing.json"));

        var duplicate = source.Replace(
            "\"schema_version\": 42,",
            "\"schema_version\": 42, \"schema_version\": 42,",
            StringComparison.Ordinal);
        Assert.NotEqual(source, duplicate);
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(duplicate),
            "duplicate.json"));

        var wrongType = source.Replace(
            "\"resource_capacity\": 256",
            "\"resource_capacity\": \"256\"",
            StringComparison.Ordinal);
        Assert.NotEqual(source, wrongType);
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(wrongType),
            "wrong-type.json"));
    }

    [Fact]
    public void Loader_UsesCanonicalProfileDigest()
    {
        var source = ReadDefaultProfile();
        var compact = Encoding.UTF8.GetBytes(JsonNode.Parse(source)!.ToJsonString());

        var first = StrictHostManagerProfileLoader.LoadBytes(source, "pretty.json");
        var second = StrictHostManagerProfileLoader.LoadBytes(compact, "compact.json");

        Assert.Equal(first.SourceSha256, second.SourceSha256);
    }

    [Fact]
    public void MetricSnapshotManifestRejectsMissingDuplicateAndInvalidBindings()
    {
        var missingRules = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        missingRules["hot_publish"]!["metric_snapshot"]!.AsObject()
            .Remove("rule_templates");
        Assert.Throws<InvalidDataException>(() =>
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(missingRules.ToJsonString()),
                "metric-missing-rules.json"));

        var duplicateTemplate = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        var duplicateTemplates = duplicateTemplate["hot_publish"]![
            "metric_snapshot"]!["rule_templates"]!.AsArray();
        duplicateTemplates[1]!["template_id"] =
            duplicateTemplates[0]!["template_id"]!.GetValue<string>();
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(duplicateTemplate.ToJsonString()),
                "metric-duplicate-template.json")));

        var unknownSource = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        unknownSource["hot_publish"]!["metric_snapshot"]![
            "rule_templates"]![0]!["source_id"] = "missing.source";
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(unknownSource.ToJsonString()),
                "metric-unknown-source.json")));

        var inventoryAsMetricSource =
            JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        inventoryAsMetricSource["hot_publish"]!["metric_snapshot"]![
            "rule_templates"]![0]!["source_id"] =
            "windows.gpu-adapter-order";
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(
                    inventoryAsMetricSource.ToJsonString()),
                "metric-inventory-source.json")));
    }

    [Fact]
    public void MetricSnapshotManifestDriftChangesHotDigestAtSameGeneration()
    {
        var first = Compile(StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "metric-first.json"));
        var changedJson = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        changedJson["hot_publish"]!["metric_snapshot"]![
            "rule_templates"]![0]!["semantic_fingerprint"] = 50001;
        var changed = Compile(StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(changedJson.ToJsonString()),
            "metric-changed.json"));

        Assert.Equal(
            first.MetricSnapshot.HotPublish.ConfigurationGeneration,
            changed.MetricSnapshot.HotPublish.ConfigurationGeneration);
        Assert.NotEqual(
            first.MetricSnapshot.HotPublish.CatalogManifestSha256,
            changed.MetricSnapshot.HotPublish.CatalogManifestSha256);
        Assert.NotEqual(
            first.DeploymentDigests.MetricSnapshot.HotPublishSha256,
            changed.DeploymentDigests.MetricSnapshot.HotPublishSha256);
    }

    [Fact]
    public void MetricSnapshotManifestRejectsExpansionOverflowAndInvalidGrammar()
    {
        var overflow = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        var storageTemplate = overflow["hot_publish"]!["metric_snapshot"]![
            "rule_templates"]!.AsArray().First(node =>
                string.Equals(
                    node!["scope_expansion"]!.GetValue<string>(),
                    "storage_sensor",
                    StringComparison.Ordinal));
        storageTemplate!["maximum_instance_count"] = 4096;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(overflow.ToJsonString()),
                "metric-expansion-overflow.json")));

        var unknownPlaceholder =
            JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        unknownPlaceholder["hot_publish"]!["metric_snapshot"]![
            "rule_templates"]![0]!["metric_id_template"] =
            "cpu.{unknown}";
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(
                    unknownPlaceholder.ToJsonString()),
                "metric-unknown-placeholder.json")));

        var conflictingFlags =
            JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        conflictingFlags["hot_publish"]!["metric_snapshot"]![
            "rule_templates"]![0]!["metric_flags"] =
            new JsonArray("used_value", "total_value");
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(
                    conflictingFlags.ToJsonString()),
                "metric-conflicting-flags.json")));
    }

    [Fact]
    public void CompilerRejectsUnknownOrDuplicatePublicServiceTaskOutcomes()
    {
        var unknown = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        unknown["host_recreate"]!["public_service_coordinator"]![
            "retryable_task_outcomes"]![0] = "future_outcome";
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(unknown.ToJsonString()),
                "unknown-public-service-outcome.json")));

        var duplicate = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        duplicate["host_recreate"]!["public_service_coordinator"]![
            "retryable_task_outcomes"]![1] = "provider_unavailable";
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(duplicate.ToJsonString()),
                "duplicate-public-service-outcome.json")));
    }

    [Theory]
    [InlineData(10622, true)]
    [InlineData(10621, false)]
    public void DieExpansionCountsEveryInstanceAtTheActualMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        // A profile can remove optional ADLX fields; retain that capacity boundary too.
        RemoveIntelGpuRules(profile);
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["metric_id_template"]!.GetValue<string>()
            is "gpu.{gpu}.adlxActivity" or "gpu.{gpu}.adlxVoltage" or "gpu.{gpu}.adlxSampleTimestamp" or "gpu.{gpu}.adlxDedicatedMemoryMb").ToArray())
            rules.Remove(rule);
        AssertLegacyMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(10670, true)]
    [InlineData(10669, false)]
    public void OptionalAdlxExpansionUsesConfiguredGpuLimitAtMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        RemoveIntelGpuRules(profile);
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["metric_id_template"]!.GetValue<string>() == "gpu.{gpu}.adlxDedicatedMemoryMb").ToArray())
            rules.Remove(rule);
        AssertLegacyMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(10686, true)]
    [InlineData(10685, false)]
    public void AdlxRawMemoryExpansionUsesConfiguredGpuLimitAtMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        RemoveIntelGpuRules(profile);
        AssertLegacyMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(10974, true)]
    [InlineData(10973, false)]
    public void IntelGpuExpansionUsesConfiguredGpuLimitAtMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        RemoveIntelIntervalRules(profile);
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["metric_id_template"]!.GetValue<string>()
            is "gpu.{gpu}.igclPciCurrentGeneration" or "gpu.{gpu}.igclPciCurrentWidth" or "gpu.{gpu}.igclPciMaximumGeneration" or "gpu.{gpu}.igclPciMaximumWidth").ToArray()) rules.Remove(rule);
        AssertLegacyMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(11006, true)]
    [InlineData(11005, false)]
    public void IntelPciExpansionUsesConfiguredGpuLimitAtMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        RemoveIntelIntervalRules(profile);
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["metric_id_template"]!.GetValue<string>()
            is "gpu.{gpu}.igclPciMaximumGeneration" or "gpu.{gpu}.igclPciMaximumWidth").ToArray()) rules.Remove(rule);
        AssertLegacyMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(11038, true)]
    [InlineData(11037, false)]
    public void IntelPciCapabilityExpansionUsesConfiguredGpuLimitAtMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        RemoveIntelIntervalRules(profile);
        AssertLegacyMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(11134, true)]
    [InlineData(11133, false)]
    public void IntelIntervalExpansionUsesConfiguredGpuLimitAtMetricCapacityBoundary(int maximum, bool accepted)
        => AssertLegacyMetricCapacity(JsonNode.Parse(ReadDefaultProfile())!.AsObject(), maximum, accepted);

    [Theory]
    [InlineData(13488, true)]
    [InlineData(13487, false)]
    public void FullDefaultMetricExpansionUsesTheActualMetricCapacityBoundary(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        var diskRules = rules.Where(rule =>
            rule?["source_id"]?.GetValue<string>() == "windows.disk-performance").ToArray();

        // The pre-disk default boundary was 11,262. Seven physical-disk templates add
        // 224 instances; NVML P-state adds 16 adapters and two per-fan quantities add 512.
        Assert.Equal(7, diskRules.Length);
        Assert.All(diskRules, rule =>
        {
            Assert.Equal("storage_sensor", rule!["scope_expansion"]!.GetValue<string>());
            Assert.Equal(32, rule["maximum_instance_count"]!.GetValue<int>());
        });
        var addedDiskInstances = diskRules.Sum(rule => rule!["maximum_instance_count"]!.GetValue<int>());
        Assert.Equal(224, addedDiskInstances);
        const int previousDefaultBoundary = 11_262;
        var performanceState = Assert.Single(rules, rule => rule!["template_id"]!.GetValue<string>() == "gpu_nvidia_performance_state.optional")!;
        Assert.Equal("gpu_adapter", performanceState["scope_expansion"]!.GetValue<string>());
        var gpuLimit = profile["host_recreate"]!["metric_snapshot"]!["capacity"]!["maximum_gpu_adapter_count"]!.GetValue<int>();
        var addedPerformanceStates = Math.Min(performanceState["maximum_instance_count"]!.GetValue<int>(), gpuLimit);
        Assert.Equal(16, addedPerformanceStates);
        var fanRules = rules.Where(rule => rule!["template_id"]!.GetValue<string>()
            is "fan.nvmlSpeedPercentOfMax" or "fan.nvmlSpeedRpm").ToArray();
        Assert.Equal(2, fanRules.Length);
        Assert.All(fanRules, rule =>
        {
            Assert.Equal("system_fan", rule!["scope_expansion"]!.GetValue<string>());
            Assert.Equal(ResourceManager.ReadWrite.Vendors.Nvidia.NvidiaNvmlReader.MaxFanCount,
                rule["maximum_instance_count"]!.GetValue<int>());
        });
        var addedFanInstances = fanRules.Sum(rule => rule!["maximum_instance_count"]!.GetValue<int>());
        Assert.Equal(512, addedFanInstances);
        var platformRules = rules.Where(rule => rule!["scope_expansion"]!.GetValue<string>() == "intel_platform").ToArray();
        Assert.Equal(2, platformRules.Length);
        Assert.All(platformRules, rule => Assert.Equal(1, rule!["maximum_instance_count"]!.GetValue<int>()));
        var npuRules = rules.Where(rule => rule!["source_id"]!.GetValue<string>() == "windows.npu-memory").ToArray();
        Assert.Equal(4, npuRules.Length);
        Assert.All(npuRules, rule => Assert.Equal(64, rule!["maximum_instance_count"]!.GetValue<int>()));
        var fullDefaultBoundary = previousDefaultBoundary + addedDiskInstances + addedPerformanceStates + addedFanInstances + platformRules.Length + npuRules.Sum(rule => rule!["maximum_instance_count"]!.GetValue<int>());
        var intelFan = Assert.Single(rules, rule => rule!["template_id"]!.GetValue<string>() == "fan.igclRpm")!;
        Assert.Equal(16, intelFan["maximum_instance_count"]!.GetValue<int>());
        fullDefaultBoundary += intelFan["maximum_instance_count"]!.GetValue<int>();
        var engineRules = rules.Where(rule => rule!["source_id"]!.GetValue<string>() == "windows.npu-engine"
            && rule["scope_expansion"]!.GetValue<string>() == "npu_engine").ToArray();
        Assert.Equal(2, engineRules.Length);
        Assert.All(engineRules, rule => Assert.Equal(256, rule!["maximum_instance_count"]!.GetValue<int>()));
        fullDefaultBoundary += engineRules.Sum(rule => rule!["maximum_instance_count"]!.GetValue<int>());
        Assert.Equal(12_800, fullDefaultBoundary);

        var numaCapacity = Assert.Single(rules, rule => rule!["template_id"]!.GetValue<string>() == "memory_all_numa_node_capacity_mib.optional")!;
        Assert.Equal("numa_node", numaCapacity["scope_expansion"]!.GetValue<string>());
        Assert.Equal(64, numaCapacity["maximum_instance_count"]!.GetValue<int>());
        var effectiveFrequency = Assert.Single(rules, rule => rule!["template_id"]!.GetValue<string>() == "cpu_intel_effective_frequency_mhz.optional")!;
        Assert.Equal("cpu_logical_processor", effectiveFrequency["scope_expansion"]!.GetValue<string>());
        Assert.Equal(512, effectiveFrequency["maximum_instance_count"]!.GetValue<int>());
        var npuDeviceBusy = Assert.Single(rules, rule => rule!["template_id"]!.GetValue<string>() == "npu_all_engine_busy_percent.optional")!;
        Assert.Equal("npu_device", npuDeviceBusy["scope_expansion"]!.GetValue<string>());
        Assert.Equal(64, npuDeviceBusy["maximum_instance_count"]!.GetValue<int>());
        var amdNpuRules = rules.Where(rule =>
            rule!["template_id"]!.GetValue<string>() is "gpu_amd_npu_activity_percent.optional"
                or "gpu_amd_npu_frequency_mhz.optional").ToArray();
        Assert.Equal(2, amdNpuRules.Length);
        Assert.All(amdNpuRules, rule =>
        {
            Assert.Equal("gpu_adapter", rule!["scope_expansion"]!.GetValue<string>());
            Assert.Equal(32, rule["maximum_instance_count"]!.GetValue<int>());
        });
        var addedAmdNpuInstances = amdNpuRules.Sum(rule => Math.Min(rule!["maximum_instance_count"]!.GetValue<int>(), gpuLimit));
        Assert.Equal(32, addedAmdNpuInstances);
        var addedRuleAndMetricInstances = numaCapacity["maximum_instance_count"]!.GetValue<int>()
            + effectiveFrequency["maximum_instance_count"]!.GetValue<int>()
            + npuDeviceBusy["maximum_instance_count"]!.GetValue<int>() + addedAmdNpuInstances;
        Assert.Equal(672, addedRuleAndMetricInstances);
        fullDefaultBoundary += addedRuleAndMetricInstances;
        var ccd = Assert.Single(rules, rule => rule!["source_id"]!.GetValue<string>() == "amd.smn")!;
        Assert.Equal(16, ccd["maximum_instance_count"]!.GetValue<int>());
        fullDefaultBoundary += ccd["maximum_instance_count"]!.GetValue<int>();
        Assert.Equal(13_488, fullDefaultBoundary);
        Assert.Equal(accepted ? fullDefaultBoundary : fullDefaultBoundary - 1, maximum);

        AssertMetricCapacity(profile, maximum, accepted);
    }

    [Theory]
    [InlineData(65, true)]
    [InlineData(68, true)]
    [InlineData(69, false)]
    public void NpuInstanceCapacityUsesTheUpdatedTotalMetricGate(int instances, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        var capacity = profile["host_recreate"]!["metric_snapshot"]!["capacity"]!;
        Assert.Equal(13_488, capacity["maximum_metric_count"]!.GetValue<int>());
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        // Isolate the existing NPU scaling proof; the combined default gate above includes Intel fan slots.
        rules.Remove(rules.Single(rule => rule!["template_id"]!.GetValue<string>() == "fan.igclRpm"));
        var npu = rules.Where(rule => rule!["source_id"]!.GetValue<string>() == "windows.npu-memory").ToArray();
        Assert.Equal(4, npu.Length);
        foreach (var rule in npu) rule!["maximum_instance_count"] = instances;
        AssertMetricCapacity(profile, 13_488, accepted);
    }

    [Theory]
    [InlineData(13859, true)]
    [InlineData(13858, false)]
    public void FullDefaultRuleShapeUsesTheUpdatedRuleGate(int maximum, bool accepted)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        profile["host_recreate"]!["metric_snapshot"]!["capacity"]!["maximum_rule_count"] = maximum;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(Encoding.UTF8.GetBytes(profile.ToJsonString()), "metric-rule-boundary.json");
        if (accepted) Assert.True(Compile(loaded).IsPublished);
        else Assert.Throws<InvalidDataException>(() => Compile(loaded));
    }

    private static void RemoveIntelIntervalRules(JsonObject profile)
    {
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["metric_kind"]!.GetValue<string>() is "floating_counter_ratio" or "floating_counter_percentage").ToArray()) rules.Remove(rule);
    }

    private static void RemoveIntelGpuRules(JsonObject profile)
    {
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule!["source_id"]!.GetValue<string>() == "intel.igcl").ToArray())
            rules.Remove(rule);
    }

    private static void AssertLegacyMetricCapacity(JsonObject profile, int maximum, bool accepted)
    {
        // Keep original CPU/GPU boundaries independent of later disk, OEM/NVML fan,
        // NUMA, Intel-frequency, NPU-device and AMD-NPU additions.
        // The complete current default is tested separately above.
        var rules = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray();
        foreach (var rule in rules.Where(rule =>
            rule?["source_id"]?.GetValue<string>() == "amd.smn"
                || rule?["source_id"]?.GetValue<string>() == "windows.disk-performance"
                || rule?["source_id"]?.GetValue<string>() == "windows.npu-memory"
                || rule?["source_id"]?.GetValue<string>() == "windows.npu-engine"
                || rule?["template_id"]?.GetValue<string>() is "memory_all_numa_node_capacity_mib.optional"
                    or "cpu_intel_effective_frequency_mhz.optional"
                    or "gpu_amd_npu_activity_percent.optional"
                    or "gpu_amd_npu_frequency_mhz.optional"
                || rule?["scope_expansion"]?.GetValue<string>() == "intel_platform").ToArray())
            rules.Remove(rule);
        foreach (var rule in rules.Where(rule => rule!["template_id"]!.GetValue<string>()
            is "fan.speed.oem" or "fan.pwmDuty.oem" or "gpu_nvidia_performance_state.optional"
                or "fan.nvmlSpeedPercentOfMax" or "fan.nvmlSpeedRpm" or "fan.igclRpm").ToArray())
            rules.Remove(rule);
        foreach (var rule in rules.Where(rule => rule!["template_id"]!.GetValue<string>()
            is "fan.speed.wmi" or "fan.speedPercent.wmi"))
            rule!["maximum_instance_count"] = 32;
        AssertMetricCapacity(profile, maximum, accepted);
    }

    private static void AssertMetricCapacity(JsonObject profile, int maximum, bool accepted)
    {
        profile["host_recreate"]!["metric_snapshot"]!["capacity"]!["maximum_metric_count"] = maximum;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(Encoding.UTF8.GetBytes(profile.ToJsonString()), "die-capacity.json");
        if (accepted)
        {
            Assert.True(Compile(loaded).IsPublished);
        }
        else
        {
            var error = Assert.Throws<InvalidDataException>(() => Compile(loaded));
            Assert.Contains("worst-case metric expansion", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("die.{die}.{die}.intelPackageEnergyCounter")]
    [InlineData("die.{package}.intelPackageEnergyCounter")]
    [InlineData("die.intelPackageEnergyCounter")]
    public void DieTemplateRejectsDuplicateForeignOrMissingInstancePlaceholders(string metricId)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        var template = profile["hot_publish"]!["metric_snapshot"]!["rule_templates"]!.AsArray()
            .First(static node => node!["scope_expansion"]!.GetValue<string>() == "cpu_die");
        template!["metric_id_template"] = metricId;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(Encoding.UTF8.GetBytes(profile.ToJsonString()), "die-placeholder.json");
        var error = Assert.Throws<InvalidDataException>(() => Compile(loaded));
        Assert.Contains("scope expansion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_RejectsInvalidSmartCoordinatorProcessPolicyOrdering()
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        profile["hot_publish"]!["smart_coordinator"]!["process_policy"]!["level2_maximum_cpu_score_scale"] = 80;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(profile.ToJsonString()),
            "invalid-policy.json");

        Assert.Throws<InvalidDataException>(() => Compile(loaded));
    }

    [Fact]
    public void Compiler_RejectsPdhBaselineBelowNativeMinimum()
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        profile["host_recreate"]!["pdh_collector"]!["baseline_reset_interval_ms"] = 999;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(profile.ToJsonString()),
            "invalid-pdh-baseline.json");

        Assert.Throws<InvalidDataException>(() => Compile(loaded));
    }

    [Fact]
    public void Compiler_BindsUserTargetsAndGpuThresholdsWhileKeepingInternalDangerRatios()
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "test/default.json");
        var performance = AppSettingsDefaults.Create().Performance with
        {
            PhysicalMemoryOptimizationTargetUsagePercent = 76,
            VirtualMemoryOptimizationTargetUsagePercent = 82,
            GpuOverflowUsagePercent = 14,
            GpuOverflowDedicatedMemoryPercent = 17,
            PhysicalMemoryAutomaticCleanupPercent = 9,
            VirtualMemoryAutomaticCleanupPercent = 11
        };

        var plan = new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(performance),
            73,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
        var configuration = Assert.IsType<ResourceSchedulerConfig>(
            plan.HotPublish.ResourceScheduler.Configuration);

        Assert.Equal(0.24, configuration.TargetFreeRatios[1], 12);
        Assert.Equal(0.18, configuration.TargetFreeRatios[2], 12);
        Assert.Equal(0.10, configuration.DangerMinimumPhysicalAfterVramMoveRatio, 12);
        Assert.Equal(0.12, configuration.DangerMinimumVirtualAfterPhysicalMoveRatio, 12);
        Assert.Equal(new CompiledGpuOverflowPolicy(14, 17), plan.HotPublish.PlacementCoordinator.GpuOverflow);
        Assert.Equal(0.09, plan.HotPublish.MemoryCleanup.PhysicalEmergencyFreeRatio, 12);
        Assert.Equal(0.11, plan.HotPublish.MemoryCleanup.VirtualEmergencyFreeRatio, 12);
        Assert.NotEqual(0UL, configuration.Generation);
    }

    [Fact]
    public void Compiler_UsesEffectiveConfigurationIdentityInsteadOfPlanEpoch()
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "test/default.json");
        var performance = AppSettingsDefaults.Create().Performance;

        var first = new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(performance),
            73,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
        var second = new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(performance),
            74,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
        var changed = new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(
                performance with { PhysicalMemoryOptimizationTargetUsagePercent = 75 }),
            75,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));

        var firstConfig = Assert.IsType<ResourceSchedulerConfig>(
            first.HotPublish.ResourceScheduler.Configuration);
        var secondConfig = Assert.IsType<ResourceSchedulerConfig>(
            second.HotPublish.ResourceScheduler.Configuration);
        var changedConfig = Assert.IsType<ResourceSchedulerConfig>(
            changed.HotPublish.ResourceScheduler.Configuration);

        Assert.Equal(first.PlanSha256, second.PlanSha256);
        Assert.Equal(first.HotPublishSha256, second.HotPublishSha256);
        Assert.Equal(firstConfig.Generation, secondConfig.Generation);
        Assert.Equal(first.SmartCoordinator.ConfigurationGeneration, second.SmartCoordinator.ConfigurationGeneration);
        Assert.NotEqual(first.PlanEpoch, second.PlanEpoch);
        Assert.NotEqual(first.PlanSha256, changed.PlanSha256);
        Assert.NotEqual(firstConfig.Generation, changedConfig.Generation);
        Assert.Equal(first.SmartCoordinator.ConfigurationGeneration, changed.SmartCoordinator.ConfigurationGeneration);
    }

    [Theory]
    [InlineData("fixed", 1, 100)]
    [InlineData("fixed", 8192, 1)]
    [InlineData("percent", 1, 100)]
    [InlineData("percent", 8192, 1)]
    public void Compiler_BindsBothRealtimeBudgetValuesWithoutDiscardingTheInactiveValue(
        string mode,
        int count,
        int percent)
    {
        var performance = AppSettingsDefaults.Create().Performance with
        {
            RealtimeActionBudgetMode = mode,
            RealtimeActionBudgetCount = count,
            RealtimeActionBudgetPercent = percent
        };

        var plan = HostManagerTestPlanFactory.CreatePlan(performance: performance);

        Assert.True(plan.IsPublished);
        Assert.Equal(new CompiledHostManagerRealtimeActionBudgetPlan(mode, count, percent),
            plan.SmartCoordinator.HotPublish.RealtimeActionBudget);
    }

    [Theory]
    [InlineData("unknown", 32, 5)]
    [InlineData("fixed", 0, 5)]
    [InlineData("percent", 8193, 5)]
    [InlineData("fixed", 32, 0)]
    [InlineData("percent", 32, 101)]
    public void Compiler_RejectsInvalidRealtimeBudgetSettings(string mode, int count, int percent)
    {
        var performance = AppSettingsDefaults.Create().Performance with
        {
            RealtimeActionBudgetMode = mode,
            RealtimeActionBudgetCount = count,
            RealtimeActionBudgetPercent = percent
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerTestPlanFactory.CreatePlan(performance: performance));
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("percent")]
    public void Compiler_RejectsRealtimeBudgetCountBeyondTheConfiguredActionCapacity(string mode)
    {
        static void ReduceActionCapacity(JsonObject profile)
            => profile["host_recreate"]!["smart_coordinator"]!["maximum_actions"] = 6144;
        var performance = AppSettingsDefaults.Create().Performance with
        {
            RealtimeActionBudgetMode = mode,
            RealtimeActionBudgetCount = 6144
        };
        var valid = HostManagerTestPlanFactory.CreatePlan(ReduceActionCapacity, performance);
        Assert.True(valid.IsPublished);
        Assert.Equal(6144, valid.SmartCoordinator.Recreate.MaximumActions);

        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(
            ReduceActionCapacity,
            performance with { RealtimeActionBudgetCount = 6145 }));

        Assert.Contains("maximum_actions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_PercentBudgetRequiresAnActionEnvelopeCoveringAllProcesses()
    {
        var performance = AppSettingsDefaults.Create().Performance with
        {
            RealtimeActionBudgetMode = AppRealtimeActionBudgetModes.Percent,
            RealtimeActionBudgetPercent = 100
        };
        var valid = HostManagerTestPlanFactory.CreatePlan(performance: performance);
        Assert.True(valid.SmartCoordinator.Recreate.MaximumProcesses <= valid.SmartCoordinator.Recreate.MaximumActions);

        // The existing process-and-software capacity guard is stronger than the percentage envelope.
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(
            profile => profile["host_recreate"]!["smart_coordinator"]!["maximum_actions"] =
                valid.SmartCoordinator.Recreate.MaximumProcesses - 1,
            performance));

        Assert.Contains("maximum_actions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictProfileRejectsRetiredEventBoostMillisecondsKey()
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        profile["hot_publish"]!["smart_coordinator"]!["event_boost_ms"] = 10000;
        var error = Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(profile.ToJsonString()), "retired-event-boost.json"));
        Assert.Contains("event_boost_ms", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CompilerRejectsNonpositiveEventBoostRounds(int rounds)
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        profile["hot_publish"]!["smart_coordinator"]!["event_boost_rounds"] = rounds;
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(profile.ToJsonString()), "invalid-event-boost.json");
        var error = Assert.Throws<InvalidDataException>(() => Compile(loaded));
        Assert.Contains("event_boost_rounds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_RejectsTheRetiredProfileRealtimeBudgetKey()
    {
        var profile = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        profile["hot_publish"]!["smart_coordinator"]!["maximum_actions_per_realtime_tick"] = 32;

        var error = Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(profile.ToJsonString()),
            "retired-realtime-budget.json"));

        Assert.Contains("maximum_actions_per_realtime_tick", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("percent", 32, 5)]
    [InlineData("fixed", 33, 5)]
    [InlineData("fixed", 32, 6)]
    public void Compiler_RealtimeBudgetChangesAreHotAppliedWithoutHostRecreation(
        string mode,
        int count,
        int percent)
    {
        var first = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(
            performance: AppSettingsDefaults.Create().Performance with
            {
                RealtimeActionBudgetMode = mode,
                RealtimeActionBudgetCount = count,
                RealtimeActionBudgetPercent = percent
            },
            planEpoch: 2);

        Assert.Equal(first.BuildSha256, changed.BuildSha256);
        Assert.Equal(first.RecreateSha256, changed.RecreateSha256);
        Assert.Equal(first.SmartCoordinator.Recreate, changed.SmartCoordinator.Recreate);
        Assert.Equal(first.DeploymentDigests.SmartCoordinator.RecreateSha256,
            changed.DeploymentDigests.SmartCoordinator.RecreateSha256);
        Assert.NotEqual(first.HotPublishSha256, changed.HotPublishSha256);
        Assert.NotEqual(first.DeploymentDigests.SmartCoordinator.HotPublishSha256,
            changed.DeploymentDigests.SmartCoordinator.HotPublishSha256);
        Assert.NotEqual(first.SmartCoordinator.ConfigurationSha256, changed.SmartCoordinator.ConfigurationSha256);

        var state = new HostManagerDeploymentState();
        state.PublishRuntimePlan(CompiledRuntimePlan.Default with { Version = 1, HostManager = first });
        var initial = state.BeginAttempt(HostManagerModuleKind.SmartCoordinator, first,
            HostManagerDeploymentOperation.InitialCreate);
        Assert.Equal(HostManagerDeploymentAttemptSettlement.Applied, state.CompleteAttemptSucceeded(initial));
        state.PublishRuntimePlan(CompiledRuntimePlan.Default with { Version = 2, HostManager = changed });

        Assert.Equal(HostManagerPendingLifecycle.HotPublish, state.Snapshot.SmartCoordinator.PendingLifecycle);
        Assert.True(state.CanApplyHot(HostManagerModuleKind.SmartCoordinator, changed));
        var hot = state.BeginAttempt(HostManagerModuleKind.SmartCoordinator, changed,
            HostManagerDeploymentOperation.HotPublish);
        Assert.Equal(HostManagerDeploymentAttemptSettlement.Applied, state.CompleteAttemptSucceeded(hot));
        Assert.Equal(HostManagerDeploymentStatus.InSync, state.Snapshot.SmartCoordinator.Status);
    }

    [Fact]
    public void Compiler_SmartCoordinatorRevisionAndDigestCoverEveryConfigurationDomain()
    {
        var first = Compile(StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "first.json"));
        Action<JsonObject>[] mutations =
        [
            profile => profile["host_recreate"]!["smart_coordinator"]!["maximum_gpu_states"] = 32769,
            profile => profile["hot_publish"]!["smart_coordinator"]!["feature_flags"] =
                new JsonArray("process_policy", "adapter_cpu", "adapter_gpu"),
            profile => profile["hot_publish"]!["smart_coordinator"]!["event_interval_ms"] = 1100,
            profile => profile["hot_publish"]!["smart_coordinator"]!["base_score_tiers"]!["high_minimum_base_score"] = 82,
            profile => profile["hot_publish"]!["smart_coordinator"]!["process_policy"]!["default_minimum_cpu_score_scale"] = 101,
            profile => profile["hot_publish"]!["smart_coordinator"]!["cpu_adapter_policy"]!["extreme_minimum_score"] = 121,
            profile => profile["hot_publish"]!["smart_coordinator"]!["gpu_adapter_policy"]!["optimize_minimum_score"] = 24
        ];

        foreach (var mutate in mutations)
        {
            var changedJson = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
            changedJson["profile_revision"] = first.ProfileRevision + 1;
            mutate(changedJson);
            var changed = Compile(StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(changedJson.ToJsonString()),
                "changed.json"));

            Assert.NotEqual(
                first.SmartCoordinator.ConfigurationGeneration,
                changed.SmartCoordinator.ConfigurationGeneration);
            Assert.NotEqual(
                first.SmartCoordinator.ConfigurationSha256,
                changed.SmartCoordinator.ConfigurationSha256);
            Assert.True(
                changed.SmartCoordinator.ConfigurationGeneration
                > first.SmartCoordinator.ConfigurationGeneration);
            Assert.Equal(0U, unchecked((uint)changed.SmartCoordinator.ConfigurationGeneration));
        }

        Assert.Equal(68UL, first.SmartCoordinator.ConfigurationGeneration >> 32);
        Assert.Equal(0U, unchecked((uint)first.SmartCoordinator.ConfigurationGeneration));
    }

    [Fact]
    public void Compiler_SmartCoordinatorSemanticDriftRequiresRevisionIncrease()
    {
        var first = Compile(StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "first.json"));
        var changedJson = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        changedJson["hot_publish"]!["smart_coordinator"]!["event_interval_ms"] = 1100;
        var changed = Compile(StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(changedJson.ToJsonString()),
            "changed.json"));

        Assert.Equal(
            first.SmartCoordinator.ConfigurationGeneration,
            changed.SmartCoordinator.ConfigurationGeneration);
        Assert.NotEqual(
            first.SmartCoordinator.ConfigurationSha256,
            changed.SmartCoordinator.ConfigurationSha256);
    }

    [Fact]
    public void Compiler_PlacementCoordinatorUsesRevisionGenerationAndRejectsLegacyShape()
    {
        var first = Compile(StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "first.json"));

        var sameRevisionJson = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        sameRevisionJson["hot_publish"]!["placement_coordinator"]!["retry_delay_ms"] = 6000;
        var sameRevision = Compile(StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(sameRevisionJson.ToJsonString()),
            "same-revision.json"));

        Assert.Equal(
            first.HotPublish.PlacementCoordinator.ConfigurationGeneration,
            sameRevision.HotPublish.PlacementCoordinator.ConfigurationGeneration);
        Assert.NotEqual(
            first.DeploymentDigests.PlacementCoordinator.HotPublishSha256,
            sameRevision.DeploymentDigests.PlacementCoordinator.HotPublishSha256);

        sameRevisionJson["profile_revision"] = first.ProfileRevision + 1;
        var nextRevision = Compile(StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(sameRevisionJson.ToJsonString()),
            "next-revision.json"));
        Assert.Equal((ulong)(first.ProfileRevision + 1) << 32, nextRevision.HotPublish.PlacementCoordinator.ConfigurationGeneration);
        Assert.Equal(0U, unchecked((uint)nextRevision.HotPublish.PlacementCoordinator.ConfigurationGeneration));

        var retiredPressureTrigger = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        retiredPressureTrigger["hot_publish"]!["placement_coordinator"]!["minimum_pressure_benefit_score"] = 3;
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(retiredPressureTrigger.ToJsonString()),
            "retired-pressure-trigger.json"));

        var legacy = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        legacy["build_specialize"]!["cpu_placement_abi_version"] = 0x0002_0000;
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(legacy.ToJsonString()),
            "legacy.json"));
    }

    [Fact]
    public void Compiler_SmartCoordinatorRevisionProducesLegalNativeHotReconfiguration()
    {
        var first = Compile(StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "first.json"));
        var changedJson = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        changedJson["profile_revision"] = first.ProfileRevision + 1;
        changedJson["hot_publish"]!["smart_coordinator"]!["required_consecutive_decisions"] = 4;
        var changed = Compile(StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(changedJson.ToJsonString()),
            "changed.json"));
        var firstConfiguration = NativeSmartCoordinatorConfigurationWriter.Create(
            first.SmartCoordinator.Build,
            first.SmartCoordinator.Recreate,
            first.SmartCoordinator.HotPublish,
            first.SmartCoordinator.ConfigurationGeneration);
        var changedConfiguration = NativeSmartCoordinatorConfigurationWriter.Create(
            changed.SmartCoordinator.Build,
            changed.SmartCoordinator.Recreate,
            changed.SmartCoordinator.HotPublish,
            changed.SmartCoordinator.ConfigurationGeneration);

        Assert.True(changedConfiguration.Generation > firstConfiguration.Generation);
        using var session = new NativeSmartCoordinatorSession(in firstConfiguration);
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.Reconfigure(in changedConfiguration));
        Assert.Equal(changedConfiguration.Generation, session.Capacity.ConfigurationGeneration);
    }

    [Fact]
    public void Compiler_RejectsInvalidSmartCoordinatorCapacityAndAdapterOrdering()
    {
        var invalidCapacity = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidCapacity["host_recreate"]!["smart_coordinator"]!["maximum_actions"] = 4096;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidCapacity.ToJsonString()),
                "invalid-capacity.json")));

        var incompleteGpuCapacity = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        incompleteGpuCapacity["host_recreate"]!["smart_coordinator"]!["maximum_gpu_states"] = 32767;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(incompleteGpuCapacity.ToJsonString()),
                "incomplete-gpu-capacity.json")));

        var invalidAdapter = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidAdapter["hot_publish"]!["smart_coordinator"]!["cpu_adapter_policy"]!["normal_minimum_score"] = 130;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidAdapter.ToJsonString()),
                "invalid-adapter.json")));

        var invalidBaseScoreTiers = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidBaseScoreTiers["hot_publish"]!["smart_coordinator"]!["base_score_tiers"]!["middle_minimum_base_score"] = 81;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidBaseScoreTiers.ToJsonString()),
                "invalid-base-score-tiers.json")));

        var emptyLowBaseScoreTier = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        emptyLowBaseScoreTier["hot_publish"]!["smart_coordinator"]!["base_score_tiers"]!["middle_minimum_base_score"] = 0;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(emptyLowBaseScoreTier.ToJsonString()),
                "empty-low-base-score-tier.json")));

        var emptyHighBaseScoreTier = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        emptyHighBaseScoreTier["hot_publish"]!["smart_coordinator"]!["base_score_tiers"]!["high_minimum_base_score"] = 101;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(emptyHighBaseScoreTier.ToJsonString()),
                "empty-high-base-score-tier.json")));

        var invalidTiming = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidTiming["hot_publish"]!["smart_coordinator"]!["event_interval_ms"] = 0;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidTiming.ToJsonString()),
                "invalid-timing.json")));

        var invalidWelfareBase = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidWelfareBase["hot_publish"]!["smart_coordinator"]!["welfare_base_score"] = 0;
        var retiredWelfareError = Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidWelfareBase.ToJsonString()),
                "invalid-welfare-base.json")));
        Assert.Contains("welfare_base_score", retiredWelfareError.Message, StringComparison.Ordinal);

        var invalidFeature = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidFeature["hot_publish"]!["smart_coordinator"]!["feature_flags"] =
            new JsonArray("process_policy", "unknown");
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidFeature.ToJsonString()),
                "invalid-feature.json")));

        var overflowingScore = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        overflowingScore["hot_publish"]!["smart_coordinator"]!["process_policy"]!["state_multipliers"]![0] = 1e308;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(overflowingScore.ToJsonString()),
                "overflowing-score.json")));

        var overflowingGpuScore = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        overflowingGpuScore["hot_publish"]!["smart_coordinator"]!["gpu_adapter_policy"]!["state_multipliers"]![0] = 1e308;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(overflowingGpuScore.ToJsonString()),
                "overflowing-gpu-score.json")));

        var redundantCpuStateMultipliers = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        redundantCpuStateMultipliers["hot_publish"]!["smart_coordinator"]!["cpu_adapter_policy"]!["state_multipliers"] =
            new JsonArray(1, 1.35, 1.12, 0.75, 0.55, 0.45, 0);
        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(redundantCpuStateMultipliers.ToJsonString()),
            "redundant-cpu-state-multipliers.json"));
    }

    private static CompiledHostManagerPlan Compile(LoadedHostManagerProfile loaded)
    {
        return new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(),
            41,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
    }

    private static byte[] ReadDefaultProfile()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        // Preserve the original pretty-input fixture: several rejection cases mutate its literal properties,
        // and the digest case explicitly compares pretty and compact representations.
        var expanded = StrictHostManagerProfileLoader.ExpandMetricDefaults(buffer.ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(JsonNode.Parse(expanded), new JsonSerializerOptions { WriteIndented = true });
    }
}
