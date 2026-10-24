using System.Collections.Concurrent;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanControlIsolationBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReturnsWhileDescribeIsBlockedAndStopDrainsTheSameOperation(bool blockDuringRecovery)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var creates = 0;
        var restored = false;
        var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            Interlocked.Increment(ref creates);
            if (blockDuringRecovery) { entered.TrySetResult(); release.Wait(); }
            return new DelegateFanSession(request =>
            {
                Assert.Equal("describe", request.GetProperty("op").GetString());
                entered.TrySetResult();
                release.Wait();
                return JsonSerializer.SerializeToElement(new { ok = true, available = true, fans = Array.Empty<object>() });
            }, () => restored = true);
        });
        var log = new FanBoundaryLogger<FanControlCoreLifetimeService>();
        var lifetime = new FanControlCoreLifetimeService(client, log);
        try
        {
            var starting = lifetime.StartAsync(default);
            Assert.True(starting.IsCompletedSuccessfully);
            var startup = lifetime.StartupTask;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, creates);
            using var cancellation = new CancellationTokenSource();
            var stopping = lifetime.StopAsync(cancellation.Token);
            Assert.False(stopping.IsCompleted);
            cancellation.Cancel();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(startup.IsCompleted);
            Assert.False(restored);
            Assert.Single(log.Messages, message => message.Contains(blockDuringRecovery ? "start/recovery for describe" : "describe"));
            release.Set();
            await startup;
            await client.DisposeAsync();
            Assert.Same(startup, lifetime.StartupTask);
            Assert.True(restored);
        }
        finally
        {
            release.Set();
            await lifetime.StartupTask;
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartupFailureIsLoggedOnceByTheExistingRequestBoundary()
    {
        var clientLog = new FanBoundaryLogger<FanControlCoreClient>();
        var lifetimeLog = new FanBoundaryLogger<FanControlCoreLifetimeService>();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(),
            (_, _) => throw new ApplicationException("injected startup discovery failure"), clientLog);
        var lifetime = new FanControlCoreLifetimeService(client, lifetimeLog);
        await lifetime.StartAsync(default);
        await lifetime.StartupTask;
        Assert.Single(clientLog.Messages, message => message.Contains("request failed (describe)"));
        Assert.Empty(lifetimeLog.Messages);
    }

    [Theory]
    [InlineData("tick")]
    [InlineData("release")]
    [InlineData("loop")]
    public async Task BackgroundCurveFaultIsVisibleInDescribeAndLoggedOnlyOnce(string fault)
    {
        var backend = new ProbeFanBackend(twoFans: true) { ThrowDrive = fault != "loop", ThrowFirstRelease = fault == "release" };
        var libraryCore = new FanCore(backend);
        var log = new FanBoundaryLogger<FanControlCoreClient>();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(),
            (_, _) => new LibraryFanSession(libraryCore), log);
        await client.SendAsync("describe", null, default);
        await client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample> {
            ["cpu"] = new(60, DateTime.UtcNow.Ticks) }, default, skipIfBusy: false);
        var arguments = new Dictionary<string, object?> {
            ["fan"] = 0, ["execution"] = "software",
            ["curve"] = new[] { new { temperatureCelsius = 30, percent = 40 }, new { temperatureCelsius = 90, percent = 90 } }
        };
        Assert.True((await client.SendAsync("setcurve", arguments, default))!.Value.GetProperty("ok").GetBoolean());
        if (fault == "loop")
        {
            var second = new Dictionary<string, object?>(arguments) { ["fan"] = 1 };
            Assert.True((await client.SendAsync("setcurve", second, default))!.Value.GetProperty("ok").GetBoolean());
            Assert.True(SpinWait.SpinUntil(() => backend.Read().All(fan => !fan.AutomaticControl), 3000));
            // 用库的回归注入点在 Tick 外抛错，证明整个循环的边界也会交还风扇。
            var engine = typeof(FanCore).GetField("engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(libraryCore)!;
            var hook = engine.GetType().GetProperty("BeforeIteration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            hook.SetValue(engine, (Action)(() =>
            {
                hook.SetValue(engine, null);
                throw new ApplicationException("injected background loop body");
            }));
        }
        Assert.True(SpinWait.SpinUntil(() => log.Messages.Any(message => message.Contains("injected background"))
            && libraryCore.ExecutionOf(0) is null && backend.Read().All(fan => fan.AutomaticControl), 3000));
        var describe = (await client.SendAsync("describe", null, default))!.Value;
        Assert.Contains(describe.GetProperty("diagnostics").EnumerateArray(), entry => entry.GetString()!.Contains("injected background"));
        Assert.All(backend.Read(), fan => Assert.True(fan.AutomaticControl));
        client.ResetDetection();
        client.Describe();
        Assert.Single(log.Messages, message => message.Contains("injected background"));
        if (fault == "release")
        {
            Assert.Contains(describe.GetProperty("diagnostics").EnumerateArray(), entry => entry.GetString()!.Contains("Injected restoration exception"));
            Assert.Single(log.Messages, message => message.Contains("Injected restoration exception"));
        }
        backend.ThrowDrive = false;
        var next = (await client.SendAsync("setcurve", arguments, default))!.Value;
        if (!next.GetProperty("ok").GetBoolean())
        {
            Assert.Contains("交还", next.GetProperty("error").GetString());
            Assert.True((await client.SendAsync("auto", new Dictionary<string, object?> { ["fan"] = 0 }, default))!.Value.GetProperty("ok").GetBoolean());
            Assert.True((await client.SendAsync("setcurve", arguments, default))!.Value.GetProperty("ok").GetBoolean());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStopDeadlineReturnsForBlockedRequestOrDisposal(bool blockDisposal)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(
            _ => { if (!blockDisposal) { entered.TrySetResult(); release.Wait(); } return JsonSerializer.SerializeToElement(new { ok = true }); },
            () => { if (blockDisposal) { entered.TrySetResult(); release.Wait(); } }));
        var pending = client.SendAsync("set", null, default);
        if (blockDisposal) await pending;
        else await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var log = new FanBoundaryLogger<FanControlCoreLifetimeService>();
        using var cancellation = new CancellationTokenSource();
        var stopping = new FanControlCoreLifetimeService(client, log).StopAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            cancellation.Cancel();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(log.Messages, message => message.Contains(blockDisposal ? "shutdown restoration/disposal" : "set"));
            Assert.False(client.DisposeAsync().IsCompleted);
        }
        finally { release.Set(); }
        await pending;
        await client.DisposeAsync();
    }

    [Fact]
    public async Task TimedOutStopKeepsJournalAndNextOwnerRestoresBeforeWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "fan-stop-recovery-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(root, "UserData", "Control", "FanControlCore");
        using var release = new ManualResetEventSlim();
        var backend = new BlockingDurableBackend(release);
        var environment = new FanTestEnvironment(Path.Combine(root, "ResourceManager-APP"));
        var client = new FanControlCoreClient(environment, (path, _) => new LibraryFanSession(
            new FanCore(backend, new FanCoreOptions(path, MachineIdentity: () => "stop-fixture"))));
        try
        {
            var pending = client.SendAsync("set", new Dictionary<string, object?> { ["fan"] = 0, ["percent"] = 60 }, default);
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var log = new FanBoundaryLogger<FanControlCoreLifetimeService>();
            using var cancellation = new CancellationTokenSource();
            var stopping = new FanControlCoreLifetimeService(client, log).StopAsync(cancellation.Token);
            cancellation.Cancel();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(log.Messages, message => message.Contains("set"));
            AssertJournal(state, 1);
            // 放行卡住的调用，但让旧所有者恢复失败，证明下个所有者仍须先交还再写入。
            backend.FailRelease = true;
            release.Set();
            await pending;
            await client.DisposeAsync();
            AssertJournal(state, 1);
            var recovered = new BlockingDurableBackend(release);
            await using (var next = new FanControlCoreClient(environment, (path, _) => new LibraryFanSession(
                new FanCore(recovered, new FanCoreOptions(path, MachineIdentity: () => "stop-fixture")))))
            {
                var lifetime = new FanControlCoreLifetimeService(next);
                await lifetime.StartAsync(default);
                await lifetime.StartupTask;
                Assert.Equal(["auto"], recovered.Operations);
                await next.SendAsync("set", new Dictionary<string, object?> { ["fan"] = 0, ["percent"] = 70 }, default);
                Assert.Equal(["auto", "set"], recovered.Operations);
            }
            AssertJournal(state, 0);
        }
        finally
        {
            release.Set();
            await client.DisposeAsync();
            if (Directory.Exists(state))
            {
                foreach (var file in Directory.EnumerateFiles(state)) File.Delete(file);
                Directory.Delete(state);
                Directory.Delete(Path.Combine(root, "UserData", "Control"));
                Directory.Delete(Path.Combine(root, "UserData"));
                Directory.Delete(root);
            }
        }
    }

    [Fact]
    public async Task BrokenDemandObserverDoesNotPropagateAndOtherObserversRun()
    {
        var log = new FanBoundaryLogger<FanControlCoreClient>();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new DelegateFanSession(_ => JsonSerializer.SerializeToElement(new {
                ok = true, execution = "software", curve = new[] { new { temperatureCelsius = 50, percent = 40 } }
            })), log);
        var observed = false;
        client.TemperatureDemandChanged += () => throw new ApplicationException("injected callback");
        client.TemperatureDemandChanged += () => observed = true;
        Assert.NotNull(await client.SendAsync("setcurve", new Dictionary<string, object?> { ["fan"] = 0 }, default));
        Assert.True(observed);
        Assert.Contains(log.Messages, message => message.Contains("callback failed"));
    }

    [Fact]
    public async Task TemperatureCancellationReturnsButRetainsTheAcceptedOwnerUntilCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var closed = false;
        var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new DelegateFanSession(
            request =>
            {
                if (request.GetProperty("op").GetString() == "temp") { entered.TrySetResult(); release.Wait(); }
                return JsonSerializer.SerializeToElement(new { ok = true });
            }, () => closed = true));
        await client.SendAsync("describe", null, default);
        using var cancellation = new CancellationTokenSource();
        var push = client.PushTemperaturesAsync(new Dictionary<string, FanTemperatureSample>(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            cancellation.Cancel();
            Assert.False(await push.WaitAsync(TimeSpan.FromSeconds(2)));
            var disposal = client.DisposeAsync();
            Assert.False(disposal.IsCompleted);
            Assert.False(closed);
        }
        finally { release.Set(); }
        await client.DisposeAsync();
        Assert.True(closed);
    }

    private static void AssertJournal(string path, int count)
    {
        using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "temporary-takeovers.json")));
        Assert.Equal(count, journal.RootElement.GetProperty("Fans").GetArrayLength());
    }

    private sealed class BlockingDurableBackend(ManualResetEventSlim release) : IFanBackend
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Operations = [];
        public bool FailRelease;
        public string Name => "deadline-fixture";
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() => [new(0, "CPU", true, true)];
        public IReadOnlyList<FanState> Read() => [];
        public bool TrySetDuty(int fan, double percent, out string? reason)
        { Operations.Add("set"); Entered.TrySetResult(); release.Wait(); reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
        public bool TrySetAutomatic(int fan, out string? reason)
        { reason = FailRelease ? "fixture restore unavailable" : null; if (FailRelease) return false; Operations.Add("auto"); return true; }
        public void Dispose() { }
    }
}

internal sealed class FanBoundaryLogger<T> : ILogger<T>
{
    internal readonly ConcurrentQueue<string> Messages = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        => Messages.Enqueue(formatter(state, error));
}
