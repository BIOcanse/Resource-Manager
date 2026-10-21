using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Usb;

/// <summary>Public ReadWriteCore consumer exercised with a recorded USB topology row.</summary>
internal static class UsbSpeedConsumerExample
{
    public const string NegotiatedField = "usb_all_link_speed_negotiated";
    public const string CapabilityField = "usb_all_link_speed_capability";

    internal static async Task RunRecordedAsync()
    {
        var source = new RecordedUsbTopologySource();
        AssertRawSspDeclaration(source.RecordedPort.Descriptor.SuperSpeedPlusCapability);
        var current = source.ReadFields();
        using var client = new HttpClient();
        var backend = new InformationReadWriteBackend(() => current, token =>
        {
            token.ThrowIfCancellationRequested();
            current = source.ReadFields();
            return ValueTask.FromResult(current);
        }, static _ => new(true, null, []));
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var catalog = await core.DiscoverAsync();
        var selection = ImmutableArray.Create(
            new FieldReference(source.DeviceIdentity, NegotiatedField),
            new FieldReference(source.DeviceIdentity, CapabilityField));
        Require(selection.All(field => catalog.Fields.Any(candidate => candidate.Field == field)),
            "candidate package catalog exposes both USB speed fields for the recorded attached device");
        Require(selection.All(field => core.Query(field).Read.Supported && !core.Query(field).Write.Supported),
            "both public USB speed fields are read-only");

        var readings = await ReadRatesAsync(core, source.DeviceIdentity, CancellationToken.None);
        var negotiated = readings.Single(reading => reading.FieldName == NegotiatedField);
        var capability = readings.Single(reading => reading.FieldName == CapabilityField);
        Require(negotiated.Status == FieldObservationStatus.Current && negotiated.MegabitsPerSecond == 10000d,
            "public collect returns the recorded negotiated link rate in Mbit/s");
        Require(capability.Status == FieldObservationStatus.Unavailable && capability.MegabitsPerSecond is null,
            "unknown SSP aggregate BOS capability remains unavailable through the public consumer");

        var captures = source.ReadCount;
        var cached = core.ReadCached(selection[0]);
        Require(cached.Status == FieldObservationStatus.Current && cached.Value?.Number == 10000d
            && source.ReadCount == captures, "public cached read shares current data without recapturing topology");
        Console.WriteLine("USB speed recorded PackageConsumer passed: public field catalog, selected collect, Mbit/s value, null SSP capability and cached current; no live USB read.");
        Console.WriteLine("USB SSP recorded PackageConsumer passed: public typed BOS declaration, selected ReadWriteCore fields, current negotiated rate, unavailable aggregate capability and cached current; no live USB read.");
    }

    /// <summary>
    /// Select the two canonical USB fields by the caller's exact device identity.
    /// Capture and current-value lifetime remain owned by ReadWriteCore.
    /// </summary>
    public static async ValueTask<ImmutableArray<UsbSpeedReading>> ReadRatesAsync(
        ReadWriteCore core,
        string usbDeviceIdentity,
        CancellationToken cancellationToken)
    {
        var selection = ImmutableArray.Create(
            new FieldReference(usbDeviceIdentity, NegotiatedField),
            new FieldReference(usbDeviceIdentity, CapabilityField));
        var readings = await core.CollectAsync(selection, cancellationToken);
        return [.. readings.Select(static reading => new UsbSpeedReading(
            reading.Field.FieldName,
            reading.Status,
            reading.Value?.Number))];
    }

    private sealed class RecordedUsbTopologySource
    {
        private readonly DateTimeOffset observedAt = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);
        private int readCount;

        public string DeviceIdentity { get; } = @"windows-pnp|USB\VID_0BDA&PID_8153\RECORDED-0001";
        private string DriverKey { get; } = @"USB\VID_0BDA&PID_8153\RECORDED-0001";
        public int ReadCount => Volatile.Read(ref readCount);
        public DeviceTopologyUsbPort RecordedPort { get; } = CreateRecordedPort();

