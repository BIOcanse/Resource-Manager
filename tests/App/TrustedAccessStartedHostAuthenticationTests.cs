using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class TrustedAccessStartedHostAuthenticationTests
{
    private const string SubscriptionPath = "/api/operations/subscribe";

    [Theory]
    [InlineData(AccessKeyScopes.MetricsRead)]
    [InlineData(AccessKeyScopes.ProcessesRead)]
    public async Task TargetMetricsSubscriptionRequiresBothMetricAndProcessFactsScopes(string scope)
    {
        var (app, credential) = await StartAsync(scope);
        await using (app)
        using (var client = Client(app, credential.ApiKey))
        using (var response = await client.PostAsJsonAsync("/api/subscriptions/stream", new
        {
            version = 1,
            subscriptions = new[] { new { id = "target", path = "/api/performance-overlay/subscribe?softwareId=game&ids=target.cpu" } }
        }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-scope-required", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task StartedHostEnforcesOrdinaryGetScopeAndRealHeadOptionsAndCatchallDispatch()
    {
        var (app, credential) = await StartAsync(AccessKeyScopes.Session);
        await using (app)
        using (var client = Client(app, credential.ApiKey))
        {
            using var get = await client.GetAsync("/api/runtime/identity");
            Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            Assert.Equal("access-scope-required", await ErrorAsync(get));

            var readKey = await credential.Owner.CreateAsync(new AccessKeyCreateRequest(
                "backend identity reader", AccessKeyPurposes.Control, [AccessKeyScopes.Read]), CancellationToken.None);
            using var readClient = Client(app, readKey.ApiKey);
            using var allowed = await readClient.GetAsync("/api/runtime/identity?challenge=" + new string('a', 64));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var identity = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
            Assert.Equal(Environment.ProcessId, identity.RootElement.GetProperty("processId").GetInt32());

            using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/api/runtime/identity");
            using var head = await client.SendAsync(headRequest);
            // MapGet does not register HEAD; the actual selected endpoint is
            // production's method-independent 404, not the GET scope alias.
            Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());

            using var optionsRequest = new HttpRequestMessage(HttpMethod.Options, "/api/runtime/identity");
            using var optionsOnBusinessGet = await client.SendAsync(
                optionsRequest);
            Assert.Equal(HttpStatusCode.NotFound, optionsOnBusinessGet.StatusCode);
            Assert.Equal("api-endpoint-not-found", await ErrorAsync(optionsOnBusinessGet));

            using var invalidMethod = await client.PostAsync(
                "/api/metrics/fields/snapshot", JsonContent.Create(new { }));
            Assert.Equal(HttpStatusCode.NotFound, invalidMethod.StatusCode);
            Assert.Equal("api-endpoint-not-found", await ErrorAsync(invalidMethod));

            using var optionsCatchallRequest = new HttpRequestMessage(HttpMethod.Options, "/api/new-unclassified-route");
            using var optionsOnCatchall = await client.SendAsync(
                optionsCatchallRequest);
            Assert.Equal(HttpStatusCode.NotFound, optionsOnCatchall.StatusCode);
            Assert.Equal("api-endpoint-not-found", await ErrorAsync(optionsOnCatchall));

            using var headCatchallRequest = new HttpRequestMessage(HttpMethod.Head, "/api/new-unclassified-route");
            using var headOnCatchall = await client.SendAsync(
                headCatchallRequest);
            Assert.Equal(HttpStatusCode.NotFound, headOnCatchall.StatusCode);
        }
    }

    [Fact]
    public async Task StartedHostRejectsOrdinaryGetWithoutAKey()
    {
        var (app, _) = await StartAsync(AccessKeyScopes.Read);
        await using (app)
        using (var client = Client(app))
        using (var response = await client.GetAsync("/api/access-keys"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-key-required", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task StartedHostRejectsOrdinaryGetKeyWithoutRequiredScope()
    {
        var (app, credential) = await StartAsync(AccessKeyScopes.Read);
        await using (app)
        using (var client = Client(app, credential.ApiKey))
        using (var response = await client.GetAsync("/api/access-keys"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-scope-required", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task StartedHostRejectsRevokedOrdinaryGetKey()
    {
        var (app, credential) = await StartAsync(AccessKeyScopes.KeysManage);
        await using (app)
        {
            using var client = Client(app, credential.ApiKey);
            using var before = await client.GetAsync("/api/access-keys");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            Assert.True(await credential.Owner.RevokeAsync(credential.CredentialId, CancellationToken.None));
            using var response = await client.GetAsync("/api/access-keys");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("access-key-invalid", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task StartedHostRejectsSubscriptionWithoutAKeyForNonKeylessScope()
    {
        var (app, _) = await StartAsync(AccessKeyScopes.Read);
        await using (app)
        using (var client = Client(app))
        using (var response = await PostSubscriptionAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-key-required", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task StartedHostRejectsSubscriptionKeyWithoutRequiredScope()
    {
        var (app, credential) = await StartAsync(AccessKeyScopes.Read);
        await using (app)
        using (var client = Client(app, credential.ApiKey))
        using (var response = await PostSubscriptionAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-scope-required", await ErrorAsync(response));
        }
    }

    [Fact]
    public async Task StartedHostRejectsRevokedSubscriptionKey()
    {
        var (app, credential) = await StartAsync(AccessKeyScopes.Session);
        await using (app)
        {
            Assert.True(await credential.Owner.RevokeAsync(credential.CredentialId, CancellationToken.None));
            using var client = Client(app, credential.ApiKey);
            using var response = await PostSubscriptionAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("access-key-invalid", await ErrorAsync(response));
        }
    }

    private static async Task<(WebApplication App, TrustedAccessEndpointTestAuthentication.AddedCredential Credential)>
        StartAsync(params string[] scopes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(builder.Services);
        var credential = TrustedAccessEndpointTestAuthentication.Add(builder.Services, scopes);
        builder.Services.RemoveAll<IHostedService>();
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        app.MapResourceManagerEndpoints(
            LoopbackApiPipelineMode.StandardUser,
            StartupCapabilitySet.NormalReadOnly);
        await app.StartAsync();
        return (app, credential);
    }

    private static HttpClient Client(WebApplication app, string? apiKey = null)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add(TrustedAccessHeaders.KeyHeader, apiKey);
        return client;
    }

    private static Task<HttpResponseMessage> PostSubscriptionAsync(HttpClient client)
        => client.PostAsJsonAsync("/api/subscriptions/stream", new
        {
            version = 1,
            subscriptions = new[] { new { id = "authorization", path = SubscriptionPath } }
        });

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        using var content = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return content.RootElement.GetProperty("error").GetString()!;
    }
}
