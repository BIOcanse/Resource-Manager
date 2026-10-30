using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class FanActualUseAuditTests
{
    [Fact]
    public async Task ASeparateGpuFanCommandDoesNotHoldFanCore()
    {
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => throw new InvalidOperationException("Unexpected Open"));
        var pending = false;
        core.PendingCommandsChanged += () => pending |= core.PendingCommands;
        var plane = new ControlPlane(new Store(), new AppliedLayer(), fan: core);
        await plane.SetObjectSettingsAsync("fan:gpu0", [new("fan.duty", Number: 40)], default);
        await plane.ReassertAsync(default);
        Assert.False(pending);
        Assert.False(plane.AppliedFanControlExists);
        Assert.True(plane.AppliedControlIntentsExist);
    }

    [Theory]
    [InlineData("fan.duty", null, true)]
    [InlineData("fan.lock-maximum", true, true)]
    [InlineData("fan.lock-maximum", false, false)]
    [InlineData("software", null, false)]
    [InlineData("firmware", null, false)]
    public async Task OnlyTemporaryManualOverridesPublishAppliedFanHold(string kind, bool? toggle, bool expected)
    {
        var setting = kind is "software" or "firmware"
            ? new ControlSetting("fan.curve", Curve: [new(60, 40)], CurveExecution: kind)
            : new ControlSetting(kind, Number: kind == "fan.duty" ? 40 : null, Toggle: toggle);
        var plane = new ControlPlane(new Store(), new AppliedLayer());
        await plane.SetObjectSettingsAsync("fan:oem0", [setting], default);
        Assert.True(plane.AppliedControlIntentsExist); // Reassertion retains every applied intent.
        Assert.Equal(expected, plane.AppliedFanControlExists);
    }

    [Fact]
    public async Task EmptyInitializationAndReassertionDoNotDiscover()
    {
        var catalog = new Catalog();
        var plane = new ControlPlane(new Store(), new ControlWriteLayer(catalog, [], new Access()));
        await plane.InitializeAsync(default);
        await plane.ReassertAsync(default);
        Assert.Equal(0, catalog.Discoveries);
    }

    [Theory]
    [InlineData(BackendItems.AppliedFanControlExists)]
    [InlineData(BackendItems.FanCommandPending)]
    [InlineData(BackendItems.FanReadingsInUse)]
    [InlineData(BackendItems.SoftwareCurveRunNeeded)]
    public async Task SessionTimelineFollowsEachRemainingFactAndDoesNotDiscoverWhileIdle(int fact)
    {
        var history = new ConcurrentQueue<(long Time, string Event)>();
        var active = 0;
        var discoveries = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            history.Enqueue((Stopwatch.GetTimestamp(), "open"));
            Interlocked.Increment(ref active);
            return new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "describe") Interlocked.Increment(ref discoveries);
                return JsonSerializer.SerializeToElement(new { ok = true, available = true, fans = Array.Empty<object>() });
            }, () => { history.Enqueue((Stopwatch.GetTimestamp(), "close")); Interlocked.Decrement(ref active); });
        });
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockLifetime<FanControlCoreLifetimeService>(BackendUids.FanControlCoreLifetimeService,
                    _ => new FanControlCoreLifetimeService(core), constructService: true);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var facts = new BackendPreciseRuleTestFacts(scheduler.Authority, host.Services.GetRequiredService<NativeControl>(), hostPermits: false);
        await facts.InitializeAsync();
        await host.StartAsync();
        try
        {
            await Task.Delay(100);
            Assert.Equal(0, active);
            Assert.Equal(0, discoveries);
            Assert.Empty(history);
            if (fact == BackendItems.SoftwareCurveRunNeeded)
            {
                Assert.False(core.SoftwareCurveRunNeeded); // No session can own a plan yet.
                await facts.PublishAsync(BackendItems.FanCommandPending, true);
            }
            else await facts.PublishAsync(fact, true);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Running);
            if (fact == BackendItems.SoftwareCurveRunNeeded)
            {
                await facts.PublishAsync(fact, true); // Work can be recorded only after Open.
                await facts.PublishAsync(BackendItems.FanCommandPending, false);
            }
            var held = Stopwatch.GetTimestamp();
            await Task.Delay(100);
            Assert.Equal(1, active);
            Assert.Single(history);
            await facts.PublishAsync(fact, false);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Stopped);
            Assert.Equal(0, active);
            var rows = history.ToArray();
            Assert.Equal(new[] { "open", "close" }, rows.Select(row => row.Event));
            Assert.True(rows[0].Time <= held && held < rows[1].Time);
            var after = discoveries;
            await Task.Delay(100);
            Assert.Equal(after, discoveries);
            Assert.Equal(2, history.Count);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingSdkSessionRestoresTemporaryManualOverride(bool boost)
    {
        var backend = new ProbeFanBackend();
        var session = new LibraryFanSession(new FanControlCore.Discovery.FanCore(backend));
        session.Handle(JsonSerializer.SerializeToElement(new { op = boost ? "boost" : "set", fan = 0, percent = 40 }));
        Assert.False(backend.Read()[0].AutomaticControl);
        session.Dispose();
        Assert.True(backend.Read()[0].AutomaticControl);
        Assert.True(backend.Disposed);
    }

    private sealed class Store : IControlDesiredStateStore
    {
        private ControlDesiredState current = ControlDesiredState.Empty;
        public Task<ControlDesiredState> LoadAsync(CancellationToken token) => Task.FromResult(current);
        public Task SaveAsync(ControlDesiredState value, CancellationToken token) { current = value; return Task.CompletedTask; }
    }
    private sealed class AppliedLayer : IControlWriteLayer
    {
        public Task<ControlApplyReport> ReleaseAsync(ControlDesiredState removed, ControlDesiredState remaining, CancellationToken token)
            => Task.FromResult(ControlApplyReport.Empty);
        public Task<ControlApplyReport> WriteAsync(ControlDesiredState desired, CancellationToken token)
            => Task.FromResult(new ControlApplyReport(desired.Objects.SelectMany(target => target.Settings.Select(setting =>
                new ControlApplyOutcome(target.ObjectId, setting.CapabilityId, ControlApplyStatuses.Applied))).ToArray(), DateTimeOffset.UtcNow));
    }
    private sealed class Catalog : IControlObjectCatalog
    {
        internal int Discoveries;
        public ControlObjectCatalog ReadObjects() { Discoveries++; return new([], DateTimeOffset.UtcNow); }
    }
    private sealed class Access : IControlAccessLevel
    {
        public string Current => ControlAccessLevels.Normal;
        public Task SetAsync(string level, CancellationToken token) => Task.CompletedTask;
    }
}
