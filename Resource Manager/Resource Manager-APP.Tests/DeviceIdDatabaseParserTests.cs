using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceIdDatabaseParserTests
{
    [Fact]
    public void ParseUsb_ReadsVersionVendorAndProductAndIgnoresInterfaces()
    {
        var database = DeviceIdDatabaseParser.Parse(
            [
                "# Version: 2026.06.26",
                "1234  Example Vendor",
                "\t5678  Example Product",
                "\t\t0001  Example Interface",
                "C 00  Device",
                "\t0001  Class Entry"
            ],
            DeviceIdDatabaseKind.Usb);

        Assert.Equal("2026.06.26", database.Version);
        Assert.Equal("Example Vendor", database.Vendors[0x1234]);
        Assert.Equal(
            "Example Product",
            database.Devices[DeviceIdDatabaseParser.CreateDeviceKey(0x1234, 0x5678)]);
        Assert.Empty(database.Subsystems);
        Assert.Single(database.Devices);
    }

    [Fact]
    public void ParsePci_ReadsSubsystemUnderCurrentDevice()
    {
        var database = DeviceIdDatabaseParser.Parse(
            [
                "# Version: 2026.07.09",
                "10de  NVIDIA Corporation",
                "\t28e0  Example GPU",
                "\t\t17aa 3a82  Example Laptop GPU"
            ],
            DeviceIdDatabaseKind.Pci);

        Assert.Equal("NVIDIA Corporation", database.Vendors[0x10DE]);
        Assert.Equal(
            "Example GPU",
            database.Devices[DeviceIdDatabaseParser.CreateDeviceKey(0x10DE, 0x28E0)]);
        Assert.Equal(
            "Example Laptop GPU",
            database.Subsystems[DeviceIdDatabaseParser.CreateSubsystemKey(0x10DE, 0x28E0, 0x17AA, 0x3A82)]);
    }
}
