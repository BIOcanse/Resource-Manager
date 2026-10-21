using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Theory]
     [InlineData(0x20)]
    public async Task FormulaHttpUsesOriginalFieldWithPassiveGetUnknownRawAndRecovery(byte version)
    {
        var healthy = DisplayEdidFormulaTestData.Snapshot(version);
        var reader = new Reader(healthy, DisplayEdidFormulaTestData.Snapshot(version, unsupported: true),
            new IOException("recorded formula capture failure"), healthy);
        using var fixture = new Fixture(reader); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        _ = await client.GetStringAsync("/api/device-topology/fields"); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 4; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None); var reads = reader.ReadCount;
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            var field = Hdr(document.RootElement, DisplayEdidModeInformationCollectorTests.Name);
            Assert.Equal(DisplayEdidModeInformationCollectorTests.Source, field.GetProperty("definition").GetProperty("sourceId").GetString());
            if (phase is 1 or 2) Assert.Equal(JsonValueKind.Null, field.GetProperty("value").ValueKind);
            else DisplayEdidFormulaTestData.AssertValue(field.GetProperty("value"), version);
            if (phase == 1) Assert.Equal(Convert.ToBase64String(DisplayEdidFormulaTestData.Bytes(version, unsupported: true)),
                Hdr(document.RootElement, DisplayEdidRawInformationCollectorTests.Name).GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, Hdr(document.RootElement, "display_all_resolution_actual").GetProperty("value").ValueKind);
            using var state = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/state"));
            Assert.False(state.RootElement.TryGetProperty("informationFields", out _));
            Assert.Equal(reads, reader.ReadCount);
        }
        await app.StopAsync();
    }
}
