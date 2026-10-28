using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwRecoveryTests
{
    [Fact]
    public async Task StartupFailureReturnsAndParentReevaluatesTheExistingDemand()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        harness.Sessions.Configure = (_, attempt) =>
        {
            if (attempt == 1) throw new IOException("injected startup failure");
        };
        var first = harness.Source.AcquireSubscription();
        var failed = await harness.NextResultAsync();
        Assert.Equal(BusinessBlockOutcome.Failed, failed.Outcome);
        Assert.Equal(BusinessBlockFailureStage.Start, failed.FailureStage);
        await EtwTestRunRecorder.UntilAsync(() => harness.Sessions.Attempts == 2);
        await harness.WaitRunningAsync();
        var session = harness.Sessions.Current;
        for (var index = 0; index < 3; index++)
        {
            _ = harness.Source.Read(TimeSpan.FromSeconds(1));
            _ = harness.Source.ReadIntervals(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);
            _ = harness.Controller.Read();
        }
        Assert.Equal(2, harness.Sessions.Attempts);
        await harness.ReleaseAsync(first);
        Assert.Equal(1, session.Stops);
        Assert.False(session.ConsumerThread!.IsAlive);
    }
}
