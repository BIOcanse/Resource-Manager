using System.Diagnostics;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class FanCurveReceiptTests
{
    [Fact]
    public async Task CompiledCoreRestorationExceptionKeepsRetryAndOtherFansRunning()
    {
        await using var helper = new CoreFixture(restoreFault: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = helper.Client;
        Assert.Equal(2, client.Describe().Count);
        var writer = new FanControlWriter(client);
        var capability = new ControlCapability("fan.curve", ControlValueKinds.Curve, true);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 40), new(95, 100)],
            CurveExecution: ControlFanCurveExecutions.Software);
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
            { ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, deadline.Token);
        var other = Target with { Id = "fan:oem1" };
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(Target, capability, setting, deadline.Token)).Status);
        Assert.Equal(ControlApplyStatuses.Applied, (await writer.WriteAsync(other, capability, setting, deadline.Token)).Status);
        var retry = Stopwatch.StartNew();
        while (true)
        {
            var read = (await client.SendAsync("read", null, deadline.Token))!.Value.GetProperty("fans");
            if (read[0].GetProperty("automaticControl").GetBoolean()
                && !read[1].GetProperty("automaticControl").GetBoolean()) break;
            Assert.True(retry.Elapsed < TimeSpan.FromSeconds(3), "The release exception stopped the loop instead of retrying and continuing the other fan.");
            await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
                { ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, deadline.Token);
            await Task.Delay(100, deadline.Token);
        }
        Assert.Null(await client.ReadCurveAsync(0, deadline.Token));
        Assert.Equal(setting.Curve, await client.ReadCurveAsync(1, deadline.Token));
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
            { ["cpu"] = new(100, DateTime.UtcNow.Ticks) }, deadline.Token);
        var nextWrite = Stopwatch.StartNew();
        while (true)
        {
            var read = (await client.SendAsync("read", null, deadline.Token))!.Value.GetProperty("fans");
            if (read[1].GetProperty("dutyPercent").GetDouble() == 100) break;
            Assert.True(nextWrite.Elapsed < TimeSpan.FromSeconds(2), "The other fan stopped receiving new temperature-driven writes.");
            await Task.Delay(100, deadline.Token);
        }
        Assert.True(await writer.RestoreAsync(other, capability, new HashSet<string>(), deadline.Token, setting));
    }

    [Fact]
    public Task CompiledCoreRestoreAllRetainsAFailedFanAndContinuesTheOtherFan()
        => RunRestorationProbeAsync("--restore-all-check");

    [Fact]
    public Task CompiledEngineStopAllRetainsAFailedFanAndContinuesTheOtherFan()
        => RunRestorationProbeAsync("--engine-stop-all-check");

    private static Task RunRestorationProbeAsync(string mode)
    {
        var backend = new ProbeFanBackend(twoFans: true) { RestoreFailures = 1 };
        using var core = new FanControlCore.Discovery.FanCore(backend);
        core.Open();
        Assert.True(core.TrySetDuty(0, 40, out _));
        Assert.True(core.TrySetDuty(1, 50, out _));
        if (mode == "--engine-stop-all-check")
        {
            core.Temperatures.Push(new Dictionary<string, double> { ["cpu"] = 60 });
            Assert.True(core.TryWriteCurve(0, [new(60, 40)], "software", out _));
            Assert.True(core.TryWriteCurve(1, [new(60, 50)], "software", out _));
        }
        core.RestoreAll();
        Assert.True(backend.Read()[1].AutomaticControl);
        core.RestoreAll();
        Assert.All(backend.Read(), fan => Assert.True(fan.AutomaticControl));
        return Task.CompletedTask;
    }

    [Fact]
    public Task CompiledCoreRedetectionRetriesFailedDiscoveryWithoutRestartingTheHelper()
        => RunRedetectionAsync(1);

    [Fact]
    public Task CompiledCoreRepeatedFailedRedetectionKeepsMachineReasonAndUpdatesDiagnosticsBeforeRecovery()
        => RunRedetectionAsync(2);

    private static async Task RunRedetectionAsync(int deferredOpens)
    {
        var attempts = 0;
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new LibraryFanSession(new FanControlCore.Discovery.FanCore(new ProbeFanBackend(++attempts <= deferredOpens ? int.MaxValue : 0))));
        for (var attempt = 1; attempt <= deferredOpens; attempt++)
        {
            Assert.Empty(await client.DiscoverAsync(default));
            Assert.Empty(client.Describe());
            Assert.Equal("fan-channel-missing", client.UnavailableReason);
            Assert.True(client.IsAvailable); // An untyped backend failure is Other.
            Assert.False(client.NeedsTemperatures());
            Assert.Equal(attempt, attempts);
            client.ResetDetection();
        }
        Assert.True(Assert.Single(await client.DiscoverAsync(default)).SupportsSoftwareCurve);
        Assert.Null(client.UnavailableReason);
        Assert.False(client.NeedsTemperatures());
    }
    [Fact]
    public Task CompiledCoreReceiptsAndManualRestorationUseTheProductionLibraryBoundary()
        => RunCoreConsumerAsync("manual");

    [Fact]
    public Task CompiledCoreLosesTemperatureAndStopsTheSoftwarePlanWithoutReturningACachedCurve()
        => RunCoreConsumerAsync("temperature");

    [Fact]
    public Task CompiledCoreRestoresAnActiveSoftwareOverrideWhenTheHostDisposesSession()
        => RunCoreConsumerAsync("eof");

    private static async Task RunCoreConsumerAsync(string finish)
    {
        await using var helper = new CoreFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = helper.Client;
        var fan = Assert.Single(client.Describe());
        Assert.True(fan.SupportsSoftwareCurve);
        Assert.Null(fan.FirmwareCurve);
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
        {
            ["cpu"] = new(60, DateTime.UtcNow.Ticks)
        }, deadline.Token);
        var setting = new ControlSetting("fan.curve", Curve: [new(60, 35), new(120, 100)],
            CurveExecution: ControlFanCurveExecutions.Software);
        var capability = new ControlCapability("fan.curve", ControlValueKinds.Curve, true);
        var writer = new FanControlWriter(client);
        var outcome = await writer.WriteAsync(Target, capability, setting, deadline.Token);
        Assert.Equal(ControlApplyStatuses.Applied, outcome.Status);
        Assert.Equal(setting.Curve, await client.ReadCurveAsync(0, deadline.Token));
        // Accepting the plan precedes the next actual engine tick. Keep the normal
        // host feed active until the injected backend confirms its first drive.
        var firstDrive = Stopwatch.StartNew();
        while (true)
        {
            var manual = await client.SendAsync("read", null, deadline.Token);
            if (!manual!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean()) break;
            Assert.True(firstDrive.Elapsed < TimeSpan.FromSeconds(5), "The actual engine never drove the injected backend.");
            await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>
            {
                ["cpu"] = new(60, DateTime.UtcNow.Ticks)
            }, deadline.Token);
            await Task.Delay(100, deadline.Token);
        }
        var sameOwner = helper.SessionId;
        client.ResetDetection();
        await client.DiscoverAsync(deadline.Token);
        Assert.True(Assert.Single(client.Describe()).SupportsSoftwareCurve);
        Assert.Equal(sameOwner, helper.SessionId);
        Assert.Equal(setting.Curve, await client.ReadCurveAsync(0, deadline.Token));
        var stillActive = await client.SendAsync("read", null, deadline.Token);
        Assert.False(stillActive!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean());
        if (finish == "eof")
        {
            Assert.False(helper.IsDisposed);
            return; // CoreFixture disposes the session; cleanup must to restore this active override.
        }

        if (finish == "temperature")
        {
            // Keep the actual default freshness and blind timeout. No new samples.
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var reading = await client.SendAsync("read", null, deadline.Token);
                if (reading!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean()) break;
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(14), "The core did not hand back after losing temperature.");
                await Task.Delay(100, deadline.Token);
            }
        }
        else
        {
            Assert.True(await writer.RestoreAsync(Target, capability, new HashSet<string>(), deadline.Token, setting));
        }
        Assert.Null(await client.ReadCurveAsync(0, deadline.Token));
        var after = await client.SendAsync("read", null, deadline.Token);
        Assert.True(after!.Value.GetProperty("fans")[0].GetProperty("automaticControl").GetBoolean());
        Assert.False(helper.IsDisposed);
    }

    private sealed class CoreFixture : IAsyncDisposable
    {
        private readonly ProbeFanBackend backend;
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;
        internal FanControlCoreClient Client { get; }
        internal FanBoundaryLogger<FanControlCoreClient> Log { get; } = new();
        internal bool IsDisposed => backend.Disposed;
        internal int SessionId { get; } = Random.Shared.Next();
        internal CoreFixture(int deferredOpens = 0, bool restoreFault = false)
        {
            backend = new ProbeFanBackend(deferredOpens, restoreFault);
            Client = new FanControlCoreClient(new EnvironmentInfo(@"C:\FanCoreConsumerFixture"),
                (_, _) => new LibraryFanSession(new FanControlCore.Discovery.FanCore(backend)), Log);
            Client.StartSession().GetAwaiter().GetResult();
            run = Client.RunSoftwareCurveAsync(stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            stop.Dispose();
            await Client.DisposeAsync();
            Assert.True(backend.Disposed);
            Assert.All(backend.Read(), fan => Assert.True(fan.AutomaticControl));
        }
    }
}
