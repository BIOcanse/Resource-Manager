using ResourceManager.App.Domain.Metrics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.PortableDevices;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

// This public package consumer uses recorded WPD rows with the real role-2 composition.
internal static class WpdInformationSubscriptionConsumer
{
    private const string DeviceA = @"WPD\USB\VID_1234&PID_0001#subscription-A";
    private const string DeviceB = @"WPD\USB\VID_1234&PID_0001#subscription-B";
    private const string StorageA = "Internal";
    private const string StorageB = "Removable";

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = new WindowsWpdInformationScope(WindowsWpdInformationFields.All,
            maximumDevices: 8, maximumStorageObjectsPerDevice: 8,
            maximumStorageObjects: 16, maximumStringCharacters: 512);
        var reader = new RecordedReader();
        var access = owner.CreatePortableDeviceInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var capacity = catalog.Fields.Single(item => item.Field.DeviceIdentity == StorageIdentity(DeviceB, StorageB)
            && item.Field.FieldName == "mobile_all_storage_capacity_reported").Field;
        var deviceId = catalog.Fields.Single(item => item.Field.DeviceIdentity == DeviceIdentity(DeviceB)
            && item.Field.FieldName == "mobile_all_device_id_declared").Field;
        var beforeSubscription = reader.ReadCount;

        var failures = new ConcurrentQueue<string>();
        var settings = new SubscriptionRoleSettings(2, 1_000, 1, 3_000, 1_000, 1_000);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(access.BorrowSubscriptionCapture(),
            settings, in configuration, failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-wpd-storage", [capacity],
            TimeSpan.FromMilliseconds(250), timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token),
                "explicit role-2 subscription published a selected storage reading");
            var reading = AssertSingle(stream.Current);
            Require(reading.Status == ResourceManager.ReadWrite.FieldObservationStatus.Current
                && reading.Value?.Integer == MetricInteger.Parse("9007199254740993") && reading.ObservedAt.HasValue,
                "role-2 returns the exact full UInt64 capacity value");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(reading, access.Backend.ReadCached(capacity));
            Require(access.Backend.ReadCached(deviceId).Value?.Text == "device-B-declared-ID",
                "subscription output is the original owner current and preserves another selected device field");
            Require(reader.ReadCount > beforeSubscription, "explicit subscription demand invoked the injected reader");
            var request = reader.Requests.Last();
            var target = AssertSingle(request.Targets);
            Require(request.Mode == WindowsWpdInformationReadMode.SelectedObjects
                && request.Fields == WindowsWpdInformationFields.StorageCapacity
                && target.DeviceConnectionId == DeviceB && target.ObjectId == StorageB
                && target.ObjectKind == WindowsWpdInformationObjectKind.Storage,
                "role-2 uses the exact selected device connection and storage object ID");
            await composition.StopAsync();
            Require(!await stream.MoveNextAsync(), "explicit Stop retires the selected WPD stream");
        }
        Require(failures.IsEmpty, "recorded WPD role-2 capture completed without worker faults");
        Console.WriteLine("[WPD-INFORMATION-SUBSCRIPTION-PACKAGE] recorded exact WPD storage identity used the original owner and role-2 permit; no device read.");
    }

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1_000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3_000,
        MaximumFutureSkewMilliseconds = 1_000
    };

    private static WindowsWpdInformationTarget AssertSingle(ImmutableArray<WindowsWpdInformationTarget> fields)
    {
        if (fields.Length != 1) throw new InvalidDataException("Expected one exact selected WPD target.");
        return fields[0];
    }

    private static FieldReading AssertSingle(ImmutableArray<FieldReading> readings)
    {
        if (readings.Length != 1) throw new InvalidDataException("Expected one WPD subscription reading.");
        return readings[0];
    }

    private static string DeviceIdentity(string connectionId)
        => "windows-wpd-device|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(connectionId))
            + "|" + Convert.ToBase64String(Encoding.Unicode.GetBytes("DEVICE"));

    private static string StorageIdentity(string connectionId, string objectId)
        => "windows-wpd-storage|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(connectionId))
            + "|" + Convert.ToBase64String(Encoding.Unicode.GetBytes(objectId));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException($"WPD subscription consumer failed: {message}.");
    }

    private sealed class RecordedReader : IWindowsWpdInformationReader
    {
        private readonly ConcurrentQueue<WindowsWpdInformationCaptureRequest> requests = new();
        public int ReadCount => requests.Count;
        public IReadOnlyCollection<WindowsWpdInformationCaptureRequest> Requests => requests.ToArray();

        public WindowsWpdInformationReadResult Read(WindowsWpdInformationScope scope,
            WindowsWpdInformationCaptureRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(request);
            if (request.Mode == WindowsWpdInformationReadMode.AllConnectedDevices)
                return new(true,
                [
                    new(DeviceA, "DEVICE", "device-A-declared-ID", "firmware-A", 54, true,
                        [new(DeviceA, StorageA, 4_294_967_297UL)]),
                    new(DeviceB, "DEVICE", "device-B-declared-ID", "firmware-B", 84, true,
                        [new(DeviceB, StorageB, 9_007_199_254_740_993UL)])
                ], []);

            var target = request.Targets.Single();
            if (target.DeviceConnectionId != DeviceB || target.ObjectId != StorageB
                || target.ObjectKind != WindowsWpdInformationObjectKind.Storage
                || request.Fields != WindowsWpdInformationFields.StorageCapacity)
                throw new InvalidOperationException("Recorded WPD subscription received an unexpected exact target.");
            return new(true, [new(DeviceB, "DEVICE", null, null, null, true,
                [new(DeviceB, StorageB, 9_007_199_254_740_993UL)])], []);
        }
    }
}
