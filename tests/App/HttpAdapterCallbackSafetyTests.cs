using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Infrastructure.Adaptation;

namespace Resource_Manager_APP.Tests;

public sealed class HttpAdapterCallbackSafetyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:9321/api/system/processes/terminate")]
    [InlineData("http://localhost:9321/api")]
    [InlineData("http://[::1]:9321/api")]
    [InlineData("https://example.com/")]
    [InlineData("http://user:password@127.0.0.1:9322/")]
    [InlineData("http://127.0.0.1:9322/#hidden")]
    public async Task ForbiddenCallbackDoesNotReachTransport(string target)
    {
        using var server = new FakeServer();
        var terminal = new CountingHandler();
        using var client = new HttpClient(new HttpAdapterCallbackSafetyHandler(server) { InnerHandler = terminal });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(target));
        Assert.Equal(0, terminal.Calls);
    }

    [Fact]
    public async Task AllowedLoopbackCallbackReachesTransport()
    {
        using var server = new FakeServer();
        var terminal = new CountingHandler();
        using var client = new HttpClient(new HttpAdapterCallbackSafetyHandler(server) { InnerHandler = terminal });
        using var response = await client.PostAsync("http://127.0.0.1:9322/resource-ledger", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Calls);
    }

    [Fact]
    public async Task ProductionTypedClientDoesNotFollowRedirectBackIntoService()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTransient<HttpAdapterCallbackSafetyHandler>();
        builder.Services.AddHttpClient("owned-callback")
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            .AddHttpMessageHandler<HttpAdapterCallbackSafetyHandler>();
        await using var app = builder.Build();
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var calls = 0;
        // Use a second ephemeral Kestrel listener instead of reserving a system URL ACL.
        var otherBuilder = WebApplication.CreateBuilder(); otherBuilder.Logging.ClearProviders(); otherBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var other = otherBuilder.Build();
        other.MapGet("/redirect", () => { Interlocked.Increment(ref calls); return Results.Redirect(url + "/should-not-reach"); });
        await other.StartAsync();
        try
        {
            var otherUrl = other.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("owned-callback");
            using var response = await client.GetAsync(otherUrl + "/redirect");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(1, calls);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/api"));
        }
        finally { await other.StopAsync(); await app.StopAsync(); }
    }

    private sealed class FakeServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        internal FakeServer()
        {
            var addresses = new ServerAddressesFeature(); addresses.Addresses.Add("http://127.0.0.1:9321");
            Features.Set<IServerAddressesFeature>(addresses);
        }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken token) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
