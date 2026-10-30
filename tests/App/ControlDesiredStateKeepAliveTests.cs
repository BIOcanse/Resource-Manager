using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class ControlDesiredStateKeepAliveTests
{
    [Fact]
    public async Task DefaultOffAppliesOnceAfterCatalogWithoutEverStartingReassertion()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.PublishCatalog();
        await fixture.WaitForWrites(2);
        await fixture.Plane.InitializeAsync(default);
        fixture.Clock.Advance(1200);
        Assert.Equal(["good", "failed"], fixture.Writes.ToArray());
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.ControlDesiredStateReassertion));
        Assert.DoesNotContain(fixture.Scheduler.Read().History, entry =>
            entry.BlockId == BlockUids.ControlDesiredStateReassertion && entry.To == BusinessBlockState.Starting);
        fixture.PublishCatalog();
        await fixture.Plane.InitializeAsync(default);
        fixture.Clock.Advance(1200);
        Assert.Equal(2, fixture.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlySuccessfulItemsRepeatAndDisablingStopsWithoutAnotherWrite(bool enabledAtStartup)
    {
        await using var fixture = new Fixture();
        await fixture.SetKeepAlive(enabledAtStartup);
        await fixture.StartAsync();
        fixture.PublishCatalog();
        await fixture.WaitForWrites(2);
        if (!enabledAtStartup)
        {
            Assert.Equal(0, fixture.Clock.ActiveTimers);
            Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.ControlDesiredStateReassertion));
            await fixture.SetKeepAlive(true);
        }
        await fixture.Clock.NextTimerAsync();
        await fixture.WaitForState(BusinessBlockState.Running);
        fixture.Clock.Advance(119);
        Assert.Equal(2, fixture.Writes.Count);
        fixture.Clock.Advance(1);
        await fixture.WaitForWrites(3);
        await fixture.Clock.NextTimerAsync();
        fixture.Clock.Advance(120);
        await fixture.WaitForWrites(4);
        await fixture.Clock.NextTimerAsync();
        Assert.Equal(["good", "failed", "good", "good"], fixture.Writes.ToArray());
        Assert.Contains((await fixture.Plane.ReadStateAsync(default)).LastApply.Outcomes,
            outcome => outcome.CapabilityId == "failed" && outcome.Status == ControlApplyStatuses.Failed);
        await fixture.SetKeepAlive(false);
        await fixture.WaitForState(BusinessBlockState.Stopped);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
        fixture.Clock.Advance(1200);
        Assert.Equal(4, fixture.Writes.Count);
        // A second start gets a fresh interval and cannot repeat the startup apply.
        await fixture.SetKeepAlive(true);
        await fixture.Clock.NextTimerAsync();
        await fixture.WaitForState(BusinessBlockState.Running);
        Assert.Equal(4, fixture.Writes.Count);
        fixture.Clock.Advance(119);
        Assert.Equal(4, fixture.Writes.Count);
        fixture.Clock.Advance(1);
        await fixture.WaitForWrites(5);
        await fixture.Clock.NextTimerAsync();
        await fixture.SetKeepAlive(false);
        await fixture.WaitForState(BusinessBlockState.Stopped);
        Assert.Equal(1, fixture.Writes.Count(id => id == "failed"));
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "rm-control-keepalive-" + Guid.NewGuid().ToString("N"));
        private readonly IHost host;
        private readonly HardwareMetricCatalogState catalog = new();
        internal readonly ControlDesiredStateReassertionTests.Clock Clock = new();
        internal readonly ConcurrentQueue<string> Writes = new();
        internal readonly ControlPlane Plane;
        internal BusinessBlockScheduler Scheduler => host.Services.GetRequiredService<BusinessBlockScheduler>();
        private IAppSettingsStore Settings => host.Services.GetRequiredService<IAppSettingsStore>();

        internal Fixture()
        {
            var plans = new RuntimePlanProvider(new HostManagerDeploymentState());
            plans.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
            Plane = new(new DesiredStore(), new WriteLayer(Writes), catalog: catalog);
            var environment = new Environment(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services =>
                {
                    services.AddSingleton(plans);
                    services.AddSingleton(StartupCapabilitySet.Full);
                    services.AddSingleton<IControlPlane>(Plane);
                    services.AddSingleton<IAppSettingsStore>(provider => new JsonAppSettingsStore(
                        provider.GetRequiredService<StateAuthority>(), environment));
                    services.AddSingleton<BackendDecisionStateBindings>();
                    services.AddBusinessBlockWorker<ControlDesiredStateReassertion>(BlockUids.ControlDesiredStateReassertion,
                        provider => new(Plane, provider.GetRequiredService<IAppSettingsStore>(), plans, timeProvider: Clock), constructService: true);
                    NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
                }).Build();
        }

        internal async Task StartAsync()
        {
            await Settings.LoadAsync(default);
            await host.StartAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Empty(Writes);
            Assert.Equal(0, Clock.ActiveTimers);
        }
        internal async Task SetKeepAlive(bool enabled)
        {
            var current = (await Settings.LoadAsync(default)).Settings;
            await Settings.SaveAsync(current with { Control = new(120, enabled) }, default);
        }
        internal Task WaitForWrites(int count) => BusinessBlockTestSupport.Until(() => Writes.Count >= count);
        internal Task WaitForState(BusinessBlockState state) => BusinessBlockTestSupport.Until(() =>
            Scheduler.ReadState(BlockUids.ControlDesiredStateReassertion) == state);
        internal void PublishCatalog() => catalog.PublishCatalogProbe(new HardwareMetricSnapshot(DateTimeOffset.UtcNow,
            new CpuMetrics("CPU", 25, true, CpuMetricObservationStatus.Complete, 1, 1, 3000, 5000, 60, "fixture",
                new CpuSensorMetrics(new HardwareSensorProviderState("fixture", HardwareSensorProviderStates.Unavailable, null), null, null, null, null)),
            new MemoryMetrics(UsedBytes: 8, TotalBytes: 32, UsagePercent: 25, IsUsageAvailable: true),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
            [], null!, new Dictionary<string, MetricReadingValue>()) { CatalogGeneration = 1 });
        public async ValueTask DisposeAsync()
        {
            await host.StopAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
            if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync(); else host.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class DesiredStore : IControlDesiredStateStore
    {
        private ControlDesiredState value = new([new("device", [new("good", Number: 42), new("failed", Number: 42)])]);
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(value);
        public Task SaveAsync(ControlDesiredState next, CancellationToken token) { value = next; return Task.CompletedTask; }
    }
    private sealed class WriteLayer(ConcurrentQueue<string> writes) : IControlWriteLayer
    {
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var outcomes = desired.Objects.SelectMany(target => target.Settings.Select(setting =>
            {
                writes.Enqueue(setting.CapabilityId);
                return new ControlApplyOutcome(target.ObjectId, setting.CapabilityId,
                    setting.CapabilityId == "failed" ? ControlApplyStatuses.Failed : ControlApplyStatuses.Applied);
            })).ToArray();
            return Task.FromResult(new ControlApplyReport(outcomes, DateTimeOffset.UtcNow));
        }
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => throw new NotSupportedException();
    }
    private sealed class Environment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
