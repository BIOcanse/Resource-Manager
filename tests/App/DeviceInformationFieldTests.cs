using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

using static Resource_Manager_APP.Tests.DeviceInformationTestData;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceInformationFieldTests
{
    [Fact]
    public void MachineIdentityNeverBorrowsBiosVendorOrBoardModel()
    {
        var notes = new List<BackendMessage>();
        var system = WindowsDeviceTopologyReader.CreateSystemIdentity(
            new Dictionary<string, object?> { ["Manufacturer"] = "BIOS Vendor", ["SMBIOSBIOSVersion"] = "B1" },
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["Manufacturer"] = "Board Vendor", ["Product"] = "Board Model", ["SerialNumber"] = "S1" }, notes);
        var snapshot = DeviceTopologyInformationProjection.Create(Snapshot() with { System = system }, DateTimeOffset.UtcNow);
        Assert.Null(Field(snapshot, "machine", "machine_all_manufacturer").Value);
        Assert.Null(Field(snapshot, "machine", "machine_all_model_declared").Value);
        Assert.Equal("BIOS Vendor", Field(snapshot, "bios", "bios_all_vendor").Value!.Value.GetString());
        Assert.Equal("Board Model", Field(snapshot, "motherboard", "motherboard_all_model_declared").Value!.Value.GetString());
        Assert.Equal("S1", Field(snapshot, "motherboard", "motherboard_all_serial_number").Value!.Value.GetString());
        Assert.Contains(notes, message => message.Code == BackendMessageCodes.DeviceTopology.IncompleteBrandModel);
    }

    [Fact]
    public void NativeNetworkValuesKeepBothDirectionsUnknownCodesAndExactIntegers()
    {
        var network = WindowsDeviceTopologyReader.CreateNetworkConnection(new DeviceTopologyNetworkAdapter(
            @"PCI\NET", "Ethernet", 47, 9223372036854775811, 0, "AA-BB-CC-DD-EE-FF", 1500, true, true));
        var port = Port(@"PCI\NET") with { Network = network };
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(port), DateTimeOffset.UtcNow);
        const string identity = @"windows-pnp|PCI\NET";
        Assert.Equal("9223372036854775811", Field(fields, identity, "network_all_transmit_link_speed_actual_bits_per_second").Value!.Value.GetString());
        Assert.Equal("0", Field(fields, identity, "network_all_receive_link_speed_actual_bits_per_second").Value!.Value.GetString());
        Assert.Equal("47", Field(fields, identity, "network_all_link_state_actual").Value!.Value.GetString());
        Assert.Equal("windows.wmi.netadapter", Field(fields, identity, "network_all_mtu_actual").Definition.SourceId);
        Assert.Equal("windows.pnp", Field(fields, identity, "network_all_adapter_id_declared").Definition.SourceId);
        Assert.Equal("network.pnp_device_id_declared", Field(fields, identity, "network_all_adapter_id_declared").Definition.Quantity);
        Assert.DoesNotContain(Assert.Single(fields.Devices, d => d.DeviceIdentity == identity).Fields,
            field => field.Definition.Name == "network_all_link_speed_actual");
    }

    [Fact]
    public void NetworkUnknownSpeedIsEmptyAtTheSourceAndNullValuesAreAlwaysSerialized()
    {
        var adapter = WindowsNetworkAdapterTopologyReader.CreateAdapter(new Dictionary<string, object?> {
            ["PnPDeviceID"] = @"PCI\NET", ["TransmitLinkSpeed"] = ulong.MaxValue, ["ReceiveLinkSpeed"] = 0UL });
        Assert.Null(adapter!.TransmitLinkSpeedBitsPerSecond);
        Assert.Equal(0UL, adapter.ReceiveLinkSpeedBitsPerSecond);
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(Port(@"PCI\NET") with {
            Network = WindowsDeviceTopologyReader.CreateNetworkConnection(adapter) }), DateTimeOffset.UtcNow);
        var field = Field(fields, @"windows-pnp|PCI\NET", "network_all_transmit_link_speed_actual_bits_per_second");
        Assert.Null(field.Value);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(field, new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public void DisplayUsesRawFractionAndModeAndNeverEdidDepthAsActual()
    {
        var path = new DeviceTopologyDisplayPath(1, 2, 3, 4, 5, 0, "Panel", @"\\?\DISPLAY#PANEL",
            3840, 2160, 60000, 1001, true, true, "adapter",
            Edid: new DeviceTopologyEdidCapabilities("1.4", "Panel", null, 8, "displayport", null, null, null, true, null));
        var port = WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(path, null));
        Assert.Equal(8u, port.Display!.BitsPerColorChannel); // Existing presentation can include EDID declarations.
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(port), DateTimeOffset.UtcNow);
        var display = Assert.Single(fields.Devices, d => d.ObjectType == "display_path");
        var resolution = Field(fields, display.DeviceIdentity, "display_all_resolution_actual").Value!.Value;
        Assert.Equal(3840u, resolution.GetProperty("width").GetUInt32());
        Assert.Equal(2160u, resolution.GetProperty("height").GetUInt32());
        var rate = Field(fields, display.DeviceIdentity, "display_all_refresh_rate_actual").Value!.Value;
        Assert.Equal(60000u, rate.GetProperty("numerator").GetUInt32());
        Assert.Equal(1001u, rate.GetProperty("denominator").GetUInt32());
        Assert.Equal(60000d / 1001, rate.GetProperty("numericValue").GetDouble());
        Assert.Null(Field(fields, display.DeviceIdentity, "display_all_bit_depth_actual").Value);
        Assert.Null(Field(fields, display.DeviceIdentity, "display_all_advanced_color_supported").Value);
        Assert.Null(Field(fields, display.DeviceIdentity, "display_all_hdr_capability").Value);
        Assert.Null(Field(fields, display.DeviceIdentity, "display_all_hdr_enabled_actual").Value);
        Assert.Null(Field(fields, display.DeviceIdentity, "display_all_hdr_active_actual").Value);
        Assert.Null(Field(fields, display.DeviceIdentity, "display_all_color_mode_actual").Value);
        foreach (var inactive in new[] { port.Display with { Active = false }, port.Display with { TargetAvailable = false } })
        {
            var empty = DeviceTopologyInformationProjection.Create(Snapshot(port with { Display = inactive }), DateTimeOffset.UtcNow);
            Assert.Null(Field(empty, display.DeviceIdentity, "display_all_resolution_actual").Value);
            Assert.Null(Field(empty, display.DeviceIdentity, "display_all_refresh_rate_actual").Value);
        }
    }

    [Fact]
    public void DimmDeclarationsAreLosslessAndObjectsDoNotMergeBySerial()
    {
        var first = Module("smbios-memory|1042");
        var second = first with { DeviceIdentity = "smbios-memory|1043", CapacityBytes = 8192 };
        var fields = DeviceTopologyInformationProjection.Create(Snapshot() with { MemoryModules = [first, second] }, DateTimeOffset.UtcNow);
        Assert.Equal("18446744073709551615", Field(fields, first.DeviceIdentity, "memory_all_dimm_capacity").Value!.Value.GetString());
        Assert.Equal("8192", Field(fields, second.DeviceIdentity, "memory_all_dimm_capacity").Value!.Value.GetString());
        Assert.Equal("smbios.type17", Field(fields, first.DeviceIdentity, "memory_all_dimm_capacity").Definition.SourceId);
        Assert.Equal("bit", Field(fields, first.DeviceIdentity, "memory_all_dimm_data_width_declared_bits").Definition.Unit);
        var unavailable = DeviceTopologyInformationProjection.Create(Snapshot() with { MemoryModules = [first] }, null);
        Assert.All(unavailable.Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
    }

    [Fact]
    public void ConflictingDeviceIdentitiesAreOmittedAndInvalidNumbersAreEmpty()
    {
        var port = Port(@"PCI\NET");
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(port, port with { Id = "other" }) with
            { MemoryModules = [Module("dimm") with { ConfiguredVoltageVolts = double.NaN }] }, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(fields.Devices, device => device.DeviceIdentity == @"windows-pnp|PCI\NET");
        Assert.Null(Field(fields, "dimm", "memory_all_dimm_configured_voltage_declared_volts").Value);
        Assert.Empty(DeviceTopologyInformationProjection.Create(null, DateTimeOffset.UtcNow).Devices);
    }

    [Fact]
    public void UsbDeclarationsAndCameraCapabilitiesKeepTheirSourceAndPrecision()
    {
        var fps = 10000000d / 333667;
        var descriptor = new DeviceTopologyUsbDescriptor("USB 2.0", "1.0", "Camera", null, "Camera Product", "Serial", [], [], [],
            [new(1920, 1080, fps, "YUY2")]);
        var raw = new DeviceTopologyUsbPort("hub", 1, true, 1U, null, 1, 2, 4, false, null, null, null, descriptor);
        var camera = DeviceTopologySpecializedCapabilityProjector.CreateCamera(raw);
        Assert.Equal(fps, Assert.Single(camera!.NativeModes).MaximumFrameRate);
        var usb = new DeviceTopologyUsbConnection("hub", 1, true, Message, null, 4, "0001", "0002", false,
            null, null, null, null, null, null, null, null, null, [], "USB 2.0", "1.0", "Camera", null, "Camera Product", "Serial", []);
        var port = Port(@"USB\CAMERA") with { BusKind = DeviceBusKinds.Usb, Usb = usb, Camera = camera,
            Hid = new("mouse", "1.1", 250, 4000, null, null, Message, null) };
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(port), DateTimeOffset.UtcNow);
        const string identity = @"windows-pnp|USB\CAMERA";
        Assert.Null(Field(fields, identity, "usb_all_manufacturer_declared").Value);
        Assert.Equal("Camera Product", Field(fields, identity, "usb_all_product_name_declared").Value!.Value.GetString());
        Assert.Equal(0.25, Field(fields, identity, "hid_all_poll_interval_theoretical").Value!.Value.GetDouble());
        Assert.Equal(4000, Field(fields, identity, "hid_all_report_rate_theoretical_hz").Value!.Value.GetDouble());
        var mode = Field(fields, identity, "camera_all_uvc_mode_capability").Value!.Value[0];
        Assert.Equal(fps, mode.GetProperty("maximumFrameRate").GetDouble());
        Assert.Equal("usb.video-class-descriptors", Field(fields, identity, "camera_all_uvc_mode_capability").Definition.SourceId);
    }

    [Fact]
    public void OriginalTopologyPublicationProjectsIntoIndependentTypedFieldValuesWithoutChangingIdentityOrUnits()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var snapshot = Snapshot() with { MemoryModules = [Module("smbios-memory|17")] };
        var information = DeviceTopologyInformationProjection.Create(snapshot, observedAt);
        Assert.Equal(typeof(ResourceManager.ReadWrite.ReadWriteCore).Assembly, typeof(DeviceInformationSnapshot).Assembly);
        foreach (var device in information.Devices)
        foreach (var field in device.Fields)
        {
            var reading = InformationFieldProjection.Read(device.DeviceIdentity, field, information.ObservedAt);
            Assert.Equal(device.DeviceIdentity, reading.Field.DeviceIdentity);
            Assert.Equal(field.Definition.Name, reading.Field.FieldName);
            Assert.Equal(field.Definition.Unit, InformationFieldProjection.Describe(field.Definition).Unit);
            Assert.Equal(field.Definition.SourceId, reading.SourceId);
            Assert.Equal(field.Value is null ? ResourceManager.ReadWrite.FieldObservationStatus.Unavailable
                : ResourceManager.ReadWrite.FieldObservationStatus.Current, reading.Status);
        }
        var capacity = Field(information, "smbios-memory|17", "memory_all_dimm_capacity");
        Assert.Equal(MetricInteger.FromUnsigned128(ulong.MaxValue), InformationFieldProjection.Read("smbios-memory|17", capacity, observedAt).Value!.Integer);
    }

}
