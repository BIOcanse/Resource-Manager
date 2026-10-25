using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.PortableDevices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

// This public-package consumer supplies recorded WPD rows; it never invokes a WPD API.
internal static class WpdInformationConsumer
{
    private const string DeviceA = @"WPD\USB\VID_1234&PID_0001#consumer-A";
    private const string DeviceB = @"WPD\USB\VID_1234&PID_0001#consumer-B";
    private const string StorageA = "Storage-A";
    private const string StorageB = "Storage-B";

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = new WindowsWpdInformationScope(WindowsWpdInformationFields.All,
            maximumDevices: 8, maximumStorageObjectsPerDevice: 8,
            maximumStorageObjects: 16, maximumStringCharacters: 512);
        var reader = new RecordedReader();
        var access = owner.CreatePortableDeviceInformation(scope, reader);
        var backend = access.Backend;
        Require(backend.Catalog.Fields.IsEmpty && reader.Requests.Count == 0,
            "construction and catalog inspection are passive");

        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        Require(reader.Requests.Single().Mode == WindowsWpdInformationReadMode.AllConnectedDevices,
            "explicit discovery uses the WPD directory request");
        var batteryA = Field(catalog, DeviceA, "mobile_all_battery_percent_reported");
        var deviceIdA = Field(catalog, DeviceA, "mobile_all_device_id_declared");
        var firmwareA = Field(catalog, DeviceA, "mobile_all_firmware_version_declared");
        var capacityA = Field(catalog, DeviceA, "mobile_all_storage_capacity_reported", StorageA);
        var capacityB = Field(catalog, DeviceB, "mobile_all_storage_capacity_reported", StorageB);
        Require(catalog.Fields.Length == 8 && catalog.Devices.Count(static item => item.Resource == "mobile_device") == 2
            && catalog.Devices.Count(static item => item.Resource == "mobile_storage") == 2,
            "two WPD connection identities and two storage identities stay separate");
        Require(catalog.Fields.Single(item => item.Field == batteryA).Definition.Unit == "%"
            && catalog.Fields.Single(item => item.Field == deviceIdA).Definition.Unit == "text"
            && catalog.Fields.Single(item => item.Field == firmwareA).Definition.Unit == "text"
            && catalog.Fields.Single(item => item.Field == capacityA).Definition.Unit == "B",
            "public field descriptors retain the four canonical units");
        Require(backend.ReadCached(deviceIdA).Value?.Text == "same-declared-ID"
            && backend.ReadCached(firmwareA).Value?.Text == "firmware-A"
            && backend.ReadCached(capacityA).Value?.Integer == MetricInteger.Parse("18446744073709551615")
            && backend.ReadCached(capacityB).Value?.Integer == MetricInteger.Parse("9007199254740993"),
            "recorded WPD strings and full unsigned capacity precision are public values");

        var before = owner.ReadState();
        var selected = AssertSingle(await backend.CollectAsync([capacityB], CancellationToken.None));
        Require(selected.Status == FieldObservationStatus.Current
            && selected.Value?.Integer == MetricInteger.Parse("9007199254740993"),
            "selected storage capture preserves the exact UInt64");
        var request = reader.Requests.Last();
        var target = request.Targets.Single();
        Require(request.Mode == WindowsWpdInformationReadMode.SelectedObjects
            && request.Fields == WindowsWpdInformationFields.StorageCapacity
            && target.DeviceConnectionId == DeviceB && target.ObjectId == StorageB
            && target.ObjectKind == WindowsWpdInformationObjectKind.Storage
            && target.Fields == WindowsWpdInformationFields.StorageCapacity,
            "selected collection keeps the exact manager connection and WPD storage object ID");
        Require(!ReferenceEquals(before, owner.ReadState())
            && backend.ReadCached(capacityB) == selected
            && backend.ReadCached(batteryA).Value?.Number == 44d
            && backend.ReadCached(deviceIdA).Value?.Text == "same-declared-ID"
            && backend.ReadCached(capacityA).Value?.Integer == MetricInteger.Parse("18446744073709551615"),
            "selected result shares the original current and preserves every unselected device/object");
        Console.WriteLine("[WPD-INFORMATION-PACKAGE] recorded WPD exact identities, canonical fields, selected storage capture and UInt64 precision; no device read.");
    }

    private static FieldReference Field(ReadWriteCatalog catalog, string connectionId, string fieldName,
        string? storageObjectId = null)
    {
        var identity = storageObjectId is null ? DeviceIdentity(connectionId) : StorageIdentity(connectionId, storageObjectId);
        return catalog.Fields.Single(item => item.Field.DeviceIdentity == identity
            && item.Field.FieldName == fieldName).Field;
    }

    private static string DeviceIdentity(string connectionId)
        => "windows-wpd-device|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(connectionId))
            + "|" + Convert.ToBase64String(Encoding.Unicode.GetBytes("DEVICE"));

    private static string StorageIdentity(string connectionId, string objectId)
        => "windows-wpd-storage|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(connectionId))
            + "|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(objectId));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"WPD information package consumer failed: {message}.");
    }

    private static FieldReading AssertSingle(ImmutableArray<FieldReading> readings)
    {
        if (readings.Length != 1) throw new InvalidDataException("Expected one selected WPD field reading.");
        return readings[0];
    }

    private sealed class RecordedReader : IWindowsWpdInformationReader
    {
        private readonly ConcurrentQueue<WindowsWpdInformationCaptureRequest> requests = new();
        public IReadOnlyCollection<WindowsWpdInformationCaptureRequest> Requests => requests.ToArray();

        public WindowsWpdInformationReadResult Read(WindowsWpdInformationScope scope,
            WindowsWpdInformationCaptureRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(request);
            if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
                return new(true,
                [
                    new(DeviceA, "DEVICE", "same-declared-ID", "firmware-A", 44, true,
                        [new(DeviceA, StorageA, ulong.MaxValue)]),
                    new(DeviceB, "DEVICE", "same-declared-ID", "firmware-B", 78, true,
                        [new(DeviceB, StorageB, 9_007_199_254_740_993UL)])
                ], []);

            var target = request.Targets.Single();
            if (target.DeviceConnectionId != DeviceB || target.ObjectId != StorageB
                || target.ObjectKind != WindowsWpdInformationObjectKind.Storage
                || request.Fields != WindowsWpdInformationFields.StorageCapacity)
                throw new InvalidOperationException("Recorded WPD consumer received an unexpected exact target.");
            return new(true, [new(DeviceB, "DEVICE", null, null, null, true,
                [new(DeviceB, StorageB, 9_007_199_254_740_993UL)])], []);
        }
    }
}
