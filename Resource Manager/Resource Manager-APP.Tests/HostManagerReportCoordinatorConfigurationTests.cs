using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerReportCoordinatorConfigurationTests
{
    [Fact]
    public void DefaultProfilePublishesExactReportCoordinatorPlan()
    {
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        var plan = hostPlan.ReportCoordinator;

        Assert.True(plan.IsPublished);
        Assert.Equal(NativeReportCoordinatorAbi.Version, plan.Build.AbiVersion);
        Assert.Equal("report_coordinator", plan.Build.NativeModule);
        Assert.Equal(59UL << 32, plan.ConfigurationGeneration);
        Assert.Equal(16, plan.Recreate.Capacity.MaximumSourceCount);
        Assert.Equal(64, plan.Recreate.Capacity.MaximumRuleCount);
        Assert.Equal(2048, plan.Recreate.Capacity.MaximumObservationCount);
        Assert.Equal(1024, plan.Recreate.Capacity.MaximumReportCount);
        Assert.Equal(2048, plan.Recreate.Capacity.MaximumTrustCount);
        Assert.Equal(21632, plan.Recreate.Capacity.MaximumBucketCount);
        Assert.Equal(32768, plan.Recreate.Capacity.MaximumPersistenceOperationCount);
        Assert.Equal(65536, plan.Recreate.Capacity.PlannedPersistenceIndexCapacity);
        Assert.Equal(7, plan.Recreate.Rules.Length);
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
        Assert.Equal(3_600_000, plan.HotPublish.BucketWidthMilliseconds);
        Assert.Equal(86_400_000, plan.HotPublish.Window24HoursMilliseconds);
        Assert.Equal(604_800_000, plan.HotPublish.Window7DaysMilliseconds);
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
        using var workspace = new NativeReportCoordinatorWorkspace(plan, 101, 102);
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
        Assert.Equal(
            plan.Recreate.Capacity.MaximumReportOutputCount,
            workspace.ReportBuffer.Length);
        Assert.Equal(
            plan.Recreate.Capacity.MaximumPersistenceOperationCount,
            workspace.PersistenceBuffer.Length);
        Assert.Equal(
            plan.Recreate.Capacity.MaximumPersistenceOperationCount,
            workspace.FeedbackBuffer.Length);
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
