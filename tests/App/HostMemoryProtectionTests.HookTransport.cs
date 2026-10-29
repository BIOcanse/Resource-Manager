using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostMemoryProtectionTests
{
    [Fact]
    public async Task EventExpansionRoundTripsUnchangedThroughTheSameHookChannel()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        JsonElement expanded;
        await using (var initial = Open(new { id = "active", condition = "memory-protection active" }))
            expanded = (await initial.ReadAsync()).GetProperty("hook").Clone();
        await Until(() => fixture.Source.Active == 0);
        Assert.False(expanded.TryGetProperty("intervalMs", out _));
        Assert.False(expanded.TryGetProperty("rule", out _));
        await using var roundtrip = Open(expanded);
        Assert.Equal("expanded", (await roundtrip.ReadAsync()).GetProperty("state").GetString());
        await Until(() => fixture.Source.Active == 1);
        using (var enabled = await fixture.Patch(new { enabled = true })) enabled.EnsureSuccessStatusCode();
        var occurrence = await roundtrip.ReadAsync();
        Assert.Equal("met", occurrence.GetProperty("state").GetString());
        Assert.Equal("active", occurrence.GetProperty("event").GetProperty("kind").GetString());

        ConditionSubscriptionTests.Stream Open(object hook)
        {
            var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(new { version = 1, hooks = new[] { hook } }) };
            return new(request, fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token), lifetime);
        }
    }

    [Fact]
    public async Task ProtectionSubscriptionAndEventWaitRequireProcessReadScope()
    {
        await using var fixture = await Fixture.StartAsync(scopes: [AccessKeyScopes.MetricsRead]);
        using var channel = await fixture.Client.PostAsJsonAsync("/api/subscriptions/stream", new
        {
            version = 1, subscriptions = new[] { new { id = "protection", path = "/api/memory-protection/subscribe" } }
        });
        Assert.Equal(HttpStatusCode.Forbidden, channel.StatusCode);
        using var wait = await fixture.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("memory-protection action"));
        Assert.Equal(HttpStatusCode.Forbidden, wait.StatusCode);
        Assert.Equal(0, fixture.Source.Active);
        Assert.Empty(fixture.Executor.Opened);
    }

    [Fact]
    public async Task BufferedThresholdDeliveryFromBeforeActionFinishedCannotTriggerAnotherAction()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.CreatePoint();
        fixture.Executor.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var enabled = await fixture.Patch(new { enabled = true });
        await Until(() => fixture.Source.Active == 1);
        fixture.Source.Publish(960, 96, [Process(20, "200", 800)]);
        await Until(() => fixture.Executor.Executed.Contains(20));
        var deliveries = fixture.Source.Deliveries;
        fixture.Source.Publish(970, 97, [Process(21, "300", 700)]);
        fixture.Executor.Block.SetResult();
        await Until(async () => (await fixture.Status()).Actions.Count == 1 && fixture.Source.Deliveries > deliveries);
        Assert.Equal(20, Assert.Single(fixture.Executor.Executed));
        fixture.Source.Publish(980, 98, [Process(22, "400", 600)]);
        await Until(async () => (await fixture.Status()).Actions.Count == 2);
        Assert.Equal(new[] { 20, 22 }, fixture.Executor.Executed.ToArray());
    }

    [Fact]
    public void EventHookSizeDoesNotGrowWithTheRecordList()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var record = new HostMemoryActionRecord(DateTimeOffset.UnixEpoch, 20, "200", "fixture.exe", 100, 900, 800,
            [@"C:\fixture\dump.dmp"], 1024, "terminated", null, new HostMemorySelectionBasis("process-growth", 800, null, 1000))
        { ActionId = "action-1" };
        var status = new HostMemoryProtectionStatus(true, 90, 1, new HostMemoryReserveStatus([], 0, false, null, 0), 900, 0,
            new HostMemoryCommit(960, 1000, 96), null, null, [], 16384, []) { AtOrAboveThreshold = true };
        var change = new HostMemoryProtectionEvent("action", Action: record);
        // A status holding the largest record capacity serializes to the same event as one holding no records.
        var empty = Serialize(status);
        var full = Serialize(status with { Actions = Enumerable.Repeat(record, 16384).ToArray() });
        Assert.Equal(empty, full);
        using var document = JsonDocument.Parse(full);
        var value = document.RootElement.GetProperty("value");
        Assert.False(value.GetProperty("status").TryGetProperty("actions", out _));
        Assert.True(value.GetProperty("status").GetProperty("atOrAboveThreshold").GetBoolean());
        Assert.Equal("action-1", value.GetProperty("event").GetProperty("action").GetProperty("actionId").GetString());

        string Serialize(HostMemoryProtectionStatus current) => JsonSerializer.Serialize(new MonitoringHookFrame<MonitoringEventHookEvent>(
            "hook", new("met", change, HostMemoryProtectionLiveStatus.From(current))), options);
    }
}
