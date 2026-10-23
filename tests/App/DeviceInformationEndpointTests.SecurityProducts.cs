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
    public async Task SecurityProductHttpReadsCacheAndKeepsCategoryFailureSeparateFromEmptyDirectory()
    {
        var phase = 0; var api = new WindowsSecurityProductInformationCollectorTests.Api { Open = provider => {
            var com = new WindowsSecurityProductInformationCollectorTests.Com { CountStatus = phase == 1 && provider == 4 ? 1 : 0 };
            if (phase != 2 || provider != 4) com.Add(provider == 4 ? "Antivirus" : "Firewall"); return com;
        } };
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { SecurityInformation = new WindowsSecurityInformationReader(api).Read() });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Empty(api.Providers);
        for (phase = 0; phase < 4; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            var state = fixture.Provider.ReadState().InformationFields; var calls = api.Providers.Count;
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var field = Field(result.RootElement, "machine", "security_all_antivirus_products_reported"); var value = field.GetProperty("value");
            Assert.Equal("security-products", field.GetProperty("definition").GetProperty("valueKind").GetString());
            Assert.Equal("windows.security-center-products", field.GetProperty("definition").GetProperty("sourceId").GetString());
            if (phase == 1) Assert.Equal(JsonValueKind.Null, value.ValueKind);
            else if (phase == 2) Assert.Equal(0, value.GetArrayLength());
            else { Assert.Equal("Antivirus", value[0].GetProperty("nameReported").GetString()); Assert.Equal(0U, value[0].GetProperty("stateReported").GetUInt32()); }
            Assert.Equal("Firewall", Field(result.RootElement, "machine", "security_all_firewall_products_reported").GetProperty("value")[0].GetProperty("nameReported").GetString());
            Assert.Equal("0", Field(result.RootElement, "machine", "security_all_antivirus_state_reported").GetProperty("value").GetString());
            Assert.True(Field(result.RootElement, "windows-firewall-profile|private", "security_all_windows_firewall_enabled_configured").GetProperty("value").GetBoolean());
            Assert.Equal("Machine Model", Field(result.RootElement, "machine", "machine_all_model_declared").GetProperty("value").GetString());
            Assert.Equal(calls, api.Providers.Count); Assert.Same(state, fixture.Provider.ReadState().InformationFields);
        }
        Assert.Equal(8, api.Providers.Count); await app.StopAsync();
    }

    [Fact]
    public async Task PersistedAndWholeFailedSnapshotDoNotExposeHistoricalSecurityProductLists()
    {
        var api = new WindowsSecurityProductInformationCollectorTests.Api { Open = _ => { var com = new WindowsSecurityProductInformationCollectorTests.Com(); com.Add("Product"); return com; } };
        var raw = DeviceInformationTestData.Snapshot() with { SecurityInformation = new WindowsSecurityInformationReader(api).Read() };
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7,
            new DeviceTopologySemanticComparer().ComputeHash(raw), raw);
        var reader = new Reader(raw, new InvalidOperationException("whole source failed"), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        AssertSecurityProductValuesEmpty(initial.RootElement); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            if (phase == 1) AssertSecurityProductValuesEmpty(result.RootElement);
            else Assert.Equal("Product", Field(result.RootElement, "machine", "security_all_antivirus_products_reported").GetProperty("value")[0].GetProperty("nameReported").GetString());
        }
        Assert.Equal(3, reader.ReadCount); await app.StopAsync();
    }

    [Fact]
    public async Task OriginalWorkerSecurityProductsStreamStopsReadsAfterCancellationAndKeepsCachedDisplay()
    {
        var api = new WindowsSecurityProductInformationCollectorTests.Api { Open = _ => { var com = new WindowsSecurityProductInformationCollectorTests.Com(); com.Add("Product"); return com; } };
        var reader = new Reader(() => DeviceInformationTestData.Snapshot() with { SecurityInformation = new WindowsSecurityInformationReader(api).Read() });
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Empty(api.Providers);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
            new { id = "security-products-worker", path = "/api/device-topology/fields/subscribe?intervalMs=1000" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); using var stream = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var message = JsonDocument.Parse(await ReadFirstObservedInformationAsync(stream, timeout.Token));
        var value = Field(message.RootElement.GetProperty("value"), "machine", "security_all_antivirus_products_reported").GetProperty("value");
        Assert.Equal("Product", value[0].GetProperty("nameReported").GetString()); Assert.Equal(1U, value[0].GetProperty("signatureStatusReported").GetUInt32());
        Assert.True(reader.ReadCount >= 1); Assert.Equal(2 * reader.ReadCount, api.Providers.Count);
        await timeout.CancelAsync(); response.Dispose(); await Task.Delay(250);
        var count = reader.ReadCount; var state = fixture.Provider.ReadState().InformationFields;
        using var cached = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        Assert.Equal("Product", Field(cached.RootElement, "machine", "security_all_antivirus_products_reported").GetProperty("value")[0].GetProperty("nameReported").GetString());
        await Task.Delay(1300); Assert.Equal(count, reader.ReadCount); Assert.Equal(2 * count, api.Providers.Count);
        Assert.Same(state, fixture.Provider.ReadState().InformationFields); await app.StopAsync();
    }

    private static void AssertSecurityProductValuesEmpty(JsonElement snapshot)
    {
        foreach (var name in new[] { "security_all_antivirus_products_reported", "security_all_firewall_products_reported" })
            Assert.Equal(JsonValueKind.Null, Field(snapshot, "machine", name).GetProperty("value").ValueKind);
    }
}
