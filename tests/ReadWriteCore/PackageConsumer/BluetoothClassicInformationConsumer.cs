using System.Collections.Concurrent;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Bluetooth;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

// This consumer references only the ReadWriteCore package. It injects the public recorded-source
// contract, so discovery and capture exercise the real owner without a Bluetooth API call.
internal static class BluetoothClassicInformationConsumer
{
    private const string Controller = "00:11:22:33:44:01";
    private const string Device = "AA:BB:CC:DD:EE:01";

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var owner = DeviceTopologyCaptureOwner.CreateDriverInformation();
        var scope = new WindowsBluetoothClassicInformationScope(
            WindowsBluetoothClassicInformationFields.All, maximumControllers: 4, maximumDevices: 32);
        var reader = new RecordedReader();
        var access = owner.CreateBluetoothClassicInformation(scope, reader);
        var backend = access.Backend;
        Require(backend.Catalog.Fields.IsEmpty && reader.ReadCount == 0,
            "catalog construction is passive");

        var catalog = await backend.DiscoverAsync(CancellationToken.None);
        var connected = catalog.Fields.Single(static item =>
            item.Field.DeviceIdentity == DeviceIdentity(Controller, Device)
            && item.Field.FieldName == "bluetooth_all_device_connected_actual").Field;
        var paired = catalog.Fields.Single(static item =>
            item.Field.DeviceIdentity == DeviceIdentity(Controller, Device)
            && item.Field.FieldName == "bluetooth_all_device_paired_actual").Field;
        var controller = catalog.Fields.Single(static item =>
            item.Field.DeviceIdentity == ControllerIdentity(Controller)
            && item.Field.FieldName == "bluetooth_all_controller_id_declared").Field;
        Require(reader.ReadCount == 1, "explicit discovery read once");
        Require(backend.ReadCached(connected).Value?.Boolean == true, "recorded connected flag");
        Require(backend.ReadCached(paired).Value?.Boolean == false, "recorded Windows authenticated flag remains paired false");
        Require(backend.ReadCached(controller).Value?.Text == Controller, "reported local controller address");

        var requestsAfterDiscovery = reader.Requests.Count;
        var collected = (await backend.CollectAsync([connected], CancellationToken.None)).Single();
        Require(collected.Status == FieldObservationStatus.Current && collected.Value?.Boolean == true,
            "selected connected capture");
        Require(reader.Requests.Last() == WindowsBluetoothClassicInformationFields.DeviceConnected,
            "selected collection requested only the canonical connected field");
        Require(reader.Requests.Count == requestsAfterDiscovery + 1, "one explicit selected capture");
        Require(backend.ReadCached(connected) == collected, "selected result is in the owner's current");
        Require(owner.ReadState().InformationFields.FindField(connected) is not null,
            "Bluetooth result resides in the original topology owner");
        Require(backend.ReadCached(paired).Value?.Boolean == false, "unselected paired field is preserved");

        Console.WriteLine("[BLUETOOTH-CLASSIC-PACKAGE] recorded Classic fields, selected capture, passive cache, and original owner current passed; no Bluetooth native read.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Bluetooth Classic package consumer failed: {label}.");
    }

    private static string ControllerIdentity(string address) => "windows-bluetooth-controller|" + address;
    private static string DeviceIdentity(string controller, string device)
        => "windows-bluetooth-device|" + controller + "|" + device;

    private sealed class RecordedReader : IWindowsBluetoothClassicInformationReader
    {
        private static readonly DeviceTopologyBluetoothClassicDeviceInformation RecordedDevice =
            new(Controller, Device, Connected: true, Authenticated: false);
        private static readonly DeviceTopologyBluetoothClassicControllerInformation RecordedController = new(Controller);
        private readonly ConcurrentQueue<WindowsBluetoothClassicInformationFields> requests = new();

        public IReadOnlyCollection<WindowsBluetoothClassicInformationFields> Requests => requests.ToArray();
        public int ReadCount => requests.Count;

        public WindowsBluetoothClassicInformationReadResult Read(WindowsBluetoothClassicInformationScope scope,
            WindowsBluetoothClassicInformationFields selectedFields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Enqueue(selectedFields);
            return new([Controller],
                (selectedFields & WindowsBluetoothClassicInformationFields.ControllerId) != 0
                    ? new(true, [RecordedController]) : null,
                (selectedFields & WindowsBluetoothClassicInformationFields.DeviceState) != 0
                    ? new(true, [RecordedDevice]) : null);
        }
    }
}
