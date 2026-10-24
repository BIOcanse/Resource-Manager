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
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            creates++;
            return new DelegateFanSession(_ => throw new ApplicationException("injected fan failure"), () => closes++);
        });
        Assert.Null(await client.SendAsync("read", null, default));
        Assert.Equal("injected fan failure", client.UnavailableReason);
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
            new DelegateFanSession(_ =>
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal(0, closes);
                return FanOk;
            }, () => Interlocked.Increment(ref closes)));
        var pending = client.SendAsync("set", null, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) client.Dispose(); else cancellation.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, closes);
        }
        finally { release.Set(); }
        Assert.True((await pending)!.Value.GetProperty("ok").GetBoolean());
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
            new DelegateFanSession(_ =>
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return FanOk;
            }, () => restored = true));
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
        var pending = client.SendAsync("set", null, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose();
        release.Set();
        Assert.Null(await pending);
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
        await client.SendAsync("describe", null, default);
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
