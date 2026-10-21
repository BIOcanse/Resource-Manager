using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class DeviceInformationEndpointTests
{
    [Fact]
    public async Task NativeExtendedSecurityProductFieldsReachCacheHttpAndRoundTripWithoutAnotherRead()
    {
        using var com = new WindowsSecurityProductInformationCollectorTests.Com(); var product = com.Add("Extended");
        using var extended = new WindowsSecurityProductInformationCollectorTests.ExtendedProduct(com, product);
        extended.Numbers[20] = (0, uint.MaxValue);
        var value = Assert.Single(WindowsSecurityProductInformationCollectorTests.Read(com)!);
        var raw = DeviceInformationTestData.Snapshot() with { SecurityInformation = DeviceTopologySecurityInformation.Unavailable with { AntivirusProductsReported = [value], FirewallProductsReported = [] } };
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw))!;
        Assert.Equal(value, Assert.Single(restored.SecurityInformation!.AntivirusProductsReported!));
        var comparer = new DeviceTopologySemanticComparer(); Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(restored));
        var changed = raw with { SecurityInformation = raw.SecurityInformation with { AntivirusProductsReported = [value with { ProductIsDefaultReported = true }] } };
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(changed));
        var persisted = new DeviceTopologyPersistedSnapshot(DeviceTopologySnapshotState.CurrentSchemaVersion, "test", 7, comparer.ComputeHash(raw), raw);
        var reader = new Reader(raw, new InvalidOperationException("whole source failed"), raw);
        using var fixture = new Fixture(reader, persisted); await using var app = await StartAsync(fixture.Provider); using var client = Client(app);
        using var initial = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
        AssertSecurityProductValuesEmpty(initial.RootElement); Assert.Equal(0, reader.ReadCount);
        for (var phase = 0; phase < 3; phase++)
        {
            await fixture.Provider.SampleNowAsync(CancellationToken.None);
            using var result = JsonDocument.Parse(await client.GetStringAsync("/api/device-topology/fields"));
            if (phase == 1) { AssertSecurityProductValuesEmpty(result.RootElement); continue; }
            var row = Field(result.RootElement, "machine", "security_all_antivirus_products_reported").GetProperty("value")[0];
            Assert.Equal("guid\0raw", row.GetProperty("productGuidReported").GetString()); Assert.False(row.GetProperty("productIsDefaultReported").GetBoolean());
            Assert.Equal(2U, row.GetProperty("antivirusScanSubstatusReported").GetUInt32());
            Assert.Equal(3U, row.GetProperty("antivirusSettingsSubstatusReported").GetUInt32());
            Assert.Equal(0U, row.GetProperty("antivirusProtectionUpdateSubstatusReported").GetUInt32());
            Assert.Equal(1U, row.GetProperty("firewallDomainProfileSubstatusReported").GetUInt32());
            Assert.Equal(2U, row.GetProperty("firewallPrivateProfileSubstatusReported").GetUInt32());
            Assert.Equal(3U, row.GetProperty("firewallPublicProfileSubstatusReported").GetUInt32());
            Assert.Equal(uint.MaxValue, row.GetProperty("antivirusDaysUntilExpiredReported").GetUInt32());
        }
        Assert.Equal(3, reader.ReadCount); Assert.Equal(1, extended.Releases); Assert.Equal(1, product.Releases);
        await app.StopAsync();
    }
}
