using System.Diagnostics;
using ResourceManager.ReadWrite.Helpers;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeSessionTests
{
    internal static string Fixture => Path.Combine(AppContext.BaseDirectory, "HardwareBridgeProcessFixture", "HardwareBridgeProcessFixture.exe");
    internal static HardwareBridgeProcessOptions Options => new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1),
        new(128, 1024, 1024, ".NET", "10", "missing", "failed"))
        {
            StopTimeout = TimeSpan.FromMilliseconds(500),
            ReadingInterval = TimeSpan.FromSeconds(1)
        };

    public static IEnumerable<object[]> InvalidOptions()
    {
        yield return [Options with { ErrorOutput = null! }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { ReadBufferSize = 0 } }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { FirstCharacterLimit = -1 } }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { TailCharacterLimit = -1 } }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { RuntimeName = null! } }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { RuntimeVersionMarker = null! } }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { MissingRuntimeReason = null! } }];
        yield return [Options with { ErrorOutput = Options.ErrorOutput with { StartupFailedReason = null! } }];
        yield return [Options with { RequestTimeout = TimeSpan.MaxValue }];
        yield return [Options with { StopTimeout = TimeSpan.MaxValue }];
        yield return [Options with { DiagnosticDrainTimeout = TimeSpan.MaxValue }];
        yield return [Options with { AvailabilityCheckInterval = TimeSpan.FromMilliseconds(-1) }];
        yield return [Options with { AvailabilityCheckInterval = TimeSpan.MaxValue }];
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptionsAreRejectedBeforeAnyChildCanStart(HardwareBridgeProcessOptions options)
    {
        var launches = 0;
        Assert.ThrowsAny<ArgumentException>(() => new HardwareBridgeProcessOwner(() => Fixture, options,
            startFactory: _ => { launches++; throw new Exception("Invalid options started a child"); }));
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task StoppedFacadeNeverStartsForDescriptionsReadsOrCommands()
    {
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() => Fixture, Options,
            startFactory: _ => { launches++; throw new Exception("Unexpected process start"); });
        Assert.Null(await client.SendAsync("describe", null, default));
        Assert.Null(await client.SendAsync("set-test", null, default));
        Assert.Null(client.ReadCurrentReadings());
        using (client.SubscribeReadings()) Assert.True(client.Owner.ReadingsInUse);
        Assert.False(client.Owner.ReadingsInUse);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopJoinsRealJobBoundChildClosesSectionAndWithdrawsValues(bool ignoresEof)
    {
        using var client = new HardwareBridgeProcessClient(() => Fixture, Options);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.Owner.RunAsync(stop.Token, () => ready.SetResult());
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(45, client.ReadCurrentReadings()!.Value.StapmLimitWatts);
            var status = (await client.SendAsync(ignoresEof ? "ignore-eof" : "status", null, default))!.Value;
            Assert.True(status.GetProperty("inJob").GetBoolean());
            var name = status.GetProperty("sectionName").GetString()!;
            using var observer = Process.GetProcessById(status.GetProperty("pid").GetInt32());
            _ = observer.SafeHandle;
            var timer = Stopwatch.StartNew();
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observer.HasExited);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4));
            Assert.Null(client.ReadCurrentReadings());
            Assert.Null(await client.SendAsync("describe", null, default));
            Assert.Throws<FileNotFoundException>(() => HardwareBridgeReadingsSection.OpenReader(name));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task StopCancelsInFlightCommandAndTerminatesHungChild()
    {
        using var client = new HardwareBridgeProcessClient(() => Fixture, Options);
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.Owner.RunAsync(stop.Token, () => ready.SetResult());
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pending = client.SendAsync("hang", null, default);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            try { Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2))); }
            catch (OperationCanceledException) { }
            Assert.Null(client.ReadCurrentReadings());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CrashFailsEntryAndRequestsNeverRestartItNextEntryIsFresh()
    {
        using var client = new HardwareBridgeProcessClient(() => Fixture, Options);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.Owner.RunAsync(default, () => ready.SetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var status = (await client.SendAsync("status", null, default))!.Value;
        var first = status.GetProperty("sectionName").GetString();
        Assert.Null(await client.SendAsync("crash", null, default));
        await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        for (var i=0;i<3;i++) Assert.Null(await client.SendAsync("describe", null, default));
        Assert.Null(client.ReadCurrentReadings());
        Assert.True(client.Owner.IsAvailable);
        using var stop = new CancellationTokenSource();
        ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run = client.Owner.RunAsync(stop.Token, () => ready.SetResult());
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            status = (await client.SendAsync("status", null, default))!.Value;
            Assert.NotEqual(first, status.GetProperty("sectionName").GetString());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
