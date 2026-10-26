using ResourceManager.ReadWrite.Windows.Frames;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class TargetedRecordingEndpointTests
{
    [Fact]
    public async Task StartDefaultsToTenMinutesAndStopCompletesTheActiveRecording()
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync();
        var started = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: """{"softwareId":"software"}""");
        Assert.Equal(StatusCodes.Status200OK, started.Status);
        using var startedDocument = JsonDocument.Parse(started.Body);
        var header = startedDocument.RootElement;
        var id = header.GetProperty("id").GetString()!;
        Assert.Equal("software", header.GetProperty("softwareId").GetString());
        Assert.Equal(600, header.GetProperty("maximumDurationSeconds").GetInt32());
        Assert.Equal("recording", header.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, header.GetProperty("endedAt").ValueKind);

        var conflict = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: """{"softwareId":"other-software"}""");
        Assert.Equal(StatusCodes.Status409Conflict, conflict.Status);
        Assert.Empty(conflict.Body);
        var activeDelete = await InvokeAsync(fixture.App, "/api/targeted-recordings/{id}", "DELETE", id);
        Assert.Equal(StatusCodes.Status404NotFound, activeDelete.Status);
        var list = await InvokeAsync(fixture.App, "/api/targeted-recordings", "GET");
        Assert.Equal(StatusCodes.Status200OK, list.Status);
        using (var listed = JsonDocument.Parse(list.Body))
        {
            var recording = Assert.Single(listed.RootElement.EnumerateArray());
            Assert.Equal(id, recording.GetProperty("id").GetString());
            Assert.Equal("recording", recording.GetProperty("status").GetString());
        }

        var stopped = await InvokeAsync(fixture.App, "/api/targeted-recordings/{id}/stop", "POST", id);
        Assert.Equal(StatusCodes.Status200OK, stopped.Status);
        using (var completed = JsonDocument.Parse(stopped.Body))
        {
            Assert.Equal(id, completed.RootElement.GetProperty("id").GetString());
            Assert.Equal("completed", completed.RootElement.GetProperty("status").GetString());
            Assert.Equal("user", completed.RootElement.GetProperty("stopReason").GetString());
            Assert.Equal(JsonValueKind.String, completed.RootElement.GetProperty("endedAt").ValueKind);
        }
        var stoppedAgain = await InvokeAsync(fixture.App, "/api/targeted-recordings/{id}/stop", "POST", id);
        Assert.Equal(StatusCodes.Status404NotFound, stoppedAgain.Status);
        var deleted = await InvokeAsync(fixture.App, "/api/targeted-recordings/{id}", "DELETE", id);
        Assert.Equal(StatusCodes.Status204NoContent, deleted.Status);
    }

    [Theory]
    [InlineData("{\"softwareId\":\"software\",\"maximumDurationSeconds\":0}", StatusCodes.Status400BadRequest)]
    [InlineData("{\"softwareId\":\"software\",\"maximumDurationSeconds\":3601}", StatusCodes.Status400BadRequest)]
    [InlineData("{\"softwareId\":\" \"}", StatusCodes.Status400BadRequest)]
    [InlineData("{\"softwareId\":\"missing\"}", StatusCodes.Status404NotFound)]
    public async Task StartRejectsInvalidRequestsWithoutCreatingARecording(string json, int expectedStatus)
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync();
        var result = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST", json: json);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.Body);
        var list = await InvokeAsync(fixture.App, "/api/targeted-recordings", "GET");
        Assert.Equal(StatusCodes.Status200OK, list.Status);
        using var document = JsonDocument.Parse(list.Body);
        Assert.Empty(document.RootElement.EnumerateArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public async Task StartAcceptsTheDurationLimits(int seconds)
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync();
        var result = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: JsonSerializer.Serialize(new { softwareId = "software", maximumDurationSeconds = seconds }));
        Assert.Equal(StatusCodes.Status200OK, result.Status);
        using var document = JsonDocument.Parse(result.Body);
        Assert.Equal(seconds, document.RootElement.GetProperty("maximumDurationSeconds").GetInt32());
    }

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
        string pattern, string method, string id = "", string query = "", string? json = null)
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
        await using var requestBody = new MemoryStream(json is null ? [] : Encoding.UTF8.GetBytes(json));
        if (json is not null)
        {
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = requestBody.Length;
            context.Request.Body = requestBody;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        }
        await using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, body.ToArray());
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class RecordingEndpointFixture(string root, TargetedRecordingService service,
        WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public static async Task<RecordingEndpointFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"rm-targeted-endpoints-{Guid.NewGuid():N}");
            var database = new ResourceManagerDatabase(new TestHostEnvironment(root));
            Assert.Equal(Path.Combine(root, "UserData", "Database", "resource-manager.db"), database.DatabasePath);
            var store = new TargetedRecordingStore(database);
            var observations = new EndpointObservations();
            var service = new TargetedRecordingService(store, observations, observations, observations, observations,
                NullLogger<TargetedRecordingService>.Instance);
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(service);
            var app = builder.Build();
            app.MapTargetedRecordingEndpoints();
            await service.StartAsync(CancellationToken.None);
            return new RecordingEndpointFixture(root, service, app);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await service.StopAsync(CancellationToken.None);
            }
            finally
            {
                await App.DisposeAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try
                {
                    var databaseDirectory = Path.Combine(root, "UserData", "Database");
                    var databasePath = Path.Combine(databaseDirectory, "resource-manager.db");
                    foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                    {
                        File.Delete(databasePath + suffix);
                    }
                    foreach (var directory in new[] { databaseDirectory, Path.Combine(root, "UserData"), root })
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (DirectoryNotFoundException) { }
                catch (IOException) { }
            }
        }
    }

    private sealed class EndpointObservations : ISoftwareRegistryView, IFrameTimingObservationSource,
        IResourceBreakdownObservationSource, IMetricSnapshotObservationSource
    {
        private static readonly IReadOnlyList<SoftwareRecord> Records =
        [
            new("software", "Example", "game", "Running", [], [],
                new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"), null),
            new("other-software", "Other Example", "game", "Running", [], [],
                new SoftwareOperationCapabilities(false, "None", "missing_uninstall_entry"), null)
        ];

        public Task<IReadOnlyList<SoftwareRecord>> GetSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult(Records);
        public Task<IReadOnlyList<SoftwareRecord>> RefreshSoftwareAsync(CancellationToken cancellationToken)
            => Task.FromResult(Records);
        public IDisposable AcquireSubscription() => new EmptyLease();
        public FrameTimingSnapshot? Read(TimeSpan window) => null;
        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request,
            TimeSpan refreshInterval) => new EmptyLease();
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request)
            => new(DateTimeOffset.UtcNow, []);
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request,
            TimeSpan refreshInterval) => new EmptyLease();
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
    }

    private sealed class EmptyLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
