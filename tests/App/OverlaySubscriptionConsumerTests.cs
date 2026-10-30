using ResourceManager.NativeUi.Overlay;
using ResourceManager.NativeUi.Preferences;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;

namespace Resource_Manager_APP.Tests;

public sealed class OverlaySubscriptionConsumerTests
{
    private static FrontendOverlayItem Item(string kind, string id, string source = "auto", int? intervalMs = null,
        string syncPrecision = "mid") => new(kind, id, source, intervalMs, syncPrecision);

    [Fact]
    public void DisabledAndEmptySelectionsHaveNoDataSubscriptionAndEachSoftwareKeepsItsOwnItems()
    {
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
        {
            ["game"] = new() { Enabled = true, Items = [Item("software", "memory.usage"), Item("software", "cpu.usage")] },
            ["empty"] = new() { Enabled = true, Items = [] },
            ["off"] = new() { Enabled = false, Items = [Item("frames", "frames.fps")] }
        } };
        var plan = OverlaySubscriptions.Create(preferences);
        var subscription = Assert.Single(plan);
        Assert.Equal("/api/performance-overlay/subscribe?softwareId=game&intervalMs=500", subscription.Path);
        // The stored order is the request order (and the drawing order); nothing is sorted.
        Assert.Equal(new[] { Item("software", "memory.usage"), Item("software", "cpu.usage") }, subscription.Items);
        Assert.Empty(OverlaySubscriptions.Create(new()));
        var defaultPlan = Assert.Single(OverlaySubscriptions.Create(new() { Overlays = new Dictionary<string, FrontendOverlayPresentation> { ["fresh"] = new() { Enabled = true } } }));
        Assert.Equal(FrontendOverlayItems.PointItems(), defaultPlan.Items);
        Assert.Equal(FrontendPoints.MetricOrder.Length, defaultPlan.Items.Count);
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
    public void EachEnabledSoftwareIsOneEntryWhoseBodyCarriesItsItems()
    {
        // docs/targeted_recording_views_2026-10-30.md §3.9: the path names the software and the interval, the entry's
        // items are the item contract {kind, id, source, intervalMs, syncPrecision}. No ids=, device= or fields=.
        var preferences = new FrontendPreferences { Overlays = new Dictionary<string, FrontendOverlayPresentation>
        {
            ["game"] = new()
            {
                Enabled = true,
                Items = [Item("frames", "frames.fps", syncPrecision: "high"), Item("metric", "gpu.0.temperature",
                    "gpu_amd_edge_temperature_celsius@gpu|0", 1000), Item("software", "cpu.usage", intervalMs: 250, syncPrecision: "low")]
            },
            ["Other Game"] = new() { Enabled = true, Items = [Item("metric", "cpu.temperature")] }
        } };
        var plan = OverlaySubscriptions.Create(preferences);
        Assert.Equal(new[] { "overlay-0", "overlay-1" }, plan.Select(static item => item.Id));
        Assert.Equal("/api/performance-overlay/subscribe?softwareId=game&intervalMs=500", plan[0].Path);
        Assert.Equal("/api/performance-overlay/subscribe?softwareId=Other%20Game&intervalMs=500", plan[1].Path);
        foreach (var path in plan.Select(static item => item.Path))
        {
            Assert.DoesNotContain("ids=", path);
            Assert.DoesNotContain("device=", path);
            Assert.DoesNotContain("fields=", path);
        }
        // BackendServiceSession.OpenSubscriptionStreamAsync posts JsonContent.Create(new { version = 1, subscriptions }),
        // which uses the web defaults.
        var body = System.Text.Json.JsonSerializer.Serialize(new { version = 1, subscriptions = plan },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var expected = """{"version":1,"subscriptions":[{"id":"overlay-0","path":"/api/performance-overlay/subscribe?softwareId=game&intervalMs=500","items":[""" +
            """{"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"high"},""" +
            """{"kind":"metric","id":"gpu.0.temperature","source":"gpu_amd_edge_temperature_celsius@gpu|0","intervalMs":1000,"syncPrecision":"mid"},""" +
            """{"kind":"software","id":"cpu.usage","source":"auto","intervalMs":250,"syncPrecision":"low"}]},""" +
            """{"id":"overlay-1","path":"/api/performance-overlay/subscribe?softwareId=Other%20Game&intervalMs=500","items":[""" +
            """{"kind":"metric","id":"cpu.temperature","source":"auto","intervalMs":null,"syncPrecision":"mid"}]}]}""";
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected),
            System.Text.Json.Nodes.JsonNode.Parse(body)), body);
        // A changed item (not only a changed path) is a different subscription set.
        var changed = OverlaySubscriptions.Create(preferences with { Overlays = new Dictionary<string, FrontendOverlayPresentation>(preferences.Overlays)
        {
            ["game"] = preferences.Overlays["game"] with { Items = [Item("frames", "frames.fps", syncPrecision: "mid")] }
        } });
        Assert.NotEqual(OverlaySubscriptions.Signature(plan), OverlaySubscriptions.Signature(changed));
        Assert.Equal(OverlaySubscriptions.Signature(plan), OverlaySubscriptions.Signature(OverlaySubscriptions.Create(preferences)));
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
                { ["game"] = new() { Enabled = true, Items = [Item("software", "memory.usage")] } } });
            wake.Writer.TryWrite(true);
            await Wait(() => streams.Plans.Count == 2 && Volatile.Read(ref streams.Active) == 1, stop.Token);
            Assert.Equal(new[] { Item("software", "memory.usage") }, streams.Plans.ToArray()[1][0].Items);

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
