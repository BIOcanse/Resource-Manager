using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// The optimization, GPU placement and persistence values that used to be literals are declared freedom points
/// (2026-10-31: no effective hard-coded values). Their defaults are the former literals, the plan publishes them,
/// the owners read the same values, and an edited declaration flows into the plan.
/// </summary>
public sealed class OptimizationFreedomPointDeclarationTests
{
    [Fact]
    public void DeclaredDefaultsAreTheFormerLiteralsAndOwnersReadThePublishedValues()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var optimization = plan.Optimization!;
        Assert.Equal(new RollbackStateStoreOptions(60000, 4 * 1024 * 1024), optimization.RollbackStateStore);
        Assert.Equal(new AuthorityRetirementOptions(64, 64, 64, 4096, 16L * 1024 * 1024 * 1024), optimization.AuthorityRetirement);
        Assert.Equal(129, optimization.AuthorityRetirement.MaximumRetirementDirectoryEntries);
        Assert.Equal(new IdealProcessorWorkspaceOptions(4, 128, 4, 32, 2), optimization.IdealProcessorWorkspace);
        Assert.Equal(new NormalRestorationOptions(10000, 4096), optimization.NormalRestoration);
        Assert.Equal(new EffectValidationLeaseOptions(60000, 7200000) { MinimumReleaseTokenCharacters = 32, MaximumReleaseTokenCharacters = 512 }, optimization.EffectValidationLease);
        Assert.Equal(new MemoryCleanupValidationEvidenceOptions(256, 4096), optimization.MemoryCleanupValidationEvidence);
        Assert.Equal(new SmartRecordHistoryOptions(500), optimization.SmartRecordHistory);
        Assert.Equal(new SoftwareKindBaseScores(95, 85, 80, 60, 60, 60, 45, 42, 30, 35), optimization.SoftwareKindBaseScores);
        Assert.Equal(new CpuCoreClassScores(100, 65, 45, 82), optimization.CoreClassScores);
        Assert.Equal(TimeSpan.FromSeconds(10), optimization.ReportSampleInterval);
        Assert.Equal(1000U, optimization.ReportRuleLimits.MaximumConsecutiveCount);
        Assert.Equal(10, optimization.ReportRuleLimits.MaximumZeroActivityPercent);
        Assert.Equal(256, optimization.ReportRuleLimits.MaximumExecutableNames);
        Assert.Equal(260, optimization.ReportRuleLimits.MaximumExecutableNameCharacters);
        Assert.Equal(new ReportRuleThresholdRange("celsius", 1, 1, 40, 110), optimization.ReportRuleLimits.ThresholdRanges["CpuTemperatureCelsius"]);
        Assert.Equal(new OptimizationStorageLimits(65536, 32, 8192, 1024), optimization.StorageLimits);
        Assert.Equal(new LegacyAffinityOptions(6, 4, 8, 2, 16, 4, 4, 2), optimization.LegacyAffinity);
        Assert.Equal(new OptimizationRuntimeLimits(100, 256, 1024, 1000, 10, 0, 1, 2), optimization.RuntimeLimits);

        var placement = plan.GpuPlacement!;
        Assert.Equal(new GpuPlacementProcessHistoryOptions(64, 600000, 30000, 5000, 55, 85, 10), placement.ProcessHistory);
        Assert.Equal(new ExternalGpuRuntimeOptions(10000, 1000, 5000, 50, 30, 32, 1000, 50), placement.ExternalRuntime);
        Assert.Equal(new GpuInjectionOptions(20, 25, 10) { MaximumRendererQualificationRows = 4096 }, placement.Injection);
        Assert.Equal(new GpuLaunchReportOptions(256), placement.LaunchReports);
        Assert.Equal(new SqliteVacuumOptions(32L * 1024 * 1024, 0.10), plan.Persistence!.SqliteVacuum);

