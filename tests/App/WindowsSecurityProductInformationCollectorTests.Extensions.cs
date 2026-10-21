using System.Runtime.InteropServices;
using System.Text.Json;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsSecurityProductInformationCollectorTests
{
    private static readonly string[] ExtendedNames = ["ProductGuidReported", "ProductIsDefaultReported", "AntivirusScanSubstatusReported",
        "AntivirusSettingsSubstatusReported", "AntivirusProtectionUpdateSubstatusReported", "FirewallDomainProfileSubstatusReported",
        "FirewallPrivateProfileSubstatusReported", "FirewallPublicProfileSubstatusReported", "AntivirusDaysUntilExpiredReported"];

    [Fact]
    public void VersionedInterfacesAndSlotsMatchTheReviewedMicrosoftHeader()
    {
        var assembly = GetType().Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".Fixtures.Security.windows-sdk-security-products.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!; using var sdk = JsonDocument.Parse(stream);
        var interfaces = sdk.RootElement.GetProperty("extendedInterfaces");
        Assert.Equal(Guid.Parse(interfaces.GetProperty("IWscProduct2").GetProperty("iid").GetString()!), WindowsSecurityProductListReadSession.Product2Id);
        Assert.Equal(Guid.Parse(interfaces.GetProperty("IWscProduct3").GetProperty("iid").GetString()!), WindowsSecurityProductListReadSession.Product3Id);
        var slots = interfaces.EnumerateObject().SelectMany(x => x.Value.GetProperty("getters").EnumerateArray()).ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("slot").GetInt32());
        foreach (var (name, actualSlot) in new[] {
            ("get_ProductGuid", WindowsSecurityProductListReadSession.GuidSlot),
            ("get_ProductIsDefault", WindowsSecurityProductListReadSession.DefaultSlot),
            ("get_AntivirusScanSubstatus", WindowsSecurityProductListReadSession.ScanSlot),
            ("get_AntivirusSettingsSubstatus", WindowsSecurityProductListReadSession.SettingsSlot),
            ("get_AntivirusProtectionUpdateSubstatus", WindowsSecurityProductListReadSession.ProtectionUpdateSlot),
            ("get_FirewallDomainProfileSubstatus", WindowsSecurityProductListReadSession.DomainProfileSlot),
            ("get_FirewallPrivateProfileSubstatus", WindowsSecurityProductListReadSession.PrivateProfileSlot),
            ("get_FirewallPublicProfileSubstatus", WindowsSecurityProductListReadSession.PublicProfileSlot),
            ("get_AntivirusDaysUntilExpired", WindowsSecurityProductListReadSession.DaysSlot) })
            Assert.Equal(slots[name], actualSlot);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(-1, true)] [InlineData(int.MinValue, true)]
    public void Product3UsesItsOwnInterfaceAndBalancesReferencesWithRealBoolWidth(int boolean, bool expected)
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product) { BooleanValue = boolean };
        extended.Numbers[20] = (0, uint.MaxValue);
        var value = Assert.Single(Read(com)!);
        Assert.Equal("guid\0raw", value.ProductGuidReported); Assert.Equal(expected, value.ProductIsDefaultReported);
        Assert.Equal(new uint?[] { 2, 3, 0, 1, 2, 3 }, new[] { value.AntivirusScanSubstatusReported, value.AntivirusSettingsSubstatusReported,
            value.AntivirusProtectionUpdateSubstatusReported, value.FirewallDomainProfileSubstatusReported, value.FirewallPrivateProfileSubstatusReported, value.FirewallPublicProfileSubstatusReported });
        Assert.Equal(uint.MaxValue, value.AntivirusDaysUntilExpiredReported);
        Assert.Equal(new[] { WindowsSecurityProductListReadSession.Product3Id }, extended.Queries);
        Assert.Equal(Enumerable.Range(12, 9), extended.Requested); Assert.Equal(new[] { 7, 8, 9, 10, 11 }, product.Requested);
        Assert.Equal(1, extended.Releases); Assert.Equal(1, product.Releases); Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes);
        Assert.Equal(4, com.StringReads); Assert.Equal(4, com.StringFrees); com.AssertStringsReleased();
    }

    [Theory]
    [InlineData(1, 0, false, true)] [InlineData(-2147467262, 0, false, true)] [InlineData(0, 0, true, true)]
    [InlineData(-2147467262, -2147467262, false, false)] [InlineData(1, 1, false, false)]
    public void FailedVersionedQueriesIgnoreDirtyOutAndKeepOriginalFiveFields(int status3, int status2, bool empty3, bool hasExtension)
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product)
        { Product3Status = status3, Product2Status = status2, EmptyProduct3 = empty3 };
        var value = Assert.Single(Read(com)!);
        Assert.Equal("Product", value.NameReported); Assert.Equal(0U, value.StateReported); Assert.Equal(1U, value.SignatureStatusReported);
        Assert.Equal("C:\\Repair.exe", value.RemediationPathReported); Assert.Equal("raw timestamp", value.StateTimestampReported);
        Assert.Equal(new[] { WindowsSecurityProductListReadSession.Product3Id, WindowsSecurityProductListReadSession.Product2Id }, extended.Queries);
        Assert.Null(value.AntivirusDaysUntilExpiredReported);
        Assert.Equal(hasExtension ? "guid\0raw" : null, value.ProductGuidReported);
        Assert.Equal(hasExtension ? 1 : 0, extended.Releases); Assert.Equal(1, product.Releases);
        Assert.Equal(hasExtension ? Enumerable.Range(12, 8) : Array.Empty<int>(), extended.Requested); com.AssertStringsReleased();
    }

    [Theory]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)]
    [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)]
    public void EachExtendedGetterFailureLeavesEveryOtherPropertyUnchanged(int slot)
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product);
        var before = JsonSerializer.SerializeToElement(Assert.Single(Read(com)!));
        if (slot == 12) extended.GuidStatus = 1;
        else if (slot == 13) extended.BooleanStatus = 1;
        else extended.Numbers[slot] = (1, uint.MaxValue);
        var after = JsonSerializer.SerializeToElement(Assert.Single(Read(com)!));
        ExtendedAssert.NullExcept(before, after, ExtendedNames[slot - 12]);
        Assert.Equal(2, extended.Releases); Assert.Equal(2, product.Releases); com.AssertStringsReleased();
    }

    [Theory]
    [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public void UnknownSubstatusIsNullAndDoesNotAffectAnotherGetter(int slot)
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product);
        var before = JsonSerializer.SerializeToElement(Assert.Single(Read(com)!)); extended.Numbers[slot] = (0, 4);
        var after = JsonSerializer.SerializeToElement(Assert.Single(Read(com)!));
        ExtendedAssert.NullExcept(before, after, ExtendedNames[slot - 12]); com.AssertStringsReleased();
    }

    [Theory]
    [InlineData(0U)] [InlineData(4294967295U)]
    public void ExpirationDaysPreserveRawZeroAndMaximum(uint days)
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product);
        extended.Numbers[20] = (0, days); Assert.Equal(days, Assert.Single(Read(com)!).AntivirusDaysUntilExpiredReported);
    }

    [Fact]
    public void OversizedGuidIsFreedAndOnlyThatPropertyIsEmpty()
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product) { GuidValue = new('x', 32768) };
        var value = Assert.Single(Read(com)!); Assert.Null(value.ProductGuidReported); Assert.Equal(false, value.ProductIsDefaultReported);
        Assert.Equal(2U, value.AntivirusScanSubstatusReported); Assert.Equal(1, extended.Releases);
        Assert.Equal(4, com.StringReads); Assert.Equal(4, com.StringFrees); com.AssertStringsReleased();
    }

    [Fact]
    public void GuidSharesTheExistingCategoryTextLimitAndNeverPublishesAPartialList()
    {
        using var com = new Com(); var product = com.Add("n"); product.Text[10] = product.Text[11] = (0, null);
        using var extended = new ExtendedProduct(com, product) { GuidValue = new('g', 256) };
        while (com.Products.Count < 4096) com.Products.Add(product);
        Assert.Null(Read(com)); Assert.Equal(4081, product.Releases); Assert.Equal(product.Releases, extended.Releases);
        Assert.Equal(4081, extended.Queries.Count); Assert.Equal(8162, com.StringReads); Assert.Equal(com.StringReads, com.StringFrees);
        Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); com.AssertStringsReleased();
    }

    [Fact]
    public void ExtendedReleaseFailureStillReleasesOriginalProductAndList()
    {
        using var com = new Com(); var product = com.Add(); using var extended = new ExtendedProduct(com, product) { ThrowRelease = true };
        Assert.Throws<COMException>(() => Read(com)); Assert.Equal(1, extended.Releases); Assert.Equal(1, product.Releases);
        Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); com.AssertStringsReleased();
    }

    private static class ExtendedAssert
    {
        internal static void NullExcept(JsonElement before, JsonElement after, string failed)
        {
            foreach (var property in before.EnumerateObject())
                if (property.Name == failed) Xunit.Assert.Equal(JsonValueKind.Null, after.GetProperty(property.Name).ValueKind);
                else Xunit.Assert.Equal(property.Value.GetRawText(), after.GetProperty(property.Name).GetRawText());
        }
    }
}
