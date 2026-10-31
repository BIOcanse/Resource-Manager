using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.NativeUi.Preferences;
using ResourceManager.NativeUi;

namespace Resource_Manager_APP.Tests;

// Migration frontend-preferences-overlays-to-backend-v1: the window process writes the overlay entries an older preference
// cache still carries to the backend setting performanceOverlay once, and removes them from the cache only after the backend
// committed them (docs/frontend_block_trees_2026-10-26.md「浮窗设置归后端」).
public sealed class FrontendOverlayBackendMigrationTests : IDisposable
{
    private static readonly Uri ApiBase = new("http://127.0.0.1:19321/");
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
        ?? throw new InvalidOperationException("A registered validation scratch root is required."),
        "rm-overlay-backend-migration-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(root, "frontend-preferences.cache.json");

    public FrontendOverlayBackendMigrationTests() => Directory.CreateDirectory(root);

    private const string OldCache = """
        {"appearance":{"language":"de-DE"},
         "overlays":{
          "game":{"enabled":true,"mode":"injected","sizeMode":"windowRatio","fontSizePx":22,"regionWidthRatio":0.4,
            "regionHeightRatio":0.3,"anchor":"bottomRight","marginPx":30,
            "items":[{"kind":"software","id":"cpu.usage","source":"auto","intervalMs":1000,"syncPrecision":"high"}]},
          "Other":{"enabled":false,"metrics":["target.fps","cpu.temperature"],
            "metricSources":{"cpu.temperature":{"fieldName":"cpu_amd_tctl_temperature_celsius","deviceIdentity":"cpu|0"}}}}}
        """;

