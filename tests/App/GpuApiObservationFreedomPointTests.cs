using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class GpuApiObservationFreedomPointTests
{
    [Fact]
    public void NoPresentExpiryIsVisibleAndOverridesReachTheCompiledObserverSetting()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(86400, baseline.HotPublish.PlacementCoordinator.NoPresentExpirySeconds);
        var point = Assert.Single(baseline.FreedomPoints.EnumeratePoints(), item => item.Address == BackendFreedomPointPaths.GpuNoPresentExpiry);
        Assert.Equal("gpu_no_present_expiry_seconds", point.Id);
        Assert.Equal("rebuild_backend", point.UpdateClass);
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuNoPresentExpiry)["value"] = 3600);
        Assert.Equal(3600, changed.HotPublish.PlacementCoordinator.NoPresentExpirySeconds);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
    }
    [Fact]
    public void ObservationWindowHasOneDeclaredCompiledOwner()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(5000, plan.HotPublish.PlacementCoordinator.ApiObservationWindowMilliseconds);
        var point = Assert.Single(plan.FreedomPoints.EnumeratePoints(), x => x.Address == BackendFreedomPointPaths.GpuApiObservationWindow);
        Assert.Equal("gpu_api_observation_window_ms", point.Id);
        Assert.Equal("active", point.Status);
        Assert.Equal("rebuild_backend", point.UpdateClass);
        Assert.Equal("HostManagerPlanCompiler.CompilePlacementCoordinatorHotPublish", Assert.Single(point.Consumers));
        Assert.Equal(5000, point.Value!.Value.GetInt32());
    }

    [Fact]
    public void OverrideChangesCompiledWindowAndDigestNotSamplingOrScore()
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuApiObservationWindow)["value"] = 7500);
        Assert.Equal(7500, changed.HotPublish.PlacementCoordinator.ApiObservationWindowMilliseconds);
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
        Assert.Equal(baseline.SchedulerSamplingInterval, changed.SchedulerSamplingInterval);
        Assert.Equal(baseline.SmartCoordinator.ConfigurationSha256, changed.SmartCoordinator.ConfigurationSha256);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.25")]
    [InlineData("\"5000\"")]
    [InlineData("2147483648")]
    [InlineData("null")]
    public void InvalidWindowIsNotSilentlyDefaulted(string literal)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuApiObservationWindow)["value"] = JsonNode.Parse(literal)));

    [Fact]
    public void MissingWindowIsRejected()
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.GpuApiObservationWindow).Parent!.AsArray().Clear()));
}
