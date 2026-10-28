using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class EventDropEndpointTests
{
    [Fact]
    public async Task EndpointSerializesRunProviderLossAndKindCountsUsingProductionJsonMetadata()
    {
        var name = "event-drop-endpoint-" + Guid.NewGuid();
        var counter = new EtwEventDropCounter("ThreadCSwitch");
        var run = EtwEventDropDiagnostics.Start(name, () => 7, [counter], 17, DateTimeOffset.UnixEpoch);
        run.EnableProvider("kernel", 0x10);
        counter.Receive();
        counter.Receive();
        counter.Drop();
        run.Complete();
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        await using var app = builder.Build();
        app.MapEventDropsDebugEndpoint();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(item => item.RoutePattern.RawText == "/api/debug/event-drops");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        using var json = JsonDocument.Parse(body.ToArray());
        var session = json.RootElement.GetProperty("sessions").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        Assert.Equal(17, session.GetProperty("generation").GetInt64());
        Assert.Equal(DateTimeOffset.UnixEpoch, session.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(7, session.GetProperty("eventsLost").GetInt64());
        var provider = Assert.Single(session.GetProperty("enabledProviders").EnumerateArray());
        Assert.Equal("kernel", provider.GetProperty("provider").GetString());
        Assert.Equal(0x10UL, provider.GetProperty("keywords").GetUInt64());
        var kind = Assert.Single(session.GetProperty("kinds").EnumerateArray());
        Assert.Equal("ThreadCSwitch", kind.GetProperty("kind").GetString());
        Assert.Equal(2, kind.GetProperty("received").GetInt64());
        Assert.Equal(1, kind.GetProperty("dropped").GetInt64());
        Assert.Equal((int)ReadVolatilityKind.Sampled, json.RootElement.GetProperty("volatility").GetProperty("kind").GetInt32());
    }

    [Fact]
    public void NewRunResetsDynamicKindsAndProviderMetadata()
    {
        var name = "event-drop-restart-" + Guid.NewGuid();
        var provider = Guid.NewGuid();
        var old = EtwEventDropDiagnostics.Start(name, () => 3);
        old.EnableProvider(provider.ToString("D"), 0x41);
        var kind = old.Kind(provider, 33, 1);
        kind.Receive();
        kind.Drop();
        Assert.Same(kind, old.Kind(provider, 33, 1));
        var otherOpcode = old.Kind(provider, 33, 2);
        otherOpcode.Receive();
        old.DisableProvider(provider.ToString("D"));
        var first = old.Read();
        Assert.Empty(first.EnabledProviders);
        Assert.Equal(2, first.Kinds.Count);
        var droppedKind = Assert.Single(first.Kinds, item => item.Kind == $"{provider:D}/33/1");
        Assert.Equal((1L, 1L), (droppedKind.Received, droppedKind.Dropped));
        old.Complete();
        var next = EtwEventDropDiagnostics.Start(name, () => 0);
        Assert.Equal(old.Generation + 1, next.Generation);
        Assert.Empty(next.Read().Kinds);
        Assert.Equal(0, next.Read().EventsLost);
        Assert.Equal(next.Generation, EtwEventDropDiagnostics.Read().Sessions.Single(item => item.Name == name).Generation);
        next.Complete();
    }

    [Fact]
    public void FailedLossQueriesPreserveTheLastCountAndCannotPreventCleanup()
    {
        var fail = false;
        var run = EtwEventDropDiagnostics.Start("event-drop-loss-" + Guid.NewGuid(),
            () => fail ? throw new UnauthorizedAccessException("fixture denied") : 9);
        Assert.Equal(9, run.Read().EventsLost);
        fail = true;
        run.Complete();
        Assert.Equal(9, run.Read().EventsLost);
        Assert.Contains("fixture denied", run.Read().EventsLostError);
    }
}