        public DeviceInformationSnapshot ReadFields()
        {
            Interlocked.Increment(ref readCount);
            var capture = new DeviceTopologyUsbPortSnapshot([RecordedPort],
                new Dictionary<string, DeviceTopologyUsbPort>(StringComparer.OrdinalIgnoreCase)
                {
                    [WindowsUsbHubIoctlReader.NormalizeDriverKey(DriverKey)] = RecordedPort
                }, null);
            var device = new DeviceTopologyNativeDevice(
                DeviceId: DeviceIdentity["windows-pnp|".Length..],
                FriendlyName: "Recorded SSP Device",
                DeviceDescription: "Recorded SSP Device",
                Manufacturer: "Recorded Vendor",
                Service: null,
                DriverKey: DriverKey,
                ClassName: "USB",
                ClassGuid: null,
                EnumeratorName: "USB",
                LocationInfo: null,
                LocationPaths: [],
                HardwareIds: [],
                CompatibleIds: [],
                ParentDeviceId: null,
                DevNodeStatus: null,
                ProblemCode: null);
            return WindowsUsbHubIoctlReader.ProjectFields(capture, [device], observedAt);
        }

        private static DeviceTopologyUsbPort CreateRecordedPort()
        {
            var descriptor = new DeviceTopologyUsbDescriptor(
                DeviceSpecification: "USB 3.2",
                DeviceRevision: "1.00",
                DeviceClass: "interface-defined",
                ManufacturerName: "Recorded Vendor",
                ProductName: "Recorded SSP Device",
                SerialNumber: "RECORDED-0001",
                InterfaceProtocols: [],
                Endpoints: [],
                HidDescriptors: [],
                CameraModes: [],
                DeclaredMaximumLinkSpeedMegabitsPerSecond: null,
                SuperSpeedPlusCapability: new DeviceTopologyUsbSuperSpeedPlusCapability(
                    DescriptorLength: 20,
                    DescriptorType: 0x10,
                    DeviceCapabilityType: 0x0A,
                    ReservedByte: 0,
                    RawBmAttributes: 0x00000001,
                    RawFunctionalitySupport: 0x1100,
                    RawReservedWord: 0,
                    SublinkSpeedAttributes: [
                        new(0x000A4030),
                        new(0x000A40B0)]));
            return new DeviceTopologyUsbPort(
                HubDevicePath: "recorded-hub",
                PortNumber: 3,
                DeviceConnected: true,
                ConnectionStatus: 1,
                NegotiatedSpeed: "SuperSpeedPlus",
                VendorId: 0x0BDA,
                ProductId: 0x8153,
                DeviceAddress: 1,
                DeviceIsHub: false,
                DriverKeyName: @"USB\VID_0BDA&PID_8153\RECORDED-0001",
                ConnectorProperties: null,
                Capability: new DeviceTopologyUsbPortCapability(
                    SupportsUsb11: true,
                    SupportsUsb20: true,
                    SupportsUsb30: true,
                    OperatingAtSuperSpeedOrHigher: true,
                    SuperSpeedCapableOrHigher: true,
                    OperatingAtSuperSpeedPlusOrHigher: true,
                    SuperSpeedPlusCapableOrHigher: true),
                Descriptor: descriptor,
                NegotiatedSpeedCode: 3,
                NegotiatedLinkSpeedMegabitsPerSecond: 10000d);
        }
    }

    private static void AssertRawSspDeclaration(DeviceTopologyUsbSuperSpeedPlusCapability? capability)
    {
        var declaration = capability
            ?? throw new InvalidOperationException("public descriptor DTO retains the recorded SSP declaration");
        Require(declaration.DescriptorLength == 20
            && declaration.RawBmAttributes == 1
            && declaration.RawFunctionalitySupport == 0x1100
            && declaration.RawReservedWord == 0,
            "public typed BOS DTO preserves exact SSP header and functionality words");
        Require(declaration.SublinkSpeedAttributeCount == 2
            && declaration.SublinkSpeedIdCount == 1
            && declaration.MinimumReceiveLaneCountEncoded == 1
            && declaration.MinimumTransmitLaneCountEncoded == 1,
            "typed BOS DTO exposes per-declaration counts and minimum functionality lane fields");
        Require(declaration.SublinkSpeedAttributes.Length == 2
            && declaration.SublinkSpeedAttributes[0].RawValue == 0x000A4030
            && declaration.SublinkSpeedAttributes[0].MaximumLaneMegabitsPerSecond == 10000m
            && declaration.SublinkSpeedAttributes[0].SublinkTypeDirection == 0
            && declaration.SublinkSpeedAttributes[1].RawValue == 0x000A40B0
            && declaration.SublinkSpeedAttributes[1].SublinkTypeDirection == 1,
            "typed BOS DTO retains paired receive/transmit sublink attributes without aggregating them");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

public sealed record UsbSpeedReading(
    string FieldName,
    FieldObservationStatus Status,
    double? MegabitsPerSecond);
