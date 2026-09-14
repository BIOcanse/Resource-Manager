using System.Text.Json;
using ResourceManager.App.Endpoints;

namespace ResourceManager.App.Tests;

public sealed class SubscriptionChannelContractTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/api/metrics/subscribe")]
    [InlineData("/api/metrics/gpu-specialized/subscribe")]
    [InlineData("/api/resource-monitor/subscribe")]
    [InlineData("/api/adapters/resource-manager/scheduling/subscribe")]
    [InlineData("/api/local-system/status/subscribe")]
    [InlineData("/api/device-topology/state/subscribe")]
    [InlineData("/api/cpu/topology/subscribe")]
    [InlineData("/api/cpu/residency/subscribe")]
    [InlineData("/api/optimization/smart/state/subscribe")]
    [InlineData("/api/operations/subscribe")]
    public void ExactLogicalSelectorsAreAccepted(string path)
    {
        var accepted = ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out var selector, out var query);

        Assert.True(accepted);
        Assert.Equal(path, selector);
        Assert.Empty(query);
    }

    [Fact]
    public void QueryValuesAreParsedOnlyAfterExactSelectorAcceptance()
    {
        const string path =
            "/api/metrics/subscribe?ids=cpu.usage&ids=memory.usage&intervalMs=5000";

        var accepted = ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out var selector, out var query);

        Assert.True(accepted);
        Assert.Equal("/api/metrics/subscribe", selector);
        Assert.Equal(2, query["ids"].Count);
        Assert.Equal("cpu.usage", query["ids"][0]);
        Assert.Equal("memory.usage", query["ids"][1]);
        Assert.Equal("5000", Assert.Single(query["intervalMs"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" /api/metrics/subscribe")]
    [InlineData("/api/metrics/subscribe ")]
    [InlineData("http://127.0.0.1/api/metrics/subscribe")]
    [InlineData("//api/metrics/subscribe")]
    [InlineData("\\api\\metrics\\subscribe")]
    [InlineData("/api/metrics/../metrics/subscribe")]
    [InlineData("/api/metrics/%73ubscribe")]
    [InlineData("/api/metrics/subscribe/")]
    [InlineData("/api/metrics/snapshot")]
    [InlineData("/api/metrics/subscribe#fragment")]
    [InlineData("/api/metrics/subscribe?ids=cpu.usage\n")]
    [InlineData("/api/operations/cancel")]
    [InlineData("/api/optimization/smart/diagnostics/decision-snapshot/subscribe")]
    public void AliasedOrNonSubscriptionSelectorsAreRejected(string? path)
    {
        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out _, out _));
    }

    [Fact]
    public void OversizedSelectorIsRejected()
    {
        var path = "/api/metrics/subscribe?ids=" + new string('a', 8_192);

        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .TryParseSubscriptionSelector(path, out _, out _));
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("metric.cpu:1")]
    [InlineData("resource_table-2")]
    [InlineData("A_B.C-D:9")]
    public void RestrictedSubscriptionIdsAreAccepted(string id)
    {
        Assert.True(ResourceManagerEndpointRouteBuilderExtensions
            .IsValidSubscriptionId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu usage")]
    [InlineData("cpu/usage")]
    [InlineData("cpu?usage")]
    [InlineData("cpu占用")]
    public void UnrestrictedSubscriptionIdsAreRejected(string? id)
    {
        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .IsValidSubscriptionId(id));
    }

    [Fact]
    public void OversizedSubscriptionIdIsRejected()
    {
        Assert.False(ResourceManagerEndpointRouteBuilderExtensions
            .IsValidSubscriptionId(new string('a', 129)));
    }

    [Fact]
    public void CallbackFrameContainsOnlySubscriptionIdAndCurrentValue()
    {
        var json = JsonSerializer.Serialize(
            new FrontendSubscriptionChannelFrame<object>(
                "metric.cpu:1",
                new { displayValue = "-" }),
            WebJson);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            ["subscriptionId", "value"],
            document.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
        Assert.Equal(
            "metric.cpu:1",
            document.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal(
            "-",
            document.RootElement
                .GetProperty("value")
                .GetProperty("displayValue")
                .GetString());
    }
}
