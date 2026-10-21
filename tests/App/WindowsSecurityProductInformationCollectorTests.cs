using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsSecurityProductInformationCollectorTests
{
    [Theory]
    [InlineData(0U, 0U)] [InlineData(1U, 1U)] [InlineData(2U, 0U)] [InlineData(3U, 1U)]
    public void ProductStatesKeepAllSdkCodesAndZeroWithoutInferringCategoryHealth(uint state, uint signature)
    {
        using var com = new Com(); var product = com.Add(); product.Numbers[8] = (0, state); product.Numbers[9] = (0, signature);
        var result = Assert.Single(Read(com)!); Assert.Equal(state, result.StateReported); Assert.Equal(signature, result.SignatureStatusReported);
    }

    [Theory]
    [InlineData(1, 0U)] [InlineData(-2147467259, 0U)] [InlineData(0, 4U)] [InlineData(0, uint.MaxValue)]
    public void FailedOrUnknownNumbersOnlyClearTheirOwnProperties(int status, uint code)
    {
        using var com = new Com(); var product = com.Add(); product.Numbers[8] = product.Numbers[9] = (status, code);
        var result = Assert.Single(Read(com)!); Assert.Null(result.StateReported); Assert.Null(result.SignatureStatusReported);
        Assert.Equal("Product", result.NameReported); Assert.Equal("raw timestamp", result.StateTimestampReported); Assert.Equal(1, product.Releases);
    }

    [Theory]
    [InlineData(1)] [InlineData(-2147467259)]
    public void FailedStringGetterDoesNotDereferenceOrFreeDirtyOutAndKeepsOtherStrings(int status)
    {
        using var com = new Com(); var product = com.Add(); product.Text[7] = (status, "dirty");
        var result = Assert.Single(Read(com)!); Assert.Null(result.NameReported); Assert.Equal("C:\\Repair.exe", result.RemediationPathReported);
        Assert.Equal("raw timestamp", result.StateTimestampReported); Assert.Equal(2, com.StringReads); Assert.Equal(2, com.StringFrees); com.AssertStringsReleased();
    }

    [Fact]
    public void NullEmptyAndEmbeddedNulBstrRemainDistinctAndMissingPropertyDoesNotDropRecord()
    {
        using var com = new Com(); var first = com.Add(" A\0B "); first.Text[10] = (0, ""); first.Text[11] = (0, null);
        var second = com.Add(" A\0B "); second.Missing(8); second.Missing(11);
        var records = Read(com)!; Assert.Equal(2, records.Count); Assert.Equal(" A\0B ", records[0].NameReported);
        Assert.Equal("", records[0].RemediationPathReported); Assert.Null(records[0].StateTimestampReported);
        Assert.Null(records[1].StateReported); Assert.Equal(" A\0B ", records[1].NameReported); Assert.Equal(1, second.Releases); com.AssertStringsReleased();
    }

    [Fact]
    public void OversizedBstrIsFreedAndOnlyItsPropertyBecomesEmpty()
    {
        using var com = new Com(); com.Add(new string('x', 32768));
        var result = Assert.Single(Read(com)!); Assert.Null(result.NameReported); Assert.Equal("C:\\Repair.exe", result.RemediationPathReported);
        Assert.Equal(3, com.StringReads); Assert.Equal(3, com.StringFrees); com.AssertStringsReleased();
    }

    [Theory]
    [InlineData(32, true)] [InlineData(33, false)]
    public void TotalStringBudgetAcceptsBoundaryAndRejectsWholeOversizedDirectory(int finalLength, bool complete)
    {
        using var com = new Com();
        for (var i = 0; i < 33; i++) { var product = com.Add(new string('x', i == 32 ? finalLength : 32767)); product.Text[10] = product.Text[11] = (0, null); }
        var result = Read(com);
        if (complete) Assert.Equal(33, result!.Count); else Assert.Null(result);
        Assert.Equal(33, com.StringFrees); Assert.All(com.Products, product => Assert.Equal(1, product.Releases));
        Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); com.AssertStringsReleased();
    }

    [Fact]
    public void TwoCategoriesUseSeparateListsAndNeverMergeDuplicateProductNames()
    {
        var lists = new List<Com>(); var api = new Api { Open = provider => {
            var com = new Com(); var first = com.Add("same"); first.Numbers[8] = (0, provider == 4 ? 0U : 3U); com.Add("same"); lists.Add(com); return com;
        } };
        var result = new WindowsSecurityInformationReader(api).Read(); Assert.Equal(new uint[] { 4, 1 }, api.Providers);
        Assert.Equal(2, result.AntivirusProductsReported!.Count); Assert.Equal(2, result.FirewallProductsReported!.Count);
        Assert.Equal(0U, result.AntivirusProductsReported[0].StateReported); Assert.Equal(3U, result.FirewallProductsReported[0].StateReported);
        Assert.Equal(new uint[] { 4 }, lists[0].Providers); Assert.Equal(new uint[] { 1 }, lists[1].Providers);
        Assert.Equal(0U, result.AntivirusHealthReported); Assert.Equal(3U, result.FirewallHealthReported);
        Assert.All(lists, com => { Assert.Equal(1, com.Creates); Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); });
    }

    [Fact]
    public void TypedProjectionAndRoundTripPreserveOrderPropertiesAndSemanticChanges()
    {
        var api = new Api { Open = _ => { var com = new Com(); com.Add("second"); com.Add("first"); com.Add("second"); return com; } };
        var raw = DeviceInformationTestData.Snapshot() with { SecurityInformation = new WindowsSecurityInformationReader(api).Read() };
        var snapshot = DeviceTopologyInformationProjection.Create(raw, DateTimeOffset.UtcNow);
        var machine = Assert.Single(snapshot.Devices, item => item.DeviceIdentity == "machine");
        var field = Assert.Single(machine.Fields, item => item.Definition.Name == "security_all_antivirus_products_reported");
        Assert.Equal("security-products", field.Definition.ValueKind); Assert.Equal("record[]", field.Definition.Unit);
        Assert.Equal("windows.security-center-products", field.Definition.SourceId); Assert.Equal("all", field.Definition.Vendor);
        Assert.Equal(new[] { "second", "first", "second" }, field.Value!.Value.EnumerateArray().Select(item => item.GetProperty("nameReported").GetString()));
        Assert.Equal(0U, field.Value.Value[0].GetProperty("stateReported").GetUInt32());
        Assert.DoesNotContain(snapshot.Devices, item => item.ObjectType == "security_product");
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw))!;
        Assert.Equal(raw.SecurityInformation.AntivirusProductsReported, restored.SecurityInformation.AntivirusProductsReported);
        var comparer = new DeviceTopologySemanticComparer(); Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(restored));
        var altered = raw with { SecurityInformation = raw.SecurityInformation with { AntivirusProductsReported = [] } };
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(altered));
        var products = raw.SecurityInformation.AntivirusProductsReported!;
        altered = raw with { SecurityInformation = raw.SecurityInformation with { AntivirusProductsReported = [products[1], products[0], products[2]] } };
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(altered));
    }
}
