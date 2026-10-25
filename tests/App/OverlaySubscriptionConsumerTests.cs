using ResourceManager.NativeUi.Overlay;
using ResourceManager.NativeUi.Preferences;
using System.Collections.Concurrent;

namespace Resource_Manager_APP.Tests;

public sealed class OverlaySubscriptionConsumerTests
{
    [Fact]
    public void DisabledAndEmptySelectionsHaveNoDataSubscriptionAndEachSoftwareKeepsItsOwnIds()
    {
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
        {
            ["GAME"] = new() { Metrics = ["target.memory", "target.cpu"] },
            ["empty"] = new() { Metrics = [] }
        } };
        var plan = OverlaySubscriptions.Create([
            new("game", true, "external"), new("off", false, "external"), new("empty", true, "injected")], preferences);
        var subscription = Assert.Single(plan);
        Assert.Contains("softwareId=game", subscription.Path);
        Assert.Contains("ids=target.cpu&ids=target.memory", subscription.Path);
        Assert.DoesNotContain("target.fps", subscription.Path);
        Assert.Empty(OverlaySubscriptions.Create([new("off", false, "external")], new()));
        var defaultPlan = Assert.Single(OverlaySubscriptions.Create([new("fresh", true, "external")], new()));
        Assert.Equal(FrontendPoints.MetricOrder.Count(), defaultPlan.Path.Split("&ids=").Length - 1);
    }

    [Fact]
    public async Task ChangingCachedSelectionReplacesDemandAndDisablingReleasesIt()
    {
        var enabled = false;
        var preferences = new FrontendPreferences();
        var plans = new ConcurrentQueue<OverlaySubscription[]>();
        var active = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = OverlaySubscriptions.RunAsync(
            _ => Task.FromResult(new OverlaySettingsDocument(3, [new("game", Volatile.Read(ref enabled), "external")])),
            () => Volatile.Read(ref preferences),
            async (plan, token) =>
            {
                plans.Enqueue(plan); Interlocked.Increment(ref active);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally { Interlocked.Decrement(ref active); }
            }, () => { }, TimeSpan.FromMilliseconds(10), stop.Token);
        try
        {
            await Task.Delay(30, stop.Token);
            Assert.Empty(plans);
            Volatile.Write(ref enabled, true);
            await Wait(() => plans.Count == 1 && Volatile.Read(ref active) == 1);
            Volatile.Write(ref preferences, new() { Overlays = new Dictionary<string, FrontendOverlayPresentation>
                { ["game"] = new() { Metrics = ["target.memory"] } } });
            await Wait(() => plans.Count == 2 && Volatile.Read(ref active) == 1);
            Assert.EndsWith("&ids=target.memory", plans.ToArray()[1][0].Path);
            Volatile.Write(ref enabled, false);
            await Wait(() => Volatile.Read(ref active) == 0);
            await Task.Delay(30, stop.Token);
            Assert.Equal(2, plans.Count);
        }
        finally { stop.Cancel(); await run; }
        Assert.Equal(0, active);

        async Task Wait(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(10, stop.Token);
        }
    }
}