    [Fact]
    public async Task MigrationUsesTheActiveSessionsConfiguredEndpointAndTransport()
    {
        File.WriteAllText(Cache, OldCache);
        var owner = new FrontendPreferencesOwner(Cache);
        var backend = new FakeBackend("""{"revision":"r1","settings":{"performanceOverlay":{"software":{}}}}""");
        using var session = new BackendServiceSession(ApiBase.AbsoluteUri, apiKeyReader: () => "migration-fixture-key",
            identityReader: () => new BackendProcessIdentity(123, 456, "isolated-backend.exe"),
            handlerFactory: () => new MigrationHandler(backend));
        session.PrepareWindowTransport();
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Completed,
            await FrontendOverlayBackendMigration.RunAsync(owner, session.ApiBaseAddress, session.SendFrontendRequestAsync, default));
        Assert.Equal(["GET /api/settings/app", "PATCH /api/settings/app"], backend.Requests);
        Assert.Empty(owner.PendingOverlayEntries());
    }

    private sealed class MigrationHandler(FakeBackend backend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal("migration-fixture-key", Assert.Single(request.Headers.GetValues(
                ResourceManager.Shared.Runtime.TrustedAccess.TrustedAccessHeaders.KeyHeader)));
            return backend.SendAsync(request, cancellation);
        }
    }

    [Fact]
    public async Task OldEntriesArePatchedWholeAndRemovedOnlyAfterTheBackendCommitted()
    {
        File.WriteAllText(Cache, OldCache);
        var owner = new FrontendPreferencesOwner(Cache);
        var backend = new FakeBackend("""{"revision":"r1","settings":{"version":"1","performanceOverlay":{"software":{}}}}""");
        backend.OnPatch = () => Assert.Contains("\"overlays\"", File.ReadAllText(Cache));

        var outcome = await FrontendOverlayBackendMigration.RunAsync(owner, ApiBase, backend.SendAsync, default);

        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Completed, outcome);
        Assert.Equal(["GET /api/settings/app", "PATCH /api/settings/app"], backend.Requests);
        var expected = JsonNode.Parse("""
            {"expectedRevision":"r1","changes":{"performanceOverlay":{"software":{
              "game":{"enabled":true,"mode":"injected",
                "items":[{"kind":"software","id":"cpu.usage","source":"auto","intervalMs":1000,"syncPrecision":"high"}],
                "sizeMode":"windowRatio","fontSizePx":22,"regionWidthRatio":0.4,"regionHeightRatio":0.3,"anchor":"bottomRight",
                "marginPx":30},
              "Other":{"enabled":false,
                "items":[{"kind":"frames","id":"frames.fps","source":"auto","intervalMs":null,"syncPrecision":"mid"},
                  {"kind":"metric","id":"cpu.temperature","source":"cpu_amd_tctl_temperature_celsius@cpu|0","intervalMs":null,
                   "syncPrecision":"mid"}]}}}}}
            """);
        Assert.True(JsonNode.DeepEquals(expected, backend.PatchBody), backend.PatchBody?.ToJsonString());
        // The entries are gone from the file; the preferences stay.
        Assert.DoesNotContain("\"overlays\"", File.ReadAllText(Cache));
        var restarted = new FrontendPreferencesOwner(Cache);
        Assert.Equal("de-DE", restarted.Read().Appearance.Language);
        Assert.Empty(restarted.PendingOverlayEntries());
        // Nothing pending: the next start sends nothing.
        var again = new FakeBackend("{}");
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.NothingPending,
            await FrontendOverlayBackendMigration.RunAsync(restarted, ApiBase, again.SendAsync, default));
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task SoftwareTheBackendAlreadyHasIsSkippedIgnoringCase()
    {
        File.WriteAllText(Cache, OldCache);
        var backend = new FakeBackend("""
            {"revision":"r7","settings":{"version":"1","performanceOverlay":{"software":{"GAME":{"enabled":false}}}}}
            """);
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Completed,
            await FrontendOverlayBackendMigration.RunAsync(new FrontendPreferencesOwner(Cache), ApiBase, backend.SendAsync, default));
        var software = backend.PatchBody!["changes"]!["performanceOverlay"]!["software"]!.AsObject();
        Assert.Equal(["Other"], software.Select(static pair => pair.Key));
        Assert.Equal("r7", backend.PatchBody["expectedRevision"]!.GetValue<string>());
        Assert.Empty(new FrontendPreferencesOwner(Cache).PendingOverlayEntries());

        // The backend has every id: nothing is written and the cache entries are removed (the backend wins).
        File.WriteAllText(Cache, OldCache);
        var complete = new FakeBackend("""
            {"revision":"r8","settings":{"version":"1","performanceOverlay":{"software":{"Game":{},"other":{}}}}}
            """);
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Completed,
            await FrontendOverlayBackendMigration.RunAsync(new FrontendPreferencesOwner(Cache), ApiBase, complete.SendAsync, default));
        Assert.Equal(["GET /api/settings/app"], complete.Requests);
        Assert.Empty(new FrontendPreferencesOwner(Cache).PendingOverlayEntries());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"runtimeApplicationDisposition":"rejectedBeforeCommit","runtimeFailureCode":"performance-overlay-too-many-software"}""")]
    [InlineData(HttpStatusCode.Conflict, """{"runtimeApplicationDisposition":"revisionConflict"}""")]
    [InlineData(HttpStatusCode.OK, """{"revision":"r2","runtimeApplicationDisposition":"savedNotApplied"}""")]
    [InlineData(HttpStatusCode.OK, """{"revision":"r2"}""")]
    [InlineData(HttpStatusCode.OK, """{"revision":"r2","runtimeApplicationDisposition":"committedAndApplied","settings":{"version":"1"}}""")]
    [InlineData(HttpStatusCode.OK, """{"revision":"r2","runtimeApplicationDisposition":"committedAndApplied","settings":{"performanceOverlay":{"software":{"game":{}}}}}""")]
    public async Task ARejectedWriteKeepsTheCacheEntriesForTheNextStart(HttpStatusCode status, string response)
    {
        File.WriteAllText(Cache, OldCache);
        var backend = new FakeBackend("""{"revision":"r1","settings":{"version":"1","performanceOverlay":{"software":{}}}}""")
        {
            PatchStatus = status, PatchResponse = response
        };
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Failed,
            await FrontendOverlayBackendMigration.RunAsync(new FrontendPreferencesOwner(Cache), ApiBase, backend.SendAsync, default));
        Assert.Equal(2, new FrontendPreferencesOwner(Cache).PendingOverlayEntries().Count);
    }

    [Fact]
    public async Task AnUnreachableBackendKeepsTheCacheEntries()
    {
        File.WriteAllText(Cache, OldCache);
        var owner = new FrontendPreferencesOwner(Cache);
        // The read-time cache migrations (overlay items, import marker) may rewrite the file; the failed write must not.
        var pending = owner.PendingOverlayEntries();
        var before = File.ReadAllText(Cache);
        static Task<HttpResponseMessage> Unreachable(HttpRequestMessage request, CancellationToken cancellation) =>
            throw new HttpRequestException("connection refused");
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Failed,
            await FrontendOverlayBackendMigration.RunAsync(owner, ApiBase, Unreachable, default));
        Assert.Equal(before, File.ReadAllText(Cache));
        Assert.Equal(pending.Count, new FrontendPreferencesOwner(Cache).PendingOverlayEntries().Count);
    }

    [Fact]
    public void MissingOrMalformedFieldsUseBackendDefaultsAndNumericRangesUseTheBackendWriteGate()
    {
        var entry = FrontendOverlayBackendMigration.ConvertEntry("game", JsonNode.Parse("""
            {"enabled":"yes","mode":"hooked","fontSizePx":200,"marginPx":-1,"regionWidthRatio":0,"anchor":"center",
             "sizeMode":"windowRatio","regionHeightRatio":0.5,
             "items":[{"kind":"metric","id":"x","source":"auto","intervalMs":null,"syncPrecision":"mid"},
                      {"kind":"metric","id":"X","source":"auto","intervalMs":null,"syncPrecision":"mid"}]}
            """)!.AsObject());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"fontSizePx":200,"marginPx":-1,"regionWidthRatio":0,"sizeMode":"windowRatio","regionHeightRatio":0.5}
            """), entry), entry.ToJsonString());
    }

    [Theory]
    [InlineData("""{"version":"1"}""")]
    [InlineData("""{"performanceOverlay":null}""")]
    [InlineData("""{"performanceOverlay":{}}""")]
    [InlineData("""{"performanceOverlay":{"software":null}}""")]
    [InlineData("""{"performanceOverlay":{"software":{"game":null}}}""")]
    public async Task AnOlderOrMalformedBackendNeverReceivesAPatchOrClearsTheCache(string settings)
    {
        File.WriteAllText(Cache, OldCache);
        var backend = new FakeBackend(new JsonObject { ["revision"] = "r1", ["settings"] = JsonNode.Parse(settings) }.ToJsonString());
        var owner = new FrontendPreferencesOwner(Cache);
        var pending = owner.PendingOverlayEntries();
        var before = File.ReadAllText(Cache);

        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Failed,
            await FrontendOverlayBackendMigration.RunAsync(owner, ApiBase, backend.SendAsync, default));

        Assert.Equal(["GET /api/settings/app"], backend.Requests);
        Assert.Equal(before, File.ReadAllText(Cache));
        Assert.Equal(pending.Count, owner.PendingOverlayEntries().Count);
    }

    [Theory]
    [InlineData("committedAndApplied")]
    [InlineData("committedWithCapabilityConstraints")]
    [InlineData("committedWithDeliveryFailures")]
    public async Task ConcurrentPreferencesSaveSurvivesEveryCommittedDisposition(string disposition)
    {
        File.WriteAllText(Cache, OldCache);
        var owner = new FrontendPreferencesOwner(Cache);
        var patchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend("""{"revision":"r1","settings":{"performanceOverlay":{"software":{}}}}""")
        {
            PatchResponse = new JsonObject { ["revision"] = "r2", ["runtimeApplicationDisposition"] = disposition,
                ["settings"] = JsonNode.Parse("""{"performanceOverlay":{"software":{"game":{},"Other":{}}}}""") }.ToJsonString(),
            BeforePatchResponse = async () => { patchStarted.SetResult(); await releasePatch.Task; }
        };
        var migration = FrontendOverlayBackendMigration.RunAsync(owner, ApiBase, backend.SendAsync, default);
        await patchStarted.Task;
        try
        {
            owner.SaveGlobal(owner.Read() with { Appearance = new() { Language = "ja-JP" } });
            Assert.Equal(2, owner.PendingOverlayEntries().Count);
        }
        finally { releasePatch.SetResult(); }
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Completed, await migration);
        var restarted = new FrontendPreferencesOwner(Cache);
        Assert.Equal("ja-JP", restarted.Read().Appearance.Language);
        Assert.Empty(restarted.PendingOverlayEntries());
    }

    [Fact]
    public async Task FailedCacheReplacementKeepsPendingEntriesInMemoryAndOnDisk()
    {
        File.WriteAllText(Cache, OldCache);
        var owner = new FrontendPreferencesOwner(Cache);
        owner.Read();
        var backend = new FakeBackend("""{"revision":"r1","settings":{"performanceOverlay":{"software":{}}}}""");
        using (var blocked = new FileStream(Cache, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(FrontendOverlayBackendMigration.Outcome.Failed,
                await FrontendOverlayBackendMigration.RunAsync(owner, ApiBase, backend.SendAsync, default));
            Assert.Equal(2, owner.PendingOverlayEntries().Count);
        }
        Assert.Equal(2, new FrontendPreferencesOwner(Cache).PendingOverlayEntries().Count);
        // A later start can complete cleanup after the lock is released.
        Assert.Equal(FrontendOverlayBackendMigration.Outcome.Completed,
            await FrontendOverlayBackendMigration.RunAsync(owner, ApiBase, backend.SendAsync, default));
        Assert.Empty(new FrontendPreferencesOwner(Cache).PendingOverlayEntries());
    }

    [Fact]
    public void TheOldItemCheckStillGuardsTheConvertedItems()
    {
        // FrontendOverlayItems.Issue is the check the cache used; the migration sends only item lists it accepts.
        static FrontendOverlayItem Fine(int index) => new("metric", "m." + index, "auto", null, "mid");
        Assert.Null(FrontendOverlayItems.Issue([]));
        Assert.Null(FrontendOverlayItems.Issue(Enumerable.Range(0, FrontendOverlayItems.MaximumItems).Select(Fine).ToArray()));
        Assert.Null(FrontendOverlayItems.Issue([new("metric", "cpu.usage", "auto", FrontendOverlayItems.MinimumIntervalMs, "low"),
            new("software", "cpu.usage", "auto", FrontendOverlayItems.MaximumIntervalMs, "high")]));
        Assert.Equal("too-many", FrontendOverlayItems.Issue(Enumerable.Range(0, FrontendOverlayItems.MaximumItems + 1).Select(Fine).ToArray()));
        Assert.Equal("duplicate", FrontendOverlayItems.Issue([Fine(1), new("metric", "M.1", "auto", null, "mid")]));
        Assert.Equal("frames-interval", FrontendOverlayItems.Issue([new("frames", "frames.fps", "auto", 500, "mid")]));
        Assert.Equal("interval", FrontendOverlayItems.Issue([new("metric", "x", "auto", 99, "mid")]));
        Assert.Equal("precision", FrontendOverlayItems.Issue([new("metric", "x", "auto", null, "max")]));
        Assert.Equal("source", FrontendOverlayItems.Issue([new("metric", "x", " ", null, "mid")]));
        Assert.Equal("kind", FrontendOverlayItems.Issue([new("device", "x", "auto", null, "mid")]));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }

    private sealed class FakeBackend(string settings)
    {
        internal List<string> Requests { get; } = [];
        internal JsonNode? PatchBody { get; private set; }
        internal Action? OnPatch { get; set; }
        internal Func<Task>? BeforePatchResponse { get; init; }
        internal HttpStatusCode PatchStatus { get; init; } = HttpStatusCode.OK;
        internal string PatchResponse { get; init; } =
            """{"revision":"r2","runtimeApplicationDisposition":"committedAndApplied","settings":{"version":"1","performanceOverlay":{"software":{"game":{},"Other":{}}}}}""";

        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal(ApiBase.Authority, request.RequestUri!.Authority);
            Requests.Add($"{request.Method} {request.RequestUri.AbsolutePath}");
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, settings);
            PatchBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            OnPatch?.Invoke();
            if (BeforePatchResponse is not null) await BeforePatchResponse();
            return Json(PatchStatus, PatchResponse);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