        // Owners built before the first publication read the same declarations.
        Assert.Equal(optimization.StorageLimits, OptimizationSettings.ProductDefaults.StorageLimits);
        Assert.Equal(optimization.LegacyAffinity, OptimizationSettings.ProductDefaults.LegacyAffinity);
        Assert.Equal(optimization.RuntimeLimits, OptimizationSettings.ProductDefaults.RuntimeLimits);
        Assert.Equal(optimization.SoftwareKindBaseScores, OptimizationSettings.ProductDefaults.SoftwareKindBaseScores);
        Assert.Equal(placement, GpuPlacementSettings.ProductDefaults);
        Assert.Equal(plan.Persistence, PersistenceSettings.ProductDefaults);
        Assert.Equal(TimeSpan.FromMinutes(1), JsonHostManagerRollbackStateStore.DefaultCheckpointInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), GpuPlacementProcessHistoryObserver.ObservationWindow);
        Assert.Equal(256, HostManagerMemoryCleanupValidationEvidenceLedger.MaximumBatchCount);
    }

    [Fact]
    public void EditedDeclarationsFlowIntoThePlan()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.IdealProcessorWorkspace)["value"]!["low_usage_calls_before_trim"] = 9;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SoftwareKindBaseScores)["value"]!["other"] = 40;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.ReportSampleInterval)["value"] = 20000;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuPlacementProcessHistory)["value"]!["maximum_processes_per_software"] = 8;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.SqliteVacuum)["value"]!["minimum_free_page_ratio"] = 0.25;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.OptimizationStorageLimits)["value"]!["maximum_json_depth"] = 48;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.LegacyAffinity)["value"]!["minimum_logical_processors"] = 8;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.OptimizationRuntimeLimits)["value"]!["startup_catalog_subscription_ms"] = 2000;
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.ReportRuleLimits)["value"]!["threshold_ranges"]!["CpuTemperatureCelsius"]!["maximum"] = 105;
        });
        Assert.Equal(9, plan.Optimization!.IdealProcessorWorkspace.LowUsageCallsBeforeTrim);
        Assert.Equal(40, plan.Optimization.SoftwareKindBaseScores.ForKind("UnknownFutureKind"));
        Assert.Equal(TimeSpan.FromSeconds(20), plan.Optimization.ReportSampleInterval);
        Assert.Equal(8, plan.GpuPlacement!.ProcessHistory.MaximumProcessesPerSoftware);
        Assert.Equal(0.25, plan.Persistence!.SqliteVacuum.MinimumFreePageRatio);
        Assert.Equal(48, plan.Optimization.StorageLimits.MaximumJsonDepth);
        Assert.Equal(8, plan.Optimization.LegacyAffinity.MinimumLogicalProcessors);
        Assert.Equal(2000, plan.Optimization.RuntimeLimits.StartupCatalogSubscriptionMilliseconds);
        Assert.Equal(105, plan.Optimization.ReportRuleLimits.ThresholdRanges["CpuTemperatureCelsius"].Maximum);
    }

    [Theory]
    [InlineData(BackendFreedomPointPaths.ReportSampleInterval, null, 1500)] // The report status sends whole seconds.
    [InlineData(BackendFreedomPointPaths.IdealProcessorWorkspace, "trim_usage_divisor", 0)]
    [InlineData(BackendFreedomPointPaths.EffectValidationLease, "maximum_ms", 1000)]
    [InlineData(BackendFreedomPointPaths.SoftwareKindBaseScores, "game", 101)]
    [InlineData(BackendFreedomPointPaths.CoreClassScores, "efficient", 0)]
    [InlineData(BackendFreedomPointPaths.GpuPlacementProcessHistory, "confidence_with_path", 101)]
    [InlineData(BackendFreedomPointPaths.ExternalGpuRuntime, "controller_poll_ms", 0)]
    [InlineData(BackendFreedomPointPaths.SqliteVacuum, "minimum_free_page_ratio", 2)]
    [InlineData(BackendFreedomPointPaths.OptimizationStorageLimits, "io_buffer_bytes", 0)]
    [InlineData(BackendFreedomPointPaths.LegacyAffinity, "large_machine_reservation_divisor", 0)]
    [InlineData(BackendFreedomPointPaths.OptimizationRuntimeLimits, "startup_catalog_subscription_ms", 0)]
    public void InvalidDeclarationsFailCompilation(string address, string? field, double value)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var point = FreedomPointTestFactory.Point(root, address);
            if (field is null) point["value"] = value;
            else point["value"]![field] = value;
        }));

    [Fact]
    public void ResourceSchedulerDangerRatiosComeFromTheProfile()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan().HotPublish.ResourceScheduler.Configuration!;
        Assert.Equal((0.10, 0.12), (baseline.DangerMinimumPhysicalAfterVramMoveRatio, baseline.DangerMinimumVirtualAfterPhysicalMoveRatio));
        var changed = HostManagerTestPlanFactory.CreatePlan(editProfile: root =>
        {
            var configuration = root["hot_publish"]!["resource_scheduler"]!["configuration"]!;
            configuration["danger_minimum_physical_after_vram_move_ratio"] = 0.08;
            configuration["danger_minimum_virtual_after_physical_move_ratio"] = 0.15;
        }).HotPublish.ResourceScheduler.Configuration!;
        Assert.Equal((0.08, 0.15), (changed.DangerMinimumPhysicalAfterVramMoveRatio, changed.DangerMinimumVirtualAfterPhysicalMoveRatio));
    }
}
