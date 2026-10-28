using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeOwnerRoundTwoTests
{
    [Fact]
    public async Task NativeControllerRetriesUnknownFailureWhileReadingSubscriptionRemainsInUse()
    {
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, startFactory: start =>
            {
                if (Interlocked.Increment(ref launches) == 1) throw new IOException("fixture-transient-unknown-startup");
                return Process.Start(start);
            });
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            {
                [BlockUids.HardwareBridgeProcessOwner] = _ => new HardwareBridgeRunningBlock(client, queue!)
            });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, client.Owner);
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "unknown-retry-test", "bridge-availability", 0);
        var availability = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable);
        client.Owner.AvailabilityChanged += value => _ = availability.PublishAsync(value, reason);
        await availability.PublishAsync(client.Owner.IsAvailable, reason);
        var reading = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeReadingsInUse);
        client.Owner.ReadingsInUseChanged += value => _ = reading.PublishAsync(value, reason);
        await reading.PublishAsync(client.Owner.ReadingsInUse, reason);
        using var subscription = client.SubscribeReadings();
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Running);
        Assert.True(client.Owner.IsAvailable);
        Assert.Equal(2, launches);
        Assert.NotNull(client.ReadCurrentReadings());
        Assert.True(fixture.Authority.Read<bool>(StateItemUids.HardwareBridgeReadingsInUse).Value);
        Assert.True(fixture.Authority.Read<bool>(StateItemUids.HardwareBridgeAvailable).Value);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("invalid")]
    [InlineData("win32")]
    public async Task UnknownStartupFailureReturnsFailureWithoutChangingAvailability(string failure)
    {
        var launches = 0;
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, startFactory: _ =>
            {
                launches++;
                throw failure switch
                {
                    "io" => (Exception)new IOException("unknown-startup-failure"),
                    "invalid" => new InvalidOperationException("unknown-startup-failure"),
                    _ => new Win32Exception(87, "unknown-startup-failure")
                };
            });
        var changes = new List<bool>();
        owner.AvailabilityChanged += changes.Add;
        for (var attempt = 0; attempt != 2; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => owner.RunAsync(default, () => Assert.Fail("Failed startup reported running")));
            Assert.True(owner.IsAvailable);
            Assert.Equal(HardwareBridgeUnavailableKind.None, owner.UnavailableKind);
        }
        Assert.Empty(changes);
        Assert.Equal(2, launches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task UnconfirmedReturnedChildRetainsHandleAndOnlyPollsAtConfiguredInterval(int intervalMilliseconds)
    {
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { AvailabilityCheckInterval = TimeSpan.FromMilliseconds(intervalMilliseconds) });
        SafeProcessHandle? handle = null;
        owner.WaitForChildExitAsync = (process, _) =>
        {
            handle = process.SafeHandle;
            return Task.FromException(new TimeoutException("fixture-exit-not-confirmed"));
        };
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = owner.RunAsync(stop.Token, () => ready.SetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(owner.IsAvailable);
        Assert.Equal("ExitNotConfirmed", owner.UnavailableKind.ToString());
        Assert.NotNull(handle);
        Assert.False(handle.IsClosed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RunAsync(default, () => Assert.Fail("Unconfirmed child overlapped")));
        await Assert.ThrowsAnyAsync<TimeoutException>(async () => await owner.SuspendForInstallationAsync(default));
        if (intervalMilliseconds == 0)
        {
            await Task.Delay(150);
            Assert.False(owner.IsAvailable);
            Assert.False(handle.IsClosed);
        }
        else
        {
            await BusinessBlockTestSupport.Until(() => owner.IsAvailable);
            Assert.Equal(HardwareBridgeUnavailableKind.None, owner.UnavailableKind);
            Assert.True(handle.IsClosed);
            await using (await owner.SuspendForInstallationAsync(default)) { }
        }
    }

    [Fact]
    public async Task UnconfirmedExitDoesNotReleaseRunBeforeAcceptedCommandsHaveJoined()
    {
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { AvailabilityCheckInterval = TimeSpan.FromMilliseconds(10) });
        var waits = 0;
        var cleanupFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.WaitForChildExitAsync = (_, _) =>
        {
            if (Interlocked.Increment(ref waits) == 2) cleanupFailed.TrySetResult();
            return Task.FromException(new TimeoutException("fixture-exit-not-confirmed"));
        };
        using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCommands = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = owner.RunAsync(stop.Token, () => ready.SetResult(), _ => finishCommands.Task);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.Cancel();
            await cleanupFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Assert.False(run.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RunAsync(default, () => Assert.Fail("Command callback overlapped")));
            finishCommands.TrySetResult();
            await Assert.ThrowsAnyAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            await BusinessBlockTestSupport.Until(() => owner.IsAvailable);
            await using (await owner.SuspendForInstallationAsync(default)) { }
        }
        finally { finishCommands.TrySetResult(); stop.Cancel(); }
    }

    [Fact]
    public async Task FailedInstallationStopResumesRetainedExitChecksAfterSuspensionIsCleared()
    {
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with { AvailabilityCheckInterval = TimeSpan.FromMilliseconds(100) });
        SafeProcessHandle? handle = null;
        var waits = 0;
        owner.WaitForChildExitAsync = (process, _) =>
        {
            handle = process.SafeHandle;
            Interlocked.Increment(ref waits);
            return Task.FromException(new TimeoutException("fixture-installation-exit-not-confirmed"));
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = owner.RunAsync(default, () => ready.SetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<TimeoutException>(async () => await owner.SuspendForInstallationAsync(default));
        await Assert.ThrowsAnyAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, waits);
        Assert.NotNull(handle);
        Task? overlap = null;
        // A delayed test continuation may arrive after the first successful
        // retained-handle check. Inspect exclusion and availability atomically.
        var sync = typeof(HardwareBridgeProcessOwner).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        lock (sync)
        {
            if (owner.IsAvailable) Assert.True(handle.IsClosed);
            else
            {
                Assert.Equal(HardwareBridgeUnavailableKind.ExitNotConfirmed, owner.UnavailableKind);
                Assert.False(handle.IsClosed);
                overlap = owner.RunAsync(default, () => Assert.Fail("Unconfirmed installation stop overlapped"));
            }
        }
        if (overlap is not null) await Assert.ThrowsAsync<InvalidOperationException>(() => overlap);
        await BusinessBlockTestSupport.Until(() => owner.IsAvailable);
        Assert.True(handle.IsClosed);
        await using (await owner.SuspendForInstallationAsync(default)) { }
    }

    [Fact]
    public async Task InstallationEventCannotRestoreAvailabilityInsideAnActiveInstallation()
    {
        string? installed = null;
        using var owner = new HardwareBridgeProcessOwner(() => installed, HardwareBridgeSessionTests.Options);
        await using (await owner.SuspendForInstallationAsync(default))
        {
            installed = HardwareBridgeSessionTests.Fixture;
            typeof(HardwareBridgeProcessOwner).GetMethod("InstallationChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(owner, null);
            Assert.False(owner.IsAvailable);
        }
        Assert.True(owner.IsAvailable);
    }

    [Fact]
    public async Task PollAlreadyInFlightCannotRestoreAvailabilityDuringInstallation()
    {
        using var probing = new ManualResetEventSlim();
        using var finishProbe = new ManualResetEventSlim();
        var probeFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options with
            {
                AvailabilityCheckInterval = TimeSpan.FromMilliseconds(10),
                IsRuntimeAvailable = () =>
                {
                    probing.Set();
                    if (!finishProbe.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture probe not released");
                    probeFinished.TrySetResult();
                    return true;
                }
            }, startFactory: start =>
            {
                start.Environment["RM_BRIDGE_FIXTURE_MODE"] = "runtime-missing";
                return Process.Start(start);
            });
        try
        {
            await Assert.ThrowsAsync<IOException>(() => owner.RunAsync(default, () => Assert.Fail("Missing runtime reported running")));
            Assert.True(await Task.Run(() => probing.Wait(TimeSpan.FromSeconds(5))));
            await using (await owner.SuspendForInstallationAsync(default))
            {
                finishProbe.Set();
                await probeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(30);
                Assert.False(owner.IsAvailable);
            }
            Assert.True(owner.IsAvailable);
        }
        finally { finishProbe.Set(); }
    }

    [Fact]
    public async Task AvailabilityCallbacksCanReadOwnerFromAnotherThread()
    {
        using var owner = new HardwareBridgeProcessOwner(() => HardwareBridgeSessionTests.Fixture,
            HardwareBridgeSessionTests.Options, startFactory: _ => throw new Win32Exception(5, "fixture-access-denied"));
        var unlocked = new List<bool>();
        owner.AvailabilityChanged += _ =>
        {
            var read = Task.Run(() => owner.UnavailableKind);
            unlocked.Add(read.Wait(TimeSpan.FromSeconds(2)));
        };
        await Assert.ThrowsAsync<Win32Exception>(() => owner.RunAsync(default, () => Assert.Fail("Denied startup reported running")));
        await using (await owner.SuspendForInstallationAsync(default)) { }
        Assert.Equal(new[] { true, true }, unlocked);
    }
}
