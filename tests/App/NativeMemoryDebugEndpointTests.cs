using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.ReadWrite;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMemoryDebugEndpointTests
{
    [Fact]
    public async Task EndpointSerializesTheLedgerTableAndEveryWorkspaceCapacityWithProductionJsonMetadata()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var scratch = ledger.CreateScratch(8192);
        using var native = ((IWorkspaceCapacityRegistry)ledger).RegisterNativeWorkspace("fixture-native",
            static () => new NativeWorkspaceCapacityDescription("records=3, index=8", 4096));
        using (var use = scratch.Acquire())
        {
            use.GetMemoryChunk(0, 1).Span[0] = 1;
            use.Complete();
        }
        var table = ledger.ReadTable();
        Assert.True(table.RegisteredBytes >= 8192);

        var (status, cacheControl, json) = await GetAsync(ledger);

        Assert.Equal(200, status);
        Assert.Equal("no-store", cacheControl);
        using (json)
        {
            var root = json.RootElement;
            Assert.Equal(table.RegisteredBytes, root.GetProperty("ledger").GetProperty("registeredBytes").GetUInt64());
            Assert.Equal(table.SystemPageBytes, root.GetProperty("ledger").GetProperty("systemPageBytes").GetUInt64());
            var workspaces = root.GetProperty("workspaces").EnumerateArray().ToArray();
            Assert.Equal(2, workspaces.Length);
            var region = Assert.Single(workspaces, item => item.GetProperty("inLedger").GetBoolean());
            Assert.Equal("compute-scratch", region.GetProperty("workArea").GetString());
            Assert.Equal(8192UL, region.GetProperty("bytes").GetUInt64());
            Assert.False(string.IsNullOrEmpty(region.GetProperty("uid").GetString()));
            var nativeRow = Assert.Single(workspaces, item => !item.GetProperty("inLedger").GetBoolean());
            Assert.Equal("fixture-native", nativeRow.GetProperty("workArea").GetString());
            Assert.Equal("records=3, index=8", nativeRow.GetProperty("rows").GetString());
            Assert.Equal(4096UL, nativeRow.GetProperty("bytes").GetUInt64());
            Assert.Equal((int)ReadVolatilityKind.Sampled, root.GetProperty("volatility").GetProperty("kind").GetInt32());
        }
        // The read itself registered, leased or allocated nothing.
        Assert.Equal(table.RegisteredBytes, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(2, ledger.ReadWorkspaceCapacities().Count);
    }

    [Fact]
    public async Task ReleasedWorkspacesLeaveTheReading()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var native = ((IWorkspaceCapacityRegistry)ledger).RegisterNativeWorkspace("fixture-native",
            static () => new NativeWorkspaceCapacityDescription("records=1", 64));
        native.Dispose();
        var (status, _, json) = await GetAsync(ledger);
        Assert.Equal(200, status);
        using (json)
        {
            Assert.Empty(json.RootElement.GetProperty("workspaces").EnumerateArray());
            Assert.Equal(0UL, json.RootElement.GetProperty("ledger").GetProperty("registeredBytes").GetUInt64());
        }
    }

    [Fact]
    public void RouteUsesTheSameReadScopeAsTheOtherDebugReads()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/debug/native-memory";
        var requirement = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        context.Request.Path = "/api/debug/business-blocks";
        var businessBlocks = TrustedAccessHttpPolicy.GetRequirement(context.Request);
        Assert.Equal(TrustedAccessHttpAuthorizationMode.Protected, requirement.Mode);
        Assert.Equal([AccessKeyScopes.Read], requirement.RequiredScopes);
        Assert.Equal(businessBlocks.RequiredScopes, requirement.RequiredScopes);
        Assert.Equal(businessBlocks.AllowKeylessLocalWindowsCaller, requirement.AllowKeylessLocalWindowsCaller);
    }

    private static async Task<(int Status, string? CacheControl, JsonDocument Json)> GetAsync(ResourceManagerLedgerOwner ledger)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton(ledger);
        await using var app = builder.Build();
        app.MapNativeMemoryDebugEndpoint();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(item => item.RoutePattern.RawText == "/api/debug/native-memory");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, context.Response.Headers.CacheControl, JsonDocument.Parse(body.ToArray()));
    }
}
