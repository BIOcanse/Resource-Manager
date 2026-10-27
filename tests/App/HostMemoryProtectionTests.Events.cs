using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostMemoryProtectionTests
{
    [Fact]
    public async Task SubscribeReplaysAndPublishesEachChangeOnceAndHooksShareItsAction()
    {
        await using var fixture = await Fixture.StartAsync();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var publications = Channel.CreateUnbounded<HostMemoryProtectionPublication>();
        var observed = new List<HostMemoryProtectionPublication>();
        var pump = Pump();
        var replay = await publications.Reader.ReadAsync(lifetime.Token);
        Assert.Empty(replay.Events);
        Assert.False(replay.Status.AtOrAboveThreshold);
        await fixture.CreatePoint();
        await ReadEvent("normal-running-point-created");
        using (var enabled = await fixture.Patch(new { enabled = true })) enabled.EnsureSuccessStatusCode();
        Assert.True((await ReadEvent("active")).Status.Active);
        observed.RemoveAll(item => item.Events.Any(change => change.Kind == "inactive"));
        fixture.Executor.SliceBytes = 8193;
        // The wait endpoint uses the same hook compilation, scopes and source as the channel.
        var wait = fixture.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("memory-protection action"), lifetime.Token);
        await Until(() => fixture.Source.Active == 3); // protection, publication, hook
        fixture.Source.Publish(960, 96, [Process(20, "200", 800)]);
        Assert.True((await ReadEvent("threshold-reached")).Status.AtOrAboveThreshold);
        await ReadEvent("action-started");
        var action = await ReadEvent("action");
        await ReadEvent("reserve-remaining-changed");
        using var response = await wait;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var value = document.RootElement.GetProperty("value");
        var wireOutput = Environment.GetEnvironmentVariable("RM_MEMORY_PROTECTION_WIRE_REPORT");
        if (wireOutput is not null) await File.WriteAllTextAsync(wireOutput + ".hook.json", document.RootElement.GetRawText());
        Assert.Equal("met", value.GetProperty("state").GetString());
        Assert.Equal("action", value.GetProperty("event").GetProperty("kind").GetString());
        Assert.Equal(action.Events.Single(item => item.Kind == "action").Action!.ActionId,
            value.GetProperty("event").GetProperty("action").GetProperty("actionId").GetString());
        Assert.Equal("processes.read", Assert.Single(response.Headers.GetValues("X-Resource-Manager-Required-Scopes")));
        fixture.Source.Publish(200, 20, []);
        Assert.False((await ReadEvent("below-threshold")).Status.AtOrAboveThreshold);
        using (var disabled = await fixture.Patch(new { enabled = false })) disabled.EnsureSuccessStatusCode();
        Assert.Equal("disabled", (await ReadEvent("inactive")).Status.InactiveReason);
        await lifetime.CancelAsync();
        await pump;

        async Task Pump()
        {
            try { await foreach (var item in fixture.Protection.SubscribeAsync(lifetime.Token)) publications.Writer.TryWrite(item); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        async Task<HostMemoryProtectionPublication> ReadEvent(string kind)
        {
            if (observed.FirstOrDefault(item => item.Events.Any(change => change.Kind == kind)) is { } seen) return seen;
            while (true)
            {
                var item = await publications.Reader.ReadAsync(lifetime.Token);
                observed.Add(item);
                if (item.Events.Any(change => change.Kind == kind)) return item;
            }
        }
    }

    [Fact]
    public async Task PublicChannelReplaysStatusAndSuppressesIdenticalDeliveries()
    {
        await using var fixture = await Fixture.StartAsync();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "protection", path = "/api/memory-protection/subscribe" } } }) };
        await using var stream = new ConditionSubscriptionTests.Stream(request,
            fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token), lifetime);
        var replay = await stream.ReadFrameAsync();
        Assert.Equal("protection", replay.GetProperty("subscriptionId").GetString());
        Assert.False(replay.GetProperty("value").GetProperty("atOrAboveThreshold").GetBoolean());
        var delivered = fixture.Source.Deliveries;
        fixture.Source.Publish(200, 20, []);
        await Until(() => fixture.Source.Deliveries > delivered);
        fixture.Source.Publish(960, 96, []);
        var next = (await stream.ReadFrameAsync()).GetProperty("value");
        Assert.True(next.GetProperty("atOrAboveThreshold").GetBoolean());
        Assert.Equal(960UL, next.GetProperty("currentCommit").GetProperty("currentBytes").GetUInt64());
        var output = Environment.GetEnvironmentVariable("RM_MEMORY_PROTECTION_WIRE_REPORT");
        if (output is not null)
        {
            await File.WriteAllTextAsync(output, replay.GetRawText() + Environment.NewLine + next.GetRawText());
            fixture.Source.Publish(200, 20, [Process(10, "100", 100) with { ParentProcessId = 17,
                ExecutablePath = @"C:\fixture\example.exe", SoftwareId = "example" }]);
            await fixture.CreatePoint(ensureReserve: false);
            await File.WriteAllTextAsync(output + ".point.json", await fixture.Client.GetStringAsync("/api/memory-protection/normal-running-point"));
        }
    }

    private sealed class FaultingReserve : IHostMemoryProtectionReserve
    {
        private readonly HostMemoryDiskReserves inner = new();
        public volatile bool Failed;
        public HostMemoryReserveStatus ReadStatus() => Failed ? inner.ReadStatus() with { Built = false, Reason = "reserve-release-failed" } : inner.ReadStatus();
        public HostMemoryReserveStatus Prepare(IReadOnlyList<HostMemoryReserveSettings> entries) => inner.Prepare(entries);
        public HostMemoryReserveStatus Rebuild() => inner.Rebuild();
        public IHostMemoryProtectionReserveLease? TryAcquire() => Failed ? null : inner.TryAcquire();
        public void Dispose() => inner.Dispose();
    }
}
