using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class CurrentValuePublicationSignalTests
{
    [Fact]
    public async Task PublishWakesEveryCurrentSubscriber()
    {
        var signal = new CurrentValuePublicationSignal();
        using var first = signal.Subscribe();
        using var second = signal.Subscribe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstWait = first.WaitAsync(timeout.Token).AsTask();
        var secondWait = second.WaitAsync(timeout.Token).AsTask();
        signal.Publish();

        await Task.WhenAll(firstWait, secondWait);
    }

    [Fact]
    public async Task PendingPublicationsCollapseToOneCurrentValueNotification()
    {
        var signal = new CurrentValuePublicationSignal();
        using var subscription = signal.Subscribe();
        signal.Publish();
        signal.Publish();

        await subscription.WaitAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await subscription.WaitAsync(timeout.Token));
    }
}
