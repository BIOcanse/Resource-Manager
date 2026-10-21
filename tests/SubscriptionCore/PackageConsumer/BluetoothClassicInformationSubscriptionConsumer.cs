using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite.Windows.Devices.Bluetooth;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

// This consumer references the ReadWriteCore and SubscriptionCore packages only. It provides
// recorded Bluetooth rows to the public owner contract and then uses the real role-2 composition.
internal static class BluetoothClassicInformationSubscriptionConsumer
{
    private const string Controller = "00:11:22:33:44:01";
    private const string Device = "AA:BB:CC:DD:EE:01";

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = new WindowsBluetoothClassicInformationScope(
            WindowsBluetoothClassicInformationFields.DeviceState, maximumControllers: 4, maximumDevices: 32);
        var reader = new RecordedReader();
        var access = owner.CreateBluetoothClassicInformation(scope, reader);
        var catalog = await access.Backend.DiscoverAsync(CancellationToken.None);
        var connected = catalog.Fields.Single(static item =>
            item.Field.DeviceIdentity == DeviceIdentity(Controller, Device)
            && item.Field.FieldName == "bluetooth_all_device_connected_actual").Field;
        var paired = catalog.Fields.Single(static item =>
            item.Field.DeviceIdentity == DeviceIdentity(Controller, Device)
            && item.Field.FieldName == "bluetooth_all_device_paired_actual").Field;
        var beforeSubscription = reader.ReadCount;

        var failures = new ConcurrentQueue<string>();
        var settings = new SubscriptionRoleSettings(2, 1000, 1, 3000, 1000, 1000);
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(access.BorrowSubscriptionCapture(),
            settings, in configuration, failure => failures.Enqueue($"{failure.Stage}: {failure.Error.Message}"),
            TimeSpan.FromMilliseconds(10));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var stream = composition.SubscribeAsync("recorded-classic-bluetooth", [connected, paired],
            TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator())
        {
            Require(await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token), "role-2 stream published recorded values");
            var readings = stream.Current.ToDictionary(static item => item.Field);
            Require(readings[connected].Status == ResourceManager.ReadWrite.FieldObservationStatus.Current
                && readings[connected].Value?.Boolean == true, "connected state passed through role-2 sampling");
            Require(readings[paired].Status == ResourceManager.ReadWrite.FieldObservationStatus.Current
                && readings[paired].Value?.Boolean == false, "paired state keeps the Classic authenticated flag meaning");
            Require(reader.ReadCount > beforeSubscription, "explicit subscription demand caused a recorded capture");
            Require(reader.Requests.Last() == WindowsBluetoothClassicInformationFields.DeviceState,
                "role-2 requested only the two selected state fields");
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[connected], access.Backend.ReadCached(connected));
            FieldObservationConsumerAssertions.RequireOriginalOwnerObservation(readings[paired], access.Backend.ReadCached(paired));
        }
        await composition.StopAsync();
        Require(failures.IsEmpty, "recorded role-2 sampling completed without worker faults");
        Console.WriteLine("[BLUETOOTH-CLASSIC-SUBSCRIPTION-PACKAGE] recorded Classic state used the original owner, provider, permit, and role-2 composition; no Bluetooth native read.");
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
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Bluetooth Classic subscription consumer failed: {label}.");
    }

    private static string DeviceIdentity(string controller, string device)
        => "windows-bluetooth-device|" + controller + "|" + device;

    private sealed class RecordedReader : IWindowsBluetoothClassicInformationReader
    {
        private static readonly DeviceTopologyBluetoothClassicDeviceInformation RecordedDevice =
            new(Controller, Device, Connected: true, Authenticated: false);
        private readonly ConcurrentQueue<WindowsBluetoothClassicInformationFields> requests = new();

        public IReadOnlyCollection<WindowsBluetoothClassicInformationFields> Requests => requests.ToArray();
        public int ReadCount => requests.Count;

        public WindowsBluetoothClassicInformationReadResult Read(WindowsBluetoothClassicInformationScope scope,
            WindowsBluetoothClassicInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            return new([Controller], Devices:
                (selectedFields & WindowsBluetoothClassicInformationFields.DeviceState) != 0
                    ? new(true, [RecordedDevice]) : null);
        }
    }
}
