using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerMemoryModeCalibrationConfigurationTests
{
    [Fact]
    public void DefaultProfilePublishesTheInlineProductBaseline()
    {
        var policy = HostManagerTestPlanFactory.CreatePlan()
            .SmartCoordinator.HotPublish.MemoryModePolicy;

        Assert.True(policy.IsPublished);
        Assert.True(policy.Enabled);
        Assert.Equal(HostManagerMemoryModePolicySourceKinds.ProductBaseline, policy.SourceKind);
        Assert.Equal(10_000U, policy.RatioUnitsMaximum);
        Assert.Equal(1_000U, policy.StrongBeginFreeRatioUnits);
        Assert.Equal(3_000U, policy.NormalMinimumFreeRatioUnits);
        Assert.Equal(6_000U, policy.UnrestrictedMinimumFreeRatioUnits);
        Assert.True(policy.AllowUnrestricted);
        Assert.Equal(3U, policy.OptimizeMemoryPriority);
        Assert.Equal(1U, policy.PagedFrozenMemoryPriority);
        Assert.Equal(HostManagerMemoryPriorityForeignDispositions.Reject,
            policy.ForeignMemoryPriorityDisposition);
        Assert.Equal(1U, policy.OwnedStateVerificationIntervalCycles);
        Assert.NotEqual(0UL, policy.ConfigurationGeneration);
        Assert.Equal(64, policy.ConfigurationSha256.Length);
    }

    [Fact]
    public void PolicyDigestBindsThresholdCapacityAndExecutionFields()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changedThreshold = HostManagerTestPlanFactory.CreatePlan(root =>
            Policy(root)["normal_minimum_free_ratio_units"] = 3_001);
        var changedCapacity = HostManagerTestPlanFactory.CreatePlan(root =>
            root["host_recreate"]!["smart_coordinator"]!
                ["maximum_software_groups"] = 2_047);
        var changedPriority = HostManagerTestPlanFactory.CreatePlan(root =>
            Policy(root)["optimize_memory_priority"] = 4);

        var current = baseline.SmartCoordinator.HotPublish.MemoryModePolicy;
        Assert.NotEqual(current.ConfigurationGeneration,
            changedThreshold.SmartCoordinator.HotPublish.MemoryModePolicy.ConfigurationGeneration);
        Assert.NotEqual(current.ConfigurationGeneration,
            changedCapacity.SmartCoordinator.HotPublish.MemoryModePolicy.ConfigurationGeneration);
        Assert.NotEqual(current.ConfigurationGeneration,
            changedPriority.SmartCoordinator.HotPublish.MemoryModePolicy.ConfigurationGeneration);
    }

    [Fact]
    public void DisabledPolicyRejectsResidualActionState()
    {
        Assert.Throws<InvalidDataException>(() =>
            HostManagerTestPlanFactory.CreatePlan(root =>
            {
                Disable(Policy(root));
                Policy(root)["allow_unrestricted"] = true;
            }));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerTestPlanFactory.CreatePlan(root =>
            {
                Disable(Policy(root));
                Policy(root)["source_kind"] = "product-baseline";
            }));
    }

    [Theory]
    [InlineData("source_kind", "observed-workload")]
    [InlineData("foreign_memory_priority_disposition", "overwrite")]
    [InlineData("owned_state_verification_interval_cycles", 0)]
    [InlineData("owned_state_verification_interval_cycles", 2)]
    [InlineData("ratio_units_maximum", 9_999)]
    [InlineData("strong_begin_free_ratio_units", 0)]
    [InlineData("normal_minimum_free_ratio_units", 1_000)]
    [InlineData("unrestricted_minimum_free_ratio_units", 3_000)]
    public void ProductBaselineRejectsUnsupportedOrAmbiguousFields(
        string field,
        object value)
        => Assert.Throws<InvalidDataException>(() =>
            HostManagerTestPlanFactory.CreatePlan(root =>
                Policy(root)[field] = JsonValue.Create(value)));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 4)]
    public void PolicyRejectsInvalidMemoryPriorityTargets(
        int optimizeMemoryPriority,
        int pagedFrozenMemoryPriority)
        => Assert.Throws<InvalidDataException>(() =>
            HostManagerTestPlanFactory.CreatePlan(root =>
            {
                var policy = Policy(root);
                policy["optimize_memory_priority"] = optimizeMemoryPriority;
                policy["paged_frozen_memory_priority"] = pagedFrozenMemoryPriority;
            }));

    private static JsonObject Policy(JsonObject root)
        => root["hot_publish"]!["smart_coordinator"]!
            ["memory_mode_policy"]!.AsObject();

    private static void Disable(JsonObject policy)
    {
        policy["enabled"] = false;
        policy["source_kind"] = string.Empty;
        policy["ratio_units_maximum"] = 0;
        policy["strong_begin_free_ratio_units"] = 0;
        policy["normal_minimum_free_ratio_units"] = 0;
        policy["unrestricted_minimum_free_ratio_units"] = 0;
        policy["allow_unrestricted"] = false;
        policy["foreign_memory_priority_disposition"] = string.Empty;
        policy["owned_state_verification_interval_cycles"] = 0;
    }
}
