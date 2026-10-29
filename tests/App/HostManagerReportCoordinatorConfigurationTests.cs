using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerReportCoordinatorConfigurationTests
{
    [Fact]
    public void ProjectionGroupsDoNotChangeSerializedPlanOrConfigurationDigest()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        Assert.NotEmpty(plan.SourceRuleGroups);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(plan with { SourceRuleGroups = [] }),
            System.Text.Json.JsonSerializer.Serialize(plan));
        Assert.Equal(plan.ConfigurationSha256, HostManagerPlanIdentity.ComputeDigest(
            new ReportCoordinatorConfigurationDigestInput(plan.Build.AbiVersion, plan.Recreate, plan.HotPublish)));
    }

    [Fact]
    public void DefaultProfilePublishesExactReportCoordinatorPlan()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var plan = hostPlan.ReportCoordinator;

        Assert.True(plan.IsPublished);
        Assert.Equal(NativeReportCoordinatorAbi.Version, plan.Build.AbiVersion);
        Assert.Equal("report_coordinator", plan.Build.NativeModule);
        Assert.Equal(69UL << 32, plan.ConfigurationGeneration);
        Assert.Equal(16, plan.Recreate.Capacity.MaximumSourceCount);
        Assert.Equal(64, plan.Recreate.Capacity.MaximumRuleCount);
        Assert.Equal(8192, plan.Recreate.Capacity.MaximumObservationCount);
        Assert.Equal(4096, plan.Recreate.Capacity.MaximumReportCount);
        Assert.Equal(8192, plan.Recreate.Capacity.MaximumTrustCount);
        Assert.Equal(86528, plan.Recreate.Capacity.MaximumBucketCount);
        Assert.Equal(131072, plan.Recreate.Capacity.MaximumPersistenceOperationCount);
        Assert.Equal(4096, plan.Recreate.Capacity.MaximumReportOutputCount);
        Assert.Equal(512, plan.Recreate.Capacity.MaximumRollingObservationCount);
        Assert.Equal(32, plan.Recreate.Capacity.SourceIndexCapacity);
        Assert.Equal(128, plan.Recreate.Capacity.RuleIndexCapacity);
        Assert.Equal(16384, plan.Recreate.Capacity.ObservationIndexCapacity);
        Assert.Equal(8192, plan.Recreate.Capacity.ReportIndexCapacity);
        Assert.Equal(16384, plan.Recreate.Capacity.TrustIndexCapacity);
        Assert.Equal(262144, plan.Recreate.Capacity.BucketIndexCapacity);
        Assert.Equal(262144, plan.Recreate.Capacity.PlannedPersistenceIndexCapacity);
        Assert.Equal(4096, plan.Build.CapacityLimits.MaximumReportCount);
        Assert.Equal(8192, plan.Build.CapacityLimits.MaximumTrustCount);
        Assert.Equal(16384, plan.Build.CapacityLimits.TrustIndexCapacity);
        Assert.Equal(9, plan.Recreate.Rules.Length);
        Assert.DoesNotContain(plan.Recreate.Rules, rule => rule.RuleHandle == 1035);
        Assert.All(plan.Recreate.Rules, rule => Assert.NotEqual(0UL, rule.RuleGeneration));
        Assert.Equal(
            plan.Recreate.Rules.Length,
            plan.Recreate.Rules.Select(static rule => rule.RuleGeneration).Distinct().Count());
        Assert.Equal(
            3_600_000,
            plan.HotPublish.MetadataCheckpointIntervalMilliseconds);
        Assert.Equal(
            HostManagerReportFactKind.CpuTemperatureCelsius,
            plan.Recreate.Rules.Single(rule => rule.RuleHandle == 1031).FactKind);
        Assert.Equal(3U, plan.Recreate.Rules.Single(rule => rule.RuleHandle == 1031).PredicateCount);
        Assert.Equal(2U, plan.Recreate.Rules.Single(rule => rule.RuleHandle == 1033).Comparison);
        // 内存报告是双重判断：百分比和绝对字节各自成组、共用同一个 family，
        // 所以任一越线都会出报告（或关系），而不是两个条件同时成立才报。
        var percentRule = plan.Recreate.Rules.Single(rule => rule.RuleHandle == 1037);
        var bytesRule = plan.Recreate.Rules.Single(rule => rule.RuleHandle == 1038);
        Assert.Equal(HostManagerReportFactKind.SoftwareMemorySystemPercent, percentRule.FactKind);
        Assert.Equal(HostManagerReportFactKind.SoftwareMemoryBytes, bytesRule.FactKind);
        Assert.Equal(percentRule.FamilyHandle, bytesRule.FamilyHandle);
        Assert.NotEqual(percentRule.PredicateGroupHandle, bytesRule.PredicateGroupHandle);
        Assert.Equal(1U, percentRule.PredicateCount);
        Assert.Equal(1U, bytesRule.PredicateCount);
        // 两者同时越线时，报告带绝对值：优先级更高的那条观测胜出。
        Assert.True(bytesRule.Priority > percentRule.Priority);
        Assert.Equal(4L * 1024 * 1024 * 1024, bytesRule.ActivationThreshold);
        Assert.Equal(3L * 1024 * 1024 * 1024, bytesRule.ClearThreshold);
        Assert.Equal(3_600_000, plan.HotPublish.BucketWidthMilliseconds);
        Assert.Equal(86_400_000, plan.HotPublish.Window24HoursMilliseconds);
        Assert.Equal(604_800_000, plan.HotPublish.Window7DaysMilliseconds);
    }

    [Fact]
    public void StrayProcessRulesPublishVisibleDefaultsAndRejectMissingOptions()
    {
        var rules = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator.Recreate.Rules;
        var memory = rules.Single(rule => rule.FactKind == HostManagerReportFactKind.ProcessMemoryBytes);
        var idle = rules.Single(rule => rule.FactKind == HostManagerReportFactKind.ProcessIdleMilliseconds);
        Assert.Equal(50 * 1024 * 1024, memory.ActivationThreshold);
        Assert.Equal(45 * 1024 * 1024, memory.ClearThreshold);
        Assert.Equal(900_000, idle.ActivationThreshold);
        Assert.Equal(0, idle.ZeroActivityThreshold);
        Assert.Equal(24, idle.ExecutableNames.Length);
        Assert.Contains("VBCSCompiler", idle.ExecutableNames);
        Assert.Equal(memory.PredicateGroupHandle, idle.PredicateGroupHandle);
        Assert.Equal(2U, idle.PredicateCount);
        Assert.Equal(1U, idle.Severity);
        foreach (var field in new[] { "executable_names", "zero_activity_threshold" })
            Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
                root["host_recreate"]!["report_coordinator"]!["rules"]!.AsArray().Last()!.AsObject().Remove(field)));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["report_coordinator"]!["rules"]!.AsArray().Last()!["zero_activity_threshold"] = -1));
        var changed = HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["report_coordinator"]!["rules"]!.AsArray().Last()!["executable_names"] = new System.Text.Json.Nodes.JsonArray("custom"));
        Assert.NotEqual(idle.RuleGeneration, changed.ReportCoordinator.Recreate.Rules.Last().RuleGeneration);
    }

    [Theory]
    [InlineData("maximum_report_count", 8192)]
    [InlineData("maximum_observation_count", 16384)]
    [InlineData("maximum_trust_count", 16384)]
    [InlineData("maximum_persistence_operation_count", 393216)]
    [InlineData("report_index_capacity", 16384)]
    [InlineData("observation_index_capacity", 32768)]
    [InlineData("trust_index_capacity", 32768)]
    [InlineData("bucket_index_capacity", 524288)]
    [InlineData("planned_persistence_index_capacity", 1048576)]
    public void ReportCapacitiesStillRejectValuesAboveBuildLimits(string field, int value)
    {
        var failure = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var capacity = root["host_recreate"]!["report_coordinator"]!["capacity"]!;
            capacity[field] = value;
            // Keep the requested shape valid so rejection exercises the build limit.
            switch (field)
            {
                case "maximum_report_count": capacity["report_index_capacity"] = value * 2; break;
                case "maximum_observation_count": capacity["observation_index_capacity"] = value * 2; break;
                case "maximum_trust_count": capacity["trust_index_capacity"] = value * 2; break;
                case "maximum_persistence_operation_count": capacity["planned_persistence_index_capacity"] = 1048576; break;
            }
        }));
        Assert.Contains($"host_recreate.report_coordinator.capacity.{field}", failure.Message);
    }

    [Fact]
    public void UnrelatedProfileRevisionDoesNotInvalidateReportRuleIdentity()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var nextRevision = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });

        Assert.Equal(
            baseline.ReportCoordinator.Recreate.Rules
                .Select(static rule => rule.RuleGeneration),
            nextRevision.ReportCoordinator.Recreate.Rules
                .Select(static rule => rule.RuleGeneration));
        Assert.NotEqual(
            baseline.ReportCoordinator.ConfigurationGeneration,
            nextRevision.ReportCoordinator.ConfigurationGeneration);
    }

    [Fact]
    public void WorkspaceProjectsEveryPublishedCapacityAndTimingField()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        using var workspace = new NativeReportCoordinatorWorkspace(plan, 101, 102, OperationScratchFixture.Create);
        var capacity = workspace.Session.Capacity;

        Assert.Equal(plan.Recreate.Rules.Length, workspace.Rules.Length);
        Assert.Equal(
            plan.Recreate.Rules[0].RuleGeneration,
            workspace.Rules[0].RuleGeneration);
        Assert.Equal(
            plan.Recreate.Rules[3].PredicateCount,
            workspace.Rules[3].PredicateCount);
        Assert.All(workspace.Rules, rule => Assert.Equal(0U, rule.Flags));
        Assert.Equal((uint)plan.Recreate.Capacity.MaximumSourceCount, capacity.SourceCapacity);
        Assert.Equal((uint)plan.Recreate.Capacity.MaximumRuleCount, capacity.RuleCapacity);
        Assert.Equal(
            (uint)plan.Recreate.Capacity.MaximumObservationCount,
            capacity.ObservationCapacity);
        Assert.Equal(
            (uint)plan.Recreate.Capacity.MaximumPersistenceOperationCount,
            capacity.PersistenceOperationCapacity);
        Assert.Equal(0, workspace.Layout.ReportCapacity);
        Assert.Equal(0, workspace.Layout.PersistenceCapacity);
        workspace.EnsureScratch(2, 3);
        Assert.Equal(2, workspace.Layout.ReportCapacity);
        Assert.Equal(3, workspace.Layout.PersistenceCapacity);
        Assert.Equal(1680UL, workspace.Layout.Length);
    }

    [Fact]
    public void RuntimeSettlesOnlyExactReportCoordinatorAttempt()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = hostPlan
        });
        var runtime = new HostManagerReportCoordinatorRuntime(deployment);
        using var publication = provider.AcquirePublicationLease();

        Assert.Equal(
            hostPlan.ReportCoordinator.ConfigurationSha256,
            runtime.CaptureDesired(publication).ConfigurationSha256);
        runtime.CompleteSucceeded(runtime.BeginInitialCreate(hostPlan));

        Assert.Equal(
            HostManagerDeploymentStatus.InSync,
            deployment.Snapshot.ReportCoordinator.Status);
        Assert.Equal(
            hostPlan.DeploymentDigests.ReportCoordinator.HotPublishSha256,
            deployment.Snapshot.ReportCoordinator.AppliedHotPublishSha256);
    }

    [Fact]
    public void ProfileRejectsImplicitCapacityAndInvalidWindowRelation()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["report_coordinator"]!["capacity"]!
                .AsObject()
                .Remove("planned_persistence_index_capacity");
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["hot_publish"]!["report_coordinator"]!
                .AsObject()["window_7d_ms"] = 86_400_000;
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["report_coordinator"]!["rules"]!
                .AsArray()
                .RemoveAt(1);
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["report_coordinator"]!["rules"]![1]!
                .AsObject()["rule_handle"] = 1031;
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["report_coordinator"]!["rules"]![1]!
                .AsObject()["predicate_index"] = 0;
        }));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["host_recreate"]!["report_coordinator"]!["rules"]![0]!
                .AsObject()["fact_kind"] = 999;
        }));
    }
}
