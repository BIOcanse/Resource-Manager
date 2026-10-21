using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class UsbParentPortInformationCollectorTests
{
    internal const string Name = "usb_all_parent_port_actual";
    internal const string Source = "windows.usb-hub-connection";
    internal const string Identity = @"windows-pnp|USB\DEVICE";
    internal const string Parent = @"USB#ROOT_HUB#INSTANCE|7";
    internal const string DriverKey = @"{01234567-89AB-CDEF-0123-456789ABCDEF}\0007";

    public static IEnumerable<object[]> SdkFrames()
    {
        var assembly = typeof(UsbParentPortInformationCollectorTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(Assert.Single(assembly.GetManifestResourceNames(),
            name => name.EndsWith("windows-sdk-usb-parent-port.json", StringComparison.Ordinal)))!;
        using var json = JsonDocument.Parse(stream);
        foreach (var frame in json.RootElement.GetProperty("frames").EnumerateArray())
            yield return [frame.GetProperty("kind").GetString()!, frame.GetProperty("name").GetString()!,
                Convert.FromHexString(frame.GetProperty("hex").GetString()!), frame.GetProperty("returned").GetUInt32(),
                frame.GetProperty("port").GetUInt32(), frame.GetProperty("valid").GetBoolean()];
    }

    [Theory]
    [MemberData(nameof(SdkFrames))]
    public void ActualSdkFramesValidateRequestIndexAndCompleteReturn(string kind, string name, byte[] bytes, uint returned, uint port, bool valid)
    {
        if (kind == "key")
        {
            var key = WindowsUsbHubIoctlReader.ReadDriverKeyName(bytes, returned, port);
            if (valid) Assert.Equal(DriverKey, key); else Assert.Null(key);
        }
        else
        {
            Assert.Equal(35, Marshal.SizeOf<WindowsUsbHubIoctlReader.UsbNodeConnectionInformationEx>());
            Assert.Equal(31, Marshal.OffsetOf<WindowsUsbHubIoctlReader.UsbNodeConnectionInformationEx>("ConnectionStatus").ToInt32());
            var info = WindowsUsbHubIoctlReader.ReadConnectionInformation(bytes, returned, port);
            if (!valid) Assert.Null(info);
            else
            {
                Assert.Equal(7U, info!.Value.ConnectionIndex);
                Assert.Equal((ushort)0x1234, info.Value.DeviceDescriptor.IdVendor);
                Assert.Equal((ushort)0x5678, info.Value.DeviceAddress);
                Assert.Equal(name == "disconnected" ? 0U : 1U, info.Value.ConnectionStatus);
                Assert.Equal(3U, info.Value.NumberOfOpenPipes); // The fixed parent header does not need those pipe entries.
            }
        }
    }

    [Fact]
    public void SmallSpansDoNotDecodeZeroInitializedTailAsAKeyOrConnection()
    {
        for (var size = 0; size < 8; size++) Assert.Null(WindowsUsbHubIoctlReader.ReadDriverKeyName(new byte[size], (uint)size, 7));
        for (var size = 0; size < 35; size++) Assert.Null(WindowsUsbHubIoctlReader.ReadConnectionInformation(new byte[size], (uint)size, 7));
    }

    [Fact]
    public void UniqueConnectedDriverKeyMatchesNativeDeviceAndKeepsMetadata()
    {
        var port = Raw(); var index = WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port]);
        Assert.Same(port, WindowsDeviceTopologyReader.FindUsbPort(Native(" " + DriverKey.ToLowerInvariant() + " "), index));
        Assert.Null(WindowsDeviceTopologyReader.FindUsbPort(null, index));
        Assert.Null(WindowsDeviceTopologyReader.FindUsbPort(Native("unknown"), index));
        var connection = WindowsDeviceTopologyReader.CreateUsbConnection(port, publishActualParentPort: true)!;
        Assert.Equal(Parent, connection.ActualParentPortIdentity);
        Assert.Equal("USB 2.0", connection.DeviceSpecification); Assert.Equal("Product", connection.ProductName);
        var field = Field(DeviceTopologyInformationProjection.Create(Snapshot(connection), DateTimeOffset.UtcNow));
        Assert.Equal(Parent, field.Value!.Value.GetString()); Assert.Equal("text", field.Definition.ValueKind);
        Assert.Equal(Source, field.Definition.SourceId); Assert.Equal("text", field.Definition.Unit);
        Assert.Equal("usb.windows_attached_device_immediate_parent_port_actual", field.Definition.Quantity);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("blank-hub")]
    [InlineData("zero-port")]
    [InlineData("missing-key")]
    [InlineData("blank-key")]
    [InlineData("mismatched-key")]
    public void InvalidMappedRowsCannotAssertAnAttachedParent(string invalid)
    {
        var port = invalid switch {
            "disconnected" => Raw() with { DeviceConnected = false },
            "blank-hub" => Raw() with { HubDevicePath = " " },
            "zero-port" => Raw() with { PortNumber = 0 },
            "missing-key" => Raw() with { DriverKeyName = null },
            "blank-key" => Raw() with { DriverKeyName = " " },
            _ => Raw() with { DriverKeyName = "different-key" }
        };
        var malformedIndex = new Dictionary<string, DeviceTopologyUsbPort>(StringComparer.OrdinalIgnoreCase) { [DriverKey] = port };
        Assert.Null(WindowsDeviceTopologyReader.FindUsbPort(Native(DriverKey), malformedIndex));
        var index = WindowsUsbHubIoctlReader.CreateDriverKeyIndex([port]);
        Assert.Null(WindowsDeviceTopologyReader.FindUsbPort(Native(DriverKey), index));
        if (invalid != "mismatched-key") Assert.Null(WindowsDeviceTopologyReader.CreateUsbConnection(port, true)!.ActualParentPortIdentity);
    }

    [Fact]
    public void AmbiguousKeysChooseNeitherPortInEitherEnumerationOrder()
    {
        var first = Raw(); var second = first with { HubDevicePath = "other hub", PortNumber = 9, DriverKeyName = " " + DriverKey.ToLowerInvariant() + " " };
        foreach (var ports in new[] { new[] { first, second }, new[] { second, first }, new[] { first, first } })
        {
            var index = WindowsUsbHubIoctlReader.CreateDriverKeyIndex(ports);
            Assert.Empty(index); Assert.Null(WindowsDeviceTopologyReader.FindUsbPort(Native(DriverKey), index));
        }
    }

    [Fact]
    public void DisconnectedKeyDoesNotHideUniqueAttachedPort()
    {
        var attached = Raw(); var old = attached with { DeviceConnected = false, PortNumber = 2 };
        var index = WindowsUsbHubIoctlReader.CreateDriverKeyIndex([old, attached]);
        Assert.Same(attached, WindowsDeviceTopologyReader.FindUsbPort(Native(DriverKey), index));
    }

    [Theory]
    [InlineData(@"\\?\usb#root_hub#instance\")]
    [InlineData(@"\??\USB#ROOT_HUB#INSTANCE")]
    [InlineData(@"\\.\USB#ROOT_HUB#INSTANCE")]
    [InlineData("usb#root_hub#instance")]
    public void ParentUsesOriginalHubPortNormalization(string hub)
    {
        Assert.Equal(Parent, WindowsDeviceTopologyReader.CreateUsbConnection(Raw() with { HubDevicePath = hub }, true)!.ActualParentPortIdentity);
    }

    [Fact]
    public void UnmatchedConnectorRepresentativeNeverClaimsDeviceParent()
    {
        var connection = WindowsDeviceTopologyReader.CreateUsbConnection(Raw(), publishActualParentPort: false)!;
        Assert.True(connection.DeviceConnected); Assert.Equal("Product", connection.ProductName);
        Assert.Null(connection.ActualParentPortIdentity);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(connection), DateTimeOffset.UtcNow)).Value);
        Assert.Null(WindowsDeviceTopologyReader.CreateUsbConnection(null, true));
    }

    [Fact]
    public void ParentSurvivesTypedRoundTripChangesHashAndIsNullInOldOrUnobservedSnapshots()
    {
        var healthy = Healthy(); var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(healthy, options), options)!;
        Assert.Equal(Parent, Field(DeviceTopologyInformationProjection.Create(restored, DateTimeOffset.UtcNow)).Value!.Value.GetString());
        var node = JsonSerializer.SerializeToNode(healthy.Ports[0].Usb!, options)!.AsObject(); node.Remove("actualParentPortIdentity");
        var old = node.Deserialize<DeviceTopologyUsbConnection>(options)!; Assert.Null(old.ActualParentPortIdentity);
        Assert.Null(Field(DeviceTopologyInformationProjection.Create(Snapshot(old), DateTimeOffset.UtcNow)).Value);
        var comparer = new DeviceTopologySemanticComparer(); Assert.NotEqual(comparer.ComputeHash(healthy), comparer.ComputeHash(Snapshot(old)));
        var field = Field(DeviceTopologyInformationProjection.Create(healthy, null)); Assert.Null(field.Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(field, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
        var disconnected = healthy.Ports[0].Usb! with { DeviceConnected = false };
        Assert.DoesNotContain(DeviceTopologyInformationProjection.Create(Snapshot(disconnected), DateTimeOffset.UtcNow).Devices.SelectMany(d => d.Fields), f => f.Definition.Name == Name);
    }

    internal static DeviceTopologyUsbPort Raw() => new(@"\\?\usb#root_hub#instance\", 7, true, 1U,
        "HighSpeed", 0x1234, 0x5678, 3, false, DriverKey, null, null,
        new("USB 2.0", "1.0", "Device", "Vendor", "Product", "Serial", [], [], [], []));
    private static DeviceTopologyNativeDevice Native(string key) => new(@"USB\DEVICE", null, null, null, null, key, null, null, null, null, [], [], [], null, null, null);
    internal static DeviceTopologySnapshot Snapshot(DeviceTopologyUsbConnection connection) => DeviceInformationTestData.Snapshot(
        DeviceInformationTestData.Port(@"USB\DEVICE") with { BusKind = DeviceBusKinds.Usb, Usb = connection });
    internal static DeviceTopologySnapshot Healthy() => Snapshot(WindowsDeviceTopologyReader.CreateUsbConnection(Raw(), true)!);
    internal static DeviceInformationField Field(DeviceInformationSnapshot snapshot) => DeviceInformationTestData.Field(snapshot, Identity, Name);
}
