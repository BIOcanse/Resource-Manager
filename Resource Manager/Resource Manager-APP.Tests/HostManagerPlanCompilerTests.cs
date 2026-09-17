using System.Text;
using System.Text.Json.Nodes;
using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.NativeLedger;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization.Scoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerPlanCompilerTests
{
    [Fact]
    public void DefaultProfile_CompilesCompleteHostPublicAndPrivateResourcePlans()
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "test/default.json");

        var plan = Compile(loaded);

        Assert.Equal(42, plan.SchemaVersion);
        Assert.Equal(59, plan.ProfileRevision);
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
        Assert.Equal(0x0008_0000U, plan.BuildSpecialize.SmartCoordinator.AbiVersion);
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
        Assert.Equal(0x0001_0000U, plan.BuildSpecialize.PdhCollectorAbiVersion);
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
        Assert.Equal(59UL << 32, metricSnapshot.HotPublish.ConfigurationGeneration);
        Assert.Equal(
            (59UL << 32) | 1UL,
            metricSnapshot.HotPublish.CatalogGenerationBase);
        Assert.Equal(1U, metricSnapshot.HotPublish.CatalogManifestVersion);
        Assert.Equal(64, metricSnapshot.HotPublish.CatalogManifestSha256.Length);
        Assert.Equal(14, metricSnapshot.HotPublish.SourcePolicies.Length);
        // 82 = 原来的 79 加上核显那三条（频率 / 温度 / 电压改走 SMU）。
        Assert.Equal(82, metricSnapshot.HotPublish.RuleTemplates.Length);
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
        Assert.Equal(59UL << 32, reportCoordinator.ConfigurationGeneration);
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
        Assert.Equal(10000, smartCoordinator.HotPublish.NormalIntervalMilliseconds);
        Assert.Equal(1000, smartCoordinator.HotPublish.EventIntervalMilliseconds);
        Assert.Equal(10000, smartCoordinator.HotPublish.EventBoostMilliseconds);
        Assert.Equal(10000, smartCoordinator.HotPublish.GameStartGraceMilliseconds);
        Assert.Equal(3, smartCoordinator.HotPublish.RequiredConsecutiveDecisions);
        Assert.Equal(5000, smartCoordinator.HotPublish.FailureRetryMilliseconds);
        Assert.Equal(30000, smartCoordinator.HotPublish.ReservationTimeoutMilliseconds);
        Assert.Equal(1, smartCoordinator.HotPublish.MaximumActionsPerRealtimeTick);
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
        Assert.Equal(59UL << 32, placement.ConfigurationGeneration);
        Assert.Equal(5000, placement.RetryDelayMilliseconds);
        Assert.Equal(30000, placement.ActionTimeoutMilliseconds);
        Assert.Equal(5000, placement.MaximumFutureSkewMilliseconds);

        var transactionJournal = plan.HotPublish.TransactionJournal;
        Assert.Equal(59UL << 32, transactionJournal.ConfigurationGeneration);
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

        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(source.Replace(
                "\"schema_version\": 42,",
                "\"schema_version\": 42, \"schema_version\": 42,",
                StringComparison.Ordinal)),
            "duplicate.json"));

        Assert.Throws<InvalidDataException>(() => StrictHostManagerProfileLoader.LoadBytes(
            Encoding.UTF8.GetBytes(source.Replace(
                "\"resource_capacity\": 256",
                "\"resource_capacity\": \"256\"",
                StringComparison.Ordinal)),
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
    public void Compiler_BindsUserTargetAndDangerLinesIntoNativeConfiguration()
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "test/default.json");
        var performance = AppSettingsDefaults.Create().Performance with
        {
            PhysicalMemoryOptimizationTargetUsagePercent = 76,
            VirtualMemoryOptimizationTargetUsagePercent = 82,
            VramMoveDownPhysicalMemoryDangerPercent = 14,
            PhysicalMemoryMoveDownVirtualMemoryDangerPercent = 17,
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
        Assert.Equal(0.14, configuration.DangerMinimumPhysicalAfterVramMoveRatio, 12);
        Assert.Equal(0.17, configuration.DangerMinimumVirtualAfterPhysicalMoveRatio, 12);
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
            profile => profile["hot_publish"]!["smart_coordinator"]!["maximum_actions_per_realtime_tick"] = 2,
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

        Assert.Equal(59UL, first.SmartCoordinator.ConfigurationGeneration >> 32);
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

        var invalidRealtimeLimit = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        invalidRealtimeLimit["hot_publish"]!["smart_coordinator"]!["maximum_actions_per_realtime_tick"] = 8193;
        Assert.Throws<InvalidDataException>(() => Compile(
            StrictHostManagerProfileLoader.LoadBytes(
                Encoding.UTF8.GetBytes(invalidRealtimeLimit.ToJsonString()),
                "invalid-realtime-limit.json")));

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
        return buffer.ToArray();
    }
}
