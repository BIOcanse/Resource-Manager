using System.Text.Json;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

public sealed class FrontendBackendSessionProjectionTests
{
    [Fact]
    public void ReadyProjectionPublishesOnlyOpaqueConnectionState()
    {
        var projection = FrontendBackendSessionProjection.CreateReady(
            publicationRevision: 9);

        using var document = JsonDocument.Parse(projection.ToJson());
        var root = document.RootElement;
        Assert.Equal("host.backend-session", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("9", root.GetProperty("publicationRevision").GetString());
        Assert.Equal("ready", root.GetProperty("state").GetString());
        Assert.Equal("session:9", root.GetProperty("epoch").GetString());

        var json = projection.ToJson();
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("challenge", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executablePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("buildVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backendGeneration", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(root.TryGetProperty("backend", out _));
        Assert.False(root.TryGetProperty("reasonCode", out _));
    }

    [Fact]
    public void UnavailableProjectionContainsOnlyStableReasonCode()
    {
        var projection = FrontendBackendSessionProjection.CreateUnavailable(
            publicationRevision: 10,
            reasonCode: "backend-unavailable");

        using var document = JsonDocument.Parse(projection.ToJson());
        var root = document.RootElement;
        Assert.Equal("unavailable", root.GetProperty("state").GetString());
        Assert.Equal("10", root.GetProperty("publicationRevision").GetString());
        Assert.Equal("backend-unavailable", root.GetProperty("reasonCode").GetString());
        Assert.False(root.TryGetProperty("epoch", out _));
        Assert.False(root.TryGetProperty("backend", out _));
    }
}
