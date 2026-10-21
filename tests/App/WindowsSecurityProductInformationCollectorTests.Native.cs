using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsSecurityProductInformationCollectorTests
{
    internal static IReadOnlyList<ResourceManager.ReadWrite.Windows.Devices.Models.DeviceTopologySecurityProductInformation>? Read(Com com, uint provider = 4)
        => WindowsSecurityInformationNativeApi.ReadProducts(provider, value => WindowsSecurityProductListReadSession.Open(value, com.Functions));

    [Fact]
    public void OfficialSdkConfirmsDispatchSlotsEnumsAndClassIds()
    {
        var assembly = GetType().Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".Fixtures.Security.windows-sdk-security-products.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!; using var sdk = JsonDocument.Parse(stream);
        var value = sdk.RootElement; Assert.Equal(IntPtr.Size, value.GetProperty("pointerSize").GetInt32());
        foreach (var (name, slot) in new[] { ("initialize", WindowsSecurityProductListReadSession.InitializeSlot), ("count", WindowsSecurityProductListReadSession.CountSlot),
            ("item", WindowsSecurityProductListReadSession.ItemSlot), ("name", WindowsSecurityProductListReadSession.NameSlot), ("state", WindowsSecurityProductListReadSession.StateSlot),
            ("signature", WindowsSecurityProductListReadSession.SignatureSlot), ("remediation", WindowsSecurityProductListReadSession.RemediationSlot), ("timestamp", WindowsSecurityProductListReadSession.TimestampSlot) })
            Assert.Equal(slot, value.GetProperty(name).GetInt32());
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, value.GetProperty("stateValues").EnumerateArray().Select(item => item.GetUInt32()));
        Assert.Equal(new uint[] { 0, 1 }, value.GetProperty("signatureValues").EnumerateArray().Select(item => item.GetUInt32()));
        Assert.Equal(WindowsSecurityProductListReadSession.ClassId, Guid.Parse(value.GetProperty("classId").GetString()!));
        Assert.Equal(WindowsSecurityProductListReadSession.InterfaceId, Guid.Parse(value.GetProperty("interfaceId").GetString()!));
        Assert.Equal(4, Marshal.SizeOf<int>()); Assert.Equal(4, Marshal.SizeOf<uint>());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(-2147417850, 0)]
    public void OriginalNativeAdapterBalancesOwnedApartmentAndBothProductAndList(int status, int closed)
    {
        using var com = new Com { ApartmentStatus = status }; var product = com.Add();
        Assert.Single(Read(com)!); Assert.Equal(new uint[] { 4 }, com.Providers); Assert.Equal(new uint[] { 0 }, com.Items);
        Assert.Equal(1, com.Initializes); Assert.Equal(1, com.Creates); Assert.Equal(1, com.Releases); Assert.Equal(closed, com.Uninitializes);
        Assert.Equal(1, product.Releases); Assert.Equal(new[] { 7, 8, 9, 10, 11 }, product.Requested);
        Assert.Equal(3, com.StringReads); Assert.Equal(3, com.StringFrees); com.AssertStringsReleased();
    }

    [Fact]
    public void FailedApartmentNeverCreatesAndInvalidProviderNeverInitializes()
    {
        using var com = new Com { ApartmentStatus = unchecked((int)0x80004005) };
        Assert.Null(Read(com)); Assert.Equal(0, com.Creates); Assert.Equal(0, com.Releases); Assert.Equal(0, com.Uninitializes);
        Assert.Throws<ArgumentOutOfRangeException>(() => Read(com, 5)); Assert.Equal(1, com.Initializes);
    }

    [Theory]
    [InlineData(0, true)] [InlineData(1, false)] [InlineData(-2147467259, false)]
    public void FailedCreateNeverUsesDirtyOutAndBalancesApartment(int status, bool empty)
    {
        using var com = new Com { CreateStatus = status, EmptyCreate = empty };
        Assert.Null(Read(com)); Assert.Equal(1, com.Uninitializes); Assert.Equal(0, com.Releases); Assert.Empty(com.Providers);
    }

    [Theory]
    [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void MissingRequiredListSlotReleasesInterfaceWithoutPublishing(int slot)
    {
        using var com = new Com(); com.Missing(slot); Assert.Null(Read(com));
        Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); Assert.Equal(0, com.Counts); Assert.Empty(com.Providers);
    }

    [Theory]
    [InlineData(1)] [InlineData(-2147467259)]
    public void FailedListInitializationUsesNoGettersAndReleasesOnce(int status)
    {
        using var com = new Com { InitializeStatus = status }; Assert.Null(Read(com));
        Assert.Equal(new uint[] { 4 }, com.Providers); Assert.Equal(0, com.Counts); Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes);
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(-2147467259, 1)] [InlineData(0, -1)] [InlineData(0, 4097)]
    public void InvalidDirectoryCountNeverReturnsAnEmptySuccess(int status, int count)
    {
        using var com = new Com { CountStatus = status, CountOverride = count }; Assert.Null(Read(com));
        Assert.Empty(com.Items); Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes);
    }

    [Fact]
    public void CompleteEmptyDirectoryIsAnEmptyArrayAndMaximumCountKeepsDuplicates()
    {
        using var empty = new Com(); Assert.Empty(Read(empty)!); Assert.Empty(empty.Items); Assert.Equal(1, empty.Releases);
        using var full = new Com(); var product = full.Add(null); product.Text[10] = product.Text[11] = (0, null);
        while (full.Products.Count < 4096) full.Products.Add(product);
        var records = Read(full)!; Assert.Equal(4096, records.Count); Assert.Equal(4096, product.Releases);
        Assert.Equal(4096, full.Items.Count); Assert.Equal(1, full.Releases); Assert.All(records, value => Assert.Equal(0U, value.StateReported));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailedMiddleItemDiscardsWholeDirectoryAndReleasesPriorProduct(bool nullPointer)
    {
        using var com = new Com(); var first = com.Add(); var second = com.Add();
        if (nullPointer) com.NullItem = 1; else com.FailedItem = 1;
        Assert.Null(Read(com)); Assert.Equal(1, first.Releases); Assert.Equal(0, second.Releases);
        Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); com.AssertStringsReleased();
    }

    [Fact]
    public void OriginalReaderClearsOnlyReleaseFailedCategoryAndStillBalancesApartment()
    {
        var lists = new List<Com>(); var api = new Api { Open = provider => { var com = new Com { FailRelease = provider == 4 }; com.Add(); lists.Add(com); return com; } };
        var value = new WindowsSecurityInformationReader(api).Read();
        Assert.Null(value.AntivirusProductsReported); Assert.Single(value.FirewallProductsReported!);
        Assert.Equal(0U, value.AntivirusHealthReported); Assert.True(value.PrivateFirewallEnabledConfigured);
        Assert.All(lists, com => { Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); Assert.Equal(1, com.Products[0].Releases); com.AssertStringsReleased(); });
    }

    [Fact]
    public void ExplicitSessionDisposalIsIdempotentAndPreventsFurtherGetters()
    {
        using var com = new Com(); var session = WindowsSecurityProductListReadSession.Open(1, com.Functions)!;
        session.Dispose(); session.Dispose(); Assert.Throws<ObjectDisposedException>(() => session.ReadProducts());
        Assert.Equal(1, com.Releases); Assert.Equal(1, com.Uninitializes); Assert.Equal(0, com.Counts); Assert.Equal(new uint[] { 1 }, com.Providers);
    }
}
