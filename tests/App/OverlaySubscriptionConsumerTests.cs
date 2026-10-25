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
            ["game"] = new() { Enabled = true, Metrics = ["target.memory", "target.cpu"] },
            ["empty"] = new() { Enabled = true, Metrics = [] }
        } };
        var plan = OverlaySubscriptions.Create(preferences);
        var subscription = Assert.Single(plan);
        Assert.Contains("softwareId=game", subscription.Path);
        Assert.Contains("ids=target.cpu&ids=target.memory", subscription.Path);
        Assert.DoesNotContain("target.fps", subscription.Path);
        Assert.Empty(OverlaySubscriptions.Create(new()));
        var defaultPlan = Assert.Single(OverlaySubscriptions.Create(new() { Overlays = new Dictionary<string, FrontendOverlayPresentation> { ["fresh"] = new() { Enabled = true } } }));
        Assert.Equal(FrontendPoints.MetricOrder.Count(), defaultPlan.Path.Split("&ids=").Length - 1);
    }

    [Fact]
    public void InjectedDemandUsesAuthoritativeModuleSelectionRatherThanCachedEnablement()
    {
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { Mode = "injected", Enabled = true } } };
        Assert.Empty(OverlaySubscriptions.Create(preferences, new(1, [])));
        var operations = new StartupModuleOperationsDocument(1, [new("game", ["performance-overlay-preload", "performance-overlay-vulkan"])]);
        Assert.Single(OverlaySubscriptions.Create(preferences, operations));
    }

    [Fact]
    public void ItemsWithAChosenSourceSubscribeTheirRawFieldInsteadOfThePartAddress()
    {
        // docs/metric_item_source_choice_2026-10-25.md: a chosen source reads only that raw field.
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
        {
            ["game"] = new()
            {
                Enabled = true,
                Metrics = ["target.fps", "gpu.0.temperature", "cpu.temperature"],
                MetricSources = new Dictionary<string, FrontendOverlayMetricSource>
                {
                    ["gpu.0.temperature"] = new("gpu_amd_edge_temperature_celsius", "gpu|0"),
                    ["cpu.temperature"] = new("gpu_amd_edge_temperature_celsius", "gpu|1")
                }
            },
            ["fields-only"] = new()
            {
                Enabled = true,
                Metrics = ["cpu.temperature"],
                MetricSources = new Dictionary<string, FrontendOverlayMetricSource>
                    { ["cpu.temperature"] = new("cpu_amd_tctl_temperature_celsius", "cpu|0") }
            }
        } };
        var plan = OverlaySubscriptions.Create(preferences);
        Assert.Equal(2, plan.Length);
        var fieldsOnly = plan.Single(item => item.Path.Contains("softwareId=fields-only"));
        Assert.DoesNotContain("&ids=", fieldsOnly.Path);
        Assert.EndsWith("&fields=cpu_amd_tctl_temperature_celsius", fieldsOnly.Path);
        var game = plan.Single(item => item.Path.Contains("softwareId=game"));
        Assert.EndsWith("&ids=target.fps&fields=gpu_amd_edge_temperature_celsius", game.Path);
        Assert.DoesNotContain("temperature&ids", game.Path);
        Assert.DoesNotContain("ids=gpu.0.temperature", game.Path);
        Assert.DoesNotContain("ids=cpu.temperature", game.Path);
    }

    [Fact]
    public async Task ChangingCachedSelectionReplacesDemandAndDisablingReleasesIt()
    {
        var preferences = new FrontendPreferences();
        var plans = new ConcurrentQueue<OverlaySubscription[]>();
        var active = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = OverlaySubscriptions.RunAsync(
            _ => Task.FromResult(new StartupModuleOperationsDocument(1, [])),
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
            Volatile.Write(ref preferences, new() { Overlays = new Dictionary<string, FrontendOverlayPresentation> { ["game"] = new() { Enabled = true } } });
            await Wait(() => plans.Count == 1 && Volatile.Read(ref active) == 1);
            Volatile.Write(ref preferences, new() { Overlays = new Dictionary<string, FrontendOverlayPresentation>
                { ["game"] = new() { Enabled = true, Metrics = ["target.memory"] } } });
            await Wait(() => plans.Count == 2 && Volatile.Read(ref active) == 1);
            Assert.EndsWith("&ids=target.memory", plans.ToArray()[1][0].Path);
            Volatile.Write(ref preferences, new());
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
