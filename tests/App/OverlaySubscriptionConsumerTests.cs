using ResourceManager.NativeUi.Overlay;
using ResourceManager.NativeUi.Preferences;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

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
    public void ItemsWithAChosenSourceSubscribeTheirRawFieldOnTheChosenDeviceInsteadOfThePartAddress()
    {
        // docs/metric_item_source_choice_2026-10-25.md: a chosen source reads only that raw field on that device.
        // Each chosen device is one device=/fields= pair (the backend pairs them in order), never a field on every device.
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
        {
            ["game"] = new()
            {
                Enabled = true,
                Metrics = ["target.fps", "gpu.0.temperature", "gpu.0.hotspot", "cpu.temperature"],
                MetricSources = new Dictionary<string, FrontendOverlayMetricSource>
                {
                    ["gpu.0.temperature"] = new("gpu_amd_edge_temperature_celsius", "gpu|0"),
                    ["gpu.0.hotspot"] = new("gpu_amd_hotspot_temperature_celsius", "gpu|0"),
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
        Assert.EndsWith("&device=cpu%7C0&fields=cpu_amd_tctl_temperature_celsius", fieldsOnly.Path);
        var game = plan.Single(item => item.Path.Contains("softwareId=game"));
        Assert.EndsWith("&ids=target.fps"
            + "&device=gpu%7C0&fields=gpu_amd_edge_temperature_celsius,gpu_amd_hotspot_temperature_celsius"
            + "&device=gpu%7C1&fields=gpu_amd_edge_temperature_celsius", game.Path);
        Assert.DoesNotContain("ids=gpu.0", game.Path);
        Assert.DoesNotContain("ids=cpu.temperature", game.Path);
        foreach (var path in plan.Select(static item => item.Path))
            Assert.Equal(path.Split("&fields=").Length, path.Split("&device=").Length);
    }

    [Fact]
    public async Task OnlyPushedSettingsAndPreferenceWakeupsRecomputeDemand()
    {
        var preferences = new FrontendPreferences();
        var reads = 0;
        var pushes = new SettingsPushes();
        var streams = new DataStreams();
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = OverlaySubscriptions.RunAsync(pushes.Subscribe,
            () => { Interlocked.Increment(ref reads); return Volatile.Read(ref preferences); },
            wake.Reader, streams.ObserveAsync, () => { }, TimeSpan.FromMilliseconds(10), stop.Token);
        try
        {
            await Wait(() => pushes.Opened.Count == 1, stop.Token);
            // Before the first push there is nothing to compute; a wakeup alone does not read anything.
            wake.Writer.TryWrite(true);
            await Task.Delay(50, stop.Token);
            Assert.Equal(0, Volatile.Read(ref reads));
            await pushes.Latest.Writer.WriteAsync(new(1, []), stop.Token);
            await Wait(() => Volatile.Read(ref reads) == 1, stop.Token);
            Assert.Empty(streams.Plans);

            // Nothing samples the preferences: a change is seen only when its wakeup arrives.
            Volatile.Write(ref preferences, new() { Overlays = new Dictionary<string, FrontendOverlayPresentation> { ["game"] = new() { Enabled = true } } });
            await Task.Delay(100, stop.Token);
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Empty(streams.Plans);
            wake.Writer.TryWrite(true);
            await Wait(() => streams.Plans.Count == 1 && Volatile.Read(ref streams.Active) == 1, stop.Token);

            Volatile.Write(ref preferences, new() { Overlays = new Dictionary<string, FrontendOverlayPresentation>
                { ["game"] = new() { Enabled = true, Metrics = ["target.memory"] } } });
            wake.Writer.TryWrite(true);
            await Wait(() => streams.Plans.Count == 2 && Volatile.Read(ref streams.Active) == 1, stop.Token);
            Assert.EndsWith("&ids=target.memory", streams.Plans.ToArray()[1][0].Path);

            // The same pushed selections keep the running data stream.
            await pushes.Latest.Writer.WriteAsync(new(1, []), stop.Token);
            await Wait(() => Volatile.Read(ref reads) == 4, stop.Token);
            Volatile.Write(ref preferences, new());
            wake.Writer.TryWrite(true);
            await Wait(() => Volatile.Read(ref streams.Active) == 0, stop.Token);
            await Task.Delay(50, stop.Token);
            Assert.Equal(2, streams.Plans.Count);
            Assert.Equal(5, Volatile.Read(ref reads));
            Assert.Single(pushes.Opened);
        }
        finally { stop.Cancel(); await run; }
        Assert.Equal(0, streams.Active);
    }

    [Fact]
    public async Task PushedModuleSelectionsDriveInjectedDemandAndAnEndedSubscriptionIsOpenedAgain()
    {
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
            { ["game"] = new() { Mode = "injected", Enabled = true } } };
        var selected = new StartupModuleOperationsDocument(1, [new("game", ["performance-overlay-preload", "performance-overlay-vulkan"])]);
        var reads = 0;
        var clears = 0;
        var pushes = new SettingsPushes();
        var streams = new DataStreams();
        var wake = Channel.CreateBounded<bool>(1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = OverlaySubscriptions.RunAsync(pushes.Subscribe,
            () => { Interlocked.Increment(ref reads); return preferences; },
            wake.Reader, streams.ObserveAsync, () => Interlocked.Increment(ref clears), TimeSpan.FromMilliseconds(10), stop.Token);
        try
        {
            await Wait(() => pushes.Opened.Count == 1, stop.Token);
            await Task.Delay(50, stop.Token);
            Assert.Equal(0, Volatile.Read(ref reads));
            await pushes.Latest.Writer.WriteAsync(selected, stop.Token);
            await Wait(() => streams.Plans.Count == 1 && Volatile.Read(ref streams.Active) == 1, stop.Token);
            await pushes.Latest.Writer.WriteAsync(new(1, []), stop.Token);
            await Wait(() => Volatile.Read(ref streams.Active) == 0, stop.Token);
            await pushes.Latest.Writer.WriteAsync(selected, stop.Token);
            await Wait(() => streams.Plans.Count == 2 && Volatile.Read(ref streams.Active) == 1, stop.Token);

            // An ended subscription releases the data stream, clears, and opens the subscription again;
            // the reopened one waits for its first push instead of reading on a timer.
            var clearsBeforeEnd = Volatile.Read(ref clears);
            pushes.Latest.Writer.Complete();
            await Wait(() => Volatile.Read(ref streams.Active) == 0 && pushes.Opened.Count == 2, stop.Token);
            Assert.True(Volatile.Read(ref clears) > clearsBeforeEnd);
            await Task.Delay(50, stop.Token);
            Assert.Equal(3, Volatile.Read(ref reads));
            await pushes.Latest.Writer.WriteAsync(selected, stop.Token);
            await Wait(() => streams.Plans.Count == 3 && Volatile.Read(ref streams.Active) == 1, stop.Token);

            // A failed subscription is opened again the same way.
            pushes.Latest.Writer.Complete(new HttpRequestException("lost"));
            await Wait(() => Volatile.Read(ref streams.Active) == 0 && pushes.Opened.Count == 3, stop.Token);
        }
        finally { stop.Cancel(); await run; }
        Assert.Equal(0, streams.Active);
    }

    [Fact]
    public async Task SettingsFramesAreReadForTheirOwnIdUntilTheSelectorIsDenied()
    {
        var body = string.Join('\n',
            """{"subscriptionId":"other","value":{"version":1,"software":[]}}""",
            """{"subscriptionId":"startup-modules","value":{"version":1,"software":[{"softwareId":"game","moduleIds":["performance-overlay-preload"]}]}}""",
            """{"subscriptionId":"startup-modules","value":{"statusCode":403,"error":"denied"}}""",
            """{"subscriptionId":"startup-modules","value":{"version":1,"software":[]}}""") + "\n";
        var documents = new List<StartupModuleOperationsDocument>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var document in OverlaySubscriptions.ReadOperationsAsync(
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }),
                CancellationToken.None))
                documents.Add(document);
        });
        var read = Assert.Single(documents);
        var software = Assert.Single(read.Software);
        Assert.Equal("game", software.SoftwareId);
        Assert.Equal(new[] { "performance-overlay-preload" }, software.ModuleIds);
    }

    private static async Task Wait(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

    // Each opened settings subscription is a channel the test pushes frames into and ends.
    private sealed class SettingsPushes
    {
        public readonly ConcurrentQueue<Channel<StartupModuleOperationsDocument>> Opened = new();

        public Channel<StartupModuleOperationsDocument> Latest => Opened.Last();

        public IAsyncEnumerable<StartupModuleOperationsDocument> Subscribe(CancellationToken token)
        {
            var frames = Channel.CreateUnbounded<StartupModuleOperationsDocument>();
            Opened.Enqueue(frames);
            return frames.Reader.ReadAllAsync(token);
        }
    }

    private sealed class DataStreams
    {
        public readonly ConcurrentQueue<OverlaySubscription[]> Plans = new();
        public int Active;

        public async Task ObserveAsync(OverlaySubscription[] plan, CancellationToken token)
        {
            Plans.Enqueue(plan);
            Interlocked.Increment(ref Active);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
