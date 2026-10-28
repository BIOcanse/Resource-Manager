using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class BackendControlDecisionFactsTests
{
    [Fact]
    public void BackgroundReportFactFollowsThePublishedRuleSet()
    {
        var provider = new RuntimePlanProvider(new HostManagerDeploymentState());
        Assert.False(provider.DecisionFacts.BackgroundReportsEnabled);
        var hostPlan = HostManagerTestPlanFactory.CreatePlan();
        Assert.False(hostPlan.ReportCoordinator.Recreate.Rules.IsDefaultOrEmpty);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = hostPlan });
        Assert.True(provider.DecisionFacts.BackgroundReportsEnabled);
    }

    [Fact]
    public async Task HostBindingsObserveStoppedSourceAndCancelUnpublishedFinitePreparation()
    {
        var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
        using var reader = new ControlActualStateReader(new Catalog(), []);
        using var services = new ServiceCollection().AddSingleton(plans).AddSingleton(reader).BuildServiceProvider();
        var authority = new StateAuthority(StateRegistry.Items, StateRegistry.Blocks);
        var bindings = new BackendDecisionStateBindings(services, NullLogger<BackendDecisionStateBindings>.Instance);
        try
        {
            await bindings.InitializeAsync(authority, default);
            Assert.Equal(NoValueReason.NotPublished,
                authority.Read<bool>(StateItemUids.GpuApiHistoryEnabled).NoValueReason);
            Assert.Equal(NoValueReason.NotPublished,
                authority.Read<bool>(StateItemUids.BackgroundReportsEnabled).NoValueReason);
            Assert.False(authority.Read<bool>(StateItemUids.ActualControlValuesInUse).Value);
            using (reader.AcquireSubscription())
                Assert.True(authority.Read<bool>(StateItemUids.ActualControlValuesInUse).Value);
            Assert.False(authority.Read<bool>(StateItemUids.ActualControlValuesInUse).Value);
            await bindings.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await bindings.DisposeAsync(); }
        Assert.Equal(NoValueReason.OwnerStopped,
            authority.Read<bool>(StateItemUids.ActualControlValuesInUse).NoValueReason);
    }

    [Fact]
    public async Task AppliedFactsFollowSuccessfulIntentAndExcludeSuccessfulReleaseReceipts()
    {
        var store = new Store();
        var writer = new Writer();
        var plane = new ControlPlane(store, writer);
        var changes = new List<(bool Applied, bool Fan)>();
        plane.AppliedIntentsChanged += () => changes.Add((plane.AppliedControlIntentsExist, plane.AppliedFanControlExists));
        Assert.False(plane.AppliedControlIntentsExist);
        Assert.False(plane.AppliedFanControlExists);

        await plane.ApplyDesiredStateAsync(new([new("fan:0", [new("duty", 40)])]), default);
        Assert.True(plane.AppliedControlIntentsExist);
        Assert.True(plane.AppliedFanControlExists);

        await plane.ApplyDesiredStateAsync(new([new("cpu:0", [new("power", 60)])]), default);
        Assert.True(plane.AppliedControlIntentsExist);
        Assert.False(plane.AppliedFanControlExists);
        await plane.ApplyDesiredStateAsync(ControlDesiredState.Empty, default);
        Assert.False(plane.AppliedControlIntentsExist);
        Assert.False(plane.AppliedFanControlExists);
        Assert.Equal([(true, true), (false, true), (true, false), (false, false), (false, false)], changes);

        // A rejected edit keeps old receipts for other objects, including prior
        // successful releases. Those receipts cannot become live ownership.
        await plane.SetObjectSettingsAsync("fan:1", [
            new("fan.duty", 40),
            new("fan.curve", Curve: [new(60, 40)], CurveExecution: ControlFanCurveExecutions.Firmware)
        ], default);
        Assert.False(plane.AppliedControlIntentsExist);
        Assert.False(plane.AppliedFanControlExists);
    }

    [Fact]
    public async Task FailedIntentCannotStartMaintenance()
    {
        var plane = new ControlPlane(new Store(), new Writer { Succeed = false });
        await plane.ApplyDesiredStateAsync(new([new("fan:0", [new("duty", 40)])]), default);
        Assert.False(plane.AppliedControlIntentsExist);
        Assert.False(plane.AppliedFanControlExists);
    }

    [Fact]
    public async Task ActualValueSubscriptionsPublishUsageWhileTheWorkerIsStopped()
    {
        using var reader = new ControlActualStateReader(new Catalog(), []);
        var changes = 0;
        reader.SubscriptionsChanged += () => changes++;
        Assert.False(reader.ActualControlValuesInUse);
        using var first = reader.AcquireSubscription();
        using var second = reader.AcquireSubscription();
        Assert.True(reader.ActualControlValuesInUse);
        Assert.Equal(1, changes);
        await reader.StopAsync(default);
        Assert.True(reader.ActualControlValuesInUse);
        first.Dispose();
        Assert.True(reader.ActualControlValuesInUse);
        second.Dispose();
        Assert.False(reader.ActualControlValuesInUse);
        Assert.Equal(2, changes);
        Assert.Same(ControlActualState.Empty, reader.Current);
    }

    private sealed class Store : IControlDesiredStateStore
    {
        private ControlDesiredState current = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(current);
        public Task SaveAsync(ControlDesiredState desired, CancellationToken token)
        { current = desired; return Task.CompletedTask; }
    }

    private sealed class Writer : IControlWriteLayer
    {
        internal bool Succeed = true;
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token) =>
            Task.FromResult(Report(desired, Succeed ? ControlApplyStatuses.Applied : ControlApplyStatuses.Failed));
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining,
            CancellationToken token) => Task.FromResult(Report(removed, ControlApplyStatuses.Applied));
        private static ControlApplyReport Report(ControlDesiredState desired, string status) => new(
            desired.Objects.SelectMany(target => target.Settings.Select(setting =>
                new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, status))).ToArray(), DateTimeOffset.UtcNow);
    }

    private sealed class Catalog : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects() => new([], DateTimeOffset.UtcNow);
    }
}
