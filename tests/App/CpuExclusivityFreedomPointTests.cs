using System.Text.Json.Nodes;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed class CpuExclusivityFreedomPointTests
{
    [Theory]
    [InlineData("enabled")]
    [InlineData("core_enter_percent")]
    [InlineData("core_exit_percent")]
    [InlineData("ccd_enter_percent")]
    [InlineData("ccd_exit_percent")]
    [InlineData("qualification_completed_rounds")]
    public void MissingPolicyFieldsAreNotDefaulted(string field)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuAutomaticExclusivity)["value"]!.AsObject().Remove(field)));
    [Fact]
    public void AutomaticExclusivityIsActiveAndThresholdsHaveOneCompiledOwner()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        var policy = plan.HotPublish.PlacementCoordinator.CpuAutomaticExclusivity!;
        Assert.True(policy.Enabled);
        Assert.True(policy.IsValid);
        Assert.Equal((75d, 20d, 60d, 30d, 3), (policy.CoreEnterPercent, policy.CoreExitPercent, policy.CcdEnterPercent, policy.CcdExitPercent, policy.QualificationCompletedRounds));
        var point = Assert.Single(plan.FreedomPoints.EnumeratePoints(), p => p.Address == BackendFreedomPointPaths.CpuAutomaticExclusivity);
        Assert.Equal("active", point.Status);
        Assert.Equal("HostManagerPlanCompiler.CompilePlacementCoordinatorHotPublish", Assert.Single(point.Consumers));
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuAutomaticExclusivity)["value"]!["core_enter_percent"] = 80);
        Assert.Equal(80, changed.HotPublish.PlacementCoordinator.CpuAutomaticExclusivity!.CoreEnterPercent);
        Assert.NotEqual(plan.DeploymentDigests.PlacementCoordinator.HotPublishSha256, changed.DeploymentDigests.PlacementCoordinator.HotPublishSha256);
        Assert.Equal(plan.DeploymentDigests.PlacementCoordinator.RecreateSha256, changed.DeploymentDigests.PlacementCoordinator.RecreateSha256);
        Assert.Equal(plan.SmartCoordinator.ConfigurationSha256, changed.SmartCoordinator.ConfigurationSha256);
    }

    [Theory]
    [InlineData("core_enter_percent", "0")]
    [InlineData("core_exit_percent", "75")]
    [InlineData("ccd_enter_percent", "101")]
    [InlineData("ccd_exit_percent", "-1")]
    [InlineData("qualification_completed_rounds", "-1")]
    public void InvalidPolicyIsRejected(string field, string literal)
        => Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            FreedomPointTestFactory.Point(root, BackendFreedomPointPaths.CpuAutomaticExclusivity)["value"]![field] = JsonNode.Parse(literal)));
}
