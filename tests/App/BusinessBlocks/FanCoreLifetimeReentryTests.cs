using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class FanCoreLifetimeReentryTests
{
    [Fact]
    public async Task FirstManualCommandStartsNativeFanBlockWithEveryOtherFanFactFalseAndStopsAfterCompletion()
    {
        var entered = BusinessBlockTestSupport.Signal();
        using var release = new ManualResetEventSlim();
        var opened = 0;
        var closed = 0;
        await using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "set")
                {
                    entered.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                return JsonSerializer.SerializeToElement(new { ok = true, available = true, fans = Array.Empty<object>() });
            }, () => Interlocked.Increment(ref closed));
        });
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(core);
                services.AddSingleton(new RuntimePlanProvider(new HostManagerDeploymentState()));
                services.AddSingleton<BackendDecisionStateBindings>();
                services.AddBusinessBlockHostedService<FanControlCoreLifetimeService>(BackendUids.FanControlCoreLifetimeService,
                    _ => new FanControlCoreLifetimeService(core));
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        await scheduler.Authority.ClaimCell<bool>(BackendItems.SoftwareFanCurveEnabled).PublishAsync(false, BusinessBlockTestSupport.Reason());
        await scheduler.Authority.ClaimCell<bool>(BackendItems.AppliedFanControlExists).PublishAsync(false, BusinessBlockTestSupport.Reason());
        await host.StartAsync();
        Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(BackendUids.FanControlCoreLifetimeService));
        Assert.False(core.PendingCommands);
        Assert.False(core.NeedsTemperatures());
        Assert.Equal(0, opened);
        var command = core.SendAsync("set", new Dictionary<string, object?> { ["fan"] = 0, ["percent"] = 60 }, default);
        try
        {
            await entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Running);
            Assert.True(core.PendingCommands);
            Assert.True(scheduler.Authority.Read<bool>(BackendItems.FanCommandPending).Value);
            Assert.False(scheduler.Authority.Read<bool>(BackendItems.SoftwareFanCurveEnabled).Value);
            Assert.False(scheduler.Authority.Read<bool>(BackendItems.AppliedFanControlExists).Value);
            Assert.Equal(1, opened);
            Assert.Equal(0, closed);
        }
        finally { release.Set(); }
        Assert.True((await command)!.Value.GetProperty("ok").GetBoolean());
        await BusinessBlockTestSupport.State(scheduler, BackendUids.FanControlCoreLifetimeService, BusinessBlockState.Stopped);
        Assert.False(core.PendingCommands);
        Assert.False(scheduler.Authority.Read<bool>(BackendItems.FanCommandPending).Value);
        Assert.Equal(1, closed);
        await host.StopAsync();
    }

    [Fact]
    public async Task SameLifetimeRestartsWithFreshCoreSessionAndAcceptsCommands()
    {
        var opened = 0;
        var closed = 0;
        using var core = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref opened);
            return new DelegateFanSession(_ => JsonSerializer.SerializeToElement(new { ok = true, fans = Array.Empty<object>() }),
                () => Interlocked.Increment(ref closed));
        });
        var lifetime = new FanControlCoreLifetimeService(core);
        for (var run = 1; run <= 2; run++)
        {
            await lifetime.StartAsync(default);
            await lifetime.StartupTask.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var response = await core.SendAsync("read", null, default);
                Assert.True(response!.Value.GetProperty("ok").GetBoolean());
                Assert.Equal(run, opened);
            }
            finally { await lifetime.StopAsync(default); }
            Assert.Equal(run, closed);
        }
    }
}
