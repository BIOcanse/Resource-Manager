using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class FanCurveRunNativeLifetimeTests
{
    [Fact]
    public async Task UnavailableCoreCannotStartEvenWithEveryStartFactTrue()
    {
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(new Dictionary<int, IBusinessBlock>());
        var facts = new BackendPreciseRuleTestFacts(fixture.Authority, fixture.Native);
        await facts.InitializeAsync();
        await facts.PublishAsync(BackendItems.FanCoreAvailable, false);
        foreach (var fact in BackendExpectedProductionTree.FanCoreFacts.Where(fact => fact is not
            (BackendItems.HostStartPermitted or BackendItems.FanCoreAvailable)))
            await facts.PublishAsync(fact, true);
        await BusinessBlockTestSupport.State(fixture.Scheduler, BackendUids.HardwareControl, BusinessBlockState.Running);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BackendUids.FanControlCoreLifetimeService));
        Assert.DoesNotContain(fixture.Scheduler.Read().History, row => row.BlockId == BackendUids.FanControlCoreLifetimeService
            && row.To == BusinessBlockState.Starting);
    }

    [Fact]
    public async Task DroppedPlanStopsFeedAndReassertRecordsItAgainWithoutDroppingAppliedOutcome()
    {
        var backends = new List<ProbeFanBackend>();
        var opens = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            var backend = new ProbeFanBackend { DriveFailures = opens++ == 0 ? 5 : 0 };
            backends.Add(backend);
            return new LibraryFanSession(new FanControlCore.Discovery.FanCore(backend));
        });
        var metrics = new EmptyTemperatures();
        var plane = new ControlPlane(new Store(), new Layer(core), fan: core);
        using var feed = new FanCurveTemperatureFeed(metrics, core, new HardwareMetricCatalogState());
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(core);
                services.AddSingleton<IControlPlane>(plane);
                services.AddSingleton(new RuntimePlanProvider(new HostManagerDeploymentState()));
                services.AddSingleton<BackendDecisionStateBindings>();
                services.AddBusinessBlockHostedService<FanControlCoreLifetimeService>(BackendUids.FanControlCoreLifetimeService,
                    _ => new FanControlCoreLifetimeService(core));
                services.AddBusinessBlockHostedService<FanCurveTemperatureFeed>(BackendUids.FanCurveTemperatureFeed, _ => feed);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        await host.StartAsync();
        try
        {
            Assert.False(core.SoftwareCurveRunNeeded);
            Assert.Equal(0, opens); // Run-needed cannot exist before a session opens.
            var setting = new ControlSetting("fan.curve", Curve: [new(60, 40)], CurveExecution: "software");
            var applied = await plane.SetObjectSettingsAsync("fan:oem0", [setting], default);
            Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(applied.LastApply.Outcomes).Status);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanCurveTemperatureFeed, BusinessBlockState.Running);
            await BusinessBlockTestSupport.Until(() => metrics.Active == 1);
            Assert.Equal(1, metrics.Active);
            Assert.True(core.SoftwareCurveRunNeeded);

            // The real SDK drops its plan after five failed writes. The control
            // outcome remains Applied; no RM receipt or private retry changes it.
            await BusinessBlockTestSupport.Until(() => !core.SoftwareCurveRunNeeded);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanCurveTemperatureFeed, BusinessBlockState.Stopped);
            Assert.Equal(0, metrics.Active);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Stopped);
            Assert.True(backends[0].Read()[0].AutomaticControl);
            Assert.True(backends[0].Disposed);
            Assert.False(plane.AppliedFanControlExists);
            Assert.Equal(ControlApplyStatuses.Applied, Assert.Single((await plane.ReadStateAsync(default)).LastApply.Outcomes).Status);
            var reasserted = await plane.ReassertAsync(default);
            Assert.Equal(ControlApplyStatuses.Applied, Assert.Single(reasserted.Outcomes).Status);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanCurveTemperatureFeed, BusinessBlockState.Running);
            Assert.True(core.SoftwareCurveRunNeeded);
            Assert.Equal(2, opens);

            // Remove every start fact while retaining the SDK plan. Only the
            // session's existing work keeps the core alive.
            await plane.ForgetInstanceAsync("fan:oem0", default);
            Assert.False(plane.AppliedFanControlExists);
            Assert.False(core.PendingCommands);
            Assert.True(core.SoftwareCurveRunNeeded);
            await core.SendAsync("auto", new Dictionary<string, object?> { ["fan"] = 0 }, default);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Stopped);
            Assert.False(core.SoftwareCurveRunNeeded);
            Assert.Equal(2, opens);
        }
        finally { await host.StopAsync(); }
    }

    private sealed class Layer(FanControlCoreClient core) : IControlWriteLayer
    {
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(ControlApplyReport.Empty);
        public async Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            var outcomes = new List<ControlApplyOutcome>();
            foreach (var target in desired.Objects)
                foreach (var setting in target.Settings)
                {
                    await core.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample> {
                        ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, token, skipIfBusy: false);
                    outcomes.Add(await new FanControlWriter(core).WriteAsync(new(target.ObjectId, ControlObjectKinds.Fan,
                        null, new(ControlOperatingSystems.Windows, ControlVendors.Oem), []),
                        new("fan.curve", ControlValueKinds.Curve, true), setting, token));
                }
            return new(outcomes, DateTimeOffset.UtcNow);
        }
    }
    private sealed class Store : IControlDesiredStateStore
    {
        private ControlDesiredState current = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(current);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token) { current = value; return Task.CompletedTask; }
    }
    private sealed class EmptyTemperatures : IMetricSnapshotObservationSource
    {
        internal int Active;
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval) { Active++; return new Lease(this); }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        private sealed class Lease(EmptyTemperatures owner) : IDisposable { public void Dispose() => owner.Active--; }
    }
}
