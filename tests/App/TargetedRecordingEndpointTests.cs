using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class TargetedRecordingEndpointTests
{
    [Fact]
    public async Task GetExportAndDeleteUseTheRecordedReportContract()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rm-targeted-endpoints-{Guid.NewGuid():N}");
        try
        {
            var store = new TargetedRecordingStore(new ResourceManagerDatabase(new TestHostEnvironment(root)));
            var started = DateTimeOffset.UtcNow;
            await store.CreateAsync(new TargetedRecordingHeader("one", "software", "Example", started,
                null, 600, "recording", null, false), CancellationToken.None);
            await store.AppendAsync("one", [new FrameIntervalSample(42, 123, FramePresentSource.Dxgi,
                1, started.AddMilliseconds(10), 10)], null, CancellationToken.None);
            await store.FinishAsync("one", "user", false, CancellationToken.None);

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(new TargetedRecordingService(store, null!, null!, null!, null!,
                NullLogger<TargetedRecordingService>.Instance));
            await using var app = builder.Build();
            app.MapTargetedRecordingEndpoints();

            var json = await InvokeAsync(app, "/api/targeted-recordings/{id}", "GET", "one");
            Assert.Equal(StatusCodes.Status200OK, json.Status);
            using (var document = JsonDocument.Parse(json.Body))
            {
                Assert.Equal("software", document.RootElement.GetProperty("recording").GetProperty("softwareId").GetString());
                Assert.Equal(100, document.RootElement.GetProperty("summary").GetProperty("averageFps").GetDouble());
            }

            var csv = await InvokeAsync(app, "/api/targeted-recordings/{id}/export", "GET", "one", "?format=csv");
            Assert.Equal(StatusCodes.Status200OK, csv.Status);
            Assert.Contains("frame,", Encoding.UTF8.GetString(csv.Body));

            var deleted = await InvokeAsync(app, "/api/targeted-recordings/{id}", "DELETE", "one");
            Assert.Equal(StatusCodes.Status204NoContent, deleted.Status);
            var missing = await InvokeAsync(app, "/api/targeted-recordings/{id}", "GET", "one");
            Assert.Equal(StatusCodes.Status404NotFound, missing.Status);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task RoutesExposeStartStopListGetDeleteAndExport()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<TargetedRecordingService>(_ => null!);
        await using var app = builder.Build();
        app.MapTargetedRecordingEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Path: endpoint.RoutePattern.RawText,
                Methods: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods))
            .ToArray();

        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings" && item.Methods?.Contains("POST") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings" && item.Methods?.Contains("GET") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/{id}/stop" && item.Methods?.Contains("POST") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/{id}" && item.Methods?.Contains("GET") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/{id}" && item.Methods?.Contains("DELETE") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/{id}/export" && item.Methods?.Contains("GET") == true);
    }

    private static async Task<(int Status, byte[] Body)> InvokeAsync(WebApplication app,
        string pattern, string method, string id, string query = "")
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(item => item.RoutePattern.RawText == pattern &&
                item.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = method;
        context.Request.RouteValues["id"] = id;
        context.Request.QueryString = new QueryString(query);
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, body.ToArray());
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
