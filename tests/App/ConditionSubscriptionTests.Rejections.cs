using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Fact]
    public async Task SourcePeriodAdmissionRejectsOnlyItsOwningHook()
    {
        var metrics = new MetricSource { RejectOwner = "monitoring-hook:below-minimum" };
        await using var host = await Host.StartAsync(metrics);
        await using var stream = await host.OpenAsync([Hook(Rule(), "below-minimum"), Hook(Rule())]);
        var rejected = await stream.ReadFrameAsync();
        Assert.Equal("below-minimum", rejected.GetProperty("hookId").GetString());
        Assert.Equal(400, rejected.GetProperty("value").GetProperty("statusCode").GetInt32());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 0);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 1);
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
        Assert.Equal(1, metrics.Acquired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidFrequencyRejectsOnlyItsHook(int interval)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        await using var stream = await host.OpenAsync([Hook(Rule(), "bad-frequency", interval), Hook(Rule())]);
        Assert.Equal("bad-frequency", (await stream.ReadFrameAsync()).GetProperty("hookId").GetString());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 0);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 1);
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
        Assert.Equal(1, metrics.Acquired);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("numeric-string")]
    [InlineData("non-finite")]
    public async Task StrictRuleDecodingKeepsOrdinarySiblingSubscriptionRunning(string malformed)
    {
        var raw = JsonSerializer.Serialize(Hook(Rule()));
        raw = malformed switch
        {
            "duplicate" => raw.Replace("\"threshold\":100", "\"threshold\":100,\"threshold\":200"),
            "numeric-string" => raw.Replace("\"threshold\":100", "\"threshold\":\"100\""),
            _ => raw.Replace("\"threshold\":100", "\"threshold\":1e999")
        };
        using var hook = JsonDocument.Parse(raw);
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        await using var stream = await host.OpenAsync([hook.RootElement.Clone()],
            [new { id = "ordinary", path = MemorySubscription }]);
        Assert.Equal(400, (await stream.ReadAsync()).GetProperty("statusCode").GetInt32());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await metrics.PublishAsync(Memory(Epoch, 950));
        var normal = await stream.ReadFrameAsync();
        Assert.Equal("ordinary", normal.GetProperty("subscriptionId").GetString());
        Assert.False(normal.TryGetProperty("hookId", out _));
        Assert.Equal(1, metrics.Acquired);
    }

    [Fact]
    public async Task RevokingKeyEndsSilentHookAndReleasesItsSubscription()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        await using var stream = await host.OpenAsync([Hook(Rule())]);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await host.RevokeAsync());
        await UntilAsync(() => metrics.Active == 0);
        Assert.Equal(1, metrics.Acquired);
    }
}
