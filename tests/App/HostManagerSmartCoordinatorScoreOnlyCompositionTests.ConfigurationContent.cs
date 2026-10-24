using System.Reflection;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task GpuOverflowSettingChangeReconfiguresPlacementAtTheSameProfileRevision()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true);
        var ensure = typeof(HostManagerSmartCoordinator).GetMethod(
            "EnsurePlacementCoordinatorWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ensure.Invoke(fixture.Coordinator, null);
        var session = ReadPrivateField<NativePlacementCoordinatorSession>(
            fixture.Coordinator, "placementCoordinatorSession");
        var original = fixture.RuntimePlan.HostManager.HotPublish.PlacementCoordinator;
        var changed = HostManagerTestPlanFactory.CreatePlan(performance:
            AppSettingsDefaults.Create().Performance with
            {
                GpuOverflowUsagePercent = checked((byte)(original.GpuOverflow!.UsagePercent - 1)),
                GpuOverflowDedicatedMemoryPercent = checked((byte)(original.GpuOverflow.DedicatedMemoryPercent - 1))
            });
        Assert.Equal(original.ConfigurationGeneration,
            changed.HotPublish.PlacementCoordinator.ConfigurationGeneration);
        Assert.NotEqual(original.GpuOverflow, changed.HotPublish.PlacementCoordinator.GpuOverflow);
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            HostManager = changed
        });

        ensure.Invoke(fixture.Coordinator, null);
        ensure.Invoke(fixture.Coordinator, null);

        Assert.Same(session, ReadPrivateField<NativePlacementCoordinatorSession>(
            fixture.Coordinator, "placementCoordinatorSession"));
        var applied = ReadPrivateField<PlacementCoordinatorRuntimePlan>(
            fixture.Coordinator, "appliedPlacementCoordinatorPlan")!;
        Assert.Equal(changed.HotPublish.PlacementCoordinator, applied.HotPublish);
    }

    [Fact]
    public async Task SmartCoordinatorContentChangeReconfiguresAtTheSameProfileRevision()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            policyExecutionEnabled: false, automaticMemoryCleanupEnabled: false);
        var original = fixture.RuntimePlan.HostManager.SmartCoordinator;
        var changed = HostManagerTestPlanFactory.CreatePlan(root =>
            root["hot_publish"]!["smart_coordinator"]!["normal_interval_ms"] =
                original.HotPublish.NormalIntervalMilliseconds + 1);
        Assert.Equal(original.ConfigurationGeneration, changed.SmartCoordinator.ConfigurationGeneration);
        Assert.NotEqual(original.ConfigurationSha256, changed.SmartCoordinator.ConfigurationSha256);
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = fixture.RuntimePlan.Version + 1,
            HostManager = changed
        });

        await fixture.RunRealtimeCycleAsync();
        await fixture.RunRealtimeCycleAsync();

        Assert.Same(fixture.Workspace, ReadPrivateField<NativeSmartCoordinatorWorkspace>(
            fixture.Coordinator, "nativeWorkspace"));
        var applied = ReadPrivateField<HostManagerSmartCoordinatorRuntimePlan>(
            fixture.Coordinator, "appliedNativeRuntimePlan")!;
        Assert.Equal(changed.SmartCoordinator.ConfigurationSha256,
            applied.SmartCoordinator.ConfigurationSha256);
    }
}
