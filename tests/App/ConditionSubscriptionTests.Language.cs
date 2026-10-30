using System.Net;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Fact]
    public async Task ExpansionCanBeSubmittedUnchangedToThePublicLowLevelChannel()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        JsonElement hook;
        await using (var simple = await host.OpenAsync([new { id = "roundtrip", condition = "memory.free < 1GiB for 1s every 100ms" }]))
        {
            hook = (await simple.ReadAsync()).GetProperty("hook").Clone();
            await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await UntilAsync(() => metrics.Active == 0);
        await using var lowLevel = await host.OpenAsync([hook]);
        await UntilAsync(() => metrics.Active == 1);
        await metrics.PublishAsync(Memory(Epoch, 950)); host.Clock.Advance(1); await metrics.PublishAsync(Memory(Epoch, 950));
        var met = await lowLevel.ReadFrameAsync();
        Assert.Equal("roundtrip", met.GetProperty("hookId").GetString());
        Assert.Equal("met", met.GetProperty("value").GetProperty("state").GetString());
        Assert.Equal(2, metrics.Acquired);
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("intervalMs")]
    [InlineData("unexpected")]
    public async Task TextAndLowLevelFieldsCannotBeAmbiguouslyMixed(string field)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        var input = new Dictionary<string, object> { ["id"] = "bad", ["condition"] = "memory.free < 1GiB for 1s", [field] = field == "rule" ? Rule() : 1000 };
        await using var channel = await host.OpenAsync([input, new { id = "good", condition = "memory.free < 1GiB for 1s" }]);
        Assert.Equal(400, (await channel.ReadAsync()).GetProperty("statusCode").GetInt32());
        Assert.Equal("expanded", (await channel.ReadAsync()).GetProperty("state").GetString());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, metrics.Acquired);
    }

    [Fact]
    public async Task ProcessShorthandCannotReadIdentityBeforeProcessScopeAuthorization()
    {
        var resources = new ResourceSource { Foundations = Foundations() };
        await using var host = await Host.StartAsync(new MetricSource(), resources,
            scopes: [ResourceManager.Shared.Runtime.TrustedAccess.AccessKeyScopes.MetricsRead]);
        using var response = await host.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("process 22 private > 1GiB for 1s"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, resources.FoundationReads); Assert.Equal(0, resources.Acquired);
    }

    [Fact]
    public async Task VocabularyIsPublicCompleteAndDoesNotSubscribe()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };
        using var response = await anonymous.GetAsync("/api/hooks/vocabulary");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var vocabulary = document.RootElement;
        Assert.Equal(1000, vocabulary.GetProperty("defaults").GetProperty("intervalMs").GetInt32());
        Assert.Equal(0, vocabulary.GetProperty("defaults").GetProperty("topOwners").GetInt32());
        Assert.Contains(vocabulary.GetProperty("names").EnumerateArray(), item => item.GetProperty("name").GetString() == "memory.free"
            && item.GetProperty("quantity").GetString() == "remaining");
        Assert.Contains(vocabulary.GetProperty("units").EnumerateArray().SelectMany(item => item.GetProperty("spellings").EnumerateArray()),
            item => item.GetProperty("spelling").GetString() == "GiB" && item.GetProperty("scale").GetDouble() == 1073741824);
        Assert.Equal("X-Resource-Manager-Key", vocabulary.GetProperty("credentials").GetProperty("keyHeader").GetString());
        Assert.Contains("elevated", vocabulary.GetProperty("credentials").GetProperty("keylessCaller").GetString());
        Assert.Equal(0, metrics.Acquired);
    }

    [Fact]
    public async Task ProcessContractExampleWaitsForFreshIdentityThenUsesTheOriginalProcessHook()
    {
        var foundation = Foundations();
        var rows = foundation.InventoryProcesses.Where(row => row.ProcessId == 22).Select(row => row with { ProcessId = 1234 }).ToArray();
        var resources = new ResourceSource { Foundations = Foundations(reused: true) };
        await using var host = await Host.StartAsync(new MetricSource(), resources);
        await using var channel = await host.OpenAsync([new { id = "process", condition = "process 1234 private > 4GiB for 30s every 2s" }]);
        var expansion = channel.ReadAsync();
        await resources.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("monitoring-condition:process:process-identity", resources.Owner);
        Assert.Equal(0, resources.FoundationReads);
        Assert.False(expansion.IsCompleted);
        await resources.PublishAsync(IdentityPublication(Epoch));
        Assert.False(expansion.IsCompleted);
        Assert.Equal(0, resources.FoundationReads);
        resources.Foundations = foundation with { InventoryProcesses = rows, AttributionProcesses = rows };
        await resources.PublishAsync(IdentityPublication(DateTimeOffset.UtcNow));
        var expanded = (await expansion.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("hook");
        Assert.Equal("202", expanded.GetProperty("rule").GetProperty("startKey").GetString());
        Assert.Equal(2000, expanded.GetProperty("intervalMs").GetInt32());
        Assert.Equal(Math.BitIncrement(4294967296d), expanded.GetProperty("rule").GetProperty("threshold").GetDouble());
        await UntilAsync(() => resources.Active == 1 && resources.Acquired == 2);
        const string field = "process_all_private_commit_actual";
        var current = new ResourceBreakdownSnapshot(Epoch, [])
        { ProcessFields = new(Epoch, null, [ProcessMetricFieldRegistry.Find(field)!],
            [new(1234, "202", [new(field, new ResourceManager.App.Domain.Metrics.MetricInteger(5UL << 30), null)])])
            { Sources = [new("windows.process.query", Epoch, null)] } };
        await resources.PublishAsync(current); host.Clock.Advance(30); await resources.PublishAsync(current);
        Assert.Equal("met", (await channel.ReadAsync()).GetProperty("state").GetString());
        Assert.Equal(2, resources.Acquired);

        static ResourceBreakdownSnapshot IdentityPublication(DateTimeOffset at) => new(at, [])
        { Datasets = [new(SamplingDatasetIds.ProcessInventory, "ready", 1, 1, at, at, at, null, null, null)] };
    }

    [Fact]
    public async Task DisconnectingDuringIdentityLookupReleasesItsSingleFetch()
    {
        var resources = new ResourceSource { Foundations = Foundations() };
        await using var host = await Host.StartAsync(new MetricSource(), resources);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = host.Client.GetAsync("/api/hooks/wait?condition="
            + Uri.EscapeDataString("process 22 private > 1GiB for 1s"), cancellation.Token);
        await resources.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, resources.FoundationReads);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await UntilAsync(() => resources.Active == 0);
        Assert.Equal(0, resources.FoundationReads);
    }

    [Fact]
    public async Task WaitAndChannelAdvertiseAndEnforceTheSameCredentialRequirement()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };
        const string condition = "memory.free < 1GiB for 60s";
        anonymous.DefaultRequestHeaders.Add("X-Resource-Manager-Key", "invalid-key");
        using var wait = await anonymous.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString(condition));
        using var channel = await anonymous.PostAsJsonAsync("/api/subscriptions/stream", new { version = 1, hooks = new[] { new { id = "x", condition } } });
        Assert.Equal(HttpStatusCode.Unauthorized, wait.StatusCode); Assert.Equal(channel.StatusCode, wait.StatusCode);
        Assert.Equal("X-Resource-Manager-Key", Assert.Single(wait.Headers.GetValues("X-Resource-Manager-Key-Header")));
        Assert.Equal(0, metrics.Acquired);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    public async Task WaitQueryIsStrict(string form)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        var query = "?condition=" + Uri.EscapeDataString("memory.free < 1GiB for 60s");
        var uri = "/api/hooks/wait" + (form == "missing" ? "" : query + (form == "extra" ? "&other=1" : "&condition=cpu"));
        using var response = await host.Client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, metrics.Acquired);
    }

    [Fact]
    public async Task RevocationCancelsOneShotHookAndObjectEndIsReportedAsConflict()
    {
        var metrics = new MetricSource();
        await using (var host = await Host.StartAsync(metrics))
        {
            var waiting = host.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("memory.free < 1GiB for 60s"));
            await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await host.RevokeAsync());
            using var revoked = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
            await UntilAsync(() => metrics.Active == 0);
        }
        var resources = new ResourceSource { Foundations = Foundations(reused: true) };
        await using var endedHost = await Host.StartAsync(new MetricSource(), resources);
        var pending = endedHost.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("process 22:202 private > 1GiB for 1s"));
        await resources.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); await resources.PublishAsync(ProcessValues(Epoch));
        using var ended = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, ended.StatusCode);
        await UntilAsync(() => resources.Active == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimpleMemoryExampleEchoesTheCompleteHookAndUsesOriginalHold(bool wait)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = wait ? host.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("memory.free < 1GiB for 60s"), timeout.Token) : null;
        await using var channel = wait ? null : await host.OpenAsync([new { id = "simple", condition = "memory.free < 1GiB for 60s" }]);
        if (!wait)
        {
            var expansion = await channel!.ReadAsync();
            Assert.Equal("expanded", expansion.GetProperty("state").GetString());
            AssertExpandedMemory(expansion.GetProperty("hook"), "simple");
        }
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(1), metrics.Interval);
        await metrics.PublishAsync(Memory(Epoch, 950));
        host.Clock.Advance(59); await metrics.PublishAsync(Memory(Epoch, 950));
        if (wait) Assert.False(pending!.IsCompleted);
        host.Clock.Advance(1); await metrics.PublishAsync(Memory(Epoch, 950));
        if (wait)
        {
            using var response = await pending!;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertExpandedMemory(result.RootElement.GetProperty("hook"), "condition");
            Assert.Equal("met", result.RootElement.GetProperty("value").GetProperty("state").GetString());
            var encoded = Assert.Single(response.Headers.GetValues("X-Resource-Manager-Expanded-Hook"));
            using var header = JsonDocument.Parse(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded)));
            AssertExpandedMemory(header.RootElement, "condition");
            await UntilAsync(() => metrics.Active == 0);
        }
        else Assert.Equal("met", (await channel!.ReadAsync()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task CommitExampleHoldsOnlyTheMachineMetricAndFetchesOwnersOnceWhenMet()
    {
        var metrics = new MetricSource();
        var resources = new ResourceSource();
        await using var host = await Host.StartAsync(metrics, resources);
        await using var stream = await host.OpenAsync([new { id = "commit", condition = "commit >= 95% for 60s top 8" }]);
        var hook = (await stream.ReadAsync()).GetProperty("hook");
        Assert.Equal(8, hook.GetProperty("rule").GetProperty("topOwners").GetInt32());
        Assert.Equal("virtualMemory.usage", hook.GetProperty("rule").GetProperty("metricId").GetString());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("monitoring-hook:commit", metrics.Owner);
        HardwareMetricSnapshot Commit() => Memory(Epoch, 950) with
        { Items = new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase)
          { ["virtualMemory.usage"] = new("virtualMemory.usage", 950d, "B", Total: 1000)
              { Status = SamplingObservationStatus.Current, ObservedAt = Epoch } } };
        var fresh = DateTimeOffset.UtcNow.AddDays(1);
        var owners = Breakdown(fresh) with
        { Bars = Breakdown(fresh).Bars.Select(bar => bar with { MetricId = "virtualMemory.usage" }).ToArray(),
          Datasets = [new(SamplingDatasetIds.ForProcessMetric("virtualMemory.usage"), "ready", 1, 1, fresh, fresh, fresh, null, null, null)] };
        await metrics.PublishAsync(Commit());
        Assert.Equal(0, resources.Acquired);
        await DeliverMetWithOwnersAsync(host, metrics, Commit(), 60, resources, owners, 1);
        var met = await stream.ReadAsync();
        Assert.Equal("met", met.GetProperty("state").GetString());
        Assert.Equal(95, met.GetProperty("reading").GetDouble());
        Assert.Equal(2, met.GetProperty("owners").GetArrayLength());
        await UntilAsync(() => resources.Active == 0);
        Assert.Equal(1, resources.Acquired);
        Assert.Equal("monitoring-hook:commit:top-owners", resources.Owner);
    }

    [Theory]
    [InlineData("memory.free < 1GB for 60s", 15)]
    [InlineData("memory.free < 1GiB for 0s", 23)]
    public async Task InvalidStatementsHaveTheSameExactErrorInWaitAndChannel(string condition, int position)
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        using var response = await host.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString(condition));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(position, error.RootElement.GetProperty("position").GetInt32());
        await using var channel = await host.OpenAsync([new { id = "bad", condition }, new { id = "good", condition = "memory.free < 1GiB for 60s" }]);
        var rejection = await channel.ReadAsync();
        Assert.Equal(position, rejection.GetProperty("position").GetInt32());
        Assert.Equal(error.RootElement.GetProperty("reason").GetString(), rejection.GetProperty("reason").GetString());
        Assert.Equal("expanded", (await channel.ReadAsync()).GetProperty("state").GetString());
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, metrics.Acquired);
    }

    [Fact]
    public async Task DisconnectingWaitDisposesItsOriginalSubscription()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = host.Client.GetAsync("/api/hooks/wait?condition=" + Uri.EscapeDataString("memory.free < 1GiB for 60s"), cancellation.Token);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await UntilAsync(() => metrics.Active == 0);
    }

    private static void AssertExpandedMemory(JsonElement hook, string id)
    {
        Assert.Equal(id, hook.GetProperty("id").GetString());
        Assert.Equal(1000, hook.GetProperty("intervalMs").GetInt32());
        var rule = hook.GetProperty("rule");
        Assert.Equal("machine", rule.GetProperty("binding").GetString());
        Assert.Equal("memory.usage", rule.GetProperty("metricId").GetString());
        Assert.Equal("remaining", rule.GetProperty("quantity").GetString());
        Assert.Equal("below", rule.GetProperty("comparison").GetString());
        Assert.Equal(1073741824, rule.GetProperty("threshold").GetDouble());
        Assert.Equal(60, rule.GetProperty("holdSeconds").GetDouble());
        Assert.Equal(0, rule.GetProperty("topOwners").GetInt32());
    }
}
