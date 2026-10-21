using System.Collections.Immutable;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices.Usb;
using ResourceManager.ReadWrite.Dependencies;

internal static class UvcFrameIntervalConsumer
{
    internal static async Task RunRecordedAsync()
    {
        var source = new RecordedSource();
        using var owner = source.CreateOwner();
        using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Require(source.Reads == 0 && core.Catalog.Fields.IsEmpty, "construction and catalog remain passive");
        var catalog = await core.DiscoverAsync();
        var field = new FieldReference(RecordedSource.Identity, "camera_all_frame_interval_capability");
        Require(catalog.Fields.Any(item => item.Field == field), "public canonical field registered for exact parent");
        var capability = core.Query(field);
        Require(capability.Read.Supported && capability.Read.Dependencies.IsEmpty && !capability.Write.Supported
            && capability.Definition.Unit == "100ns" && capability.Definition.ValueKind == "structured",
            "source, type, units and read-only public capability");
        var readCount = source.Reads;
        Verify(core.ReadCached(field));
        Require(source.Reads == readCount, "cached access does not parse/capture again");
        var direct = (await core.CollectAsync([field])).Single();
        Verify(direct);
        Require(direct.Value!.Structure!.Value.GetRawText() == core.ReadCached(field).Value!.Structure!.Value.GetRawText(),
            "direct and original cached current agree");
        source.Fail = true;
        var failed = (await core.CollectAsync([field])).Single();
        Require(failed is { Status: FieldObservationStatus.Unavailable, Value: null, ObservedAt: null }
            && core.Catalog.Fields.Any(item => item.Field == field), "capture failure keeps definition and clears value");
        source.Fail = false;
        Verify((await core.CollectAsync([field])).Single());
        Console.WriteLine("[UVC-INTERVAL-RW-RECORDED] exact-parent; full-100ns-list; default; direct/current; failure/recovery; no-live-camera");
    }

    private sealed class RecordedSource : IDeviceTopologyReader
    {
        internal const string Identity = @"windows-pnp|USB\RECORDED_UVC";
        private const string DriverKey = "recorded-uvc-driver";
        private readonly byte[] configuration = Convert.FromBase64String("CQJPAAEBAIAyCQQCAQAOAgAAGyQEAwFZVVkyAAAAAAAAAAAAAAAAEAEAAAAAIiQFAQCABzgEAAAAAAAAAAAAAAAAKywKAAJjFwUAKywKAA==");
        private IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor> captured = [];
        internal int Reads { get; private set; }
        internal bool Fail { get; set; }
        public DeviceTopologySnapshot ReadSnapshot()
        {
            Reads++;
            if (Fail) throw new InvalidOperationException("recorded UVC descriptor failure");
            captured = UsbDescriptorParser.ParseUvcCameraModes(configuration);
            return new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []);
        }
        internal DeviceTopologyCaptureOwner CreateOwner()
            => new(new NoAcpi(), new(this,
                _ => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(configuration)),
                (_, time) => Project(time), static (_, _) => Task.CompletedTask,
                static _ => new("recorded-uvc-failure", []),
                static (_, _, _, _) => { }, static _ => { }, static () => { }));
        private DeviceInformationSnapshot Project(DateTimeOffset? time)
        {
            var descriptor = new DeviceTopologyUsbDescriptor("USB 2.0", "1.0", "UVC", null, "Recorded", null,
                [], [], [], captured);
            var port = new DeviceTopologyUsbPort("recorded-hub", 7, true, 1, "HighSpeed", 1, 2, 3, false,
                DriverKey, null, null, descriptor);
            var native = new DeviceTopologyNativeDevice(Identity["windows-pnp|".Length..], null, null, null,
                null, DriverKey, null, null, null, null, [], [], [], null, null, null);
            return WindowsUsbHubIoctlReader.ProjectFields(new([port],
                new Dictionary<string, DeviceTopologyUsbPort>(StringComparer.OrdinalIgnoreCase) { [WindowsUsbHubIoctlReader.NormalizeDriverKey(DriverKey)] = port }, null), [native], time);
        }
    }
    private sealed class NoAcpi : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("UVC does not query ACPI"); }
    private static void Require(bool value, string contract)
    { if (!value) throw new InvalidOperationException("UVC recorded consumer: " + contract); }
    private static void Verify(FieldReading reading)
    {
        Require(reading.Status == FieldObservationStatus.Current && reading.Value?.Structure is not null,
            "original public current is structured");
        var row = reading.Value!.Structure!.Value[0];
        Require(row.GetProperty("usbDeviceIdentity").GetString() == RecordedSource.Identity, "exact parent PnP identity");
        var evidence = row.GetProperty("frameIntervalsDeclared");
        Require(evidence.GetProperty("interfaceNumber").GetByte() == 2
            && evidence.GetProperty("alternateSetting").GetByte() == 1
            && evidence.GetProperty("formatIndex").GetByte() == 3
            && evidence.GetProperty("frameIndex").GetByte() == 1
            && evidence.GetProperty("defaultFrameInterval100Nanoseconds").GetUInt32() == 666667
            && evidence.GetProperty("discreteIntervals100Nanoseconds").EnumerateArray()
                .Select(static item => item.GetUInt32()).SequenceEqual(new uint[] { 333667, 666667 }),
            "complete raw integer list/default and descriptor directory identities");
    }
}
