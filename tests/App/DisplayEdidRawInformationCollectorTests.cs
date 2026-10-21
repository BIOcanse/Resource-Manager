using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayEdidRawInformationCollectorTests
{
    internal const string Name = "display_all_edid_raw_declared";
    internal const string Source = "windows.registry-monitor-edid";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(256)]
    [InlineData(4097)]
    public void CompleteBinaryValueIsLosslessAndImmutableWithoutInventingValidity(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 256)).ToArray(); var expected = bytes.ToArray();
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes); Assert.True(result.Complete);
        Assert.Equal(expected, Convert.FromBase64String(result.RawEdidDeclaredBase64!)); Assert.Null(result.Capabilities);
        Array.Fill(bytes, (byte)42); Assert.Equal(expected, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        var field = Field(DeviceTopologyInformationProjection.Create(Snapshot(result), DateTimeOffset.UtcNow));
        Assert.Equal(expected, Convert.FromBase64String(field.Value!.Value.GetString()!));
        Assert.Equal("bytes", field.Definition.ValueKind); Assert.Equal("B", field.Definition.Unit);
        Assert.Equal(Source, field.Definition.SourceId); Assert.Equal("display.windows_registry_edid_value_raw", field.Definition.Quantity);
        if (length == 0) Assert.Equal(string.Empty, field.Value!.Value.GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not binary")]
    [InlineData(42)]
    public void MissingOrNonBinaryValueIsEmptyWithoutPretendingReadFailure(object? value)
    {
        var result = WindowsMonitorEdidReader.CreateReadResult(value);
        Assert.True(result.Complete); Assert.Null(result.RawEdidDeclaredBase64); Assert.Null(result.Capabilities);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(result), DateTimeOffset.UtcNow)).Value);
    }

    [Fact]
    public void SourceUnavailableOrEmptyDeviceIdentityDoesNotReadARegistryKey()
    {
        var result = WindowsMonitorEdidReader.Read(string.Empty);
        Assert.Equal(DeviceTopologyEdidReadResult.Unavailable, result); Assert.False(result.Complete);
        Assert.Null(result.RawEdidDeclaredBase64);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(result), DateTimeOffset.UtcNow)).Value);
    }

    [Fact]
    public void OriginalCapabilitiesAndOpaqueRawBytesAreDeliveredIndependently()
    {
        var bytes = new byte[256]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[126] = 1;
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        // BackendMessage.Args is an array; compare the delivered values instead of record reference equality.
        Assert.Equal(JsonSerializer.Serialize(EdidParser.Parse(bytes)), JsonSerializer.Serialize(result.Capabilities));
        Assert.NotNull(result.Capabilities);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        bytes[0] = 42; var malformed = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(malformed.Capabilities); Assert.Equal(bytes, Convert.FromBase64String(malformed.RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CompletedEdidDeclarationDoesNotRequireActivePathButIncompleteObservationClearsOnlyRaw(bool complete, bool active)
    {
        var raw = Convert.ToBase64String([0, 1, 255]);
        var path = DisplayRotationInformationCollectorTests.Path(90) with { EdidRawDeclaredBase64 = raw, EdidObservationComplete = complete, Active = active };
        var fields = DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(path), DateTimeOffset.UtcNow);
        if (complete) Assert.Equal(raw, Field(fields).Value!.Value.GetString()); else Assert.Null(Field(fields).Value);
        var display = Assert.Single(fields.Devices, d => d.ObjectType == "display_path");
        var rotation = DeviceInformationTestData.Field(fields, display.DeviceIdentity, DisplayRotationInformationCollectorTests.Name).Value;
        if (active) Assert.Equal("90", rotation!.Value.GetString()); else Assert.Null(rotation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAECA/8=")]
    public void OriginalTypedPrimaryPayloadRoundTripsRawBytesWithoutEdidCapabilities(string raw)
    {
        var path = DisplayRotationInformationCollectorTests.Path(0) with { EdidRawDeclaredBase64 = raw };
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var fact = Assert.Single(Assert.Single(NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow),
            b => b.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var resolved = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored));
        Assert.Equal(path, resolved.Path); Assert.Null(resolved.Path.Edid);
        var fields = DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(resolved.Path), DateTimeOffset.UtcNow);
        Assert.Equal(raw, Field(fields).Value!.Value.GetString());
    }

    [Fact]
    public void RawValueChangesSemanticHashAndPayloadButUnobservedValuesSerializeNull()
    {
        var first = WindowsMonitorEdidReader.CreateReadResult(new byte[] { 1 }); var second = WindowsMonitorEdidReader.CreateReadResult(new byte[] { 2 });
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(Snapshot(first)), comparer.ComputeHash(Snapshot(second)));
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(Path(first)), catalog.AddDisplayPath(Path(second)));
        var field = Field(DeviceTopologyInformationProjection.Create(Snapshot(first), null)); Assert.Null(field.Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(field, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public void OldTypedPayloadWithoutRawDoesNotReconstructBytesFromParsedCapabilities()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var path = Path(WindowsMonitorEdidReader.CreateReadResult(new byte[] { 1, 2 })) with {
            Edid = new("1.4", "Stored Panel", null, 8, DeviceEdidDigitalInterfaces.DisplayPort,
                null, 600, 340, true, null) };
        var node = JsonSerializer.SerializeToNode(path, options)!.AsObject();
        node.Remove("edidRawDeclaredBase64"); var old = node.Deserialize<DeviceTopologyDisplayPath>(options)!;
        Assert.Null(old.EdidRawDeclaredBase64);
        Assert.NotNull(old.Edid); Assert.Equal("Stored Panel", old.Edid.ProductName);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(DisplayHdrInformationCollectorTests.Snapshot(old), DateTimeOffset.UtcNow)).Value);
    }

    internal static DeviceTopologyDisplayPath Path(DeviceTopologyEdidReadResult result) => DisplayRotationInformationCollectorTests.Path(90) with {
        Edid = result.Capabilities, EdidObservationComplete = result.Complete, EdidRawDeclaredBase64 = result.RawEdidDeclaredBase64 };
    internal static DeviceTopologySnapshot Snapshot(DeviceTopologyEdidReadResult result) => DisplayHdrInformationCollectorTests.Snapshot(Path(result));
    internal static DeviceTopologySnapshot Healthy() => Snapshot(WindowsMonitorEdidReader.CreateReadResult(new byte[] { 0, 1, 255 }));
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot) => DisplayHdrInformationCollectorTests.Field(snapshot, Name);
}
