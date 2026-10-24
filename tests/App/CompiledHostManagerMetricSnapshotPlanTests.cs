using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class CompiledHostManagerMetricSnapshotPlanTests
{
    [Fact]
    public void HotPublishValidationDoesNotAllocateDuringRepeatedReads()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot.HotPublish;
        Assert.True(plan.IsPublished);
        for (var i = 0; i < 128; i++) _ = plan.IsPublished;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var valid = true;
        for (var i = 0; i < 512; i++) valid &= plan.IsPublished;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(valid);
        Assert.Equal(0, allocated);
        Assert.False((plan with { SourcePolicies = plan.SourcePolicies.Reverse().ToImmutableArray() }).IsPublished);
        Assert.False((plan with { RuleTemplates = plan.RuleTemplates.Add(plan.RuleTemplates[0]) }).IsPublished);
        Assert.False((plan with
        {
            SourcePolicies = plan.SourcePolicies.SetItem(1,
                plan.SourcePolicies[1] with { SourceId = plan.SourcePolicies[0].SourceId })
        }).IsPublished);
    }

    [Fact]
    public void SeparatelyCompiledEquivalentPlanRetainsThePublishedWorkspace()
    {
        var first = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var equivalent = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;

        Assert.NotEqual(first, equivalent);
        Assert.True(HostManagerMetricSnapshotOwner.CanRetainWorkspace(
            first,
            equivalent,
            canApplyHot: true));
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainWorkspace(
            first,
            equivalent,
            canApplyHot: false));
    }

    [Fact]
    public void CapacityPlan_RequiresEveryNativeShapeRelationship()
    {
        var valid = CreateCapacity();

        Assert.True(valid.IsPublished);
        Assert.False((valid with
        {
            MaximumSourcePlanCount = valid.MaximumSourceCount + 1
        }).IsPublished);
        Assert.False((valid with
        {
            MaximumMetricPlanCount = valid.MaximumRuleCount + 1
        }).IsPublished);
        Assert.False((valid with
        {
            MaximumRequestedCount = valid.MaximumRuleCount + 1
        }).IsPublished);
        Assert.False((valid with
        {
            MaximumObservationCount = valid.MaximumRequestedCount - 1
        }).IsPublished);
        Assert.False((valid with
        {
            MaximumMetricPlanCount = valid.MaximumRequestedCount - 1
        }).IsPublished);
    }

    [Theory]
    [InlineData(3U)]
    [InlineData(6U)]
    [InlineData(12U)]
    public void CapacityPlan_RejectsNonPowerOfTwoIndexCapacity(uint value)
    {
        Assert.False((CreateCapacity() with
        {
            SourceIndexCapacity = value
        }).IsPublished);
    }

    [Fact]
    public void RecreatePlan_RequiresExactNativeContractVersions()
    {
        var valid = CreateRecreate();

        Assert.True(valid.IsPublished);
        Assert.Equal(0x0006_0000U, valid.CatalogContractVersion);
        Assert.Equal(0x0009_0000U, valid.ObservationContractVersion);
        Assert.False((valid with { CatalogContractVersion = 0x0004_0000U }).IsPublished);
        Assert.False((valid with { ObservationContractVersion = 0x0006_0000U }).IsPublished);
        var build = new CompiledHostManagerMetricSnapshotBuildPlan(0x000B_0000U, "metric_snapshot", CreateCapacity());
        Assert.True(build.IsPublished);
        Assert.False((build with { AbiVersion = 0x0008_0000U }).IsPublished);
        Assert.False((valid with { ObservationContractVersion = 0x0002_0000U }).IsPublished);
        Assert.False((valid with
        {
            CatalogContractVersion = 0x0001_0001U
        }).IsPublished);
        Assert.False((valid with
        {
            PersistenceContractVersion = 0x0001_0000U
        }).IsPublished);
        Assert.False((valid with
        {
            CpuCounterContractVersion = 0x0002_0000U
        }).IsPublished);
    }

    private static CompiledHostManagerMetricSnapshotRecreatePlan CreateRecreate()
        => new(
            CreateCapacity(),
            "metric-snapshot/metric-snapshot-v2.bin",
            0x0006_0000U,
            0x0004_0000U,
            0x0009_0000U,
            0x0002_0000U,
            0x0005_0000U,
            0x0001_0000U);

    private static CompiledHostManagerMetricSnapshotCapacityPlan CreateCapacity()
        => new(
            MaximumSourceCount: 16,
            MaximumMetricCount: 2048,
            MaximumRuleCount: 4096,
            MaximumRequestedCount: 2048,
            MaximumObservationCount: 4096,
            MaximumGpuAdapterCount: 16,
            MaximumPersistenceSourceCount: 16,
            MaximumPersistenceRuleCount: 4096,
            MaximumPersistenceGpuCount: 16,
            SourceIndexCapacity: 32,
            MetricIndexCapacity: 4096,
            RuleIndexCapacity: 8192,
            GpuIndexCapacity: 32,
            GpuLuidIndexCapacity: 32,
            GpuKeyIndexCapacity: 32,
            MaximumPlanMetricCount: 2048,
            MaximumSourceModeCount: 16,
            MaximumSourcePlanCount: 16,
            MaximumMetricPlanCount: 4096,
            ResidentByteBudget: 67_108_864);
}
