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
    public async Task DisplayModeHttpUsesOriginalProjectionAndGetNeverCapturesStaticInactiveDeclarations()
    {
        var healthy = DisplayEdidModeInformationCollectorTests.Healthy();
        var inactive = DisplayEdidModeInformationCollectorTests.Snapshot(DisplayEdidModeInformationCollectorTests.Path() with { Active = false, TargetAvailable = false });
        var unsupported = DisplayEdidModeInformationCollectorTests.Bytes(); unsupported[126] = 1;
        unsupported[127] = 0; unsupported[127] = unchecked((byte)-unsupported.Sum(static value => (int)value));
        var reader = new Reader(inactive,
            DisplayEdidModeInformationCollectorTests.Snapshot(DisplayEdidModeInformationCollectorTests.Path(WindowsMonitorEdidReader.CreateReadResult(unsupported))),
            DisplayEdidModeInformationCollectorTests.Snapshot(DisplayEdidModeInformationCollectorTests.Path(DeviceTopologyEdidReadResult.Unavailable)),
            healthy with { Ports = [] }, healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 5; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            if (phase == 3) Assert.DoesNotContain(document.RootElement.GetProperty("devices").EnumerateArray(), static d => d.GetProperty("objectType").GetString() == "display_path");
            else
            {
                var field = Hdr(document.RootElement, DisplayEdidModeInformationCollectorTests.Name);
                Assert.Equal(DisplayEdidModeInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
                if (phase is 1 or 2) Assert.Equal(JsonValueKind.Null, field.GetProperty("value").ValueKind);
                else DisplayEdidModeInformationCollectorTests.AssertValue(field.GetProperty("value"));
                if (phase == 0) Assert.Equal(JsonValueKind.Null, Hdr(document.RootElement, "display_all_resolution_actual").GetProperty("value").ValueKind);
                if (phase == 1) Assert.Equal(Convert.ToBase64String(unsupported), Hdr(document.RootElement, DisplayEdidRawInformationCollectorTests.Name).GetProperty("value").GetString());
            }
            using var stateDocument = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state"));
            AssertInformationFieldsInStateMatchFieldsEndpoint(stateDocument.RootElement, document.RootElement);
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayModePersistedStartupAndWholeCaptureFailureCannotPublishHistoricalCapability()
    {
        var healthy = DisplayEdidModeInformationCollectorTests.Healthy();
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 1,
            new DeviceTopologySemanticComparer().ComputeHash(healthy), healthy);
        var reader = new Reader(healthy, new IOException("recorded capture failure"), healthy);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var startup = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal(JsonValueKind.Null, Hdr(startup.RootElement, DisplayEdidModeInformationCollectorTests.Name).GetProperty("value").ValueKind);
        Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var value = Hdr(document.RootElement, DisplayEdidModeInformationCollectorTests.Name).GetProperty("value");
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind); else DisplayEdidModeInformationCollectorTests.AssertValue(value);
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }

    [Fact]
    public async Task DisplayModeUnifiedSubscriptionUsesOriginalWorkerAndCancellationRetainsCurrent()
    {
        var reader = new Reader(DisplayEdidModeInformationCollectorTests.Healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream") {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "display-edid-mode", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var field = Hdr(message.RootElement.GetProperty("value"), DisplayEdidModeInformationCollectorTests.Name);
        DisplayEdidModeInformationCollectorTests.AssertValue(field.GetProperty("value"));
        Assert.Equal(DisplayEdidModeInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
        Assert.True(reader.ReadCount >= 1);
        await timeout.CancelAsync(); response.Dispose(); await fixture.WaitForNoDemandAndIdleAsync();
        var reads = reader.ReadCount; var current = fixture.Provider.ReadState().InformationFields;
        using var displayed = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        DisplayEdidModeInformationCollectorTests.AssertValue(Hdr(displayed.RootElement, DisplayEdidModeInformationCollectorTests.Name).GetProperty("value"));
        await Task.Delay(1100); Assert.Equal(reads, reader.ReadCount); Assert.Same(current, fixture.Provider.ReadState().InformationFields);
        await app.StopAsync();
    }
}
