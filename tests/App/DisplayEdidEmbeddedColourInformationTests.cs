using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayEdidEmbeddedColourInformationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(0x20, false)] [InlineData(0x20, true)]
    public void EmbeddedColourTraversesTypedOpaqueAndInformationWithStableSemanticIdentity(byte version, bool active)
    {
        var bytes = DisplayIdCtaColourFixture.Bytes(version);
        var path = DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(bytes)) with { Active = active, TargetAvailable = active };
        var direct = WindowsDisplayPathTopologyReader.ToInformation(path, "recorded-svd");
        Assert.Same(path.Edid!.ModeCapability, direct.ModeCapabilityDeclared);
        var domain = DisplayEdidModeTestData.Snapshot(path);
        DisplayEdidEmbeddedColourTestData.AssertValue(Field(
            DeviceTopologyInformationProjection.Create(domain, DateTimeOffset.UtcNow)).Value!.Value, version);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); var handle = catalog.AddDisplayPath(path);
        var imported = new NativeDisplayCoordinatorPayloadCatalog(); imported.Import(catalog.Export());
        var roundTrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = handle }], imported)).Path;
        DisplayEdidEmbeddedColourTestData.AssertValue(JsonSerializer.SerializeToElement(roundTrip.Edid!.ModeCapability, Web), version);
        Assert.Equal(JsonSerializer.Serialize(path, Web), JsonSerializer.Serialize(roundTrip, Web));
        Assert.Equal(Convert.ToBase64String(bytes), roundTrip.EdidRawDeclaredBase64);
        var capability = path.Edid!.ModeCapability!;
        var changed = path with { Edid = path.Edid! with { ModeCapability = capability with {
            Modes = capability.Modes.SetItem(1, capability.Modes[1] with { YCbCr420Supported = false }) } } };
        Assert.Equal(path.EdidRawDeclaredBase64, changed.EdidRawDeclaredBase64);
        Assert.NotEqual(handle, catalog.AddDisplayPath(changed));
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(domain), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(roundTrip)));
        Assert.NotEqual(comparer.ComputeHash(domain), comparer.ComputeHash(DisplayEdidModeTestData.Snapshot(changed)));
    }

    [Theory]
     [InlineData(0x20)]
    public void InvalidEmbeddedColourMapClearsOnlyModesThroughTheRealInformationProjection(byte version)
    {
        var bytes = DisplayEdidEmbeddedColourTestData.Bytes(version, unsupported: true);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities); Assert.Null(read.Capabilities.ModeCapability);
        Assert.NotNull(read.Capabilities.BitsPerColorChannel);
        var fields = DeviceTopologyInformationProjection.Create(DisplayEdidEmbeddedColourTestData.Snapshot(version, unsupported: true), DateTimeOffset.UtcNow);
        Assert.Null(Field(fields).Value);
        Assert.Equal(Convert.ToBase64String(bytes), Field(fields, "display_all_edid_raw_declared").Value!.Value.GetString());
    }

    private static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string name = "display_all_mode_capability")
        => Assert.Single(Assert.Single(snapshot.Devices, device => device.ObjectType == "display_path").Fields,
            field => field.Definition.Name == name);
}
