using FanControlCore;
using FanControlCore.Discovery;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class FanCoreAvailabilityTests
{
    [Fact]
    public async Task DiscoveryFailureAlsoWakesACommandAlreadyWaitingForARun()
    {
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new LibraryFanSession(
            new FanCore(new FailedBackend(FanOpenFailureKind.AccessDenied))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var command = client.SendAsync("set", null, deadline.Token);
        Assert.True(client.PendingCommands);
        Assert.False(command.IsCompleted);
        await client.DiscoverAsync(deadline.Token);
        var response = await command.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(response!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains("fixture-AccessDenied", response.Value.GetProperty("error").GetString());
        Assert.False(client.PendingCommands);
    }

    [Theory]
    [InlineData(FanOpenFailureKind.NoSupportedController)]
    [InlineData(FanOpenFailureKind.DriverMissing)]
    [InlineData(FanOpenFailureKind.AccessDenied)]
    public async Task RecognisedOpenFailureFailsCommandsFastAndDiscoveryRecovers(FanOpenFailureKind kind)
    {
        var fail = true;
        var opened = 0;
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) =>
        {
            opened++;
            return new LibraryFanSession(new FanCore(fail ? new FailedBackend(kind) : new ProbeFanBackend()));
        });
        var edges = new List<bool>();
        client.AvailabilityChanged += () => edges.Add(client.IsAvailable);
        Assert.True(client.IsAvailable);
        Assert.Empty(await client.DiscoverAsync(default));
        Assert.False(client.IsAvailable);
        var reason = client.UnavailableReason;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var command = await client.SendAsync("set", null, deadline.Token);
        Assert.False(command!.Value.GetProperty("ok").GetBoolean());
        Assert.Contains("fixture-" + kind, command.Value.GetProperty("error").GetString());
        Assert.Equal(reason, client.UnavailableReason);
        Assert.False(client.PendingCommands);
        Assert.Equal(1, opened);
        fail = false;
        client.ResetDetection();
        Assert.Single(await client.DiscoverAsync(default));
        Assert.True(client.IsAvailable);
        Assert.Equal(new[] { false, true }, edges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OtherAndMixedFailureLeaveAvailabilityUnchangedAndRunFails(bool mixed)
    {
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new LibraryFanSession(
            new FanCore(mixed ? [new FailedBackend(FanOpenFailureKind.DriverMissing), new FailedBackend(FanOpenFailureKind.Other)]
                : new IFanBackend[] { new FailedBackend(FanOpenFailureKind.Other) })));
        var edges = 0;
        client.AvailabilityChanged += () => edges++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartSession());
        Assert.True(client.IsAvailable);
        Assert.Equal(0, edges);
    }

    [Fact]
    public async Task MixedOtherFailureDoesNotClearRecordedUnavailability()
    {
        var mixed = false;
        await using var client = new FanControlCoreClient(new FanTestEnvironment(), (_, _) => new LibraryFanSession(
            new FanCore(mixed ? [new FailedBackend(FanOpenFailureKind.AccessDenied), new FailedBackend(FanOpenFailureKind.Other)]
                : new IFanBackend[] { new FailedBackend(FanOpenFailureKind.AccessDenied) })));
        var edges = 0;
        client.AvailabilityChanged += () => edges++;
        await client.DiscoverAsync(default);
        var reason = client.UnavailableReason;
        mixed = true;
        client.ResetDetection();
        await client.DiscoverAsync(default);
        Assert.False(client.IsAvailable);
        Assert.Equal(reason, client.UnavailableReason);
        Assert.Equal(1, edges);
    }

    private sealed class FailedBackend(FanOpenFailureKind kind) : IFanBackend
    {
        public string Name => "fixture";
        public bool TryOpen(out string? reason) { reason = "fixture-" + kind; return false; }
        public bool TryOpen(out FanOpenFailureKind failure, out string? reason)
        { failure = kind; return TryOpen(out reason); }
        public IReadOnlyList<FanDescription> Describe() => [];
        public IReadOnlyList<FanState> Read() => [];
        public bool TrySetDuty(int fan, double value, out string? reason) => throw new Exception("No hardware access");
        public bool TrySetFullSpeed(int fan, out string? reason) => throw new Exception("No hardware access");
        public bool TrySetAutomatic(int fan, out string? reason) => throw new Exception("No hardware access");
        public void Dispose() { }
    }
}
