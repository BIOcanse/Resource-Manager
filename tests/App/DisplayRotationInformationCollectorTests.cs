using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayRotationInformationCollectorTests
{
    internal const string Name = "display_all_rotation_actual";
    internal const string Source = "windows.display-config";

    [Theory]
    [InlineData(1U, 0)]
    [InlineData(2U, 90)]
    [InlineData(3U, 180)]
    [InlineData(4U, 270)]
    [InlineData(0U, -1)]
    [InlineData(5U, -1)]
    [InlineData(uint.MaxValue, -1)]
    public void ActualSdkPathFrameUsesProductionLayoutAndClockwiseEnum(uint raw, int degrees)
    {
        using var stream = typeof(DisplayRotationInformationCollectorTests).Assembly.GetManifestResourceStream(
            "Resource_Manager_APP.Tests.Fixtures.Display.windows-sdk-display-rotation.json");
        using var sdk = JsonDocument.Parse(stream!); var root = sdk.RootElement;
        var pathType = typeof(WindowsDisplayPathTopologyReader).GetNestedType("DisplayConfigPathInfo", BindingFlags.NonPublic)!;
        var targetType = typeof(WindowsDisplayPathTopologyReader).GetNestedType("DisplayConfigPathTargetInfo", BindingFlags.NonPublic)!;
        Assert.Equal(root.GetProperty("pathSize").GetInt32(), Marshal.SizeOf(pathType));
        Assert.Equal(root.GetProperty("targetSize").GetInt32(), Marshal.SizeOf(targetType));
        Assert.Equal(root.GetProperty("targetOffset").GetInt32(), Marshal.OffsetOf(pathType, "TargetInfo").ToInt32());
        Assert.Equal(root.GetProperty("rotationOffset").GetInt32(), Marshal.OffsetOf(targetType, "Rotation").ToInt32());
        var frame = Assert.Single(root.GetProperty("frames").EnumerateArray(), f => f.GetProperty("raw").GetUInt32() == raw);
        var bytes = Convert.FromHexString(frame.GetProperty("hex").GetString()!); var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var path = Marshal.PtrToStructure(pinned.AddrOfPinnedObject(), pathType)!;
            var target = pathType.GetField("TargetInfo")!.GetValue(path)!;
            var rotation = (int)targetType.GetField("Rotation")!.GetValue(target)!;
            Assert.Equal(raw, unchecked((uint)rotation));
            Assert.Equal(88U, targetType.GetField("Id")!.GetValue(target));
            Assert.Equal(2, targetType.GetField("Scaling")!.GetValue(target));
            Assert.Equal(1, targetType.GetField("TargetAvailable")!.GetValue(target));
            uint? expected = degrees < 0 ? null : (uint)degrees;
            Assert.Equal(expected, WindowsDisplayPathTopologyReader.ReadClockwiseRotationDegrees(rotation));
            var port = Port(expected); var fields = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port), DateTimeOffset.UtcNow);
            var value = Field(fields);
            if (expected is null) Assert.Null(value.Value); else Assert.Equal(expected.Value.ToString(), value.Value!.Value.GetString());
            Assert.Equal(Source, value.Definition.SourceId); Assert.Equal("degrees", value.Definition.Unit);
            Assert.Equal("unsigned-integer", value.Definition.ValueKind); Assert.Equal("display.rotation_actual_clockwise_degrees", value.Definition.Quantity);
        }
        finally { pinned.Free(); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CurrentRotationRequiresActivePathAndAvailableTarget(bool active, bool available)
    {
        var port = WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(Path(90) with { Active = active, TargetAvailable = available }, null));
        var fields = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port), DateTimeOffset.UtcNow);
        var value = Field(fields).Value;
        if (active && available) Assert.Equal("90", value!.Value.GetString()); else Assert.Null(value);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(90U)]
    [InlineData(180U)]
    [InlineData(270U)]
    public void RotationChangesTypedPrimaryPayloadAndRoundTripsThroughOriginalNativeReadProjection(uint degrees)
    {
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); var path = Path(degrees);
        var old = catalog.AddDisplayPath(path with { ClockwiseRotationDegrees = null });
        var batches = NativeDisplayCoordinatorObservationProjector.Create([path], [], catalog, DateTimeOffset.UtcNow);
        var batch = Assert.Single(batches, b => b.Source == NativeDisplaySource.DisplayConfig); var fact = Assert.Single(batch.Facts);
        Assert.NotEqual(old, fact.PayloadHandle);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var resolved = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored));
        Assert.Equal(path, resolved.Path); Assert.Equal(degrees, resolved.Path.ClockwiseRotationDegrees);
        Assert.Equal(degrees, restored.Clone().ResolveDisplayPath(fact.PayloadHandle)!.ClockwiseRotationDegrees);
        var fields = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(
            WindowsDeviceTopologyReader.CreateDisplayPathCandidate(resolved)), DateTimeOffset.UtcNow);
        Assert.Equal(degrees.ToString(), Field(fields).Value!.Value.GetString());
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(360U)]
    [InlineData(uint.MaxValue)]
    public void InvalidRestoredDegreesAreEmptyWithoutAffectingIndependentPathValues(uint degrees)
    {
        var fields = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(Port(degrees)), DateTimeOffset.UtcNow);
        var device = Assert.Single(fields.Devices, d => d.ObjectType == "display_path");
        Assert.Null(Field(fields).Value);
        Assert.NotNull(DeviceInformationTestData.Field(fields, device.DeviceIdentity, "display_all_resolution_actual").Value);
        Assert.NotNull(DeviceInformationTestData.Field(fields, device.DeviceIdentity, "display_all_refresh_rate_actual").Value);
    }

    [Fact]
    public void MissingOldPayloadPropertyDoesNotInventLandscapeOrInferItFromDimensions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var node = JsonSerializer.SerializeToNode(Path(270), options)!.AsObject(); node.Remove("clockwiseRotationDegrees");
        var path = node.Deserialize<DeviceTopologyDisplayPath>(options)!; Assert.Null(path.ClockwiseRotationDegrees);
        var portraitDimensions = path with { Width = 1080, Height = 1920 };
        foreach (var item in new[] { path, portraitDimensions })
        {
            var port = WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(item, null));
            Assert.Null(Field(DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(port), DateTimeOffset.UtcNow)).Value);
        }
    }

    [Fact]
    public void RotationAffectsSemanticHashButNotOriginalPathIdentityAndUnobservedValuesSerializeNull()
    {
        var first = DeviceInformationTestData.Snapshot(Port(0)); var second = first with { Ports = [Port(90)] };
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(second));
        var a = DeviceTopologyInformationProjection.Create(first, DateTimeOffset.UtcNow);
        var b = DeviceTopologyInformationProjection.Create(second, DateTimeOffset.UtcNow);
        Assert.Equal(Assert.Single(a.Devices, d => d.ObjectType == "display_path").DeviceIdentity,
            Assert.Single(b.Devices, d => d.ObjectType == "display_path").DeviceIdentity);
        var unobserved = Field(DeviceTopologyInformationProjection.Create(first, null)); Assert.Null(unobserved.Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(unobserved, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public void SameMonitorOnDistinctTargetPathsKeepsPerPathRotation()
    {
        var first = Path(90); var second = first with { TargetId = 2, ClockwiseRotationDegrees = 270 };
        var fields = DeviceTopologyInformationProjection.Create(DeviceInformationTestData.Snapshot(
            WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(first, null)),
            WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(second, null))), DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "270", "90" }, fields.Devices.Where(d => d.ObjectType == "display_path")
            .Select(d => Assert.Single(d.Fields, f => f.Definition.Name == Name).Value!.Value.GetString()).Order(StringComparer.Ordinal));
    }

    internal static DeviceTopologyDisplayPath Path(uint? degrees) => new(1, 2, 3, 4, 5, 0, "Panel", @"\\?\DISPLAY#TEST#1",
        3840, 2160, 60000, 1001, true, true, @"PCI\GPU", ClockwiseRotationDegrees: degrees);
    internal static DeviceTopologyPort Port(uint? degrees) => WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(Path(degrees), null));
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot) => Assert.Single(
        Assert.Single(snapshot.Devices, d => d.ObjectType == "display_path").Fields, f => f.Definition.Name == Name);
}
