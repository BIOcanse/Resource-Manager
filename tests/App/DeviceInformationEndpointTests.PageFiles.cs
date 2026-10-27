using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task PageFileHttpUsesSingleFetchAndSeparatesUnitFailureFromUnavailableDirectory()
    {
        var calls = new PageFileInformationCollectorTests.Calls { Names = [PageFileInformationCollectorTests.PathA, PageFileInformationCollectorTests.PathB],
            Frames = [PageFileInformationCollectorTests.NativeFrame(), PageFileInformationCollectorTests.NativeFrame(10, 0, 1)] };
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { PageFileInformation = PageFileInformationCollectorTests.Read(calls) });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, calls.Enumerations); Assert.Equal(0, calls.SizeCalls);
        for (var phase = 0; phase < 5; phase++)
        {
            calls.PageBytes = phase == 1 ? 0U : 8192U; calls.Complete = phase != 2;
            calls.Names = phase == 4 ? [] : [PageFileInformationCollectorTests.PathA, PageFileInformationCollectorTests.PathB];
            var sampledHttpFields1 = await client.GetStringAsync("/api/device-topology/fields");
            var current = fixture.Provider.ReadState().InformationFields; var count = calls.Enumerations; var sizes = calls.SizeCalls;
            using var result = JsonDocument.Parse(sampledHttpFields1);
            var files = result.RootElement.GetProperty("devices").EnumerateArray().Where(item => item.GetProperty("objectType").GetString() == "page_file").ToArray();
            Assert.Equal(phase == 4 ? 0 : 2, files.Length);
            if (phase == 2) { AssertPageFileValuesEmpty(result.RootElement, PageFileInformationCollectorTests.IdA); AssertPageFileValuesEmpty(result.RootElement, PageFileInformationCollectorTests.IdB); }
            else if (phase != 4)
            {
                var total = Field(result.RootElement, PageFileInformationCollectorTests.IdA, "memory_all_pagefile_capacity").GetProperty("value");
                Assert.Equal(phase == 1 ? null : "8192000", total.GetString());
                Assert.Equal("1000", Field(result.RootElement, PageFileInformationCollectorTests.IdA, "memory_all_pagefile_capacity_reported_pages").GetProperty("value").GetString());
                Assert.Equal("0", Field(result.RootElement, PageFileInformationCollectorTests.IdB, "memory_all_pagefile_used_reported_pages").GetProperty("value").GetString());
            }
            Assert.Equal("Machine Model", Field(result.RootElement, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Equal(count, calls.Enumerations); Assert.Equal(sizes, calls.SizeCalls); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task PersistedPageFileValuesAndWholeSourceFailureRemainEmptyUntilNewCapture()
    {
        var file = PageFileInformationCollectorTests.Sample() with { CapacityPages = ulong.MaxValue };
        var raw = DeviceInformationTestData.Snapshot() with { PageFileInformation = [file] };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new IOException("failed"), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        AssertPageFileValuesEmpty(initial.RootElement, PageFileInformationCollectorTests.IdA); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            var sampledHttpFields2 = await client.GetStringAsync("/api/device-topology/fields");
            using var value = JsonDocument.Parse(sampledHttpFields2);
            if (phase == 1) AssertPageFileValuesEmpty(value.RootElement, PageFileInformationCollectorTests.IdA);
            else Assert.Equal("151115727451828646830080", Field(value.RootElement, PageFileInformationCollectorTests.IdA, "memory_all_pagefile_capacity").GetProperty("value").GetString());
        }
        Assert.Equal(3, reader.ReadCount); await app.StopAsync();
    }

    [Fact]
    public async Task OriginalWorkerAndUnifiedPageFileStreamStopNativeReadsOnCancellationAndRetainDisplay()
    {
        var calls = new PageFileInformationCollectorTests.Calls();
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { PageFileInformation = PageFileInformationCollectorTests.Read(calls) });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = ReadPublishedFields(fixture.Provider, app); Assert.Equal(0, calls.Enumerations);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "pagefile-worker", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var field = Field(message.RootElement.GetProperty("value"), PageFileInformationCollectorTests.IdA, "memory_all_pagefile_used");
        Assert.Equal("1638400", field.GetProperty("value").GetString());
        Assert.Equal("windows.pagefile", field.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.Equal("unsigned-integer", field.GetProperty("definition").GetProperty("valueKind").GetString());
        Assert.Equal("B", field.GetProperty("definition").GetProperty("unit").GetString());
        Assert.True(calls.Enumerations >= 1); Assert.Equal(calls.Enumerations, calls.SizeCalls);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var count = calls.Enumerations; var current = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(ReadPublishedFields(fixture.Provider, app));
        Assert.Equal("1638400", Field(cached.RootElement, PageFileInformationCollectorTests.IdA, "memory_all_pagefile_used").GetProperty("value").GetString());
        await Task.Delay(1300); Assert.Equal(count, calls.Enumerations); Assert.Equal(count, calls.SizeCalls);
        Assert.Same(current, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static void AssertPageFileValuesEmpty(JsonElement snapshot, string identity)
    {
        var file = Assert.Single(snapshot.GetProperty("devices").EnumerateArray(), item => item.GetProperty("deviceIdentity").GetString() == identity);
        Assert.Equal(8, file.GetProperty("fields").GetArrayLength());
        Assert.All(file.GetProperty("fields").EnumerateArray(), field => Assert.Equal(JsonValueKind.Null, field.GetProperty("value").ValueKind));
    }
}
