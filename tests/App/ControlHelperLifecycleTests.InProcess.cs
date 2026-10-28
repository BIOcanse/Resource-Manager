using ResourceManager.App.Infrastructure.Control;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperLifecycleTests
{
    private static JsonElement FanOk => JsonSerializer.SerializeToElement(new { ok = true });

    [Fact]
    public async Task FanLibraryExceptionBecomesFailedRequestAndKeepsOneSession()
    {
        var creates = 0;
        var closes = 0;
        var log = new FanBoundaryLogger<FanControlCoreClient>();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            creates++;
            return new DelegateFanSession(request => request.GetProperty("op").GetString() == "describe"
                ? FanOk : throw new ApplicationException("injected fan failure"), () => closes++);
        }, log);
        await client.StartSession();
        Assert.Null(await client.SendAsync("read", null, default));
        Assert.Equal("fan-core-failed", client.UnavailableReason);
        Assert.Contains(log.Messages, message => message.Contains("request failed (read)", StringComparison.Ordinal));
        Assert.Null(await client.SendAsync("read", null, default));
        Assert.Equal(1, creates);
        await client.DisposeAsync();
        Assert.Equal(1, closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanAcceptedCommandFinishesBeforeDisposalOrCancellationReleasesOwnership(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var closes = 0;
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "describe") return FanOk;
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal(0, closes);
                return FanOk;
            }, () => Interlocked.Increment(ref closes)));
        await client.StartSession();
        var pending = client.SendAsync("set", null, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) client.Dispose(); else cancellation.Cancel();
            if (dispose) Assert.False(pending.IsCompleted);
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.True(client.PendingCommands);
            }
            Assert.Equal(0, closes);
        }
        finally { release.Set(); }
        if (dispose) Assert.True((await pending)!.Value.GetProperty("ok").GetBoolean());
        await client.DisposeAsync();
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task FanNormalAsyncShutdownWaitsForAcceptedOperationAndRestore()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var restored = false;
        var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "describe") return FanOk;
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return FanOk;
            }, () => restored = true));
        await client.StartSession();
        var pending = client.SendAsync("set", null, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = client.DisposeAsync();
        Assert.False(stopping.IsCompleted);
        Assert.False(restored);
        release.Set();
        await pending;
        await stopping;
        Assert.True(restored);
    }

    [Fact]
    public async Task FanDisposalDuringStartupDisposesLateSessionWithoutCommand()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var closes = 0;
        var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return new DelegateFanSession(_ => { calls++; return FanOk; }, () => closes++);
        });
        var startup = client.StartSession();
        var pending = client.SendAsync("set", null, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose();
        release.Set();
        await startup;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await client.DisposeAsync();
        Assert.Equal(0, calls);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task FanTemperaturePublicationRunsWhileAHardwareCommandIsBusyAndNeverQueues()
    {
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tempEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRead = new ManualResetEventSlim();
        using var releaseTemp = new ManualResetEventSlim();
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
            new DelegateFanSession(request =>
            {
                if (request.GetProperty("op").GetString() == "read")
                {
                    readEntered.SetResult(); Assert.True(releaseRead.Wait(TimeSpan.FromSeconds(10)));
                }
                if (request.GetProperty("op").GetString() == "temp")
                {
                    tempEntered.SetResult(); Assert.True(releaseTemp.Wait(TimeSpan.FromSeconds(10)));
                }
                return FanOk;
            }));
        await client.StartSession();
        var read = client.SendAsync("read", null, default);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var samples = new Dictionary<string, FanTemperatureSample> { ["cpu"] = new(60, DateTime.UtcNow.Ticks) };
        var temperature = client.PushTemperaturesAsync(samples, default);
        try
        {
            await tempEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await client.SendAsync("read", null, default, skipIfBusy: true));
            Assert.False(await client.PushTemperaturesAsync(samples, default));
        }
        finally { releaseTemp.Set(); releaseRead.Set(); }
        Assert.True(await temperature);
        Assert.NotNull(await read);
    }
}
