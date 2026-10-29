using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSmartRecordHistoryEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(42L)]
    public void ActiveRecordWireUsesEnrollmentIdentityAndNeverAddsItToThePlacement(long? sequence)
    {
        var at = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var record = new HostManagerAppliedRecord("cpu_affinity", "record",
            new Dictionary<string, string> { ["rollback-secret"] = "private" })
        {
            Sequence = sequence,
            RecordedAt = sequence.HasValue ? at : null
        };
        var placement = new HostManagerAppliedPlacementReceipt("target", null, "cpu", [record], at, at);
        var state = HostManagerRollbackStateDocument.Empty with { AppliedPlacements = [placement] };
        var snapshot = HostManagerSmartStateWireProjection.Project(state);
        var wire = Assert.Single(Assert.Single(snapshot.AppliedPlacements).Records);
        Assert.Equal(sequence, wire.Sequence);
        Assert.Equal(sequence.HasValue ? "active" : null, wire.State);
        Assert.Equal(record.RecordedAt, wire.RecordedAt);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);
        var json = JsonSerializer.SerializeToElement(snapshot, options);
        var placementJson = Assert.Single(json.GetProperty("appliedPlacements").EnumerateArray());
        Assert.False(placementJson.TryGetProperty("sequence", out _));
        Assert.False(placementJson.TryGetProperty("state", out _));
        Assert.False(placementJson.TryGetProperty("recordedAt", out _));
        var recordJson = Assert.Single(placementJson.GetProperty("records").EnumerateArray());
        if (sequence.HasValue)
        {
            Assert.Equal(sequence, recordJson.GetProperty("sequence").GetInt64());
            Assert.Equal("active", recordJson.GetProperty("state").GetString());
            Assert.Equal(at, recordJson.GetProperty("recordedAt").GetDateTimeOffset());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, recordJson.GetProperty("sequence").ValueKind);
            Assert.Equal(JsonValueKind.Null, recordJson.GetProperty("state").ValueKind);
            Assert.Equal(JsonValueKind.Null, recordJson.GetProperty("recordedAt").ValueKind);
        }
        Assert.False(recordJson.TryGetProperty("metadata", out _));
        Assert.DoesNotContain("rollback-secret", json.GetRawText());
    }

    [Fact]
    public void ActiveWirePreservesEachEnrolledRecordAlongsideAnUnenrolledSibling()
    {
        var at = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var placement = new HostManagerAppliedPlacementReceipt("target", null, "cpu",
            [new("cpu_affinity", "one") { Sequence = 7, RecordedAt = at },
             new("process_priority", "two") { Sequence = 8, RecordedAt = at.AddSeconds(1) },
             new("process_memory_priority", "off")], at, at);
        var wire = Assert.Single(HostManagerSmartStateWireProjection.Project(
            HostManagerRollbackStateDocument.Empty with { AppliedPlacements = [placement] }).AppliedPlacements);

        Assert.Equal(new long?[] { 7, 8, null }, wire.Records.Select(record => record.Sequence));
        Assert.Equal(new string?[] { "active", "active", null }, wire.Records.Select(record => record.State));
        Assert.Equal(new DateTimeOffset?[] { at, at.AddSeconds(1), null }, wire.Records.Select(record => record.RecordedAt));
    }

    [Fact]
    public async Task SessionCanReadAndClearRecordLevelHistoryWithoutElevation()
    {
        var store = new HistoryStore();
        await using var app = await StartAsync(store, "session");
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var response = await client.GetAsync("/api/optimization/smart/records/history?beforeSequence=88&limit=2");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(88L, store.BeforeSequence);
        Assert.Equal(2, store.Limit);
        Assert.Equal(6, page.GetProperty("nextBeforeSequence").GetInt64());
        var entry = Assert.Single(page.GetProperty("records").EnumerateArray());
        Assert.Equal(7, entry.GetProperty("sequence").GetInt64());
        Assert.Equal("history", entry.GetProperty("state").GetString());
        Assert.Equal("restored", entry.GetProperty("restoreResult").GetString());
        Assert.Equal("133900000000000201", entry.GetProperty("processStartKey").GetString());
        Assert.Equal("cpu_affinity", entry.GetProperty("kind").GetString());
        Assert.Equal("record", entry.GetProperty("recordId").GetString());
        Assert.Equal("target", entry.GetProperty("targetId").GetString());
        Assert.Equal("software", entry.GetProperty("softwareId").GetString());
        Assert.Equal("cpu", entry.GetProperty("resourceKind").GetString());
        Assert.Equal(123, entry.GetProperty("processId").GetInt32());
        Assert.Equal("process", entry.GetProperty("processName").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), entry.GetProperty("appliedAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), entry.GetProperty("recordedAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 12, 1, 0, TimeSpan.Zero), entry.GetProperty("restoredAt").GetDateTimeOffset());
        Assert.False(entry.TryGetProperty("records", out _));
        Assert.False(entry.TryGetProperty("updatedAt", out _));
        Assert.False(entry.TryGetProperty("metadata", out _));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());

        using var cleared = await client.PostAsync("/api/optimization/smart/records/history/clear", null);
        cleared.EnsureSuccessStatusCode();
        Assert.Equal(3, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removedCount").GetInt64());
        Assert.Equal(1, store.ClearCount);
    }

    [Theory]
    [InlineData("restored")]
    [InlineData("targetExited")]
    [InlineData("ownershipLost")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public async Task HistoryWirePreservesTheContractRestoreResultValue(string restoreResult)
    {
        var store = new HistoryStore { RestoreResult = restoreResult };
        await using var app = await StartAsync(store, "session");
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var response = await client.GetAsync("/api/optimization/smart/records/history");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(page.GetProperty("records").EnumerateArray());
        Assert.Equal(restoreResult, entry.GetProperty("restoreResult").GetString());
    }

    [Fact]
    public async Task OmittingCursorUsesLatestPageWithBoundedDefaultLimit()
    {
        var store = new HistoryStore();
        await using var app = await StartAsync(store, "session");
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var response = await client.GetAsync("/api/optimization/smart/records/history");
        response.EnsureSuccessStatusCode();
        Assert.Null(store.BeforeSequence);
        Assert.Equal(100, store.Limit);
    }

    [Theory]
    [InlineData("?beforeSequence=0")]
    [InlineData("?beforeSequence=-1")]
    [InlineData("?beforeSequence=not-a-number")]
    [InlineData("?limit=0")]
    [InlineData("?limit=501")]
    [InlineData("?limit=-1")]
    public async Task InvalidPaginationDoesNotReadStore(string query)
    {
        var store = new HistoryStore();
        await using var app = await StartAsync(store, "session");
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var response = await client.GetAsync("/api/optimization/smart/records/history" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task ReadScopeCannotReadOrClearSessionHistory()
    {
        var store = new HistoryStore();
        await using var app = await StartAsync(store, "read");
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var read = await client.GetAsync("/api/optimization/smart/records/history");
        using var clear = await client.PostAsync("/api/optimization/smart/records/history/clear", null);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, store.ClearCount);
    }

    private static async Task<WebApplication> StartAsync(HistoryStore store, string scope)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, scope);
        builder.Services.AddSingleton<IHostManagerSmartRecordHistoryStore>(store);
        var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        app.MapHostManagerSmartRecordHistoryEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class HistoryStore : IHostManagerSmartRecordHistoryStore
    {
        public long? BeforeSequence { get; private set; }
        public int Limit { get; private set; }
        public int ReadCount { get; private set; }
        public int ClearCount { get; private set; }
        public string RestoreResult { get; init; } = "restored";

        public Task<HostManagerSmartRecordHistoryPage> ReadHistoryAsync(long? beforeSequence, int limit, CancellationToken token)
        {
            BeforeSequence = beforeSequence;
            Limit = limit;
            ReadCount++;
            var at = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new HostManagerSmartRecordHistoryPage([
                new HostManagerSmartRecordHistoryEntry(7, "history", "cpu_affinity", "record", "target", "software", "cpu",
                    at, 123, "133900000000000201", "process", at, at.AddMinutes(1), RestoreResult)], 6));
        }

        public Task<long> ClearHistoryAsync(CancellationToken token)
        {
            ClearCount++;
            return Task.FromResult(3L);
        }
    }
}
