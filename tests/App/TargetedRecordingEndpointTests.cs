using ResourceManager.ReadWrite.Windows.Frames;
using System.Text;
using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TargetedRecordingEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClearHistoryEndpointUsesSessionReturnsCountAndKeepsRecording(bool allowed)
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync(allowed ? AccessKeyScopes.Session : AccessKeyScopes.ProcessesRead);
        var service = fixture.App.Services.GetRequiredService<TargetedRecordingService>();
        var first = await service.BeginAsync("software", 600, CancellationToken.None);
        await service.FinishAsync(first.Id, CancellationToken.None);
        var active = await service.BeginAsync("software", 600, CancellationToken.None);
        await fixture.App.StartAsync();
        using var client = TrustedAccessEndpointTestAuthentication.Client(fixture.App);
        using var response = await client.PostAsync("/api/targeted-recordings/history/clear", null);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(document.RootElement.EnumerateObject());
            Assert.Equal(1, document.RootElement.GetProperty("removedCount").GetInt32());
            Assert.Null(await service.GetAsync(first.Id, CancellationToken.None));
            using var repeated = await client.PostAsync("/api/targeted-recordings/history/clear", null);
            using var repeatedDocument = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
            Assert.Equal(0, repeatedDocument.RootElement.GetProperty("removedCount").GetInt32());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("access-scope-required", document.RootElement.GetProperty("error").GetString());
            Assert.Equal([AccessKeyScopes.Session], document.RootElement.GetProperty("missingScopes").EnumerateArray().Select(item => item.GetString()));
            Assert.NotNull(await service.GetAsync(first.Id, CancellationToken.None));
        }
        Assert.Equal("recording", (await service.GetAsync(active.Id, CancellationToken.None))!.Recording.Status);
        var stopped = await service.FinishAsync(active.Id, CancellationToken.None);
        Assert.Equal("completed", stopped!.Status);
    }

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
            Assert.Equal("user-stop", completed.RootElement.GetProperty("stopReason").GetString());
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
                null, 600, "recording", null, false)
            {
                ProfileId = TargetedRecordingProfileDefaults.DefaultProfileId,
                ProfileName = TargetedRecordingProfileDefaults.DefaultProfileName,
                Items =
                [
                    new("frames", "frames.present", "ms", "auto", [], null, "per-present", null, "mid", 0, []),
                    new("software", "cpu.usage", "%", "auto", [], null, "profile-default", 1000, "mid", 0, []),
                    new("metric", "cpu.usage", "%", "auto", [], null, "profile-default", 1000, "high", 0, [])
                ]
            }, CancellationToken.None);
            await store.AppendAsync("one", [new FrameIntervalSample(42, 123, FramePresentSource.Dxgi,
                1, started.AddMilliseconds(10), 10)], new TargetedResourceSample(started.AddSeconds(1),
                new Dictionary<string, double?> { ["cpu.usage"] = 40 },
                new Dictionary<string, double?> { ["cpu.usage"] = 12.5 }, [new TargetedProcessIdentity(42, 123)]),
                CancellationToken.None);
            await store.FinishAsync("one", "user-stop", false, CancellationToken.None);

            var builder = WebApplication.CreateBuilder();
            var observations = new EndpointObservations();
            builder.Services.AddSingleton(new TargetedRecordingService(store, observations, observations, observations,
                observations, observations, observations, NullLogger<TargetedRecordingService>.Instance));
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

            var export = await InvokeAsync(app, "/api/targeted-recordings/{id}/export", "GET", "one", "?format=json");
            Assert.Equal(StatusCodes.Status200OK, export.Status);
            using (var document = JsonDocument.Parse(export.Body))
            {
                Assert.Equal(TargetedRecordingJsonExport.CurrentSchemaVersion,
                    document.RootElement.GetProperty("schemaVersion").GetInt32());
                var recording = document.RootElement.GetProperty("recording");
                Assert.Equal("default", recording.GetProperty("profileId").GetString());
                Assert.Equal(3, recording.GetProperty("items").GetArrayLength());
                Assert.Single(document.RootElement.GetProperty("frameIntervals").EnumerateArray());
                Assert.Single(document.RootElement.GetProperty("resourceSamples").EnumerateArray());
            }

            var longCsv = Lines((await InvokeAsync(app, "/api/targeted-recordings/{id}/export", "GET", "one",
                "?format=long-csv")).Body);
            Assert.Equal("time_ms,item,process,value,unit", longCsv[0]);
            Assert.Equal(new[] { "frames:present", "42", "10", "ms" }, longCsv[1].Split(',')[1..]);
            Assert.Contains(longCsv, line => line.EndsWith(",software:cpu.usage,all,40,%", StringComparison.Ordinal));
            Assert.Contains(longCsv, line => line.EndsWith(",metric:cpu.usage,,12.5,%", StringComparison.Ordinal));
            Assert.Equal(4, longCsv.Length);

            var framesCsv = Lines((await InvokeAsync(app, "/api/targeted-recordings/{id}/export", "GET", "one",
                "?format=frames-csv")).Body);
            Assert.Equal("time_ms,process_id,process_start_key,source,swap_chain,frame_time_ms", framesCsv[0]);
            Assert.Equal(new[] { "42", "123", "Dxgi", "1", "10" }, framesCsv[1].Split(',')[1..]);
            Assert.Equal(2, framesCsv.Length);

            var presentMon = Lines((await InvokeAsync(app, "/api/targeted-recordings/{id}/export", "GET", "one",
                "?format=presentmon")).Body);
            Assert.Equal("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,MsBetweenPresents",
                presentMon[0]);
            var present = presentMon[1].Split(',');
            Assert.Equal(new[] { "Example", "42", "0x0000000000000001", "Dxgi", "0", "0", "0" }, present[..7]);
            Assert.Equal("10.0000", present[8]);
            Assert.Equal(2, presentMon.Length);

            var unknown = await InvokeAsync(app, "/api/targeted-recordings/{id}/export", "GET", "one", "?format=xml");
            Assert.Equal(StatusCodes.Status400BadRequest, unknown.Status);

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
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/history/clear" && item.Methods?.Contains("POST") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/{id}/export" && item.Methods?.Contains("GET") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/targets" && item.Methods?.Contains("GET") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/items/catalog" && item.Methods?.Contains("GET") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/profiles" && item.Methods?.Contains("GET") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/profiles" && item.Methods?.Contains("POST") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/profiles/{id}" && item.Methods?.Contains("PATCH") == true);
        Assert.Contains(routes, item => item.Path == "/api/targeted-recordings/profiles/{id}" && item.Methods?.Contains("DELETE") == true);
    }

    [Fact]
    public async Task StartRejectsASoftwareWithoutRunningProcessesWithTheReasonAndNoReport()
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync();
        var result = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: """{"softwareId":"other-software"}""");
        Assert.Equal(StatusCodes.Status409Conflict, result.Status);
        using (var document = JsonDocument.Parse(result.Body))
        {
            Assert.Equal("target-not-running", document.RootElement.GetProperty("error").GetString());
        }
        var list = await InvokeAsync(fixture.App, "/api/targeted-recordings", "GET");
        using var listed = JsonDocument.Parse(list.Body);
        Assert.Empty(listed.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task StartRecordsTheRequestItemsAndRejectsAnUnknownProfileOrItem()
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync();
        var missingProfile = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: """{"softwareId":"software","profileId":"missing"}""");
        Assert.Equal(StatusCodes.Status404NotFound, missingProfile.Status);
        using (var document = JsonDocument.Parse(missingProfile.Body))
            Assert.Equal("targeted-recording-profile-not-found", document.RootElement.GetProperty("error").GetString());
        var unknownItem = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: """{"softwareId":"software","items":[{"kind":"metric","id":"nope"}]}""");
        Assert.Equal(StatusCodes.Status400BadRequest, unknownItem.Status);
        using (var document = JsonDocument.Parse(unknownItem.Body))
            Assert.Equal("targeted-recording-item-unknown", document.RootElement.GetProperty("error").GetString());

        var started = await InvokeAsync(fixture.App, "/api/targeted-recordings", "POST",
            json: """{"softwareId":"software","maximumDurationSeconds":60,"items":[{"kind":"frames","id":"frames.present"},{"kind":"software","id":"cpu.usage","intervalMs":250,"syncPrecision":"high"}]}""");
        Assert.Equal(StatusCodes.Status200OK, started.Status);
        using var header = JsonDocument.Parse(started.Body);
        Assert.Equal(JsonValueKind.Null, header.RootElement.GetProperty("profileId").ValueKind);
        var items = header.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("software", items[1].GetProperty("kind").GetString());
        Assert.Equal("auto", items[1].GetProperty("selectedSource").GetString());
        Assert.Equal(250, items[1].GetProperty("selectedIntervalMs").GetInt32());
        Assert.Equal("item", items[1].GetProperty("intervalOrigin").GetString());
        Assert.Equal("high", items[1].GetProperty("syncPrecision").GetString());
        var processes = header.RootElement.GetProperty("processes").EnumerateArray().ToArray();
        Assert.Equal(Environment.ProcessId, Assert.Single(processes).GetProperty("processId").GetInt32());
        var stopped = await InvokeAsync(fixture.App, "/api/targeted-recordings/{id}/stop", "POST",
            header.RootElement.GetProperty("id").GetString()!);
        Assert.Equal(StatusCodes.Status200OK, stopped.Status);
    }

    [Fact]
    public async Task TargetsListRunningSoftwareAndTheCatalogListsRecordableItems()
    {
        await using var fixture = await RecordingEndpointFixture.CreateAsync();
        var targets = await InvokeAsync(fixture.App, "/api/targeted-recordings/targets", "GET");
        Assert.Equal(StatusCodes.Status200OK, targets.Status);
        using (var document = JsonDocument.Parse(targets.Body))
        {
            var target = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("software", target.GetProperty("softwareId").GetString());
            Assert.Equal("Example", target.GetProperty("name").GetString());
            Assert.Equal(1, target.GetProperty("processCount").GetInt32());
            Assert.Equal(3, target.EnumerateObject().Count());
        }
        var catalog = await InvokeAsync(fixture.App, "/api/targeted-recordings/items/catalog", "GET");
        Assert.Equal(StatusCodes.Status200OK, catalog.Status);
        using (var document = JsonDocument.Parse(catalog.Body))
        {
            var entries = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(EndpointObservations.Catalog.Count, entries.Length);
            var software = entries.Single(item => item.GetProperty("kind").GetString() == "software");
            Assert.Equal("cpu.usage", software.GetProperty("id").GetString());
            Assert.Equal("%", software.GetProperty("unit").GetString());
            Assert.Equal(TargetedRecordingItemCatalogRules.ResourceBreakdownSource,
                Assert.Single(software.GetProperty("sources").EnumerateArray()).GetString());
        }
    }

    [Fact]
    public async Task ProfilesCanBeListedCreatedCopiedRenamedEditedAndDeletedButNeverEmptied()
    {
        var settings = new MemorySettingsStore();
        var builder = WebApplication.CreateBuilder();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<TargetedRecordingService>(_ => null!);
        builder.Services.AddSingleton(new TargetedRecordingProfileService(settings, new PatchingCoordinator(settings),
            new EndpointObservations()));
        await using var app = builder.Build();
        app.MapTargetedRecordingEndpoints();

        using (var listed = JsonDocument.Parse((await InvokeAsync(app, "/api/targeted-recordings/profiles", "GET")).Body))
        {
            var profile = Assert.Single(listed.RootElement.EnumerateArray());
            Assert.Equal("default", profile.GetProperty("id").GetString());
            Assert.Equal("默认", profile.GetProperty("name").GetString());
            Assert.Equal(1000, profile.GetProperty("defaultIntervalMs").GetInt32());
            Assert.Equal(TargetedRecordingProfileDefaults.CreateDefaultProfile().Items.Count,
                profile.GetProperty("items").GetArrayLength());
        }

        var copied = await InvokeAsync(app, "/api/targeted-recordings/profiles", "POST",
            json: """{"name":"复制","copyFromId":"default"}""");
        Assert.Equal(StatusCodes.Status201Created, copied.Status);
        string copyId;
        using (var document = JsonDocument.Parse(copied.Body))
        {
            copyId = document.RootElement.GetProperty("id").GetString()!;
            Assert.NotEqual("default", copyId);
            Assert.Equal("复制", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(TargetedRecordingProfileDefaults.CreateDefaultProfile().Items.Count,
                document.RootElement.GetProperty("items").GetArrayLength());
        }

        var created = await InvokeAsync(app, "/api/targeted-recordings/profiles", "POST",
            json: """{"name":"Mine","defaultIntervalMs":500,"items":[{"kind":"frames","id":"frames.present"},{"kind":"software","id":"cpu.usage","source":"resource-breakdown","intervalMs":250,"syncPrecision":"low"}]}""");
        Assert.Equal(StatusCodes.Status201Created, created.Status);
        string mineId;
        using (var document = JsonDocument.Parse(created.Body))
        {
            mineId = document.RootElement.GetProperty("id").GetString()!;
            Assert.Equal(500, document.RootElement.GetProperty("defaultIntervalMs").GetInt32());
            var item = document.RootElement.GetProperty("items")[1];
            Assert.Equal("resource-breakdown", item.GetProperty("source").GetString());
            Assert.Equal(250, item.GetProperty("intervalMs").GetInt32());
            Assert.Equal("low", item.GetProperty("syncPrecision").GetString());
        }

        foreach (var (json, status, reason) in new[]
        {
            ("""{"name":" "}""", 400, "targeted-recording-profile-invalid-name"),
            ("""{"name":"Bad","items":[{"kind":"metric","id":"nope"}]}""", 400, "targeted-recording-item-unknown"),
            ("""{"name":"Bad","copyFromId":"missing"}""", 404, "targeted-recording-profile-not-found")
        })
        {
            var rejected = await InvokeAsync(app, "/api/targeted-recordings/profiles", "POST", json: json);
            Assert.Equal(status, rejected.Status);
            using var document = JsonDocument.Parse(rejected.Body);
            Assert.Equal(reason, document.RootElement.GetProperty("error").GetString());
        }

        var renamed = await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "PATCH", copyId,
            json: """{"name":"Renamed"}""");
        Assert.Equal(StatusCodes.Status200OK, renamed.Status);
        using (var document = JsonDocument.Parse(renamed.Body))
        {
            Assert.Equal("Renamed", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(TargetedRecordingProfileDefaults.CreateDefaultProfile().Items.Count,
                document.RootElement.GetProperty("items").GetArrayLength());
        }
        var edited = await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "PATCH", mineId,
            json: """{"items":[{"kind":"metric","id":"cpu.usage","source":"load@cpu0","syncPrecision":"high"}]}""");
        Assert.Equal(StatusCodes.Status200OK, edited.Status);
        var badEdit = await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "PATCH", mineId,
            json: """{"defaultIntervalMs":50}""");
        Assert.Equal(StatusCodes.Status400BadRequest, badEdit.Status);
        var missing = await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "PATCH", "missing",
            json: """{"name":"x"}""");
        Assert.Equal(StatusCodes.Status404NotFound, missing.Status);

        var saved = settings.Current.TargetedRecording!.Profiles;
        Assert.Equal(new[] { "default", copyId, mineId }, saved.Select(static profile => profile.Id).ToArray());
        Assert.Equal("Renamed", saved[1].Name);
        Assert.Equal("load@cpu0", Assert.Single(saved[2].Items).Source);

        Assert.Equal(StatusCodes.Status204NoContent,
            (await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "DELETE", copyId)).Status);
        Assert.Equal(StatusCodes.Status204NoContent,
            (await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "DELETE", "default")).Status);
        var last = await InvokeAsync(app, "/api/targeted-recordings/profiles/{id}", "DELETE", mineId);
        Assert.Equal(StatusCodes.Status409Conflict, last.Status);
        using (var document = JsonDocument.Parse(last.Body))
            Assert.Equal("targeted-recording-last-profile", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(mineId, Assert.Single(settings.Current.TargetedRecording!.Profiles).Id);
    }

    private static string[] Lines(byte[] body)
        => Encoding.UTF8.GetString(body).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

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

        public static async Task<RecordingEndpointFixture> CreateAsync(string? scope = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"rm-targeted-endpoints-{Guid.NewGuid():N}");
            var database = new ResourceManagerDatabase(new TestHostEnvironment(root));
            Assert.Equal(Path.Combine(root, "UserData", "Database", "resource-manager.db"), database.DatabasePath);
            var store = new TargetedRecordingStore(database);
            var observations = new EndpointObservations();
            var service = new TargetedRecordingService(store, observations, observations, observations, observations,
                observations, observations, NullLogger<TargetedRecordingService>.Instance, liveness: observations);
            var builder = WebApplication.CreateBuilder();
            if (scope is not null)
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                TrustedAccessEndpointTestAuthentication.Add(builder.Services, scope);
            }
            builder.Services.ConfigureHttpJsonOptions(options =>
                ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
            builder.Services.AddSingleton(service);
            var app = scope is null ? builder.Build() : TrustedAccessEndpointTestAuthentication.Build(builder);
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

    /// <summary>"software" runs as this test process; "other-software" is registered but not running.</summary>
    private sealed class EndpointObservations : ISoftwareRegistryView, IFrameTimingObservationSource,
        IResourceBreakdownObservationSource, IMetricSnapshotObservationSource, IResourceBreakdownPushSource,
        IMetricSnapshotPushSource, ITargetedRecordingProcessReader, ITargetedRecordingItemCatalog, ITargetedProcessLiveness
    {
        private static readonly long CurrentStartKey =
            System.Diagnostics.Process.GetCurrentProcess().StartTime.ToFileTimeUtc();

        public static readonly IReadOnlyList<TargetedRecordingCatalogItem> Catalog =
        [
            new(TargetedRecordingItemKinds.Frames, TargetedRecordingProfileDefaults.FramesItemId, "ms", [], null, TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Software, "cpu.usage", "%", [TargetedRecordingItemCatalogRules.ResourceBreakdownSource], null, TargetedRecordingItemUses.Both),
            new(TargetedRecordingItemKinds.Metric, "cpu.usage", "%", ["load@cpu0"], null, TargetedRecordingItemUses.Both)
        ];

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
        public TimeSpan Retention => ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline.Retention;
        public IFrameSubscriptionLease AcquireSubscription(FrameSubscriptionRequest request) => new FrameLeaseAdapter(AcquireSubscription());

        public IDisposable AcquireSubscription() => new EmptyLease();
        public FrameTimingSnapshot? Read(TimeSpan window) => null;
        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);
        public IDisposable AcquireSubscription(string subscriptionId, ResourceBreakdownSampleRequest request,
            TimeSpan refreshInterval) => new EmptyLease();
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request) => Running();
        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request,
            TimeSpan refreshInterval) => new EmptyLease();
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        // The recorder's subscription chain: the breakdown publishes the running target once per interval; the
        // metric source holds the subscription and publishes nothing.
        public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string subscriptionId,
            ResourceBreakdownSampleRequest request, TimeSpan interval,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken);
                yield return Running();
            }
        }
        public async IAsyncEnumerable<HardwareMetricSnapshot> SubscribeAsync(string subscriptionId,
            MetricSampleRequest request, TimeSpan interval,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        public Task<ResourceBreakdownSnapshot?> ReadOnceAsync(CancellationToken cancellationToken)
            => Task.FromResult<ResourceBreakdownSnapshot?>(Running());
        public Task<IReadOnlyList<TargetedRecordingCatalogItem>> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Catalog);
        public IReadOnlyList<TargetedRecordingCatalogItem> ReadCurrent() => Catalog;
        public bool IsRunning(TargetedProcessIdentity identity) => true;

        private static ResourceBreakdownSnapshot Running()
            => new(DateTimeOffset.UtcNow,
            [
                new ResourceBreakdownBar("cpu.usage", "%", "capacity", 100, 100, 100,
                [
                    new ResourceSoftwareSegment("software", "Example", "game", "game", 40, 40, 1,
                        [new ResourceProcessSegment(Environment.ProcessId, "example", null, 40, 40, 100)
                        { ProcessStartKey = CurrentStartKey }])
                ])
            ]);
    }

    /// <summary>In-memory settings with a revision that changes on every save.</summary>
    private sealed class MemorySettingsStore : IAppSettingsStore
    {
        private int revision;
        public AppSettings Current { get; private set; } = AppSettingsDefaults.Create();

        public Task<AppSettingsUpdateResult> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Result());
        public Task<AppSettingsUpdateResult> LoadReadOnlyAsync(CancellationToken cancellationToken) => Task.FromResult(Result());
        public Task<AppSettingsUpdateResult> SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            Current = settings;
            revision++;
            return Task.FromResult(Result());
        }
        public Task<AppSettingsUpdateResult> SaveValidatedAsync(AppSettings settings,
            Func<AppSettingsUpdateResult, CancellationToken, Task> validateBeforeCommit, CancellationToken cancellationToken)
            => SaveAsync(settings, cancellationToken);

        private AppSettingsUpdateResult Result() => new(Current, DateTimeOffset.UnixEpoch, "memory",
            new AppSettingsSourceMetadata(AppSettingsSourceKind.Persisted, "test", "input",
                revision.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
    }

    /// <summary>Applies a patch with the product patch applier and saves it, as the coordinator does.</summary>
    private sealed class PatchingCoordinator(MemorySettingsStore store) : IRuntimeSpecializationCoordinator
    {
        public async Task<AppSettingsUpdateResult> ApplyAppSettingsPatchAsync(AppSettingsPatchRequest patch, string reason,
            CancellationToken cancellationToken)
        {
            var previous = await store.LoadAsync(cancellationToken);
            if (patch.ExpectedRevision != previous.Revision)
                return previous with { RuntimeApplicationDisposition = "revisionConflict", RuntimeFailureCode = "settings-revision-conflict" };
            var candidate = AppSettingsPatchApplier.Apply(previous.Settings, patch.Changes);
            if (TargetedRecordingProfileShape.Validate(candidate.TargetedRecording) is { } invalid)
                return previous with { RuntimeApplicationDisposition = "rejectedBeforeCommit", RuntimeFailureCode = invalid };
            return await store.SaveAsync(candidate, cancellationToken) with { RuntimeApplicationDisposition = "savedNotApplied" };
        }

        public Task<CpuBaselineRatioUpdateResult> ApplyCpuBaselineRatioAsync(double? overrideRatio, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<RuntimePlanPublicationResult> RebuildAsync(string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<AppSettingsUpdateResult> ApplyAppSettingsAsync(AppSettings settings, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
