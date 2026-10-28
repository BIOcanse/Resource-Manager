using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ResourceManager.ReadWrite.Helpers;

namespace Resource_Manager_APP.Tests;

public sealed partial class ControlHelperLifecycleTests
{
    [Fact]
    public async Task UnconfirmedExitFaultsTheRunAndKeepsInstallationAndNewEntriesExcluded()
    {
        using var client = CreateFixtureClient(HardwareBridgeSessionTests.Options with { AvailabilityCheckInterval = TimeSpan.Zero });
        using var stop = new CancellationTokenSource();
        var waits = 0;
        client.Owner.WaitForChildExitAsync = (_, _) =>
        {
            Interlocked.Increment(ref waits);
            return Task.FromException(new TimeoutException("Injected exit observation timeout"));
        };
        var (run, ready) = StartRun(client, stop.Token);
        await ready.WaitAsync(TimeSpan.FromSeconds(10));
        using var observer = Observe((await client.SendAsync("ignore-eof", null, default))!.Value);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, waits);
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.ExitNotConfirmed, client.Owner.UnavailableKind);
        // The real job is still revoked even when observing exit times out.
        await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<TimeoutException>(async () =>
        {
            await using var installation = await client.SuspendForInstallationAsync(default);
        });
        Assert.False(client.Owner.IsAvailable);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(canceled.Token, () => { }));
        Assert.Null(client.ReadCurrentReadings());
    }

    [Fact]
    public async Task InvalidJsonWithdrawsValuesAndRetainsExclusionUntilDelayedEofChildExits()
    {
        var root = Directory.CreateTempSubdirectory("rm-helper-invalid-json-").FullName;
        var observed = Path.Combine(root, "eof");
        var release = Path.Combine(root, "release");
        var launches = 0;
        Process? Start(ProcessStartInfo start)
        {
            launches++;
            start.Environment["RM_BRIDGE_FIXTURE_EOF_OBSERVED"] = observed;
            start.Environment["RM_BRIDGE_FIXTURE_EOF_RELEASE"] = release;
            return Process.Start(start);
        }
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { StopTimeout = TimeSpan.FromSeconds(10) }, startFactory: Start);
        using var stop = new CancellationTokenSource();
        var (run, ready) = StartRun(client, stop.Token);
        Task<IAsyncDisposable>? suspension = null;
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            var status = (await client.SendAsync("status", null, default))!.Value;
            var section = status.GetProperty("sectionName").GetString()!;
            using var observer = Observe(status);
            Assert.Null(await client.SendAsync("invalid-json", null, default));
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(observed) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.True(File.Exists(observed), "Fixture did not observe EOF after the invalid reply.");
            Assert.False(observer.HasExited);
            Assert.False(run.IsCompleted);
            Assert.Null(client.ReadCurrentReadings());
            Assert.Null(await client.SendAsync("status", null, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(default, () => { }));
            suspension = client.SuspendForInstallationAsync(default).AsTask();
            Assert.False(suspension.IsCompleted);
            Assert.Equal(1, launches);
            File.WriteAllText(release, "release");
            await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            await using (await suspension.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.True(observer.HasExited);
                Assert.Throws<FileNotFoundException>(() => HardwareBridgeReadingsSection.OpenReader(section));
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(default, () => { }));
            }
            Assert.Equal(1, launches);
            (run, ready) = StartRun(client, stop.Token);
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            status = (await client.SendAsync("status", null, default))!.Value;
            Assert.NotEqual(section, status.GetProperty("sectionName").GetString());
            Assert.Equal(2, launches);
        }
        finally
        {
            File.WriteAllText(release, "release");
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (IOException) { }
            if (suspension is not null && suspension.IsCompletedSuccessfully) await suspension.Result.DisposeAsync();
            File.Delete(observed); File.Delete(release); Directory.Delete(root);
        }
    }

    [Fact]
    public async Task CallerCancellationAfterAcceptanceDoesNotStopTheControlledRun()
    {
        using var client = CreateFixtureClient();
        using var stop = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var name = "Local\\ResourceManager.Bridge.Accepted." + Guid.NewGuid().ToString("N");
        using var accepted = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var (run, ready) = StartRun(client, stop.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            var pending = client.SendAsync("echo", new Dictionary<string, object?>
            { ["marker"] = "accepted", ["delayMilliseconds"] = 300, ["startedEvent"] = name }, caller.Token);
            await WaitForSignal(accepted);
            caller.Cancel();
            Assert.Equal("accepted", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))!.Value.GetProperty("marker").GetString());
            Assert.False(run.IsCompleted);
            Assert.NotNull(client.ReadCurrentReadings());
            Assert.Equal("after-caller-left", (await Echo(client, "after-caller-left")).GetProperty("marker").GetString());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ConcurrentCommandsKeepTheirOwnRepliesAndDescribeUsesSessionFacts()
    {
        using var client = CreateFixtureClient();
        using var stop = new CancellationTokenSource();
        var (run, ready) = StartRun(client, stop.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            var first = Echo(client, "first", 250);
            var queued = Enumerable.Range(0, 12).Select(index => Echo(client, "queued-" + index)).ToArray();
            Assert.Equal(45, client.ReadCurrentReadings()!.Value.StapmLimitWatts);
            var responses = await Task.WhenAll(queued.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("first", responses[0].GetProperty("marker").GetString());
            for (var index = 0; index < queued.Length; index++)
                Assert.Equal("queued-" + index, responses[index + 1].GetProperty("marker").GetString());
            Assert.Equal(responses.Length, responses.Select(reply => reply.GetProperty("id").GetInt64()).Distinct().Count());
            var last = await Echo(client, "before-description");
            var description = await client.SendAsync("describe", null, default);
            Assert.True(description!.Value.GetProperty("available").GetBoolean());
            Assert.Null(description.Value.GetProperty("sectionName").GetString());
            var after = await Echo(client, "after-description");
            Assert.Equal(last.GetProperty("id").GetInt64() + 1, after.GetProperty("id").GetInt64());
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CancelingQueuedCommandDoesNotSendItOrLoseTheActiveReplyBoundary()
    {
        using var client = CreateFixtureClient();
        using var stop = new CancellationTokenSource();
        var (run, ready) = StartRun(client, stop.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            var eventName = "Local\\ResourceManager.HelperEcho." + Guid.NewGuid().ToString("N");
            using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var active = Echo(client, "active", 1500, eventName);
            await WaitForSignal(entered);
            using var cancellation = new CancellationTokenSource();
            var queued = client.SendAsync("echo", new Dictionary<string, object?> { ["marker"] = "canceled" }, cancellation.Token);
            Assert.False(active.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            var response = await active.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("active", response.GetProperty("marker").GetString());
            var next = await Echo(client, "next");
            Assert.Equal("next", next.GetProperty("marker").GetString());
            Assert.Equal(response.GetProperty("id").GetInt64() + 1, next.GetProperty("id").GetInt64());
            Assert.False(run.IsCompleted);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task InstallationSuspensionJoinsChildAndRequiresANewExplicitEntryAfterLease()
    {
        using var client = CreateFixtureClient();
        using var stop = new CancellationTokenSource();
        var (run, ready) = StartRun(client, stop.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            var status = (await client.SendAsync("status", null, default))!.Value;
            var firstSection = status.GetProperty("sectionName").GetString()!;
            using var observer = Observe(status);
            await using (await client.SuspendForInstallationAsync(default))
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(observer.HasExited);
                Assert.Null(client.ReadCurrentReadings());
                Assert.Null(await client.SendAsync("status", null, default));
                Assert.Throws<FileNotFoundException>(() => HardwareBridgeReadingsSection.OpenReader(firstSection));
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(default, () => { }));
            }
            Assert.Null(await client.SendAsync("status", null, default));
            (run, ready) = StartRun(client, stop.Token);
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            status = (await client.SendAsync("status", null, default))!.Value;
            Assert.NotEqual(firstSection, status.GetProperty("sectionName").GetString());
            Assert.Equal(45, client.ReadCurrentReadings()!.Value.StapmLimitWatts);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task SuspensionDuringPendingCommandJoinsItsOwnedProcessBeforeReturningLease()
    {
        using var client = CreateFixtureClient();
        using var stop = new CancellationTokenSource();
        var (run, ready) = StartRun(client, stop.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            using var observer = Observe((await client.SendAsync("status", null, default))!.Value);
            var eventName = "Local\\ResourceManager.HelperEcho." + Guid.NewGuid().ToString("N");
            using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var pending = SendEcho(client, "pending-at-suspension", 2000, eventName);
            await WaitForSignal(entered);
            Assert.False(pending.IsCompleted);
            await using var installation = await client.SuspendForInstallationAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observer.HasExited);
            await ObserveInterruptedCommand(pending);
            Assert.Null(client.ReadCurrentReadings());
            Assert.Null(await client.SendAsync("status", null, default));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CancelingSuspensionWaitDoesNotReleaseAnEntryWhoseChildIsStillExiting()
    {
        using var client = CreateFixtureClient(HardwareBridgeSessionTests.Options with { StopTimeout = TimeSpan.FromSeconds(3) });
        using var stop = new CancellationTokenSource();
        var (run, ready) = StartRun(client, stop.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(10));
            using var observer = Observe((await client.SendAsync("status", null, default))!.Value);
            var eventName = "Local\\ResourceManager.HelperEcho." + Guid.NewGuid().ToString("N");
            using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var pending = SendEcho(client, "delayed-exit", 2000, eventName);
            await WaitForSignal(entered);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SuspendForInstallationAsync(cancellation.Token).AsTask());
            Assert.False(observer.HasExited);
            Assert.False(run.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(default, () => { }));
            Assert.Null(await client.SendAsync("status", null, default));
            await run.WaitAsync(TimeSpan.FromSeconds(6));
            await ObserveInterruptedCommand(pending);
            Assert.True(observer.HasExited);
            Assert.Null(await client.SendAsync("status", null, default));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(6)); }
    }

    [Fact]
    public async Task StartupFailureNeverBecomesARequestTriggeredRetry()
    {
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, startFactory: _ =>
            {
                launches++;
                throw new Win32Exception(2, "Injected offline startup failure");
            });
        await Assert.ThrowsAsync<Win32Exception>(() => client.Owner.RunAsync(default, () => Assert.Fail("Failed startup reported running")));
        for (var index = 0; index < 3; index++) Assert.Null(await client.SendAsync("describe", null, default));
        Assert.Null(client.ReadCurrentReadings());
        Assert.Equal(1, launches);
        Assert.False(client.Owner.IsAvailable);
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.Owner.RunAsync(default, () => Assert.Fail("Unavailable startup reported running")));
        Assert.Equal(1, launches);
        await using (await client.SuspendForInstallationAsync(default)) { }
        Assert.True(client.Owner.IsAvailable);
        await Assert.ThrowsAsync<Win32Exception>(() => client.Owner.RunAsync(default, () => Assert.Fail("Failed startup reported running")));
        Assert.Equal(2, launches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposalDuringFactoryStartupJoinsLateChildWithoutSendingHandshake(bool dispose)
    {
        var root = Directory.CreateTempSubdirectory("rm-helper-starting-").FullName;
        var commandReceived = Path.Combine(root, "command-received");
        var finished = Path.Combine(root, "finished");
        var script = $$"""
            $ErrorActionPreference='Stop'
            if ($null -ne [Console]::In.ReadLine()) {
                [IO.File]::WriteAllText('{{commandReceived.Replace("'", "''", StringComparison.Ordinal)}}','unexpected command')
                while ($null -ne [Console]::In.ReadLine()) { }
            }
            [IO.File]::WriteAllText('{{finished.Replace("'", "''", StringComparison.Ordinal)}}','input closed')
            """;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? observer = null;
        Process? Start(ProcessStartInfo ignored)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            var child = Process.Start(info)!;
            observer = Process.GetProcessById(child.Id);
            _ = observer.SafeHandle;
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                child.Kill();
                child.Dispose();
                throw new TimeoutException("Test startup factory was not released");
            }
            return child;
        }
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { StopTimeout = TimeSpan.FromSeconds(5) }, startFactory: Start);
        using var stop = new CancellationTokenSource();
        var reportedRunning = false;
        var run = Task.Run(() => client.Owner.RunAsync(stop.Token, () => reportedRunning = true));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (dispose) client.Dispose(); else stop.Cancel();
            Assert.False(run.IsCompleted);
            release.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(reportedRunning);
            Assert.True(observer!.HasExited);
            Assert.Equal(0, observer.ExitCode);
            Assert.False(File.Exists(commandReceived));
            Assert.True(File.Exists(finished));
            if (!dispose) Assert.Null(await client.SendAsync("status", null, default));
        }
        finally
        {
            release.Set(); stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                observer?.Dispose();
                File.Delete(commandReceived); File.Delete(finished); Directory.Delete(root);
            }
        }
    }

    private static HardwareBridgeProcessClient CreateFixtureClient(HardwareBridgeProcessOptions? options = null)
        => new(() => HardwareBridgeSessionTests.Fixture, options ?? HardwareBridgeSessionTests.Options);

    private static (Task Run, Task Ready) StartRun(HardwareBridgeProcessClient client, CancellationToken stop)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (client.Owner.RunAsync(stop, () => ready.SetResult()), ready.Task);
    }

    private static Task<JsonElement?> SendEcho(HardwareBridgeProcessClient client, string marker, int delayMilliseconds = 0,
        string? startedEvent = null)
    {
        var arguments = new Dictionary<string, object?> { ["marker"] = marker, ["delayMilliseconds"] = delayMilliseconds };
        if (startedEvent is not null) arguments["startedEvent"] = startedEvent;
        return client.SendAsync("echo", arguments, default);
    }

    private static async Task<JsonElement> Echo(HardwareBridgeProcessClient client, string marker, int delayMilliseconds = 0,
        string? startedEvent = null)
    {
        var reply = await SendEcho(client, marker, delayMilliseconds, startedEvent);
        Assert.NotNull(reply);
        return reply!.Value;
    }

    private static Process Observe(JsonElement status)
    {
        var observer = Process.GetProcessById(status.GetProperty("pid").GetInt32());
        _ = observer.SafeHandle;
        return observer;
    }

    private static async Task WaitForSignal(EventWaitHandle signal)
        => Assert.True(await Task.Run(() => signal.WaitOne(TimeSpan.FromSeconds(5))), "Fixture did not receive the command.");

    private static async Task ObserveInterruptedCommand(Task<JsonElement?> pending)
    {
        // A stop may win before the delayed fixture responds or after its line was accepted.
        try
        {
            var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (reply is { } response) Assert.True(response.GetProperty("ok").GetBoolean());
        }
        catch (OperationCanceledException) { }
    }
}
