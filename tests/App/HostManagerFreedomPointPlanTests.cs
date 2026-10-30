using System.Text.Json.Nodes;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerFreedomPointPlanTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7500)]
    public void HardwareBridgeAvailabilityIntervalCompilesIntoTheProductPlan(int milliseconds)
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root,
                "resource-manager/backend/hardware_bridge/build/simple/0")["value"] = milliseconds);

        Assert.Equal(TimeSpan.FromSeconds(30), baseline.HardwareBridgeAvailabilityCheckInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), changed.HardwareBridgeAvailabilityCheckInterval);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.True(changed.IsPublished);
    }

    [Fact]
    public void HardwareBridgeAvailabilityIntervalRejectsNegativeValues()
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root,
                "resource-manager/backend/hardware_bridge/build/simple/0")["value"] = -1));

    [Fact]
    public void CurrentTreePublishesPhysicalCpuScoringWithoutActivatingPendingHistoryAlgorithms()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var points = plan.FreedomPoints.EnumeratePoints().ToArray();
        Assert.Equal(37, points.Length);
        Assert.Equal(31, points.Count(point => point.Status == "active"));
        Assert.Equal(6, points.Count(point => point.Status == "pending"));
        Assert.Equal(new CompiledGpuOverflowPolicy(95, 80), plan.HotPublish.PlacementCoordinator.GpuOverflow);
        Assert.DoesNotContain(points, point => point.Address == "resource-manager/backend/scheduling/placement/runtime/complex/1");
        Assert.Equal(TimeSpan.FromSeconds(5), plan.SchedulerSamplingInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.CpuCoreResidency.ObservationWindow);
        Assert.Equal(
            CpuExecutionTimeSourceKinds.KernelEtwContextSwitchClosedIntervals,
            plan.CpuCoreResidency.ExecutionTimeSource.Kind);
        Assert.Equal(65536, plan.CpuCoreResidency.ExecutionTimeSource.MaximumActivePairs);
        Assert.Equal(50, plan.CpuCoreResidency.ExecutionTimeSource.BucketWidthMilliseconds);
        Assert.Equal(CpuSmtAccounting.FixedLogicalProcessorShare, plan.CpuCoreResidency.SmtAccounting);
        foreach (var address in new[]
                 {
                     BackendFreedomPointPaths.CpuResidencyObservationWindow,
                     BackendFreedomPointPaths.CpuResidencyExecutionTimeSource,
                     BackendFreedomPointPaths.CpuSmtAccounting
                 })
        {
            var point = Assert.Single(points, point => point.Address == address);
            Assert.Equal("active", point.Status);
            Assert.Equal("rebuild_backend", point.UpdateClass);
            Assert.Contains("/build/", point.Address);
            Assert.Equal(
                "HostManagerPlanCompiler.CompileCpuCoreResidency",
                Assert.Single(point.Consumers));
        }

        foreach (var id in new[] { "cpu_input_retained_rounds", "score_retained_rounds", "cpu_input_algorithm",
                     "score_algorithm", "boost_entry_exit_policy", "game_boost_notification" })
        {
            var point = Assert.Single(points, point => point.Id == id);
            Assert.Equal("pending", point.Status);
            Assert.NotEmpty(point.PendingReason!);
            Assert.Null(point.Value);
            Assert.Empty(point.Consumers);
        }

        var welfare = Assert.Single(points, point =>
            point.Address == BackendFreedomPointPaths.SoftwareWelfare);
        Assert.Equal("software_welfare", welfare.Id);
        Assert.Equal("active", welfare.Status);
        Assert.Equal("rebuild_backend", welfare.UpdateClass);
        Assert.Equal("free_capacity_welfare_bonus", welfare.Value!.Value.GetProperty("kind").GetString());
        Assert.Equal(100, welfare.Value.Value.GetProperty("base_score").GetDouble());
        Assert.Equal(100, plan.SmartCoordinator.HotPublish.WelfareBaseScore);
        Assert.Equal(
            "HostManagerPlanCompiler.CompileSmartCoordinatorHotPublish",
            Assert.Single(welfare.Consumers));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    public void BackendControlCapacitiesCompileFromFreedomPointsAndAllowZeroHistory(int historyCapacity)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.BusinessBlockHistoryCapacity)["value"] = historyCapacity);

        Assert.Equal(new CompiledBackendControlSettings(
            WorkerCount: 4,
            HistoryCapacity: historyCapacity,
            CompletionCapacity: 128,
            ReaderCapacity: 16,
            ReaderBytes: 4096,
            WorkerArenaBytes: 65536,
            ReasonTextCapacity: 256), plan.BackendControlSettings);
        Assert.True(plan.BackendControlSettings.IsPublished);
        if (historyCapacity == 128)
            Assert.Equal(plan.BackendControlSettings, BackendControlSettings.ProductDefaults);
    }

    [Fact]
    public void CpuResidencyFreedomPointsCompileIntoTheOnlyTypedReaderPlan()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyObservationWindow)["value"] = 7500;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["maximum_active_pairs"] = 262144;
        });

        Assert.Equal(TimeSpan.FromMilliseconds(7500), changed.CpuCoreResidency.ObservationWindow);
        Assert.Equal(262144, changed.CpuCoreResidency.ExecutionTimeSource.MaximumActivePairs);
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
    }

    [Theory]
    [InlineData("fixed_welfare_base")]
    [InlineData("running_process_mean")]
    [InlineData("unknown_idle_share")]
    public void WelfareFreedomPointMustDescribeTheImplementedSoftwareFormula(string kind)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(
            editFreedomPoints: root => FreedomPointTestFactory.Point(root,
                BackendFreedomPointPaths.SoftwareWelfare)["value"]!["kind"] = kind));
    }

    [Fact]
    public void RetiredFixedWelfareParameterIsRejectedInsteadOfSilentlyIgnored()
    {
        var error = Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(
            root => root["hot_publish"]!["smart_coordinator"]!["welfare_base_score"] = 16000));
        Assert.Contains("welfare_base_score", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(100)]
    public void WelfareBaseIsCompiledFromItsFreedomPoint(double welfareBase)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SoftwareWelfare)
                ["value"]!["base_score"] = welfareBase);
        Assert.Equal(welfareBase, plan.SmartCoordinator.HotPublish.WelfareBaseScore);
        var configuration = ResourceManager.App.Infrastructure.Optimization
            .HostManagerComputeScoringConfigurationFactory.Create(plan.SmartCoordinator, plan.CpuScoring!);
        Assert.Equal(welfareBase, configuration.WelfareBaseScore);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(140)]
    public void WelfareBaseRejectsValuesOutsideTheImportanceDomain(double welfareBase)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SoftwareWelfare)
                ["value"]!["base_score"] = welfareBase));
    }

    [Fact]
    public void WelfareBaseMustBeExplicitEvenThoughZeroIsValid()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SoftwareWelfare)
                ["value"]!.AsObject().Remove("base_score")));
    }

    [Fact]
    public void CpuResidencyFreedomPointsReachTheProductionReaderBufferConstructor()
    {
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyObservationWindow)["value"] = 7500;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)["value"]!["bucket_width_ms"] = 25;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["maximum_active_pairs"] = 262144;
        });

        var buffer = EtwCpuCoreResidencyReader.CreateAggregationBuffer(
            changed.CpuCoreResidency,
            qpcFrequency: 1000);
        var diagnostics = buffer.GetDiagnostics();

        Assert.Equal(1000, diagnostics.QpcFrequency);
        Assert.Equal(7500, diagnostics.WindowQpc);
        Assert.Equal(262144, diagnostics.MaximumActivePairs);
        Assert.Equal(25, diagnostics.BucketWidthQpc);
    }

    [Fact]
    public void CpuResidencyFreedomPointsDoNotAlterTheCurrentScoringOrSamplingPlan()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyObservationWindow)["value"] = 7500;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["maximum_active_pairs"] = 262144;
        });

        Assert.Equal(baseline.SchedulerSamplingInterval, changed.SchedulerSamplingInterval);
        Assert.Equal(
            baseline.SmartCoordinator.ConfigurationSha256,
            changed.SmartCoordinator.ConfigurationSha256);
        Assert.Equal(
            baseline.DataHistory.Requirements
                .Select(static item => (item.DataItem, item.RetainedRounds)),
            changed.DataHistory.Requirements
                .Select(static item => (item.DataItem, item.RetainedRounds)));
        var baselineNative = NativeSmartCoordinatorConfigurationWriter.Create(
            baseline.SmartCoordinator.Build,
            baseline.SmartCoordinator.Recreate,
            baseline.SmartCoordinator.HotPublish,
            baseline.SmartCoordinator.ConfigurationGeneration);
        var changedNative = NativeSmartCoordinatorConfigurationWriter.Create(
            changed.SmartCoordinator.Build,
            changed.SmartCoordinator.Recreate,
            changed.SmartCoordinator.HotPublish,
            changed.SmartCoordinator.ConfigurationGeneration);
        Assert.Equal(
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(
                in baselineNative,
                baseline.SmartCoordinator.HotPublish.RealtimeActionBudget),
            NativeSmartCoordinatorConfigurationWriter.ComputeSha256(
                in changedNative,
                changed.SmartCoordinator.HotPublish.RealtimeActionBudget));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CpuResidencyPairCapacityMustRemainPositive(int capacity)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["maximum_active_pairs"] = capacity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CpuResidencyBucketWidthMustBeExplicitAndPositive(int width)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["bucket_width_ms"] = width));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!.AsObject().Remove("bucket_width_ms")));
    }

    [Fact]
    public void CpuResidencyExecutionTimeSourceKindIsClosed()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuResidencyExecutionTimeSource)
                ["value"]!["kind"] = "context_switch_count"));
    }

    [Theory]
    [InlineData("active_thread_share")]
    [InlineData("logical_processors_are_full_cores")]
    public void RetiredOrUnknownSmtRulesCannotReachTheReader(string kind)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuSmtAccounting)
                ["value"]!["kind"] = kind));
    }

    [Fact]
    public void ChangedProfileValuesReachTheTreeAndActualNativeConfiguration()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var smart = root["hot_publish"]!["smart_coordinator"]!;
            smart["required_consecutive_decisions"] = 4;
            smart["game_start_grace_ms"] = 12000;
            smart["failure_retry_ms"] = 8000;
            smart["base_score_tiers"]!["high_minimum_base_score"] = 82;
            smart["process_policy"]!["state_multipliers"]![1] = 1.4;
        });
        var hot = plan.SmartCoordinator.HotPublish;
        Assert.Equal(4, hot.RequiredConsecutiveDecisions);
        Assert.Equal(12000, hot.GameStartGraceMilliseconds);
        Assert.Equal(8000, hot.FailureRetryMilliseconds);
        Assert.Equal(1.4, hot.ProcessStateMultipliers[1]);
        Assert.Empty(plan.DataHistory.Requirements);
        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(plan.SmartCoordinator.Build,
            plan.SmartCoordinator.Recreate, hot, plan.SmartCoordinator.ConfigurationGeneration);
        Assert.Equal(4U, configuration.RequiredConsecutiveDecisions);
        Assert.Equal(12000U, configuration.GameStartGraceMilliseconds);
        Assert.Equal(8000U, configuration.FailureRetryMilliseconds);
        Assert.Equal(82, configuration.HighTierMinimumBaseScore);
        var scoringConfiguration = HostManagerComputeScoringConfigurationFactory.Create(
            plan.SmartCoordinator,
            plan.CpuScoring!);
        Assert.Equal(100D, scoringConfiguration.MaximumBaseImportance);
        using var native = new NativeSmartCoordinatorSession(in configuration);
        var consecutiveDecisions = Assert.Single(plan.FreedomPoints.EnumeratePoints(),
            point => point.Address == BackendFreedomPointPaths.ConsecutiveDecisions);
        Assert.Equal(4, consecutiveDecisions.Value!.Value.GetInt32());
    }

    [Fact]
    public void RegistryValueChangesTheTypedConsumerPlanAndPlanDigestWithoutChangingOtherChoices()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SchedulerSamplingInterval)["value"] = 2500);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), changed.SchedulerSamplingInterval);
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.NotEqual(baseline.FreedomPoints.DeclarationSha256, changed.FreedomPoints.DeclarationSha256);
        Assert.Equal(baseline.ProfileSha256, changed.ProfileSha256);
        Assert.Equal(baseline.SmartCoordinator.ConfigurationSha256, changed.SmartCoordinator.ConfigurationSha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ChangingTheDeclarationTypeCannotRemoveTheConsumerIntervalConstraint(int milliseconds)
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var point = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SchedulerSamplingInterval);
            point["value_type"] = "number";
            point["value"] = milliseconds;
        }));
    }

    [Fact]
    public void PublicationRequiresTheCompiledTreeAndSubscriptionInterval()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        foreach (var incomplete in new[]
                 {
                     plan with { FreedomPoints = CompiledFreedomPointTree.Empty },
                     plan with { CpuCoreResidency = CompiledCpuCoreResidencyPlan.Unpublished },
                     plan with { CpuScoring = null },
                     plan with { SchedulerSamplingInterval = TimeSpan.Zero },
                     plan with { SchedulerSamplingInterval = TimeSpan.FromMilliseconds(-1) }
                 })
        {
            Assert.False(incomplete.IsPublished);
            Assert.Throws<InvalidOperationException>(() => incomplete.RequirePublished());
        }
    }

    [Fact]
    public void ChangingAPendingDescriptionDoesNotChangeTheCompiledBuildChoices()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, "resource-manager/backend/scoring/cpu/runtime/complex/3")
                ["pending_reason"] = "Still awaiting the score smoothing implementation.");
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
        Assert.Equal(baseline.BuildSha256, changed.BuildSha256);
        Assert.Equal(baseline.SmartCoordinator.ConfigurationSha256, changed.SmartCoordinator.ConfigurationSha256);
    }

    [Fact]
    public void ExtraActivePointCannotBeDeclaredWithoutAnActualCompilerConsumer()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var point = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SchedulerSamplingInterval);
            var unused = point.DeepClone();
            unused["index"] = 1;
            unused["id"] = "unused_interval";
            point.Parent!.AsArray().Add(unused);
        }));
    }

    [Fact]
    public void RemovingOrRetiringARequiredPointStopsCompilation()
    {
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SchedulerSamplingInterval).Parent!.AsArray().Clear()));
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var point = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.ConsecutiveDecisions);
            point["status"] = "pending";
            point["pending_reason"] = "Not connected";
            point.Remove("consumers");
            point.Remove("value_source");
            point.Remove("source_path");
        }));
    }
}
