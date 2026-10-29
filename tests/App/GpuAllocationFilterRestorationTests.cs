using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationFilterRestorationTests
{
    private static readonly int[] ReadIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55,
        74, 78, 80, 110, 227, 306, 307, 313, 314, 391];

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task EveryInitialAndRebuildRundownRestoresTheSameFilterBeforeClosingItsBoundary(int seconds)
    {
        var factory = new GpuAllocationTestSessionFactory();
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        var cadence = TimeSpan.FromSeconds(seconds);
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, cadence, default);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        AssertRestored(factory.Current);
        if (seconds == 1)
        {
            // The next due detects loss and empties; its following due is the
            // existing rebuild path, which must restore the filter as well.
            factory.Current.LoseEvent();
            await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, cadence, default);
            Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        }
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, cadence, default);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.NotNull(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.All(factory.Sessions, AssertRestored);
        await harness.StopAsync();
        Assert.All(factory.Sessions, session => { Assert.Empty(session.Enabled); Assert.True(session.Joined); });
    }

    [Fact]
    public async Task FailedFilterRestorationPublishesEmptyAndReleasesWithoutAnAlternateCapture()
    {
        var factory = new GpuAllocationTestSessionFactory { Configure = session =>
            session.DuringCapture = () => session.EnableFailure = new IOException("post-capture enable failed") };
        await using var harness = factory.Harness();
        await harness.SetInUseAsync(true);
        await harness.WaitRunningAsync();
        await harness.Collector.CaptureAsync(GpuAllocationTestSession.Processes, TimeSpan.FromSeconds(1), default);
        var session = factory.Current;
        Assert.Null(harness.Collector.ReadCurrent(GpuAllocationTestSession.Processes));
        Assert.Equal(1, factory.Attempts);
        Assert.Equal(1, session.Captures);
        Assert.Empty(session.Enabled);
        Assert.True(session.Joined);
        Assert.DoesNotContain(session.ProviderCommands, command => command.Operation == "end-scan");
    }

    private static void AssertRestored(GpuAllocationTestSession session)
    {
        var commands = session.ProviderCommands.Where(command => command.Provider == GpuAllocationCollector.Provider
            || command.Operation == "end-scan").ToArray();
        Assert.Equal(["enable", "capture-enter", "capture-return", "enable", "end-scan"], commands.Select(command => command.Operation));
        Assert.All(commands.Where(command => command.Provider == GpuAllocationCollector.Provider), command =>
        {
            Assert.Equal(0x41UL, command.Keywords);
            Assert.Equal(ReadIds, command.EventIds);
        });
    }
}
