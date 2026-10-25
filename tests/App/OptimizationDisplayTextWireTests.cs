using System.Text.Json;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationDisplayTextWireTests
{
    [Fact]
    public void ProductJsonContextCarriesExactCoordinatorAndProcessFactsWithoutDisplaySlots()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var coordinator = new HostManagerCoordinatorFacts(ulong.MaxValue, 187, 0, 0x200000090);
        var state = HostManagerRollbackStateDocument.Empty with
        {
            Coordinator = coordinator,
            AppliedPlacements = [new("process:42:133700000000000000", "example", "cpu",
                [new("cpu_affinity", "record", new Dictionary<string, string> { ["runtimeActionResult"] = "internal recovery diagnostic" })], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)
            { ProcessId = 42, ProcessStartKey = "133700000000000000", ProcessName = "example" }]
        };
        var projected = HostManagerSmartStateWireProjection.Project(state);
        var wire = JsonSerializer.SerializeToElement(projected, options);
        Assert.DoesNotContain("internal recovery diagnostic", wire.GetRawText());
        Assert.Equal("internal recovery diagnostic", state.AppliedPlacements[0].Records[0].Metadata!["runtimeActionResult"]);
        Assert.False(wire.TryGetProperty("message", out _));
        Assert.Equal("18446744073709551615", wire.GetProperty("coordinator").GetProperty("revision").GetString());
        Assert.Equal("8589934736", wire.GetProperty("coordinator").GetProperty("reasonMask").GetString());
        var placement = Assert.Single(wire.GetProperty("appliedPlacements").EnumerateArray());
        Assert.False(placement.TryGetProperty("displayName", out _));
        Assert.False(placement.GetProperty("records")[0].TryGetProperty("metadata", out _));
        var frame = JsonSerializer.SerializeToElement(new FrontendSubscriptionChannelFrame<HostManagerSmartStateWireSnapshot>("state", projected), options);
        Assert.DoesNotContain("internal recovery diagnostic", frame.GetRawText());
        Assert.Equal(42, placement.GetProperty("processId").GetInt32());
        Assert.Equal("133700000000000000", placement.GetProperty("processStartKey").GetString());
        Assert.Equal("example", placement.GetProperty("processName").GetString());
        var status = JsonSerializer.SerializeToElement(new HostManagerSmartCoordinatorStatus("smart", true,
            null, null, 187, 1, null, coordinator), options);
        Assert.False(status.TryGetProperty("message", out _));
        var statusFrame = JsonSerializer.SerializeToElement(new FrontendSubscriptionChannelFrame<HostManagerSmartCoordinatorStatus>("status",
            new("smart", true, null, null, 187, 1, null, coordinator)), options);
        Assert.False(statusFrame.GetProperty("value").TryGetProperty("message", out _));
        Assert.Equal(wire.GetProperty("coordinator").GetRawText(), status.GetProperty("coordinator").GetRawText());

        var report = new OptimizationReportItem("r", "background_high_usage", "active", "warning", "high",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            OptimizationActivityContext.Unknown(), new("software", "software:example", "Example", "example", "Example", "other", null, [], [], null),
            new("memory", 1, 1, 1, "B", 1, 1, 1), [new("dismiss", "dismiss", true)]);
        var reportWire = JsonSerializer.SerializeToElement(report, options);
        Assert.False(reportWire.TryGetProperty("title", out _));
        Assert.False(reportWire.TryGetProperty("message", out _));
        Assert.False(reportWire.GetProperty("target").TryGetProperty("displayName", out _));
        Assert.False(reportWire.GetProperty("evidence").TryGetProperty("details", out _));
        var action = Assert.Single(reportWire.GetProperty("suggestedActions").EnumerateArray());
        Assert.False(action.TryGetProperty("label", out _));
        Assert.False(action.TryGetProperty("disabledReason", out _));
    }
}

