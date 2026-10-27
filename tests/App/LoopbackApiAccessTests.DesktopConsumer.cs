using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [Fact]
    public async Task DefaultKeySelectsOneActualHttpConnectionForDesktopPublishing()
    {
        await using var fixture = await Fixture.StartAsync();
        using var current = Process.GetCurrentProcess();
        Assert.True(current.SessionId > 0, "Desktop publisher selection requires an interactive Windows session.");

        using var selection = new HttpRequestMessage(HttpMethod.Post,
            InteractiveDesktopObservation.EndpointPath + "/publisher/select");
        selection.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var selected = await fixture.Client.SendAsync(selection);
        Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);

        // The native publisher uses one dedicated HttpClient for selection and publishing.
        // Repeating selection on that connection is idempotent; a second connection cannot take over.
        using var repeatedSelection = new HttpRequestMessage(HttpMethod.Post,
            InteractiveDesktopObservation.EndpointPath + "/publisher/select");
        repeatedSelection.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var repeated = await fixture.Client.SendAsync(repeatedSelection);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);

        using var publish = new HttpRequestMessage(HttpMethod.Post, InteractiveDesktopObservation.EndpointPath)
        {
            Content = JsonContent.Create(CaptureEmptyDesktopSample(current))
        };
        publish.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var published = await fixture.Client.SendAsync(publish);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        using var competingClient = new HttpClient { BaseAddress = fixture.Client.BaseAddress };
        using var competingSelection = new HttpRequestMessage(HttpMethod.Post,
            InteractiveDesktopObservation.EndpointPath + "/publisher/select");
        competingSelection.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var conflict = await competingClient.SendAsync(competingSelection);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var competingPublish = new HttpRequestMessage(HttpMethod.Post, InteractiveDesktopObservation.EndpointPath)
        {
            Content = JsonContent.Create(CaptureEmptyDesktopSample(current))
        };
        competingPublish.Headers.Add(TrustedAccessHeaders.KeyHeader, fixture.Key);
        using var rejected = await competingClient.SendAsync(competingPublish);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(5, fixture.HttpRequests);
        Assert.Null(fixture.LastRequestError);
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    [Fact]
    public async Task DesktopPublisherRequiresAKeyWithTheDesktopScope()
    {
        await using var fixture = await Fixture.StartAsync();
        var readOnly = await fixture.Keys.CreateAsync(new AccessKeyCreateRequest(
            "read only", AccessKeyPurposes.Control, [AccessKeyScopes.Read]), CancellationToken.None);
        using var denied = new HttpRequestMessage(HttpMethod.Post,
            InteractiveDesktopObservation.EndpointPath + "/publisher/select");
        denied.Headers.Add(TrustedAccessHeaders.KeyHeader, readOnly.ApiKey);
        using var response = await fixture.Client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("access-scope-required", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Administrator.Calls);
    }

    private static InteractiveDesktopObservation CaptureEmptyDesktopSample(Process process)
        => new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), process.SessionId,
            DateTimeOffset.UtcNow.UtcTicks, Complete: false, ForegroundKnown: false,
            Foreground: null, Windows: []);
}
