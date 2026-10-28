using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareBridgeAvailabilityTests
{
    private static BusinessBlockReason Reason => new(BusinessBlockRequestKind.Configuration, "availability-test", "bridge-availability", 0);
    private static HardwareBridgeProcessOptions Polling => HardwareBridgeSessionTests.Options with
        { AvailabilityCheckInterval = TimeSpan.FromMilliseconds(10) };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingHelperKeepsNativeEntryStoppedDespitePendingCommandOrSubscription(bool pendingCommand)
    {
        var attempts = 0;
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() => null, HardwareBridgeSessionTests.Options,
            startFactory: _ => { Interlocked.Increment(ref launches); throw new Exception("Missing helper was launched"); });
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            {
                [BlockUids.HardwareBridgeProcessOwner] = _ => new CountedBlock(new HardwareBridgeRunningBlock(client, queue!),
                    () => Interlocked.Increment(ref attempts))
            });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, client.Owner);
        await PublishOwnerFacts(client, fixture);
        using var subscription = pendingCommand ? null : client.SubscribeReadings();
        using var accepted = pendingCommand ? queue.AcquirePendingScope() : null;
        await Task.Delay(100);
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.NotInstalled, client.Owner.UnavailableKind);
        Assert.Equal(BusinessBlockState.Stopped, fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner));
        Assert.Equal(0, attempts);
        Assert.Equal(0, launches);
        Assert.Null(await queue.ExecuteAsync(token => client.SendAsync("describe", null, token), default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(pendingCommand, queue.HasPending);
    }

    [Fact]
    public async Task UnsupportedHelperStartsOnceThenNativeControllerStopsDespiteRetainedReadSubscription()
    {
        var attempts = 0;
        var launches = 0;
        var resolutions = 0;
        using var client = new HardwareBridgeProcessClient(() =>
        {
            Interlocked.Increment(ref resolutions);
            return HardwareBridgeSessionTests.Fixture;
        }, Polling, startFactory: start =>
        {
            Interlocked.Increment(ref launches);
            start.Environment["RM_BRIDGE_FIXTURE_MODE"] = "unsupported";
            return Process.Start(start);
        });
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            {
                [BlockUids.HardwareBridgeProcessOwner] = _ => new CountedBlock(new HardwareBridgeRunningBlock(client, queue!),
                    () => Interlocked.Increment(ref attempts))
            });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, client.Owner);
        await PublishOwnerFacts(client, fixture);
        using var subscription = client.SubscribeReadings();
        await BusinessBlockTestSupport.Until(() => !client.Owner.IsAvailable);
        await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Stopped);
        var stableResolutions = Volatile.Read(ref resolutions);
        await Task.Delay(100);
        Assert.True(fixture.Authority.Read<bool>(StateItemUids.HardwareBridgeReadingsInUse).Value);
        Assert.False(fixture.Authority.Read<bool>(StateItemUids.HardwareBridgeAvailable).Value);
        Assert.Equal(HardwareBridgeUnavailableKind.HardwareUnsupported, client.Owner.UnavailableKind);
        Assert.Equal(1, launches);
        Assert.Equal(1, attempts);
        Assert.Equal(stableResolutions, resolutions);
        Assert.Null(await queue.ExecuteAsync(token => client.SendAsync("describe", null, token), default).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task AcceptedCommandReturnsItsTypedUnavailableResultWhenStartupAccessIsDenied()
    {
        var attempts = 0;
        var launches = 0;
        var executed = 0;
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, Polling,
            startFactory: _ => { Interlocked.Increment(ref launches); throw new Win32Exception(5, "fixture-access-denied"); });
        HardwareCommandQueue? queue = null;
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, Func<IServiceProvider, IBusinessBlock>>
            {
                [BlockUids.HardwareBridgeProcessOwner] = _ => new CountedBlock(new HardwareBridgeRunningBlock(client, queue!),
                    () => Interlocked.Increment(ref attempts))
            });
        await fixture.InitializeHostPermitsAsync();
        queue = new(fixture.Authority, client.Owner);
        await PublishOwnerFacts(client, fixture);
        Assert.True(client.Owner.IsAvailable);
        try
        {
            var result = await queue.ExecuteAsync(async token =>
            {
                Interlocked.Increment(ref executed);
                return await client.SendAsync("describe", null, token) is null ? "typed-unavailable" : "unexpected";
            }, default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("typed-unavailable", result);
            await BusinessBlockTestSupport.Until(() => fixture.Scheduler.ReadState(BlockUids.HardwareBridgeProcessOwner) == BusinessBlockState.Stopped);
            Assert.False(client.Owner.IsAvailable);
            Assert.Equal(HardwareBridgeUnavailableKind.AccessDenied, client.Owner.UnavailableKind);
            Assert.False(queue.HasPending);
            await Task.Delay(100);
            Assert.Equal(1, executed);
            Assert.Equal(1, launches);
            Assert.Equal(1, attempts);
        }
        finally { queue.FailPending(new IOException("availability test completed")); }
    }

    [Fact]
    public async Task MissingHelperPollsOnlyWhileUnavailableAndPublishesRecoveryWithoutLaunching()
    {
        string? installed = null;
        var resolutions = 0;
        var launches = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HardwareBridgeProcessClient(() =>
        {
            Interlocked.Increment(ref resolutions);
            return Volatile.Read(ref installed);
        }, Polling, startFactory: _ => { Interlocked.Increment(ref launches); throw new Exception("Availability check started a child"); });
        client.Owner.AvailabilityChanged += available => { if (available) recovered.TrySetResult(); };
        await BusinessBlockTestSupport.Until(() => Volatile.Read(ref resolutions) >= 3);
        Assert.False(client.Owner.IsAvailable);
        Volatile.Write(ref installed, HardwareBridgeSessionTests.Fixture);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.Owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.None, client.Owner.UnavailableKind);
        var stableResolutions = Volatile.Read(ref resolutions);
        await Task.Delay(100);
        Assert.Equal(stableResolutions, resolutions);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task ZeroIntervalNeverPollsAndInstallationReleaseReevaluatesWithoutLaunching()
    {
        string? installed = null;
        var resolutions = 0;
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() =>
        {
            Interlocked.Increment(ref resolutions);
            return Volatile.Read(ref installed);
        }, HardwareBridgeSessionTests.Options, startFactory: _ => { Interlocked.Increment(ref launches); throw new Exception("Installation check started a child"); });
        Volatile.Write(ref installed, HardwareBridgeSessionTests.Fixture);
        await Task.Delay(100);
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal(1, resolutions);
        await using (await client.SuspendForInstallationAsync(default))
            Assert.False(client.Owner.IsAvailable);
        Assert.True(client.Owner.IsAvailable);
        Assert.Equal(2, resolutions);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task RuntimeMissingPollsTheExplicitRuntimeProbeAndRecoversWithoutAnotherChild()
    {
        var runtimePresent = false;
        var probes = 0;
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, Polling with
        {
            IsRuntimeAvailable = () => { Interlocked.Increment(ref probes); return Volatile.Read(ref runtimePresent); }
        }, startFactory: start =>
        {
            Interlocked.Increment(ref launches);
            start.Environment["RM_BRIDGE_FIXTURE_MODE"] = "runtime-missing";
            return Process.Start(start);
        });
        await Assert.ThrowsAsync<IOException>(() => client.Owner.RunAsync(default, () => Assert.Fail("Missing runtime reported running")));
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.RuntimeMissing, client.Owner.UnavailableKind);
        Assert.Equal(HardwareBridgeSessionTests.Options.ErrorOutput.MissingRuntimeReason, client.UnavailableReason);
        await BusinessBlockTestSupport.Until(() => Volatile.Read(ref probes) >= 2);
        Volatile.Write(ref runtimePresent, true);
        await BusinessBlockTestSupport.Until(() => client.Owner.IsAvailable);
        var stableProbes = Volatile.Read(ref probes);
        await Task.Delay(100);
        Assert.Equal(stableProbes, probes);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task UnconfirmedExitOverridesPollableRuntimeFailureWhenPollingDisabled()
    {
        var launches = 0;
        var probes = 0;
        var waits = 0;
        using var client = new HardwareBridgeProcessClient(() => HardwareBridgeSessionTests.Fixture, Polling with
        {
            AvailabilityCheckInterval = TimeSpan.Zero,
            IsRuntimeAvailable = () => { Interlocked.Increment(ref probes); return true; }
        }, startFactory: start =>
        {
            Interlocked.Increment(ref launches);
            start.Environment["RM_BRIDGE_FIXTURE_MODE"] = "runtime-missing";
            return Process.Start(start);
        });
        client.Owner.WaitForChildExitAsync = async (process, _) =>
        {
            Interlocked.Increment(ref waits);
            await process.WaitForExitAsync();
            // Let the independent stderr reader consume the real runtime diagnostic
            // before forcing failure at the owner's exit-observation boundary.
            await Task.Delay(100);
            throw new TimeoutException("fixture-terminal-exit-observation-failed");
        };
        await Assert.ThrowsAnyAsync<TimeoutException>(() => client.Owner.RunAsync(default, () => Assert.Fail("Missing runtime reported running")));
        Assert.Equal(2, waits);
        Assert.Equal("ExitNotConfirmed", client.Owner.UnavailableKind.ToString());
        Assert.False(client.Owner.IsAvailable);
        await Task.Delay(100);
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal("ExitNotConfirmed", client.Owner.UnavailableKind.ToString());
        Assert.Equal(0, probes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(default, () => Assert.Fail("Terminal owner restarted")));
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task RuntimeMissingWithoutExplicitProbeNeverPollsAndWaitsForInstallationRelease()
    {
        var resolutions = 0;
        var launches = 0;
        using var client = new HardwareBridgeProcessClient(() =>
        {
            Interlocked.Increment(ref resolutions);
            return HardwareBridgeSessionTests.Fixture;
        }, Polling, startFactory: start =>
        {
            Interlocked.Increment(ref launches);
            start.Environment["RM_BRIDGE_FIXTURE_MODE"] = "runtime-missing";
            return Process.Start(start);
        });
        await Assert.ThrowsAsync<IOException>(() => client.Owner.RunAsync(default, () => Assert.Fail("Missing runtime reported running")));
        Assert.Equal(HardwareBridgeUnavailableKind.RuntimeMissing, client.Owner.UnavailableKind);
        var stableResolutions = Volatile.Read(ref resolutions);
        await Task.Delay(100);
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal(stableResolutions, resolutions);
        await using (await client.SuspendForInstallationAsync(default)) { }
        Assert.True(client.Owner.IsAvailable);
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task AccessDeniedNeverPollsOrRetriesBeforeInstallationRelease()
    {
        var resolutions = 0;
        var launches = 0;
        var probes = 0;
        using var client = new HardwareBridgeProcessClient(() =>
        {
            Interlocked.Increment(ref resolutions);
            return HardwareBridgeSessionTests.Fixture;
        }, Polling with { IsRuntimeAvailable = () => { Interlocked.Increment(ref probes); return true; } },
            startFactory: _ => { Interlocked.Increment(ref launches); throw new Win32Exception(5, "fixture-access-denied"); });
        await Assert.ThrowsAsync<Win32Exception>(() => client.Owner.RunAsync(default, () => Assert.Fail("Denied startup reported running")));
        Assert.False(client.Owner.IsAvailable);
        Assert.Equal(HardwareBridgeUnavailableKind.AccessDenied, client.Owner.UnavailableKind);
        var stableResolutions = Volatile.Read(ref resolutions);
        await Task.Delay(100);
        Assert.Equal(stableResolutions, resolutions);
        Assert.Equal(0, probes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Owner.RunAsync(default, () => { }));
        Assert.Equal(1, launches);
        await using (await client.SuspendForInstallationAsync(default)) { }
        Assert.True(client.Owner.IsAvailable);
        Assert.Equal(1, launches);
    }

    private static async Task PublishOwnerFacts(HardwareBridgeProcessClient client, NativeBackendBlockFixture fixture)
    {
        var available = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeAvailable);
        client.Owner.AvailabilityChanged += value => _ = available.PublishAsync(value, Reason);
        await available.PublishAsync(client.Owner.IsAvailable, Reason);
        var reading = fixture.Authority.ClaimCell<bool>(StateItemUids.HardwareBridgeReadingsInUse);
        client.Owner.ReadingsInUseChanged += value => _ = reading.PublishAsync(value, Reason);
        await reading.PublishAsync(client.Owner.ReadingsInUse, Reason);
    }

    private sealed class CountedBlock(IBusinessBlock inner, Action started) : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context)
        {
            started();
            return inner.RunAsync(context);
        }
    }
}
