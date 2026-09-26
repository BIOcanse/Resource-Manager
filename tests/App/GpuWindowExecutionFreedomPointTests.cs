using System.Text.Json.Nodes;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class GpuWindowExecutionFreedomPointTests
{
    [Fact]
    public void ExistingDeadlineAndWindowLimitsHaveOneExplicitCompiledSource()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var placement = plan.HotPublish.PlacementCoordinator;
        Assert.Equal(30000, placement.ActionTimeoutMilliseconds);
        Assert.Equal(new CompiledGpuWindowExecutionLimits(16, 1000, 80, 4096, 590312), placement.WindowExecution);
        foreach (var address in new[] { BackendFreedomPointPaths.PlacementActionTimeout, BackendFreedomPointPaths.GpuWindowExecutionLimits })
        {
            var point = Assert.Single(plan.FreedomPoints.EnumeratePoints(), point => point.Address == address);
            Assert.Equal("active", point.Status);
            Assert.Equal("HostManagerPlanCompiler.CompilePlacementCoordinatorHotPublish", Assert.Single(point.Consumers));
        }
    }

    [Fact]
    public void OverridesCompileIntoTheConsumerWithoutChangingUnrelatedScoring()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var plan = HostManagerTestPlanFactory.CreatePlan(
            root => root["hot_publish"]!["placement_coordinator"]!["action_timeout_ms"] = 15000,
            editFreedomPoints: root =>
            {
                var value = FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuWindowExecutionLimits)["value"]!;
                value["maximum_window_count"] = 2;
                value["cleanup_reserve_ms"] = 2000;
                value["maximum_frame_bytes"] = 160;
                value["pipe_buffer_bytes"] = 8192;
                value["preparation_maximum_frame_bytes"] = 600000;
            });
        Assert.Equal(15000, plan.HotPublish.PlacementCoordinator.ActionTimeoutMilliseconds);
        Assert.Equal(new CompiledGpuWindowExecutionLimits(2, 2000, 160, 8192, 600000), plan.HotPublish.PlacementCoordinator.WindowExecution);
        Assert.NotEqual(baseline.PlanSha256, plan.PlanSha256);
        Assert.NotEqual(baseline.BuildSha256, plan.BuildSha256);
        Assert.Equal(baseline.SmartCoordinator.ConfigurationSha256, plan.SmartCoordinator.ConfigurationSha256);
    }

    [Theory]
    [InlineData("maximum_window_count", "0")]
    [InlineData("maximum_window_count", "-1")]
    [InlineData("maximum_window_count", "1.25")]
    [InlineData("maximum_window_count", "\"2\"")]
    [InlineData("cleanup_reserve_ms", "0")]
    [InlineData("cleanup_reserve_ms", "30000")]
    [InlineData("cleanup_reserve_ms", "30001")]
    [InlineData("maximum_frame_bytes", "79")]
    [InlineData("preparation_maximum_frame_bytes", "590311")]
    [InlineData("preparation_maximum_frame_bytes", "0")]
    [InlineData("preparation_maximum_frame_bytes", "590312.25")]
    [InlineData("preparation_maximum_frame_bytes", "\"590312\"")]
    [InlineData("pipe_buffer_bytes", "0")]
    [InlineData("pipe_buffer_bytes", "-1")]
    [InlineData("extra_limit", "10")]
    public void InvalidLimitDoesNotBecomeAnExecutablePlan(string field, string literal)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuWindowExecutionLimits)["value"]![field] = JsonNode.Parse(literal)));

    [Theory]
    [InlineData("maximum_window_count")]
    [InlineData("cleanup_reserve_ms")]
    [InlineData("maximum_frame_bytes")]
    [InlineData("preparation_maximum_frame_bytes")]
    [InlineData("pipe_buffer_bytes")]
    public void MissingLimitHasNoHiddenDefault(string field)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuWindowExecutionLimits)["value"]!.AsObject().Remove(field)));
}
