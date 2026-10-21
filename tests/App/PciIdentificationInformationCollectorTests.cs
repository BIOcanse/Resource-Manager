using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class PciIdentificationInformationCollectorTests
{
    private const string DeviceIdentity = @"PCI\DEVICE_INSTANCE";
    private const string Hardware = @"PCI\VEN_1414&DEV_00E0&SUBSYS_00011414&REV_00";

    [Fact]
    public void ReportedIdentifiersKeepFixedWidthsSubsystemOrderAndCompatibleClassSpecificity()
    {
        var value = PciDeviceIdentificationReader.Read([Hardware.ToLowerInvariant(), @"PCI\VEN_1414&DEV_00E0"],
            [@"PCI\VEN_1414&DEV_00E0&CC_030000", @"PCI\VEN_1414&CC_0300", @"PCI\CC_030000&DT_0000"]);
        Assert.Equal(new("1414", "00E0", "00", "0001", "1414", "03", "00", "00"), value);
        var lessSpecific = PciDeviceIdentificationReader.Read([Hardware], [@"PCI\CC_0300"]);
        Assert.Equal("03", lessSpecific.BaseClass);
        Assert.Equal("00", lessSpecific.Subclass);
        Assert.Null(lessSpecific.ProgrammingInterface);
    }

    [Fact]
    public void RevisionFromCompatiblePropertyIsTheSameDeclaredQuantityAndConflictsRemainEmpty()
    {
        var value = PciDeviceIdentificationReader.Read([@"PCI\VEN_1414&DEV_00E0"],
            [@"PCI\VEN_1414&DEV_00E0&REV_04&CC_0300"]);
        Assert.Equal("04", value.RevisionId);
        var conflict = PciDeviceIdentificationReader.Read([Hardware], [@"PCI\VEN_1414&DEV_00E0&REV_04"]);
        Assert.Null(conflict.RevisionId);
        Assert.Equal("1414", conflict.VendorId);
    }

    [Theory]
    [InlineData(@"PCI\VEN_1415&DEV_00E0", false)]
    [InlineData(@"PCI\VEN_1414&DEV_00E1", false)]
    [InlineData(@"PCI\VEN_1415&CC_0300", true)]
    [InlineData(@"PCI\VEN_1414&DEV_00E1&CC_0300", true)]
    public void ConflictingPhysicalIdentityNeverSelectsTheFirstRow(string conflict, bool compatible)
    {
        Assert.Equal(PciDeviceIdentification.Empty, compatible
            ? PciDeviceIdentificationReader.Read([Hardware], [conflict])
            : PciDeviceIdentificationReader.Read([Hardware, conflict], []));
    }

    [Fact]
    public void OptionalDeclarationGroupsRemainIndependentAndDoNotMixConflictingClassDomains()
    {
        var value = PciDeviceIdentificationReader.Read([Hardware, @"PCI\VEN_1414&DEV_00E0&SUBSYS_00021414"],
            [@"PCI\CC_030000", @"PCI\CC_020000"]);
        Assert.Equal("1414", value.VendorId);
        Assert.Equal("00", value.RevisionId);
        Assert.Null(value.SubsystemIdentifier);
        Assert.Null(value.SubsystemVendorId);
        Assert.Null(value.BaseClass); Assert.Null(value.Subclass); Assert.Null(value.ProgrammingInterface);
        var piConflict = PciDeviceIdentificationReader.Read([Hardware], [@"PCI\CC_030001", @"PCI\CC_030002"]);
        Assert.Equal("03", piConflict.BaseClass); Assert.Equal("00", piConflict.Subclass);
        Assert.Null(piConflict.ProgrammingInterface);
    }

    [Theory]
    [InlineData(@"PCI\VEN_0000&DEV_0000")]
    [InlineData(@"PCI\VEN_FFFF&DEV_0000")]
    [InlineData(@"USB\VEN_1414&DEV_0000")]
    [InlineData(@"PCI\VEN_1414&DEV_0000\INSTANCE")]
    [InlineData(@"PCI\VEN_1414&DEV_000")]
    [InlineData(@"PCI\VEN_1414&DEV_0000&REV_G0")]
    [InlineData(@"PCI\VEN_1414&DEV_0000&UNKNOWN_0000")]
    [InlineData(@"PCI\DEV_0000&VEN_1414")]
    public void MalformedOrInvalidIdentifiersDoNotBecomeADeviceClaim(string value)
        => Assert.Equal(PciDeviceIdentification.Empty, PciDeviceIdentificationReader.Read([value], []));

    [Fact]
    public void ZeroDeviceRevisionAndClassRemainValidButInvalidSubsystemVendorDoesNot()
    {
        var value = PciDeviceIdentificationReader.Read([@"PCI\VEN_1414&DEV_0000&SUBSYS_0000FFFF&REV_00"], [@"PCI\CC_000000"]);
        Assert.Equal("0000", value.DeviceId); Assert.Equal("00", value.RevisionId);
        Assert.Equal("00", value.BaseClass); Assert.Equal("00", value.ProgrammingInterface);
        Assert.Null(value.SubsystemIdentifier); Assert.Null(value.SubsystemVendorId);
        var validZeroSubsystem = PciDeviceIdentificationReader.Read([@"PCI\VEN_1414&DEV_FFFF&SUBSYS_00001414"], []);
        Assert.Equal("FFFF", validZeroSubsystem.DeviceId);
        Assert.Equal("0000", validZeroSubsystem.SubsystemIdentifier);
    }

    [Fact]
    public void OversizedPropertiesReturnEmptyWithoutPublishingTheFirstValidPrefix()
    {
        var tooMany = Enumerable.Repeat(Hardware, 65).ToArray();
        Assert.Equal(PciDeviceIdentification.Empty, PciDeviceIdentificationReader.Read(tooMany, []));
        Assert.Equal(PciDeviceIdentification.Empty, PciDeviceIdentificationReader.Read([Hardware], tooMany));
        Assert.Equal(PciDeviceIdentification.Empty, PciDeviceIdentificationReader.Read([Hardware, new('A', 129)], []));
    }

    [Fact]
    public void TypedNativePropertyIsTheOnlySourceAndRequiresTheSamePciInstance()
    {
        var native = Native(DeviceIdentity);
        var typed = PciDeviceIdentificationReader.ReadForDevice(DeviceIdentity.ToLowerInvariant(), native);
        Assert.NotNull(typed);
        Assert.Null(PciDeviceIdentificationReader.ReadForDevice(@"PCI\OTHER", native));
        Assert.Null(PciDeviceIdentificationReader.ReadForDevice(@"ROOT\SOFTWARE", Native(@"ROOT\SOFTWARE")));
        Assert.Null(PciDeviceIdentificationReader.ReadForDevice(DeviceIdentity, null));
        var port = DeviceInformationTestData.Port(DeviceIdentity) with {
            HardwareIds = [@"PCI\VEN_DEAD&DEV_BEEF"], Manufacturer = "WMI/Database", PciIdentification = typed };
        var snapshot = DeviceInformationTestData.Snapshot(port);
        var fields = DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow);
        var device = Assert.Single(fields.Devices, d => d.DeviceIdentity == "windows-pnp|" + DeviceIdentity);
        var vendor = Assert.Single(device.Fields, f => f.Definition.Name == "pci_all_vendor_id");
        Assert.Equal("1414", vendor.Value!.Value.GetString());
        Assert.Equal("hex16", vendor.Definition.Unit);
        Assert.Equal("windows.pnp.pci-identifiers", vendor.Definition.SourceId);
        Assert.Equal(8, device.Fields.Count(f => f.Definition.SourceId == vendor.Definition.SourceId));
        var missing = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port with { PciIdentification = null }), DateTimeOffset.UtcNow);
        Assert.Null(DeviceInformationTestData.Field(missing, device.DeviceIdentity, "pci_all_vendor_id").Value);
        Assert.DoesNotContain(DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port with { BusKind = DeviceBusKinds.Usb, PciIdentification = null }), DateTimeOffset.UtcNow).Devices,
            d => d.Fields.Any(f => f.Definition.SourceId == vendor.Definition.SourceId));
        var persisted = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(typed, Assert.Single(persisted.Ports).PciIdentification);
        var declarations = DeviceTopologyInformationProjection.Create(persisted, null);
        Assert.All(Assert.Single(declarations.Devices, d => d.DeviceIdentity == device.DeviceIdentity).Fields, f => Assert.Null(f.Value));
    }

    [Fact]
    public void Usb4FunctionalClassificationDoesNotHideTheSameNativePciDevice()
    {
        var role = DeviceTopologyInterconnectClassifier.Classify("Usb4HostRouter");
        Assert.NotNull(role);
        var port = DeviceInformationTestData.Port(DeviceIdentity) with {
            BusKind = DeviceBusKinds.Usb4, AdvancedInterconnect = role,
            PciIdentification = PciDeviceIdentificationReader.ReadForDevice(DeviceIdentity, Native(DeviceIdentity)) };
        var fields = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port), DateTimeOffset.UtcNow);
        Assert.Equal("1414", DeviceInformationTestData.Field(fields, "windows-pnp|" + DeviceIdentity, "pci_all_vendor_id").Value!.Value.GetString());
        Assert.Equal(DeviceBusKinds.Usb4, port.BusKind);
    }

    private static DeviceTopologyNativeDevice Native(string identity) => new(identity, null, null, null, null, null,
        null, null, "PCI", null, [], [Hardware], [@"PCI\CC_030000"], null, null, null);
}
