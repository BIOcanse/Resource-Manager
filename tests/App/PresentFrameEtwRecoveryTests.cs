using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class PresentFrameEtwRecoveryTests
{
    [Fact]
    public async Task FailedStartupRequiresExplicitWithdrawalBeforeAnotherDependencyStartsIt()
    {
        await using var harness = PresentFrameEtwTestHarness.Create();
        harness.Sessions.Configure = (_, attempt) =>
        {
            if (attempt == 1) throw new IOException("injected startup failure");
        };
        var first = harness.Source.AcquireSubscription();
        await Assert.ThrowsAsync<IOException>(() => PresentFrameEtwTestHarness.ReadyAsync(first));
        Assert.Equal(BusinessBlockOutcome.Failed,
            (await harness.Controller.GetCompletion("present-etw")).Outcome);
        for (var index = 0; index < 3; index++)
        {
            Assert.Null(harness.Source.Read(TimeSpan.FromSeconds(1)));
            Assert.Null(harness.Source.ReadIntervals(DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow));
            _ = harness.Controller.Read();
        }
        var second = harness.Source.AcquireSubscription();
        await Assert.ThrowsAsync<InvalidOperationException>(() => PresentFrameEtwTestHarness.ReadyAsync(second));
        Assert.Equal(1, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(first);
        await PresentFrameEtwTestHarness.ReleaseAsync(second);

        var next = harness.Source.AcquireSubscription();
        await PresentFrameEtwTestHarness.ReadyAsync(next);
        Assert.Equal(2, harness.Sessions.Attempts);
        await PresentFrameEtwTestHarness.ReleaseAsync(next);
    }
}
