using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Control.Writers;

namespace Resource_Manager_APP.Tests;

public sealed class UniwillGpuPowerFirmwareProfileTests
{
    [Theory]
    [InlineData("TUXEDO", "GMxZGxx", true)]
    [InlineData("TUXEDO Computers", "X6AR5xxY_mLED", true)]
    [InlineData("MACHENIKE", "L16P", true)]
    [InlineData("MECHREVO", "GMxZGxx", false)]
    [InlineData("TUXEDO", "GM6ZG7X", false)]
    [InlineData("TUXEDO", "GMxZGxx_v2", false)]
    [InlineData("TUXEDO", "POLARIS1501A2060", false)]
    [InlineData("TUXEDO", "GXxMRXx", false)]
    [InlineData("TUXEDO", "PHxTxX1", false)]
    [InlineData("TUXEDO", "L16P", false)]
    [InlineData("MACHENIKE", "GMxZGxx", false)]
    [InlineData(null, "GMxZGxx", false)]
    [InlineData("TUXEDO", null, false)]
    public void LayoutRequiresItsOwnVendorAndExactBoard(string? vendor, string? board, bool expected)
    {
        var identity = new FirmwareIdentitySnapshot(null, null, vendor, null, null, board);
        Assert.Equal(expected, UniwillGpuPowerFirmwareProfile.Supports(identity, true,
            _ => throw new InvalidOperationException("Unrelated profiles must not read arbitrary EC addresses.")));
        Assert.False(UniwillGpuPowerFirmwareProfile.Supports(identity, false, _ => throw new Exception()));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(0x40, false)]
    [InlineData(0x80, true)]
    [InlineData(0x81, true)]
    public void SharedBoardNeedsFirmwareDiscreteGpuFeature(int? value, bool expected)
    {
        var reads = new List<ushort>();
        var identity = new FirmwareIdentitySnapshot(null, null, "TUXEDO", null, null, "PHxARX1_PHxAQF1");
        Assert.Equal(expected, UniwillGpuPowerFirmwareProfile.Supports(identity, true, address =>
        {
            reads.Add(address);
            return value is { } number ? checked((byte)number) : null;
        }));
        Assert.Equal(new ushort[] { 0x0456 }, reads);
    }

    [Fact]
    public void SharedBoardReadFailureRemainsUnknownForTheNextProbe()
    {
        var identity = new FirmwareIdentitySnapshot(null, null, "TUXEDO", null, null, "PHxARX1_PHxAQF1");
        Assert.Null(UniwillGpuPowerFirmwareProfile.Detect(identity, true, _ => null));
        Assert.True(UniwillGpuPowerFirmwareProfile.Detect(identity, true, _ => 0x80));
    }

    [Fact]
    public void UnsupportedIdentitySkipsTheAcpiPresenceQuery()
    {
        var identity = new FirmwareIdentitySnapshot(null, null, "Other Vendor", null, null, "Board");
        var queried = false;
        Assert.False(UniwillGpuPowerFirmwareProfile.Detect(identity, () =>
        {
            queried = true;
            return true;
        }, _ => throw new InvalidOperationException("Unsupported profiles must not read EC.")));
        Assert.False(queried);
    }

    [Theory]
    [InlineData("ACPI\\INOU0000\\1", true)]
    [InlineData("acpi\\inou0000\\1", true)]
    [InlineData("ACPI\\INOU00001\\1", false)]
    [InlineData("ACPI\\PNP0C14\\1", false)]
    [InlineData("USB\\INOU0000\\1", false)]
    [InlineData("ACPI\\INOU0000", false)]
    [InlineData(null, false)]
    public void SharedWmiGuidIsNotAnAcpiPlatformIdentity(string? id, bool expected)
        => Assert.Equal(expected, UniwillGpuPowerFirmwareProfile.IsAcpiDeviceId(id));
}
