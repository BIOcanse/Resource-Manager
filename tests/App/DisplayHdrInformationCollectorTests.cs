using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayHdrInformationCollectorTests
{
    internal static readonly string[] Names = ["display_all_hdr_capability", "display_all_hdr_enabled_actual", "display_all_hdr_active_actual", "display_all_color_mode_actual"];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ActualSdkBitfieldsAndModesUseProductionMarshalThenOriginalPathProjection(int id)
    {
        using var stream = typeof(DisplayHdrInformationCollectorTests).Assembly.GetManifestResourceStream(
            "Resource_Manager_APP.Tests.Fixtures.Display.windows-sdk-display-hdr.json");
        using var sdk = JsonDocument.Parse(stream!); var root = sdk.RootElement;
        var type = typeof(WindowsDisplayPathTopologyReader).GetNestedType("DisplayConfigGetAdvancedColorInfo2", BindingFlags.NonPublic)!;
        var old = typeof(WindowsDisplayPathTopologyReader).GetNestedType("DisplayConfigGetAdvancedColorInfo", BindingFlags.NonPublic)!;
        Assert.Equal(root.GetProperty("currentSize").GetInt32(), Marshal.SizeOf(type));
        Assert.Equal(root.GetProperty("legacySize").GetInt32(), Marshal.SizeOf(old));
        Assert.Equal(root.GetProperty("flagsOffset").GetInt32(), Marshal.OffsetOf(type, "Flags").ToInt32());
        Assert.Equal(root.GetProperty("modeOffset").GetInt32(), Marshal.OffsetOf(type, "ActiveColorMode").ToInt32());
        var frame = Assert.Single(root.GetProperty("frames").EnumerateArray(), f => f.GetProperty("id").GetInt32() == id);
        var bytes = Convert.FromHexString(frame.GetProperty("hex").GetString()!); var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var raw = Marshal.PtrToStructure(pinned.AddrOfPinnedObject(), type)!;
            var flags = (uint)type.GetField("Flags")!.GetValue(raw)!;
            var mode = unchecked((uint)(int)type.GetField("ActiveColorMode")!.GetValue(raw)!);
            Assert.Equal(frame.GetProperty("mode").GetUInt32(), mode);
            var color = WindowsDisplayPathTopologyReader.CreateAdvancedColorInfo2(flags,
                (int)type.GetField("ColorEncoding")!.GetValue(raw)!, (uint)type.GetField("BitsPerColorChannel")!.GetValue(raw)!, mode);
            var supported = frame.GetProperty("hdrSupported").GetUInt32() != 0;
            var userEnabled = frame.GetProperty("hdrUserEnabled").GetUInt32() != 0;
            var advancedActive = frame.GetProperty("advancedActive").GetUInt32() != 0;
            Assert.Equal(supported, color.HighDynamicRangeSupported); Assert.Equal(userEnabled, color.HighDynamicRangeUserEnabled);
            Assert.Equal(advancedActive, color.AdvancedColorEnabled); Assert.Equal(10U, color.BitsPerColorChannel);
            var fields = Fields(color);
            Assert.Equal(supported, Field(fields, Names[0]).Value!.Value.GetBoolean());
            Assert.Equal(userEnabled, Field(fields, Names[1]).Value!.Value.GetBoolean());
            Assert.Equal(mode.ToString(), Field(fields, Names[3]).Value!.Value.GetString());
            if (mode > 2) Assert.Null(Field(fields, Names[2]).Value);
            else Assert.Equal(advancedActive && mode == 2, Field(fields, Names[2]).Value!.Value.GetBoolean());
            foreach (var name in Names) Assert.Equal(DisplayRotationInformationCollectorTests.Source, Field(fields, name).Definition.SourceId);
            Assert.Equal("display.hdr_user_enabled_actual", Field(fields, Names[1]).Definition.Quantity);
            Assert.Equal("enum", Field(fields, Names[3]).Definition.Unit);
            // Cases include user-enabled WCG and active HDR with support/user flags unset; keep each OS report independent.
            if (id == 2) { Assert.True(userEnabled); Assert.False(Field(fields, Names[2]).Value!.Value.GetBoolean()); }
            if (id == 4) { Assert.False(supported); Assert.False(userEnabled); Assert.True(Field(fields, Names[2]).Value!.Value.GetBoolean()); }
        }
        finally { pinned.Free(); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CurrentSettingsAndModeRequireActiveAvailablePathButCapabilityRemainsSeparate(bool active, bool available)
    {
        var color = WindowsDisplayPathTopologyReader.CreateAdvancedColorInfo2(0x33, 0, 10, 2);
        var path = DisplayRotationInformationCollectorTests.Path(0) with { AdvancedColor = color, Active = active, TargetAvailable = available };
        var snapshot = DeviceTopologyInformationProjection.Create(Snapshot(path), DateTimeOffset.UtcNow);
        Assert.True(Field(snapshot, Names[0]).Value!.Value.GetBoolean());
        foreach (var name in Names.Skip(1))
            if (active && available) Assert.NotNull(Field(snapshot, name).Value); else Assert.Null(Field(snapshot, name).Value);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(2U)]
    [InlineData(uint.MaxValue)]
    public void ExactColorModeTravelsThroughOriginalTypedPrimaryPayload(uint mode)
    {
        var color = WindowsDisplayPathTopologyReader.CreateAdvancedColorInfo2(0x33, 0, 10, mode);
        var path = DisplayRotationInformationCollectorTests.Path(0) with { AdvancedColor = color };
        var catalog = new NativeDisplayCoordinatorPayloadCatalog();
        var batches = NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow);
        var fact = Assert.Single(Assert.Single(batches, b => b.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var resolved = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored));
        Assert.Equal(path, resolved.Path);
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(resolved.Path), DateTimeOffset.UtcNow);
        Assert.Equal(mode.ToString(), Field(fields, Names[3]).Value!.Value.GetString());
        if (mode > 2) Assert.Null(Field(fields, Names[2]).Value);
    }

    [Fact]
    public void LegacyAndMissingOldPayloadModeDoNotPublishInferredHdrStates()
    {
        var legacy = new DeviceTopologyDisplayAdvancedColor(true, true, false, false, "rgb", 10,
            HighDynamicRangeSupported: true, HighDynamicRangeUserEnabled: true, HighDynamicRangeActive: true);
        var node = JsonSerializer.SerializeToNode(legacy with { ActiveColorModeReported = 2 }, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node.Remove("activeColorModeReported");
        var restored = node.Deserialize<DeviceTopologyDisplayAdvancedColor>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var color in new DeviceTopologyDisplayAdvancedColor?[] { null, legacy, restored })
        {
            var fields = Fields(color); foreach (var name in Names) Assert.Null(Field(fields, name).Value);
            if (color is not null) Assert.True(Field(fields, "display_all_advanced_color_enabled_actual").Value!.Value.GetBoolean());
        }
    }

    [Fact]
    public void ModeChangesHashAndPayloadEvenWhenExistingFlagsAreIdenticalAndUnobservedFieldsSerializeNull()
    {
        var color = WindowsDisplayPathTopologyReader.CreateAdvancedColorInfo2(0, 0, 8, 0);
        var first = DisplayRotationInformationCollectorTests.Path(0) with { AdvancedColor = color };
        var second = first with { AdvancedColor = color with { ActiveColorModeReported = 1 } };
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(second));
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(Snapshot(first)), comparer.ComputeHash(Snapshot(second)));
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(first), null);
        foreach (var name in Names)
        {
            var field = Field(fields, name); Assert.Null(field.Value);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(field, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
        }
    }

    internal static DeviceTopologySnapshot Snapshot(DeviceTopologyDisplayPath path) => DeviceInformationTestData.Snapshot(
        WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(path, null)));
    internal static DeviceInformationSnapshot Fields(DeviceTopologyDisplayAdvancedColor? color) => DeviceTopologyInformationProjection.Create(
        Snapshot(DisplayRotationInformationCollectorTests.Path(0) with { AdvancedColor = color }), DateTimeOffset.UtcNow);
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot, string name) => Assert.Single(
        Assert.Single(snapshot.Devices, d => d.ObjectType == "display_path").Fields, f => f.Definition.Name == name);
    internal static DeviceTopologySnapshot Healthy(uint flags = 0x33, uint mode = 2) => Snapshot(
        DisplayRotationInformationCollectorTests.Path(0) with { AdvancedColor = WindowsDisplayPathTopologyReader.CreateAdvancedColorInfo2(flags, 0, 10, mode) });
}
