using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayEdidModeInformationCollectorTests
{
    internal const string Name = "display_all_mode_capability";
    internal const string Source = "windows.registry-monitor-edid";

    [Fact]
    public void ParsedTypedPathCrossesOriginalDomainProjectionWithCanonicalStructuredBinding()
    {
        var path = Path();
        var direct = WindowsDisplayPathTopologyReader.ToInformation(path, "recorded-caller-display");
        Assert.Same(path.Edid!.ModeCapability, direct.ModeCapabilityDeclared);
        var snapshot = Snapshot(path);
        Assert.Same(path.Edid.ModeCapability, Assert.Single(snapshot.Ports).Display!.ModeCapabilityDeclared);
        var information = DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow);
        var device = Assert.Single(information.Devices, static item => item.ObjectType == "display_path");
        Assert.Equal("windows-display-path|" + snapshot.Ports[0].Id + "|" + path.MonitorDevicePath!.Trim().ToUpperInvariant(), device.DeviceIdentity);
        var field = Field(information); AssertValue(field.Value!.Value);
        Assert.Equal(("display", "all", "object", "mode", Source,
            "display.windows_registry_edid_mode_capability_declared"),
            (field.Definition.Resource, field.Definition.Vendor, field.Definition.ValueKind, field.Definition.Unit,
                field.Definition.SourceId, field.Definition.Quantity));
        var descriptor = InformationFieldProjection.Describe(field.Definition);
        Assert.Equal("structured", descriptor.ValueKind); Assert.Equal("mode", descriptor.Unit);
        var reading = InformationFieldProjection.Read(device.DeviceIdentity, field, information.ObservedAt);
        Assert.Equal(FieldObservationStatus.Current, reading.Status); AssertValue(reading.Value!.Structure!.Value);
        Assert.Equal(Source, reading.SourceId);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void StaticCapabilityIgnoresActiveModeButRequiresCompletedEdidObservation(bool complete, bool active)
    {
        var path = Path() with { EdidObservationComplete = complete, Active = active, TargetAvailable = active };
        var direct = WindowsDisplayPathTopologyReader.ToInformation(path, "caller");
        var info = DeviceTopologyInformationProjection.Create(Snapshot(path), DateTimeOffset.UtcNow);
        if (complete) { Assert.NotNull(direct.ModeCapabilityDeclared); AssertValue(Field(info).Value!.Value); }
        else { Assert.Null(direct.ModeCapabilityDeclared); Assert.Null(Field(info).Value); }
        var resolution = DisplayHdrInformationCollectorTests.Field(info, "display_all_resolution_actual");
        if (active) Assert.NotNull(resolution.Value); else Assert.Null(resolution.Value);
    }

    [Fact]
    public void OriginalOpaquePrimaryPayloadPreservesExactIntegersAndRationalThroughExportImport()
    {
        var path = Path(fractional: true);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            static batch => batch.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var roundtrip = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored)).Path;
        Assert.Equal(148_350_000UL, Assert.Single(roundtrip.Edid!.ModeCapability!.Modes).Timing!.PixelClockHz);
        Assert.Equal(new EdidModeRate(1978, 33), roundtrip.Edid.ModeCapability.Modes[0].Timing!.FrameRate);
        Assert.Equal(path.Edid.ModeCapability!.Modes[0].RawDescriptor.ToArray(), roundtrip.Edid.ModeCapability.Modes[0].RawDescriptor.ToArray());
        var info = DeviceTopologyInformationProjection.Create(Snapshot(roundtrip), DateTimeOffset.UtcNow);
        Assert.Equal(1978UL, Field(info).Value!.Value.GetProperty("modes")[0].GetProperty("timing").GetProperty("frameRate").GetProperty("numerator").GetUInt64());
    }

    [Fact]
    public void LegacyPayloadWithoutModePropertyDoesNotReparseRawOrInferFromCurrentResolution()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var bytes = Bytes(); var independent = DisplayHdrLuminanceInformationCollectorTests.Bytes();
        Array.Resize(ref bytes, 256); independent.AsSpan(128, 128).CopyTo(bytes.AsSpan(128));
        independent.AsSpan(25, 10).CopyTo(bytes.AsSpan(25)); bytes[126] = 1; Checksum(bytes);
        var original = Path(WindowsMonitorEdidReader.CreateReadResult(bytes));
        Assert.NotNull(original.Edid!.ChromaticityDeclared); Assert.NotEmpty(original.Edid.HdrLuminanceDeclared!);
        var node = JsonSerializer.SerializeToNode(original, options)!.AsObject();
        node["edid"]!.AsObject().Remove("modeCapability");
        var old = node.Deserialize<DeviceTopologyDisplayPath>(options)!;
        Assert.NotNull(old.EdidRawDeclaredBase64); Assert.NotNull(old.Edid); Assert.Null(old.Edid.ModeCapability);
        Assert.Equal(original.EdidRawDeclaredBase64, old.EdidRawDeclaredBase64);
        Assert.Equal(JsonSerializer.Serialize(original.Edid.ChromaticityDeclared), JsonSerializer.Serialize(old.Edid.ChromaticityDeclared));
        Assert.Equal(JsonSerializer.Serialize(original.Edid.HdrLuminanceDeclared), JsonSerializer.Serialize(old.Edid.HdrLuminanceDeclared));
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(old), DateTimeOffset.UtcNow)).Value);
        var domain = Snapshot(Path());
        var domainJson = JsonSerializer.SerializeToNode(domain, options)!.AsObject();
        domainJson["ports"]![0]!["display"]!.AsObject().Remove("modeCapabilityDeclared");
        var legacyDomain = domainJson.Deserialize<DeviceTopologySnapshot>(options)!;
        Assert.Null(legacyDomain.Ports[0].Display!.ModeCapabilityDeclared);
    }

    [Fact]
    public void CapabilityAloneChangesOpaqueHandleAndDomainSemanticHash()
    {
        var first = Path();
        var other = first with { Edid = first.Edid! with { ModeCapability = Path(fractional: true).Edid!.ModeCapability } };
        Assert.Equal(first.EdidRawDeclaredBase64, other.EdidRawDeclaredBase64);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(other));
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(Snapshot(first)), comparer.ComputeHash(Snapshot(other)));
    }

    [Fact]
    public void FailureAndUnsupportedTimingClearOnlyModeWhileRawAndCurrentRemainIndependent()
    {
        var bytes = Bytes(); bytes[126] = 1; Checksum(bytes);
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        var information = DeviceTopologyInformationProjection.Create(Snapshot(Path(read)), DateTimeOffset.UtcNow);
        Assert.Null(Field(information).Value);
        Assert.Equal(Convert.ToBase64String(bytes), DisplayHdrInformationCollectorTests.Field(information, "display_all_edid_raw_declared").Value!.Value.GetString());
        Assert.NotNull(DisplayHdrInformationCollectorTests.Field(information, "display_all_resolution_actual").Value);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(Path(DeviceTopologyEdidReadResult.Unavailable)), DateTimeOffset.UtcNow)).Value);
    }

    [Fact]
    public void UnobservedOrPersistedDeclarationIsUnavailableAndSuccessfulEmptyRemainsStructured()
    {
        var path = Path();
        var unobserved = DeviceTopologyInformationProjection.Create(Snapshot(path), null); Assert.Null(Field(unobserved).Value);
        var bytes = Bytes(); Array.Clear(bytes, 54, 18); bytes[57] = 0x10; Checksum(bytes);
        var field = Field(DeviceTopologyInformationProjection.Create(Snapshot(Path(WindowsMonitorEdidReader.CreateReadResult(bytes))), DateTimeOffset.UtcNow));
        Assert.Equal(0, field.Value!.Value.GetProperty("modes").GetArrayLength());
        Assert.Equal(0, field.Value!.Value.GetProperty("ranges").GetArrayLength());
        Assert.Equal(FieldObservationStatus.Current, InformationFieldProjection.Read("caller", field, DateTimeOffset.UtcNow).Status);
    }

    internal static void AssertValue(JsonElement value)
    {
        var mode = Assert.Single(value.GetProperty("modes").EnumerateArray());
        Assert.Equal(1920u, mode.GetProperty("width").GetUInt32()); Assert.Equal(1080u, mode.GetProperty("height").GetUInt32());
        Assert.Equal("edid-detailed", mode.GetProperty("encoding").GetString()); Assert.Equal(54u, mode.GetProperty("byteOffset").GetUInt32());
        var rate = mode.GetProperty("timing").GetProperty("frameRate");
        Assert.Equal(60UL, rate.GetProperty("numerator").GetUInt64()); Assert.Equal(1UL, rate.GetProperty("denominator").GetUInt64());
    }

    internal static DeviceTopologyDisplayPath Path(bool fractional = false) => Path(WindowsMonitorEdidReader.CreateReadResult(Bytes(fractional)));
    internal static DeviceTopologyDisplayPath Path(DeviceTopologyEdidReadResult read) => DisplayEdidModeTestData.Path(read);
    internal static DeviceTopologySnapshot Snapshot(DeviceTopologyDisplayPath path) => DisplayEdidModeTestData.Snapshot(path);
    internal static DeviceInformationField Field(DeviceInformationSnapshot info) => DisplayHdrInformationCollectorTests.Field(info, Name);
    internal static DeviceTopologySnapshot Healthy() => Snapshot(Path());
    internal static byte[] Bytes(bool fractional = false)
    {
        var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[24] = 2;
        for (var offset = 38; offset < 54; offset += 2) { bytes[offset] = 1; bytes[offset + 1] = 1; }
        new byte[] { 0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C, 0x45, 0, 0, 0, 0, 0, 0, 0x1E }.CopyTo(bytes, 54);
        if (fractional) { bytes[54] = 0xF3; bytes[55] = 0x39; }
        for (var offset = 72; offset <= 108; offset += 18) bytes[offset + 3] = 0x10;
        Checksum(bytes); return bytes;
    }
    private static void Checksum(byte[] bytes)
    { bytes[127] = 0; bytes[127] = unchecked((byte)-bytes.Sum(static value => (int)value)); }
}
