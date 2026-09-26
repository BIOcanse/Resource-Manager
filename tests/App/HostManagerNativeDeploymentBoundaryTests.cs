using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerNativeDeploymentBoundaryTests
{
    [Fact]
    public void MemoryCleanup_RuntimeProjectionFailureDoesNotChangeDeploymentHealth()
    {
        var fixture = CreateFixture();
        using var planner = new NativeAutomaticMemoryCleanupPlanner(
            new HostManagerMemoryCleanupRuntime(fixture.Provider, fixture.Deployment));

        var empty = planner.Plan(new AutomaticMemoryCleanupPlanRequest(
            AutomaticMemoryCleanupRequestKind.Normal,
            0.50,
            0.50,
            0.50,
            []));
        Assert.Equal(
            fixture.Provider.Current.HostManager.RequirePublished()
                .HotPublish.MemoryCleanup.ConfigurationGeneration,
            empty.ConfigurationGeneration);
        AssertHealthy(fixture.Deployment.Snapshot.MemoryCleanup);

        Assert.Throws<ArgumentNullException>(() => planner.Plan(new AutomaticMemoryCleanupPlanRequest(
            AutomaticMemoryCleanupRequestKind.Normal,
            0.50,
            0.50,
            0.50,
            null!)));
        AssertHealthy(fixture.Deployment.Snapshot.MemoryCleanup);
    }

    [Theory]
    [InlineData(0.00, 6)]
    [InlineData(0.06, 4)]
    [InlineData(0.12, 2)]
    [InlineData(0.20, 1)]
    [InlineData(0.30, 0)]
    [InlineData(1.00, 0)]
    public void MemoryCleanup_NormalFiveBandsRoundTripThroughManagedAbi(
        double ordinaryMemoryFreeRatio,
        int expectedCount)
    {
        var fixture = CreateFixture();
        using var planner = new NativeAutomaticMemoryCleanupPlanner(
            new HostManagerMemoryCleanupRuntime(fixture.Provider, fixture.Deployment));
        var candidates = Enumerable.Range(0, 8)
            .Select(static index => new AutomaticMemoryCleanupCandidate(
                $"process:managed-native-band:{index}",
                $"Managed native band {index}",
                ProcessId: 30_000 + index,
                ProcessStartedAt: DateTimeOffset.FromFileTime(
                    133_700_000_000_000_000L + index * TimeSpan.TicksPerSecond),
                RuntimeState: HostManagerRuntimeStates.BackgroundProcess,
                BaseScore: 20,
                CpuScore: index + 1,
                MemoryUsedPercent: 10,
                CanApply: true))
            .ToArray();

        var result = planner.Plan(new AutomaticMemoryCleanupPlanRequest(
            AutomaticMemoryCleanupRequestKind.Normal,
            ordinaryMemoryFreeRatio,
            PhysicalMemoryFreeRatio: 1,
            VirtualMemoryFreeRatio: 1,
            Candidates: candidates));

        Assert.Equal(
            fixture.Provider.Current.HostManager.RequirePublished()
                .HotPublish.MemoryCleanup.ConfigurationGeneration,
            result.ConfigurationGeneration);
        Assert.Equal(expectedCount, result.Decisions.Count);
        Assert.Equal(
            Enumerable.Range(0, expectedCount),
            result.Decisions.Select(static decision => decision.SourceInputIndex));
        Assert.All(result.Decisions, static decision =>
            Assert.Equal(AutomaticMemoryCleanupMode.Normal, decision.Mode));
        AssertHealthy(fixture.Deployment.Snapshot.MemoryCleanup);
    }

    [Fact]
    public void MemoryCleanup_UnknownNativeModeFailsClosed()
    {
        Assert.Equal(
            AutomaticMemoryCleanupMode.Normal,
            NativeAutomaticMemoryCleanupPlanner.ProjectMode(NativeMemoryCleanupMode.Normal));
        Assert.Equal(
            AutomaticMemoryCleanupMode.Emergency,
            NativeAutomaticMemoryCleanupPlanner.ProjectMode(NativeMemoryCleanupMode.Emergency));
        Assert.Throws<InvalidDataException>(() =>
            NativeAutomaticMemoryCleanupPlanner.ProjectMode(
                (NativeMemoryCleanupMode)uint.MaxValue));
    }

    private static Fixture CreateFixture()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        return new Fixture(deployment, provider);
    }

    private static void AssertHealthy(HostManagerModuleDeploymentSnapshot module)
    {
        Assert.Equal(HostManagerDeploymentStatus.InSync, module.Status);
        Assert.Equal(HostManagerDeploymentHealth.Healthy, module.Health);
        Assert.Null(module.ActiveAttempt);
        Assert.NotEqual(0UL, module.LastSettledAttemptId);
        Assert.Null(module.LastFailure);
    }

    private sealed record Fixture(
        HostManagerDeploymentState Deployment,
        IRuntimePlanProvider Provider);
}
